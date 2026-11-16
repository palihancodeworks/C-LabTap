// C-LABTAP Logbook kiosk - Attendance tab.
(function () {
    "use strict";

    var idleView = document.getElementById("logbookIdle");
    var manualView = document.getElementById("logbookManual");
    var modeLink = document.getElementById("logbookModeLink");
    var modeLinkBack = document.getElementById("logbookModeLinkBack");
    var lrnInput = document.getElementById("logbookLrnInput");
    var searchBtn = document.getElementById("logbookSearchBtn");
    var confirmBox = document.getElementById("logbookConfirmBox");
    var confirmName = document.getElementById("logbookConfirmName");
    var confirmBtn = document.getElementById("logbookConfirmBtn");
    var rfidCatcher = document.getElementById("logbookRfidCatcher");
    var statusBox = document.getElementById("logbookStatusBox");
    var titleEl = document.getElementById("logbookTitle");
    var subtitleEl = document.getElementById("logbookSubtitle");

    var RESET_DELAY_MS = 2000;       // success: name + Time In/Out, glanceable
    var ERROR_RESET_DELAY_MS = 5000; // "Not Recognized" + a sentence of help
                                     // needs reading time - and the student's
                                     // eyes were on the reader, not the screen,
                                     // when it appeared.
    var resetTimer = null;
    var mode = "idle"; // "idle" (RFID-ready) | "manual"
    var lastLookedUpLrn = null;
    var pendingStudentName = null;
    var inputLocked = false; // true while a result message is showing - no tap/type/mode-switch until it clears

    // ---------- mode switching ----------

    function setInputLocked(locked) {
        inputLocked = locked;
        lrnInput.disabled = locked;
        idleView.classList.toggle("is-locked", locked);
        manualView.classList.toggle("is-locked", locked);
    }

    function showIdleMode() {
        setInputLocked(false);
        mode = "idle";
        titleEl.textContent = "RFID Attendance";
        subtitleEl.textContent = "Please tap your RFID to record attendance";
        idleView.style.display = "";
        manualView.style.display = "none";
        lrnInput.value = "";
        confirmBox.style.display = "none";
        confirmName.textContent = "\u00A0";
        pendingStudentName = null;
        lastLookedUpLrn = null;
        focusCatcher();
    }

    function showManualMode() {
        mode = "manual";
        titleEl.textContent = "Manual Attendance";
        subtitleEl.textContent = "Please fill in your details to record attendance";
        idleView.style.display = "none";
        manualView.style.display = "flex";
        lrnInput.value = "";
        confirmBox.style.display = "none";
        searchBtn.style.display = "";
        lrnInput.focus();
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

    // ---------- RFID catcher: always focused while idle, silently
    // receives the reader's keystrokes (acts like a keyboard), submits
    // immediately on Enter ----------

    function focusCatcher() {
        if (mode === "idle" && !inputLocked) rfidCatcher.focus();
    }

    rfidCatcher.addEventListener("blur", function () {
        // Keep stealing focus back while idle, so a tap always lands here
        // regardless of what the student/teacher last clicked.
        setTimeout(focusCatcher, 50);
    });

    rfidCatcher.addEventListener("keydown", function (e) {
        if (e.key === "Enter") {
            e.preventDefault();
            var tag = rfidCatcher.value.trim();
            rfidCatcher.value = "";
            if (inputLocked) return; // ignore taps until the previous result message clears
            if (tag) submitAttendance({ rfidTag: tag, method: "RFID" });
        }
    });

    // ---------- Manual LRN entry ----------
    // Typing only fills the field - the lookup itself only runs when the
    // "Search" button is pressed (no auto-search while typing).

    lrnInput.addEventListener("input", function () {
        if (inputLocked) {
            lrnInput.value = ""; // ignore typing until the previous result message clears
            return;
        }
        // Digits only.
        lrnInput.value = lrnInput.value.replace(/\D/g, "").slice(0, 12);

        // Field changed since the last search - any confirm box showing is stale,
        // and the Search button comes back so they can search the new value.
        if (lrnInput.value !== lastLookedUpLrn) {
            confirmBox.style.display = "none";
            searchBtn.style.display = "";
        }
    });

    searchBtn.addEventListener("click", function () {
        if (inputLocked) return;

        var lrn = lrnInput.value;
        if (lrn.length !== 12) return; // not a complete LRN yet

        lastLookedUpLrn = lrn;

        fetch("/Logbook/LookupStudent?lrn=" + encodeURIComponent(lrn))
            .then(function (res) { return res.json(); })
            .then(function (data) {
                if (lrnInput.value !== lrn) return; // value changed while we waited

                if (data.found) {
                    // Only hide Search once the "is this you?" confirm box shows.
                    searchBtn.style.display = "none";
                    pendingStudentName = data.name;
                    confirmName.textContent = data.name;
                    confirmBox.style.display = "flex";
                } else {
                    // Nothing to confirm - go straight to Not Recognized.
                    // Search stays visible since there was nothing to confirm.
                    confirmBox.style.display = "none";
                    submitAttendance({ lrn: lrn, method: "Manual" });
                }
            })
            .catch(function () {
                confirmBox.style.display = "none";
            });
    });

    confirmBtn.addEventListener("click", function () {
        if (inputLocked) return;
        var lrn = lrnInput.value;
        if (lrn.length === 12) {
            submitAttendance({ lrn: lrn, method: "Manual" });
        }
    });

    // ---------- Submit + result display ----------

    function submitAttendance(opts) {
        setInputLocked(true);
        var submittedMode = mode; // remember which screen this came from
        var body = new URLSearchParams();
        if (opts.rfidTag) body.append("RfidTag", opts.rfidTag);
        if (opts.lrn) body.append("Lrn", opts.lrn);
        body.append("Method", opts.method);

        // Clear the manual form right away so it's ready for next time,
        // but stay on the current view - we only flip back to the
        // RFID-ready (idle) screen once the status message itself clears
        // (and only then if the tap/entry actually succeeded).
        if (mode === "manual") {
            lrnInput.value = "";
            confirmBox.style.display = "none";
            lastLookedUpLrn = null;
            pendingStudentName = null;
        }

        fetch("/Logbook/RecordAttendance", {
            method: "POST",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            body: body
        })
            .then(function (res) { return res.json(); })
            .then(function (data) {
                if (data.recognized) {
                    showResult(true, data.eventType, data.studentName, submittedMode, null);
                } else {
                    showResult(false, null, null, submittedMode, data.reason);
                }
            })
            .catch(function () {
                showResult(false, null, null, submittedMode, null);
            });
    }

    function showResult(recognized, eventType, studentName, submittedMode, reason) {
        clearTimeout(resetTimer);

        if (recognized) {
            var isTimeIn = eventType === "time_in";
            var accentClass = isTimeIn ? "accent-in" : "accent-out";
            var label = isTimeIn ? "In" : "out";
            statusBox.innerHTML =
                "<div class=\"logbook-result\">" +
                "<img class=\"logbook-result-icon\" src=\"/images/status-check.png\" width=\"60\" height=\"60\" alt=\"\" />" +
                "<p class=\"logbook-result-title\">Time <span class=\"" + accentClass + "\">" + label + "</span><br/>Successfully</p>" +
                "<span class=\"logbook-result-name\">" + studentName + "</span>" +
                "</div>";
        } else {
            // Archived (transferred out) and never-registered both land
            // here now, with the same generic message - an archived
            // student's card/LRN just doesn't get recognized anymore,
            // same as one that was never registered (see chat).
            statusBox.innerHTML =
                "<div class=\"logbook-result\">" +
                "<img class=\"logbook-result-icon\" src=\"/images/status-failed.png\" width=\"60\" height=\"60\" alt=\"\" />" +
                "<p class=\"logbook-result-title\">Not Recognized</p>" +
                "<p class=\"logbook-result-sub\">We couldn't find a matching record. Please try again or ask your teacher for help.</p>" +
                "</div>";
        }

        // On success, always return to the RFID-ready (idle) screen once the
        // message clears. On a manual-entry failure, stay on the manual
        // screen instead (just unlock it) so the person can retype the LRN
        // without being bounced back to RFID. An RFID-tap failure still
        // returns to idle since that's already the screen it's on.
        resetTimer = setTimeout(function () {
            statusBox.innerHTML = "<p class=\"logbook-no-activity\" id=\"logbookNoActivity\">No Activity...</p>";
            if (recognized || submittedMode !== "manual") {
                showIdleMode();
            } else {
                setInputLocked(false);
                lrnInput.focus();
            }
        }, recognized ? RESET_DELAY_MS : ERROR_RESET_DELAY_MS);
    }

    showIdleMode();
})();
