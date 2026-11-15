// C-LABTAP Logbook kiosk - Equipment tab.
//
// Unlike Attendance (tap = instant record), Equipment needs an item
// picked before anything is submitted, so identifying a student here
// only confirms who they are - RFID tap and manual LRN entry both just
// resolve to a student id/name via LogbookController.LookupStudent
// (same endpoint Attendance uses, extended with an rfidTag param and an
// id field - see chat) and then the right panel takes over.
//
// The right panel used to be a separate Borrow/Return nav sub-page (see
// chat - dropped for being a lot of fragile nav CSS over a small
// distinction). Now it's one screen driven by the student's own data:
// once identified, we fetch both what they currently have borrowed and
// what's free to take out, and render whichever sections actually apply
// - "Your Borrowed Items" (with a staff-PIN-gated Return action) only
// appears when they have something out; "Available Equipment" (self-
// serve Borrow, same trust level as an attendance tap) always does.
//
// Items are tracked one row per physical unit (own item_code), but the
// kiosk picker groups same-name/category units into one row with a
// quantity stepper instead of a long one-by-one checklist (see chat -
// "why is it showing all one by one?") - the specific unit ids within a
// group are picked automatically (first N) when submitting, same
// Borrow/Return endpoints as before, no backend change needed.
(function () {
    "use strict";

    var idleView = document.getElementById("logbookIdle");
    var manualView = document.getElementById("logbookManual");
    var identifiedView = document.getElementById("logbookIdentified");
    var identifiedName = document.getElementById("logbookIdentifiedName");
    var startOverLink = document.getElementById("logbookStartOverLink");
    var modeLink = document.getElementById("logbookModeLink");
    var modeLinkBack = document.getElementById("logbookModeLinkBack");
    var lrnInput = document.getElementById("logbookLrnInput");
    var confirmBox = document.getElementById("logbookConfirmBox");
    var confirmName = document.getElementById("logbookConfirmName");
    var confirmBtn = document.getElementById("logbookConfirmBtn");
    var rfidCatcher = document.getElementById("logbookRfidCatcher");
    var identifyError = document.getElementById("logbookIdentifyError");

    var equipRoot = document.getElementById("logbookEquipRoot");
    var equipError = document.getElementById("logbookEquipError");

    // Return-only staff PIN overlay.
    var pinOverlay = document.getElementById("equipReturnPinOverlay");
    var pinInput = document.getElementById("equipReturnPinInput");
    var pinError = document.getElementById("equipReturnPinError");
    var pinCancel = document.getElementById("equipReturnPinCancel");
    var pinClose = document.getElementById("equipReturnPinClose");
    var pinConfirm = document.getElementById("equipReturnPinConfirm");

    var mode = "idle"; // "idle" (RFID-ready) | "manual"
    var inputLocked = false;
    var lastLookedUpLrn = null;
    var pendingStudentId = null;
    var pendingStudentName = null;
    var confirmedStudentId = null;
    var confirmedStudentName = null;

    var borrowedItems = [];  // this student's currently-out items (sessionItemId, name, itemCode)
    var availableItems = []; // items free to borrow right now (id, name, itemCode, category)
    var borrowQty = {};  // group key -> chosen quantity (Available Equipment)
    var returnQty = {};  // group key -> chosen quantity (Your Borrowed Items)
    var pendingReturnIds = []; // snapshot of the picked sessionItemIds while the PIN overlay is open

    // ---------- identify: mode switching ----------

    function setInputLocked(locked) {
        inputLocked = locked;
        lrnInput.disabled = locked;
        idleView.classList.toggle("is-locked", locked);
        manualView.classList.toggle("is-locked", locked);
    }

    function focusCatcher() {
        if (mode === "idle" && !inputLocked && confirmedStudentId == null) rfidCatcher.focus();
    }

    function showIdentifyError(message) {
        identifyError.textContent = message;
        identifyError.style.display = "block";
        clearTimeout(showIdentifyError._t);
        showIdentifyError._t = setTimeout(function () {
            identifyError.style.display = "none";
        }, 2500);
    }

    function showIdleMode() {
        setInputLocked(false);
        mode = "idle";
        idleView.style.display = "";
        manualView.style.display = "none";
        identifiedView.style.display = "none";
        lrnInput.value = "";
        confirmBox.style.display = "none";
        lastLookedUpLrn = null;
        pendingStudentId = null;
        pendingStudentName = null;
        focusCatcher();
    }

    function showManualMode() {
        mode = "manual";
        idleView.style.display = "none";
        manualView.style.display = "flex";
        identifiedView.style.display = "none";
        lrnInput.value = "";
        confirmBox.style.display = "none";
        lrnInput.focus();
    }

    function showIdentified(name) {
        idleView.style.display = "none";
        manualView.style.display = "none";
        identifiedView.style.display = "flex";
        identifiedName.textContent = name;
    }

    function startOver() {
        confirmedStudentId = null;
        confirmedStudentName = null;
        borrowQty = {};
        returnQty = {};
        borrowedItems = [];
        availableItems = [];
        resetEquipPanel();
        showIdleMode();
    }

    if (modeLink) {
        modeLink.addEventListener("click", function (e) {
            e.preventDefault();
            if (inputLocked) return;
            showManualMode();
        });
    }
    if (modeLinkBack) {
        modeLinkBack.addEventListener("click", function (e) {
            e.preventDefault();
            if (inputLocked) return;
            showIdleMode();
        });
    }
    if (startOverLink) {
        startOverLink.addEventListener("click", function (e) {
            e.preventDefault();
            startOver();
        });
    }

    // ---------- RFID catcher ----------

    rfidCatcher.addEventListener("blur", function () {
        setTimeout(focusCatcher, 50);
    });

    rfidCatcher.addEventListener("keydown", function (e) {
        if (e.key === "Enter") {
            e.preventDefault();
            var tag = rfidCatcher.value.trim();
            rfidCatcher.value = "";
            if (inputLocked || confirmedStudentId != null || !tag) return;
            identify({ rfidTag: tag });
        }
    });

    // ---------- Manual LRN entry: auto-looks-up once 12 digits are in ----------

    lrnInput.addEventListener("input", function () {
        if (inputLocked) {
            lrnInput.value = "";
            return;
        }
        lrnInput.value = lrnInput.value.replace(/\D/g, "").slice(0, 12);

        if (lrnInput.value !== lastLookedUpLrn) {
            confirmBox.style.display = "none";
        }
        if (lrnInput.value.length === 12) {
            lastLookedUpLrn = lrnInput.value;
            identify({ lrn: lrnInput.value }, true);
        }
    });

    confirmBtn.addEventListener("click", function () {
        if (inputLocked || pendingStudentId == null) return;
        confirmedStudentId = pendingStudentId;
        confirmedStudentName = pendingStudentName;
        showIdentified(confirmedStudentName);
        loadEquipPanel();
    });

    // ---------- Identify (lookup-only, not a recorded action) ----------

    function identify(opts, isManualAutoLookup) {
        var qs = opts.rfidTag ? "rfidTag=" + encodeURIComponent(opts.rfidTag) : "lrn=" + encodeURIComponent(opts.lrn);
        fetch("/Logbook/LookupStudent?" + qs)
            .then(function (res) { return res.json(); })
            .then(function (data) {
                if (isManualAutoLookup && lrnInput.value !== opts.lrn) return; // value changed while waiting

                if (!data.found) {
                    showIdentifyError("Not recognized. Please try again.");
                    if (isManualAutoLookup) confirmBox.style.display = "none";
                    return;
                }

                if (opts.rfidTag) {
                    // RFID tap identifies immediately - no separate confirm
                    // click, same trust level as an Attendance tap.
                    confirmedStudentId = data.id;
                    confirmedStudentName = data.name;
                    showIdentified(confirmedStudentName);
                    loadEquipPanel();
                } else {
                    pendingStudentId = data.id;
                    pendingStudentName = data.name;
                    confirmName.textContent = data.name;
                    confirmBox.style.display = "flex";
                }
            })
            .catch(function () {
                showIdentifyError("Could not look that up right now.");
            });
    }

    // ---------- Equipment panel (right side) ----------

    function resetEquipPanel() {
        equipError.style.display = "none";
        equipRoot.innerHTML = '<p class="logbook-equip-hint" id="logbookEquipHint">Identify yourself to start.</p>';
    }

    function escapeHtml(value) {
        return String(value == null ? "" : value).replace(/[&<>"']/g, function (ch) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch];
        });
    }

    // Collapses a flat list of physical units into one row per
    // name+category, keeping the member units (so submit can grab the
    // first N ids) in borrow/arrival order.
    function groupItems(list) {
        var order = [];
        var byKey = {};
        list.forEach(function (item) {
            var key = item.name + "|" + (item.category || "");
            if (!byKey[key]) {
                byKey[key] = { key: key, name: item.name, category: item.category || "", units: [] };
                order.push(key);
            }
            byKey[key].units.push(item);
        });
        return order.map(function (key) { return byKey[key]; });
    }

    // Builds one section (a heading, a grouped item list or empty
    // message, and an action button) and appends it to the panel. kind
    // is "borrow" or "return" - just picks which quantity map/labels/
    // handler/unit-id-field apply.
    function buildSection(kind, title, list, emptyMessage, actionLabel, onAction) {
        var qtyMap = kind === "return" ? returnQty : borrowQty;
        var idField = kind === "return" ? "sessionItemId" : "id";

        var section = document.createElement("div");
        section.className = "logbook-equip-section";

        var heading = document.createElement("h3");
        heading.className = "logbook-equip-section-title";
        heading.textContent = title;
        section.appendChild(heading);

        if (!list.length) {
            var empty = document.createElement("p");
            empty.className = "logbook-equip-empty";
            empty.textContent = emptyMessage;
            section.appendChild(empty);
            equipRoot.appendChild(section);
            return;
        }

        var groups = groupItems(list);
        var listEl = document.createElement("div");
        listEl.className = "logbook-equip-list";

        // Running selection summary ("cart") for the Return side only
        // (see chat) - a small boxed list of what's picked so far,
        // updated live as the stepper changes, so it's clear what's
        // about to be returned before hitting "Return Selected".
        var cartEl = null;
        var cartListEl = null;
        if (kind === "return") {
            cartEl = document.createElement("div");
            cartEl.className = "logbook-equip-cart";
            cartEl.innerHTML =
                '<p class="logbook-equip-cart-title">Selected to return</p>' +
                '<ul class="logbook-equip-cart-list"></ul>';
            cartListEl = cartEl.querySelector(".logbook-equip-cart-list");
        }

        function updateCart() {
            if (!cartEl) return;
            var lines = groups
                .map(function (g) { return { name: g.name, qty: qtyMap[g.key] || 0 }; })
                .filter(function (l) { return l.qty > 0; });
            cartEl.classList.toggle("is-empty", lines.length === 0);
            if (lines.length === 0) {
                cartListEl.innerHTML = '<li class="logbook-equip-cart-empty">Nothing selected yet.</li>';
                return;
            }
            cartListEl.innerHTML = lines.map(function (l) {
                return '<li><span>' + escapeHtml(l.name) + '</span><span class="logbook-equip-cart-qty">\u00d7' + l.qty + '</span></li>';
            }).join("");
        }

        groups.forEach(function (group) {
            var qty = qtyMap[group.key] || 0;

            var row = document.createElement("div");
            row.className = "logbook-equip-group" + (qty > 0 ? " is-selected" : "");

            var text = document.createElement("span");
            text.className = "logbook-equip-item-text";
            text.innerHTML =
                '<span class="logbook-equip-item-name">' + escapeHtml(group.name) + '</span>' +
                '<span class="logbook-equip-item-code">' +
                (group.category ? escapeHtml(group.category) + " · " : "") +
                group.units.length + (kind === "return" ? " borrowed" : " available") +
                '</span>';
            row.appendChild(text);

            var stepper = document.createElement("div");
            stepper.className = "logbook-equip-stepper";

            var minusBtn = document.createElement("button");
            minusBtn.type = "button";
            minusBtn.className = "logbook-equip-stepper-btn";
            minusBtn.textContent = "−";
            minusBtn.disabled = qty === 0;

            var qtyLabel = document.createElement("span");
            qtyLabel.className = "logbook-equip-stepper-qty";
            qtyLabel.textContent = qty;

            var plusBtn = document.createElement("button");
            plusBtn.type = "button";
            plusBtn.className = "logbook-equip-stepper-btn";
            plusBtn.textContent = "+";
            plusBtn.disabled = qty >= group.units.length;

            function setQty(next) {
                next = Math.max(0, Math.min(group.units.length, next));
                qtyMap[group.key] = next;
                qtyLabel.textContent = next;
                minusBtn.disabled = next === 0;
                plusBtn.disabled = next >= group.units.length;
                row.classList.toggle("is-selected", next > 0);
                var total = 0;
                Object.keys(qtyMap).forEach(function (k) { total += qtyMap[k] || 0; });
                actionBtn.disabled = total === 0;
                updateCart();
            }

            minusBtn.addEventListener("click", function () { setQty((qtyMap[group.key] || 0) - 1); });
            plusBtn.addEventListener("click", function () { setQty((qtyMap[group.key] || 0) + 1); });

            stepper.appendChild(minusBtn);
            stepper.appendChild(qtyLabel);
            stepper.appendChild(plusBtn);
            row.appendChild(stepper);

            listEl.appendChild(row);
            group._idField = idField;
        });
        section.appendChild(listEl);

        if (cartEl) {
            updateCart();
            section.appendChild(cartEl);
        }

        var actionBtn = document.createElement("button");
        actionBtn.type = "button";
        actionBtn.className = "logbook-equip-action-btn";
        actionBtn.textContent = actionLabel;
        actionBtn.disabled = true;
        actionBtn.addEventListener("click", function () {
            var ids = [];
            var summary = [];
            groups.forEach(function (group) {
                var qty = qtyMap[group.key] || 0;
                if (qty <= 0) return;
                group.units.slice(0, qty).forEach(function (unit) { ids.push(unit[idField]); });
                summary.push({ name: group.name, qty: qty });
            });
            onAction(ids, summary);
        });
        section.appendChild(actionBtn);

        equipRoot.appendChild(section);
    }

    function renderEquipPanel() {
        equipRoot.innerHTML = "";
        borrowQty = {};
        returnQty = {};

        if (borrowedItems.length) {
            buildSection(
                "return",
                "Your Borrowed Items",
                borrowedItems,
                "You don't have anything borrowed right now.",
                "Return Selected",
                function (ids, summary) { openReturnPin(ids, summary); }
            );
        }

        buildSection(
            "borrow",
            "Available Equipment",
            availableItems,
            "Nothing available to borrow right now.",
            "Borrow Selected",
            function (ids, summary) { submitBorrow(ids, summary); }
        );
    }

    function loadEquipPanel() {
        equipError.style.display = "none";
        equipRoot.innerHTML = '<p class="logbook-equip-hint">Loading...</p>';

        Promise.all([
            fetch("/Logbook/EquipmentBorrowed?studentId=" + confirmedStudentId).then(function (res) { return res.json(); }),
            fetch("/Logbook/EquipmentAvailable").then(function (res) { return res.json(); })
        ])
            .then(function (results) {
                borrowedItems = results[0] || [];
                availableItems = results[1] || [];
                renderEquipPanel();
            })
            .catch(function () {
                borrowedItems = [];
                availableItems = [];
                equipError.textContent = "Could not load the list right now.";
                equipError.style.display = "block";
                renderEquipPanel();
            });
    }

    // ---------- Submit: Borrow (self-serve) ----------

    function submitBorrow(itemIds, summary) {
        if (!itemIds.length) return;
        var body = new URLSearchParams();
        body.append("studentId", confirmedStudentId);
        itemIds.forEach(function (id) { body.append("itemIds", id); });
        body.append("method", mode === "idle" ? "RFID" : "Manual");

        fetch("/Logbook/CreateEquipmentBorrow", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            body: body
        })
            .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
            .then(function (result) {
                if (result.ok) {
                    finishWithCard("Borrowed", summary);
                } else {
                    equipError.textContent = result.data && result.data.error ? result.data.error : "Could not complete the borrow.";
                    equipError.style.display = "block";
                }
            })
            .catch(function () {
                equipError.textContent = "Could not reach the server. Try again.";
                equipError.style.display = "block";
            });
    }

    // ---------- Submit: Return (staff PIN) ----------

    function openReturnPin(ids, summary) {
        if (!ids.length) return;
        pendingReturnIds = ids;
        pendingReturnSummary = summary;
        pinError.style.display = "none";
        pinInput.value = "";
        if (pinInput.dispatchEvent) pinInput.dispatchEvent(new Event("input", { bubbles: true }));
        pinOverlay.style.display = "flex";
        pinInput.focus();
    }
    var pendingReturnSummary = [];

    function submitReturn() {
        var ids = pendingReturnIds;
        var summary = pendingReturnSummary;
        var pin = pinInput.value.trim();
        if (!pin || !ids.length) return;

        pinConfirm.disabled = true;
        var body = new URLSearchParams();
        body.append("pin", pin);
        ids.forEach(function (id) { body.append("sessionItemIds", id); });

        fetch("/Logbook/ReturnEquipment", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            body: body
        })
            .then(function (res) { return res.json(); })
            .then(function (data) {
                pinConfirm.disabled = false;
                if (data.ok) {
                    pinOverlay.style.display = "none";
                    finishWithCard("Returned", summary);
                } else {
                    pinError.textContent = data.message || "Incorrect PIN. Try again.";
                    pinError.style.display = "block";
                    pinInput.value = "";
                    pinInput.focus();
                }
            })
            .catch(function () {
                pinConfirm.disabled = false;
                pinError.textContent = "Could not verify PIN right now. Try again.";
                pinError.style.display = "block";
            });
    }

    if (pinClose && pinCancel) {
        pinClose.addEventListener("click", function () { pinCancel.click(); });
    }
    if (pinCancel) {
        pinCancel.addEventListener("click", function () {
            pinOverlay.style.display = "none";
        });
    }
    if (pinConfirm) {
        pinConfirm.addEventListener("click", submitReturn);
    }
    if (pinInput) {
        pinInput.addEventListener("keydown", function (e) {
            if (e.key === "Enter") {
                e.preventDefault();
                submitReturn();
            } else if (e.key === "Escape") {
                pinOverlay.style.display = "none";
            }
        });
    }

    // Success state: a small receipt-style card listing what just
    // happened (action + each item name x qty) instead of a plain one-
    // line message (see chat - "like it has a card of borrowed item
    // when finished"), then auto-resets back to idle.
    function finishWithCard(action, summary) {
        var itemsHtml = (summary || []).map(function (s) {
            return '<li class="logbook-equip-finish-item">' +
                '<span>' + escapeHtml(s.name) + '</span>' +
                '<span class="logbook-equip-finish-qty">×' + s.qty + '</span>' +
                '</li>';
        }).join("");

        equipRoot.innerHTML =
            '<div class="logbook-equip-finish-card">' +
            '<div class="logbook-equip-finish-icon">✓</div>' +
            '<p class="logbook-equip-finish-title">' + escapeHtml(action) + '</p>' +
            '<ul class="logbook-equip-finish-list">' + itemsHtml + '</ul>' +
            '</div>';

        setTimeout(startOver, 2800);
    }

    resetEquipPanel();
    showIdleMode();
})();
