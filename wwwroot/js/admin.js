// C-LABTAP admin shell: sliding sidebar indicator + AJAX page swapping.
(function () {
  "use strict";

  var NAV_DURATION = 600; // keep in sync with --nav-duration in admin.css

  // The pill's cubic-bezier easing means it visually looks arrived
  // well before the full NAV_DURATION technically elapses (the tail
  // end of the motion is barely perceptible) - so the color change
  // fires at a fraction of that time, matching when it LOOKS like
  // the pill reached the link, not when the transition technically
  // finishes.
  var ACTIVATE_DELAY = NAV_DURATION * 0.70;

  var nav = document.querySelector(".sidebar-nav");
  var indicator = document.querySelector(".nav-indicator");
  var mainContent = document.getElementById("main-content");
  var topbarTitle = document.getElementById("topbarPageTitle");
  if (!nav || !indicator || !mainContent) return;

  var pendingActivateTimer = null;

  // Generic gray placeholder shown WHILE a page is being fetched, so a
  // slow connection reads as "loading" rather than the old page just
  // sitting there dimmed (which is what a plain opacity fade looked
  // like - easy to miss on a fast load, confusing on a slow one). Not
  // tailored per page on purpose - it's a stand-in shape (stat cards +
  // a filter bar + a few table rows), not a preview of the real layout.
  var PAGE_SKELETON_HTML =
    '<div class="page-skeleton" aria-hidden="true">' +
      '<div class="skel-row">' +
        '<div class="skel-block skel-card"></div>' +
        '<div class="skel-block skel-card"></div>' +
      '</div>' +
      '<div class="skel-block skel-bar"></div>' +
      '<div class="skel-block skel-row-line"></div>' +
      '<div class="skel-block skel-row-line"></div>' +
      '<div class="skel-block skel-row-line"></div>' +
      '<div class="skel-block skel-row-line"></div>' +
    '</div>';

  function links() {
    return Array.prototype.slice.call(nav.querySelectorAll(".nav-link"));
  }

  // Icon + text color are pure CSS now (driven by the .active class on
  // the link - see .nav-link.active / .nav-icon-active in admin.css),
  // so switching tabs only ever needs to toggle this one class and the
  // crossfade/color transition happens on its own.
  function deactivate(link) {
    if (!link) return;
    link.classList.remove("active");
  }

  function activate(link) {
    if (!link) return;
    link.classList.add("active");
  }

  function moveIndicatorTo(link, animate) {
    if (!link) { indicator.classList.remove("ready"); return; }
    var navRect = nav.getBoundingClientRect();
    var linkRect = link.getBoundingClientRect();
    var top = linkRect.top - navRect.top;
    var height = linkRect.height;

    if (!animate) {
      indicator.style.transition = "none";
      indicator.style.top = top + "px";
      indicator.style.height = height + "px";
      indicator.classList.add("ready");
      // Force reflow so the "none" transition takes effect before we
      // hand transitions back to the stylesheet.
      // eslint-disable-next-line no-unused-expressions
      indicator.offsetHeight;
      indicator.style.transition = "";
    } else {
      indicator.style.top = top + "px";
      indicator.style.height = height + "px";
      indicator.classList.add("ready");
    }
  }

  // Setting .innerHTML never executes <script> tags in the inserted
  // markup (a deliberate browser security behavior) - so each page's own
  // inline <script> (Register Student's scan/register wiring, Register
  // Item's, Attendance's Clear button, etc.) would otherwise go dead the
  // moment you arrive via a sidebar click instead of a full page load.
  // Replacing each one with a freshly created <script> node forces it to
  // actually run.
  function runScripts(container) {
    var scripts = Array.prototype.slice.call(container.querySelectorAll("script"));
    scripts.forEach(function (oldScript) {
      var newScript = document.createElement("script");
      Array.prototype.slice.call(oldScript.attributes).forEach(function (attr) {
        newScript.setAttribute(attr.name, attr.value);
      });
      newScript.textContent = oldScript.textContent;
      oldScript.parentNode.replaceChild(newScript, oldScript);
    });
  }

  function loadPage(url, push) {
    // Every AJAX nav-swap below replaces #main-content's HTML, but any
    // window-level "resize" listener or ResizeObserver a page's own
    // script registered (CLabTapToggleGroup, CLabTapRowFit) has nothing
    // to do with that - it stays registered forever, still holding a
    // closure over the OLD, now-detached DOM nodes. Navigate around
    // enough times in one session (exactly what we did today) and
    // dozens of these pile up, each doing getBoundingClientRect() work
    // on every resize/size-change - that's the "system lag" reported in
    // chat. Running every registered teardown right before tearing the
    // page down itself fixes that at the source.
    if (window.CLabTapPageTeardown && window.CLabTapPageTeardown.length) {
      window.CLabTapPageTeardown.forEach(function (fn) {
        try { fn(); } catch (e) { /* one broken teardown shouldn't block the rest */ }
      });
      window.CLabTapPageTeardown.length = 0;
    }

    mainContent.classList.add("loading");
    mainContent.innerHTML = PAGE_SKELETON_HTML;
    fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } })
      .then(function (res) {
        if (!res.ok) throw new Error("Request failed: " + res.status);
        return res.text();
      })
      .then(function (html) {
        mainContent.innerHTML = html;
        runScripts(mainContent);
        mainContent.classList.remove("loading");
        if (push) window.history.pushState({ url: url }, "", url);
      })
      .catch(function () {
        mainContent.classList.remove("loading");
        window.location.href = url;
      });
  }

  links().forEach(function (link) {
    link.addEventListener("click", function (e) {
      e.preventDefault();
      if (link.classList.contains("active")) return;

      var prevActive = nav.querySelector(".nav-link.active");

      // The pill starts moving immediately. The item it's LEAVING drops its
      // active look right away; the item it's ARRIVING at only turns green
      // once the pill actually reaches it, not the instant you click.
      if (prevActive) deactivate(prevActive);
      moveIndicatorTo(link, true);

      // The topbar's title (what used to be the static "C-LABTAP"
      // brand-mark) swaps right away too, since the AJAX page swap
      // below only replaces #main-content - the topbar is part of
      // this shared layout and isn't touched by that fetch, so it
      // has to be updated here instead of relying on a full page
      // reload (see data-title on each nav link, set in
      // _AdminLayout.cshtml).
      if (topbarTitle) topbarTitle.textContent = link.dataset.title || "";
      var breadcrumbCurrent = document.getElementById("breadcrumbCurrent");
      if (breadcrumbCurrent) breadcrumbCurrent.textContent = link.dataset.title || "";

      if (pendingActivateTimer) clearTimeout(pendingActivateTimer);
      pendingActivateTimer = setTimeout(function () {
        activate(link);
        pendingActivateTimer = null;
      }, ACTIVATE_DELAY);

      loadPage(link.getAttribute("href"), true);
    });
  });

  window.addEventListener("popstate", function () {
    window.location.reload();
  });

  window.addEventListener("resize", function () {
    var current = nav.querySelector(".nav-link.active");
    moveIndicatorTo(current, false);
  });

  // Initial paint: the server already rendered the correct active link and
  // icon, so we only need to drop the pill into place without animating it
  // in from nowhere.
  document.addEventListener("DOMContentLoaded", function () {
    var current = nav.querySelector(".nav-link.active");
    moveIndicatorTo(current, false);
  });
})();

// Shared numbered pagination control used by Attendance, Register Student
// and Register Item's registry tables. Lives here (not duplicated per
// page) because admin.js loads once in the layout and survives the AJAX
// page swaps, so it's always available by the time a page's own inline
// script runs.
// Shared behavior for a Status/Method-style toggle group (a row of
// buttons acting as one control, used instead of a <select> for a
// field with only 2-3 options - see chat). Clicking an option just
// swaps which button has .active and updates group.dataset.value;
// it does not fire a fetch itself, same as picking a new value in
// the Date/Grade/Section fields next to it - the page's own Apply
// button is still what triggers the actual filter request.
window.CLabTapToggleGroup = {
  // Moves the sliding pill (if this group has one - see the
  // "--sliding" modifier in admin.css) to sit under a given button.
  // A plain toggle-group with no .toggle-slider child just skips
  // this - it keeps the old flat per-button background.
  _positionSlider: function (group, btn, animate) {
    var slider = group.querySelector(".toggle-slider");
    if (!slider || !btn) return;
    var groupRect = group.getBoundingClientRect();
    var btnRect = btn.getBoundingClientRect();
    var left = btnRect.left - groupRect.left;

    if (!animate) slider.style.transition = "none";
    slider.style.left = left + "px";
    slider.style.width = btnRect.width + "px";
    if (!animate) {
      // Force reflow so the "none" transition takes effect before
      // handing transitions back to the stylesheet (same trick as
      // the sidebar's own sliding indicator in admin.js).
      // eslint-disable-next-line no-unused-expressions
      slider.offsetHeight;
      slider.style.transition = "";
    }
  },
  // Positions the slider for a given button, retrying across a few
  // animation frames if the measured width still comes back as 0.
  // Covers the "pill renders as a thin line on first load, only
  // correct after navigating away and back" bug from chat - right
  // when a page's own script runs (first full load, or right after
  // this content is swapped in by the AJAX nav), the browser hasn't
  // necessarily finished laying out/painting this content yet, so
  // getBoundingClientRect() can read back a zero-width box once. By
  // the time you'd navigated away and back, everything had already
  // settled once, which is why it "worked" the second time.
  _settleSlider: function (group, btn, attemptsLeft) {
    var self = this;
    self._positionSlider(group, btn, false);
    var slider = group.querySelector(".toggle-slider");
    if (slider && parseFloat(slider.style.width) === 0 && attemptsLeft > 0) {
      requestAnimationFrame(function () {
        self._settleSlider(group, btn, attemptsLeft - 1);
      });
    }
  },
  init: function (group) {
    if (!group) return;
    var options = group.querySelectorAll(".toggle-option");
    var self = this;
    // Full label as a tooltip, since admin.css shortens it with "..."
    // when the segment gets too narrow.
    options.forEach(function (o) {
      if (!o.title) o.title = o.textContent.trim();
    });

    var initial = group.querySelector(".toggle-option.active") || options[0];
    // Position it right away, synchronously - don't wait a full
    // animation frame just to make the very first call. _settleSlider
    // still retries over a few rAF ticks on its own if this first,
    // immediate read comes back 0-width (e.g. content not painted
    // yet), so nothing is lost by not deferring the first attempt too.
    self._settleSlider(group, initial, 10);

    // The retry above only covers a 0-width read (content not painted
    // yet). It does NOT cover the case where the custom webfonts
    // (Public Sans / Roboto) are still downloading at that point: the
    // browser paints with a fallback font first, getBoundingClientRect()
    // comes back non-zero so the retry loop is satisfied, and then the
    // real font swaps in a little later with different letter widths -
    // 'Manual' and 'Time In' in particular reflow noticeably wider/
    // narrower. The already-positioned slider never gets told about
    // that, so it sits at a stale left/width and visually drifts into
    // whatever is next to it (see chat - this is the Method/Status
    // "overlaying each other" bug). document.fonts.ready resolves once
    // all fonts referenced on the page have actually loaded and the
    // final reflow has happened, so re-settle every group then.
    if (window.document.fonts && document.fonts.ready) {
      document.fonts.ready.then(function () {
        var active = group.querySelector(".toggle-option.active") || options[0];
        self._positionSlider(group, active, false);
      });
    }

    options.forEach(function (btn) {
      btn.addEventListener("click", function () {
        options.forEach(function (b) { b.classList.remove("active"); });
        btn.classList.add("active");
        group.dataset.value = btn.dataset.value;
        self._positionSlider(group, btn, true);
      });
    });

    // The toggle-group now stretches to fill its field, which itself
    // grows/shrinks with the viewport (see chat - "every
    // textbox/combobox follows screen size"), so the active pill's
    // position/width is no longer a one-time measurement - it has to
    // be re-taken whenever the group's own size changes, the same way
    // CLabTapRowFit re-measures row-fit on resize.
    var resizeTimer = null;
    function rePositionActive() {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () {
        var active = group.querySelector(".toggle-option.active") || options[0];
        self._positionSlider(group, active, false);
      }, 120);
    }
    window.addEventListener("resize", rePositionActive);
    if (window.visualViewport) {
      window.visualViewport.addEventListener("resize", rePositionActive);
    }
    // The group can also change size without the window resizing - e.g.
    // Register Student's "Date Removed" filter appearing on the Archived
    // tab squeezes the RFID toggle narrower. Watch the group itself so
    // the pill follows any size change, not just window resizes.
    var groupResizeObserver = null;
    if (window.ResizeObserver) {
      var lastWidth = group.getBoundingClientRect().width;
      groupResizeObserver = new ResizeObserver(function () {
        var w = group.getBoundingClientRect().width;
        if (w === lastWidth) return;
        lastWidth = w;
        var active = group.querySelector(".toggle-option.active") || options[0];
        // Animated, so a resize that happens *during* a click (the
        // selected option's bold text changes the group's width) just
        // re-targets the slide instead of cutting it short.
        self._positionSlider(group, active, true);
      });
      groupResizeObserver.observe(group);
    }

    // See the teardown call in loadPage() (admin.js top) - without this,
    // these two window listeners and this ResizeObserver would outlive
    // this page's own DOM (which the next AJAX nav-swap throws away),
    // permanently accumulating one more set of each per page visit.
    window.CLabTapPageTeardown = window.CLabTapPageTeardown || [];
    window.CLabTapPageTeardown.push(function () {
      window.removeEventListener("resize", rePositionActive);
      if (window.visualViewport) {
        window.visualViewport.removeEventListener("resize", rePositionActive);
      }
      if (groupResizeObserver) groupResizeObserver.disconnect();
    });
  },
  // Re-measures a group's pill against its active option right now -
  // for page code that changes the layout around a toggle (e.g. showing
  // or hiding a filter next to it) and wants the pill to follow at once.
  refresh: function (group) {
    if (!group) return;
    var active = group.querySelector(".toggle-option.active") || group.querySelector(".toggle-option");
    this._positionSlider(group, active, false);
  },
  // Resets a group back to its "All" option without firing a request
  // itself (callers decide whether to re-fetch after resetting).
  reset: function (group) {
    if (!group) return;
    var options = group.querySelectorAll(".toggle-option");
    var allBtn = null;
    options.forEach(function (b) {
      var isAll = b.dataset.value === "All";
      b.classList.toggle("active", isAll);
      if (isAll) allBtn = b;
    });
    group.dataset.value = "All";
    if (allBtn) this._positionSlider(group, allBtn, true);
  }
};

// Works out how many rows of a .data-table fit in the space actually
// available on screen right now, so a tall monitor shows more rows per
// page and a small laptop/tablet screen shows fewer - instead of every
// screen getting the same hardcoded row count (see chat: pages must
// never scroll - admin.css "html, body { overflow: hidden }" - so the
// page count has to flex with the viewport instead).
window.CLabTapRowFit = {
  // tableEl: the <table class="data-table"> whose tbody rows are being
  // counted. opts.minRows: never offer fewer than this many, even on a
  // very short screen. opts.fallbackRows: used if measurement isn't
  // possible yet (e.g. fonts/layout not settled). opts.paginationReserve:
  // vertical px to always set aside under the table for its pagination
  // bar - reserved whether or not a pager is showing right now, so the
  // row count doesn't shift once a second page of data makes the pager
  // appear (CLabTapPagination.render hides the bar entirely at 1 page).
  compute: function (tableEl, opts) {
    opts = opts || {};
    var minRows = opts.minRows || 3;
    var fallbackRows = opts.fallbackRows || 6;
    var paginationReserve = opts.paginationReserve != null ? opts.paginationReserve : 60;
    if (!tableEl) return fallbackRows;

    var thead = tableEl.querySelector("thead");
    var tbody = tableEl.querySelector("tbody");
    if (!thead || !tbody) return fallbackRows;

    // Measure one real row's rendered height. If the table has no rows
    // right now, add a throwaway probe row (same column count) just
    // long enough to measure, then remove it - more accurate than
    // hardcoding a height that would drift every time padding,
    // line-height or fonts change.
    var probe = null;
    var sampleRow = tbody.firstElementChild;
    if (!sampleRow) {
      probe = document.createElement("tr");
      var colCount = thead.querySelectorAll("th").length || 1;
      for (var i = 0; i < colCount; i++) {
        var td = document.createElement("td");
        td.textContent = "\u00A0";
        probe.appendChild(td);
      }
      tbody.appendChild(probe);
      sampleRow = probe;
    }
    var rowHeight = sampleRow.getBoundingClientRect().height;
    if (probe) tbody.removeChild(probe);
    if (!rowHeight) return fallbackRows;

    // The real scroll boundary is .main-col (height: 100vh; overflow-y:
    // auto - see admin.css), not #main-content: #main-content has no
    // height of its own (flex: 1) and just grows to fit whatever's put
    // in it. Measuring against #main-content's own rect was circular -
    // once rows overflowed even slightly, #main-content grew taller
    // than the viewport, "available space" read as bigger because of
    // that overflow, which produced even more rows, which overflowed
    // further (see chat - this is what caused the scrollbar). .main-col
    // stays pinned to the viewport no matter how tall its content gets,
    // so it's the one safe reference.
    var scrollBoundary = document.querySelector(".main-col") || document.documentElement;
    var mainContentEl = document.getElementById("main-content");
    // #main-content's own bottom padding is real gutter space between
    // the pagination bar and .main-col's edge - not available for rows.
    var bottomPadding = mainContentEl
      ? parseFloat(getComputedStyle(mainContentEl).paddingBottom) || 0
      : 0;

    // When a table is wider than its box (narrow screens), .table-scroll
    // shows a sideways scrollbar under the last row. That bar takes real
    // height too, so it has to come out of the space left for rows -
    // otherwise the page ends up one scrollbar's height too tall.
    var scrollWrap = tableEl.closest(".table-scroll");
    var sideScrollbar = 0;
    if (scrollWrap) {
      var wrapStyle = getComputedStyle(scrollWrap);
      sideScrollbar = Math.max(0, scrollWrap.offsetHeight - scrollWrap.clientHeight -
        (parseFloat(wrapStyle.borderTopWidth) || 0) -
        (parseFloat(wrapStyle.borderBottomWidth) || 0));
    }

    var available = scrollBoundary.getBoundingClientRect().bottom -
      tableEl.getBoundingClientRect().top -
      thead.getBoundingClientRect().height -
      paginationReserve -
      bottomPadding -
      sideScrollbar;

    var rows = Math.floor(available / rowHeight);
    return Math.max(minRows, isFinite(rows) && rows > 0 ? rows : fallbackRows);
  },

  // Recomputes on load and again (debounced) on resize - including a
  // page-zoom change, which doesn't always fire a plain window "resize"
  // event in every browser but does fire visualViewport's, so both are
  // wired here. Calls onChange(newRowCount) only when the fitting row
  // count actually changes, so callers can reset to page 1 and reload.
  watch: function (tableEl, opts, onChange) {
    var self = this;
    var current = this.compute(tableEl, opts);
    onChange(current);

    // Same "measured before layout settled" issue as the toggle
    // slider above (_settleSlider): right when a page's own script
    // runs - a first hard load, or right after the AJAX nav-swap drops
    // this content in - the browser hasn't necessarily finished
    // laying out this new DOM yet, so this first compute() can read
    // back more available space than genuinely exists once everything
    // actually settles, overcounting rows enough to swallow what
    // should've been a second page (see chat: "gets pagination when I
    // zoom, but it's gone again after switching to a different page
    // and back" - zooming re-triggers the resize-based recheck below,
    // which runs after layout has settled, but a plain nav-swap never
    // fires a resize event, so only this first, too-early measurement
    // ever ran). Re-measuring a couple of animation frames later
    // catches up once layout has genuinely settled, same fix shape as
    // _settleSlider's retries.
    function recheckNow() {
      var next = self.compute(tableEl, opts);
      if (next !== current) {
        current = next;
        onChange(current);
      }
    }

    requestAnimationFrame(function () {
      requestAnimationFrame(recheckNow);
    });

    var timer = null;
    function scheduleRecheck() {
      clearTimeout(timer);
      timer = setTimeout(recheckNow, 120);
    }
    window.addEventListener("resize", scheduleRecheck);
    if (window.visualViewport) {
      window.visualViewport.addEventListener("resize", scheduleRecheck);
    }

    // Same leak as CLabTapToggleGroup above - every table page that
    // calls watch() (Attendance, Register Student, Register Item, Item
    // Lending, Settings) added one more pair of these listeners that
    // outlived its own page once the AJAX nav-swap moved on. Torn down
    // automatically in loadPage() (admin.js top) instead.
    window.CLabTapPageTeardown = window.CLabTapPageTeardown || [];
    window.CLabTapPageTeardown.push(function () {
      clearTimeout(timer);
      window.removeEventListener("resize", scheduleRecheck);
      if (window.visualViewport) {
        window.visualViewport.removeEventListener("resize", scheduleRecheck);
      }
    });

    // compute()'s row-height measurement is only as good as what it
    // can measure. Until the page's own data has actually loaded, the
    // table's tbody is empty, so compute() measures a throwaway probe
    // row (plain text in a nbsp'd <td>) instead of a real one - but a
    // real row here has an Actions cell with icon buttons, which can
    // render taller than that plain probe. That height is still too
    // small once genuine rows render, so the row count this function
    // settles on above can still be an overestimate, overflowing
    // .main-col and showing a scrollbar (see chat: "still has
    // scrollbar" even after the settle-recheck above). There's no
    // fixed delay that reliably outlasts the page's own data fetch, so
    // instead this returns a recheck() the caller can invoke itself,
    // once real rows are actually sitting in the tbody - re-measuring
    // against genuine row height at that point. If the real height
    // means fewer rows actually fit, onChange fires once more with the
    // corrected count; if it already matches, this is a no-op, so
    // callers can safely call it after every load, not just the first.
    return { recheck: recheckNow };
  }
};

window.CLabTapPagination = {
  // container: an element to render the pager into (its content is
  //   replaced each call).
  // state: { page, totalPages }.
  // onChange: function(nextPage) - called when the user picks a page.
  render: function (container, state, onChange) {
    if (!container) return;
    container.innerHTML = "";

    var page = state.page || 1;
    var totalPages = state.totalPages || 1;
    if (totalPages <= 1) return;

    // Left side: "Showing page X of Y".
    var info = document.createElement("span");
    info.className = "pagination-info";
    info.textContent = "Showing page " + page + " of " + totalPages;
    container.appendChild(info);

    // Right side: Prev / numbered pages / Next.
    var buttons = document.createElement("div");
    buttons.className = "pagination-buttons";

    function makeButton(label, targetPage, opts) {
      opts = opts || {};
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "page-btn" + (opts.nav ? " page-nav" : "") + (opts.active ? " active" : "");
      if (opts.nav) {
        // Prev/Next: a chevron beside the label ("< Prev" / "Next >"),
        // chevron on the outer side - see .page-nav in admin.css.
        var isPrev = opts.nav === "prev";
        var chevron = '<svg class="page-nav-chevron" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="' +
          (isPrev ? "15 18 9 12 15 6" : "9 18 15 12 9 6") + '"/></svg>';
        var text = document.createElement("span");
        text.textContent = label;
        btn.innerHTML = chevron;
        if (isPrev) btn.appendChild(text);
        else btn.insertBefore(text, btn.firstChild);
        btn.setAttribute("aria-label", isPrev ? "Previous page" : "Next page");
      } else {
        btn.textContent = label;
      }
      if (opts.disabled) {
        btn.disabled = true;
      } else {
        btn.addEventListener("click", function () {
          onChange(targetPage);
        });
      }
      return btn;
    }

    function makeEllipsis() {
      var span = document.createElement("span");
      span.className = "page-ellipsis";
      span.textContent = "…";
      return span;
    }

    buttons.appendChild(makeButton("Prev", page - 1, { nav: "prev", disabled: page <= 1 }));

    // Always show first and last page, plus a small window around the
    // current page, with an ellipsis for any gap in between.
    var windowSize = 1;
    var shown = [];
    for (var p = 1; p <= totalPages; p++) {
      if (p === 1 || p === totalPages || (p >= page - windowSize && p <= page + windowSize)) {
        shown.push(p);
      }
    }

    var lastShown = 0;
    shown.forEach(function (p) {
      if (lastShown && p - lastShown > 1) buttons.appendChild(makeEllipsis());
      buttons.appendChild(makeButton(String(p), p, { active: p === page }));
      lastShown = p;
    });

    buttons.appendChild(makeButton("Next", page + 1, { nav: "next", disabled: page >= totalPages }));

    container.appendChild(buttons);
  }
};


// Shared filter bar (Register Student, Attendance, Item Lending): the
// "Filters (n)" popover plus the row of applied-filter chips. Pages hand
// it a list of controls (see the from* helpers below); it never loads
// data itself - pages call refresh() whenever they run a request, so the
// chips always show exactly what that request used.
window.CLabTapFilterBar = {
  fromGroup: function (group, label, inPanel, valueLabels) {
    return {
      inPanel: !!inPanel,
      read: function () {
        var v = group && group.dataset.value;
        if (!v || v === "All") return null;
        return label + ": " + ((valueLabels && valueLabels[v]) || v);
      },
      reset: function () {
        if (window.CLabTapToggleGroup) window.CLabTapToggleGroup.reset(group);
      }
    };
  },
  fromSelect: function (select, label, inPanel) {
    return {
      inPanel: !!inPanel,
      read: function () {
        if (!select || !select.value || select.value === "All") return null;
        return label + ": " + select.value;
      },
      reset: function () {
        if (select) select.selectedIndex = 0;
      }
    };
  },
  // getDefault (optional): a function returning the field's baseline
  // value (e.g. "today", for a page that pre-fills the date range
  // instead of leaving it empty - see chat: Attendance defaults to
  // today's date on load, but that's a starting point, not something
  // the user chose, so it shouldn't show as an active "From .. / To .."
  // chip or count toward the Filters badge until it's actually changed
  // away from that default). Without getDefault this behaves exactly
  // as before - any non-empty value is "active".
  fromDate: function (input, label, inPanel, getDefault) {
    return {
      inPanel: !!inPanel,
      read: function () {
        if (!input || !input.value) return null;
        if (typeof getDefault === "function" && input.value === getDefault()) return null;
        var p = input.value.split("-");
        var d = new Date(+p[0], +p[1] - 1, +p[2]);
        var text = isNaN(d.getTime()) ? input.value : d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
        return label + " " + text;
      },
      reset: function () {
        if (!input) return;
        input.value = typeof getDefault === "function" ? getDefault() : "";
        input.dispatchEvent(new Event("input", { bubbles: true }));
      }
    };
  },
  init: function (cfg) {
    var moreBtn = cfg.moreBtn, panel = cfg.panel, chipsEl = cfg.chipsEl, badge = cfg.badge;
    var controls = cfg.controls || [];

    function isOpen() { return !!panel && !panel.hidden; }
    function open() {
      if (!panel || isOpen()) return;
      panel.hidden = false;
      moreBtn.setAttribute("aria-expanded", "true");
      // groups inside were hidden (zero width) - re-measure their pills
      Array.prototype.forEach.call(panel.querySelectorAll(".toggle-group"), function (g) {
        if (window.CLabTapToggleGroup) window.CLabTapToggleGroup.refresh(g);
      });
    }
    function close(returnFocus) {
      if (!isOpen()) return;
      panel.hidden = true;
      moreBtn.setAttribute("aria-expanded", "false");
      if (returnFocus) moreBtn.focus();
    }

    if (moreBtn && panel) {
      moreBtn.addEventListener("click", function () { if (isOpen()) close(false); else open(); });
      var onDocDown = function (e) {
        if (!document.body.contains(panel)) { document.removeEventListener("mousedown", onDocDown); return; }
        if (isOpen() && !panel.contains(e.target) && !moreBtn.contains(e.target)) close(false);
      };
      var onDocKey = function (e) {
        if (!document.body.contains(panel)) { document.removeEventListener("keydown", onDocKey); return; }
        if (e.key === "Escape" && isOpen()) close(true);
      };
      document.addEventListener("mousedown", onDocDown);
      document.addEventListener("keydown", onDocKey);
    }
    if (cfg.panelApplyBtn) cfg.panelApplyBtn.addEventListener("click", function () {
      close(false);
      if (cfg.onApply) cfg.onApply();
    });
    if (cfg.panelClearBtn) cfg.panelClearBtn.addEventListener("click", function () {
      close(false);
      if (cfg.onClearAll) cfg.onClearAll();
    });

    function refresh() {
      var active = [];
      controls.forEach(function (c) {
        var text = c.read();
        if (text) active.push({ control: c, text: text });
      });

      if (chipsEl) {
        chipsEl.textContent = "";
        active.forEach(function (a) {
          var chip = document.createElement("span");
          chip.className = "fbar-chip";
          var label = document.createElement("span");
          label.textContent = a.text;
          var x = document.createElement("button");
          x.type = "button";
          x.className = "fbar-chip-x";
          x.textContent = "×";
          x.setAttribute("aria-label", "Remove filter " + a.text);
          x.addEventListener("click", function () {
            a.control.reset();
            if (cfg.onChange) cfg.onChange();
            var next = chipsEl.querySelector(".fbar-chip-x");
            if (next) next.focus(); else if (moreBtn) moreBtn.focus();
          });
          chip.appendChild(label);
          chip.appendChild(x);
          chipsEl.appendChild(chip);
        });
        if (active.length > 1) {
          var all = document.createElement("button");
          all.type = "button";
          all.className = "fbar-clearall";
          all.textContent = "Clear all";
          all.addEventListener("click", function () { if (cfg.onClearAll) cfg.onClearAll(); });
          chipsEl.appendChild(all);
        }
        // chips live inside the popover now, so showing them never
        // changes the page layout (no resize / row-fit re-measure needed)
        // the slot is always there (fixed height); empty = muted hint via CSS
        chipsEl.hidden = false;
      }

      if (badge) {
        var n = active.filter(function (a) { return a.control.inPanel; }).length;
        badge.textContent = String(n);
        badge.hidden = n === 0;
        if (moreBtn) moreBtn.classList.toggle("has-active", n > 0);
      }
    }

    return { refresh: refresh, open: open, close: close };
  }
};

// Shared-element transition for opening/closing a reg-overlay form -
// the clicked button (an "Add X" pill or a row's Edit icon) visually
// grows into the modal card, instead of the card just appearing
// disconnected from whatever was clicked. Uses the browser's native
// View Transitions API (document.startViewTransition), which handles
// the morph (position/size/shape) between the "before" and "after"
// automatically once both elements briefly share the same
// view-transition-name. Falls back to a plain, unanimated DOM change
// in any browser that doesn't support it - nothing breaks there, the
// form just opens/closes the old way.
// The button-into-modal "morph" (native View Transition API) turned
// out to be the source of the lag reported in chat - even with the
// root cross-fade disabled (admin.css), it was still real screenshot/
// compositing work on every single Add/Edit/Borrow click. Removed
// outright per chat instead of continuing to tune it: open()/close()
// now just run the plain DOM change with no transition at all.
window.CLabTapMorph = {
  open: function (sourceEl, targetEl, apply) {
    apply();
  },
  close: function (sourceEl, targetEl, apply) {
    apply();
  }
};

// Staggers a freshly-rendered batch of table rows (or any element
// list) so they animate in one after another instead of all at once -
// a small CSS animation (.row-enter, see admin.css) does the actual
// fade/slide; this just spaces out when each row's copy of it starts.
// Caps the stagger itself at a handful of rows so a full page of 20
// doesn't take a visibly long time to finish appearing.
window.CLabTapStagger = {
  apply: function (rows, stepMs) {
    var step = stepMs || 28;
    var cap = 10;
    Array.prototype.forEach.call(rows, function (row, i) {
      row.classList.add("row-enter");
      row.style.animationDelay = (Math.min(i, cap) * step) + "ms";
    });
  }
};

// Shared by every "Send code" button in the sidebar profile modals
// (Change Password, Change PIN, Google link/unlink) - posts to
// /Account/RequestOtp for the given purpose and shows the result with
// whatever showMsg function that modal already uses. A simple
// cooldown stops someone mashing the button into spamming emails.
// validate (optional) - called right before a code is sent; return an
// error string to block sending and show that message instead (e.g.
// "Enter and confirm your new PIN first" when those fields are still
// empty), or return nothing/falsy to let it through. Keeps people from
// firing off OTP emails for a change they haven't actually filled in
// yet.
function wireSendOtpButton(btn, purpose, showMsg, validate) {
  if (!btn) return;
  var cooldownTimer = null;

  function startCooldown(seconds) {
    btn.disabled = true;
    var remaining = seconds;
    var originalText = "Send code";
    btn.textContent = "Resend (" + remaining + "s)";
    cooldownTimer = setInterval(function () {
      remaining -= 1;
      if (remaining <= 0) {
        clearInterval(cooldownTimer);
        btn.disabled = false;
        btn.textContent = originalText;
      } else {
        btn.textContent = "Resend (" + remaining + "s)";
      }
    }, 1000);
  }

  btn.addEventListener("click", function () {
    if (btn.disabled) return;

    if (validate) {
      var validationError = validate();
      if (validationError) {
        showMsg(validationError, "error");
        return;
      }
    }

    btn.disabled = true;
    btn.classList.add("is-loading");
    var textBeforeSending = btn.textContent;
    btn.textContent = "Sending...";
    showMsg("");

    var body = new URLSearchParams();
    body.append("purpose", purpose);

    fetch("/Account/RequestOtp", {
      method: "POST",
      headers: { "X-Requested-With": "XMLHttpRequest" },
      body: body
    })
      .then(function (res) { return res.json(); })
      .then(function (data) {
        btn.classList.remove("is-loading");
        if (data && data.error) {
          btn.disabled = false;
          btn.textContent = textBeforeSending;
          showMsg(data.error, "error");
          return;
        }
        showMsg("Code sent - check your email.", "success");
        startCooldown(30);
      })
      .catch(function () {
        btn.disabled = false;
        btn.classList.remove("is-loading");
        btn.textContent = textBeforeSending;
        showMsg("Network error - could not reach the server.", "error");
      });
  });
}

// Sidebar profile menu (Change Password / Log Out) - see
// _AdminLayout.cshtml. Lives outside the nav/main-content guard above
// since the sidebar footer is static markup present on every admin
// page load, not swapped by the AJAX nav.
(function () {
  "use strict";

  var trigger = document.getElementById("sidebarProfileTrigger");
  var menu = document.getElementById("sidebarProfileMenu");
  if (!trigger || !menu) return;

  function closeMenu() {
    menu.hidden = true;
    trigger.setAttribute("aria-expanded", "false");
  }
  function openMenu() {
    menu.hidden = false;
    trigger.setAttribute("aria-expanded", "true");
  }

  trigger.addEventListener("click", function (e) {
    e.stopPropagation();
    if (menu.hidden) openMenu(); else closeMenu();
  });
  document.addEventListener("click", function (e) {
    if (!menu.hidden && !e.target.closest("#sidebarProfile")) closeMenu();
  });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && !menu.hidden) closeMenu();
  });

  // ---- Change Password modal ----
  // Two steps on purpose (see chat): you can't even see the New
  // Password field until Verify confirms the Current Password field is
  // actually right - step2 stays hidden, and the Current input becomes
  // read-only, until that happens.
  var overlay = document.getElementById("changePasswordOverlay");
  var openBtn = document.getElementById("openChangePasswordBtn");
  var closeBtn = document.getElementById("changePasswordClose");
  var cancelBtn = document.getElementById("changePasswordCancelBtn");
  var verifyBtn = document.getElementById("changePasswordVerifyBtn");
  var submitBtn = document.getElementById("changePasswordSubmitBtn");
  var form = document.getElementById("changePasswordForm");
  var msgEl = document.getElementById("changePasswordMsg");
  var step2 = document.getElementById("cpStep2");
  var currentInput = document.getElementById("cpCurrent");
  var newInput = document.getElementById("cpNew");
  var confirmInput = document.getElementById("cpConfirm");
  var otpInput = document.getElementById("cpOtp");
  var sendOtpBtn = document.getElementById("changePasswordSendOtpBtn");
  if (!overlay || !openBtn || !form) return;

  wireSendOtpButton(sendOtpBtn, "password", showMsg, function () {
    var next = newInput ? newInput.value : "";
    var confirmVal = confirmInput ? confirmInput.value : "";
    if (next.length < 8) return "Enter your new password first (at least 8 characters).";
    if (next !== confirmVal) return "New password and confirmation don't match.";
    return null;
  });

  function showMsg(text, type) {
    if (!msgEl) return;
    if (!text) {
      msgEl.hidden = true;
      msgEl.textContent = "";
      msgEl.classList.remove("regf-message--shake");
      return;
    }
    msgEl.textContent = text;
    msgEl.dataset.type = type || "info";
    msgEl.hidden = false;
    // Restart the shake even if a message is already showing - remove
    // the class, force a reflow, then add it back.
    msgEl.classList.remove("regf-message--shake");
    void msgEl.offsetWidth;
    msgEl.classList.add("regf-message--shake");
  }

  function showStep1() {
    if (step2) step2.hidden = true;
    if (currentInput) { currentInput.readOnly = false; currentInput.focus(); }
    if (verifyBtn) verifyBtn.hidden = false;
    if (submitBtn) submitBtn.hidden = true;
  }
  function showStep2() {
    if (step2) step2.hidden = false;
    if (currentInput) currentInput.readOnly = true;
    if (verifyBtn) verifyBtn.hidden = true;
    if (submitBtn) submitBtn.hidden = false;
    if (newInput) newInput.focus();
  }

  function resetForm() {
    form.reset();
    showMsg("");
    showStep1();
  }

  function openOverlay() {
    resetForm();
    overlay.style.display = "flex";
    closeMenu();
  }
  function closeOverlay() {
    if ((verifyBtn && verifyBtn.disabled) || (submitBtn && submitBtn.disabled)) return; // request in flight
    overlay.style.display = "none";
  }

  openBtn.addEventListener("click", openOverlay);
  if (closeBtn) closeBtn.addEventListener("click", closeOverlay);
  if (cancelBtn) cancelBtn.addEventListener("click", closeOverlay);
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && overlay.style.display !== "none") closeOverlay();
  });

  if (verifyBtn) {
    verifyBtn.addEventListener("click", function () {
      var current = currentInput ? currentInput.value : "";
      if (!current) {
        showMsg("Enter your current password.", "error");
        return;
      }

      var body = new URLSearchParams();
      body.append("currentPassword", current);

      verifyBtn.disabled = true;
      verifyBtn.classList.add("is-loading");
      showMsg("");

      fetch("/Account/VerifyCurrentPassword", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          verifyBtn.disabled = false;
          verifyBtn.classList.remove("is-loading");
          if (data && data.error) {
            showMsg(data.error, "error");
            return;
          }
          showStep2();
        })
        .catch(function () {
          verifyBtn.disabled = false;
          verifyBtn.classList.remove("is-loading");
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }

  if (submitBtn) {
    submitBtn.addEventListener("click", function () {
      var current = currentInput ? currentInput.value : "";
      var next = newInput ? newInput.value : "";
      var confirmVal = confirmInput ? confirmInput.value : "";

      if (next.length < 8) {
        showMsg("New password must be at least 8 characters.", "error");
        return;
      }
      if (next !== confirmVal) {
        showMsg("New password and confirmation don't match.", "error");
        return;
      }

      var otpVal = otpInput ? otpInput.value.trim() : "";
      if (!otpVal) {
        showMsg("Enter the code we emailed you.", "error");
        return;
      }

      var body = new URLSearchParams();
      body.append("currentPassword", current);
      body.append("newPassword", next);
      body.append("otp", otpVal);

      submitBtn.disabled = true;
      submitBtn.classList.add("is-loading");
      showMsg("");

      fetch("/Account/ChangePassword", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          submitBtn.disabled = false;
          submitBtn.classList.remove("is-loading");
          if (data && data.error) {
            showMsg(data.error, "error");
            return;
          }
          showMsg("Password changed.", "success");
          setTimeout(closeOverlay, 900);
        })
        .catch(function () {
          submitBtn.disabled = false;
          submitBtn.classList.remove("is-loading");
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }
})();

// Sidebar profile menu - Change PIN modal. Separate IIFE from Change
// Password above purely so one broken selector can't take the other
// down; both close the same #sidebarProfileMenu they're opened from.
(function () {
  "use strict";

  var menu = document.getElementById("sidebarProfileMenu");
  function closeMenu() {
    if (menu) menu.hidden = true;
    var trigger = document.getElementById("sidebarProfileTrigger");
    if (trigger) trigger.setAttribute("aria-expanded", "false");
  }

  var overlay = document.getElementById("changePinOverlay");
  var openBtn = document.getElementById("openChangePinBtn");
  var closeBtn = document.getElementById("changePinClose");
  var cancelBtn = document.getElementById("changePinCancelBtn");
  var verifyBtn = document.getElementById("changePinVerifyBtn");
  var forgotBtn = document.getElementById("changePinForgotBtn");
  var submitBtn = document.getElementById("changePinSubmitBtn");
  var form = document.getElementById("changePinForm");
  var msgEl = document.getElementById("changePinMsg");
  var step2 = document.getElementById("cpiStep2");
  var currentInput = document.getElementById("cpiCurrent");
  // Set when "Forgot PIN?" was used instead of Verify - tells the final
  // Save call to skip the current-PIN match server-side too (see
  // AccountController.ChangePin's forgotPin param).
  var forgotPinUsed = false;
  var newInput = document.getElementById("cpiNew");
  var confirmInput = document.getElementById("cpiConfirm");
  var otpInput = document.getElementById("cpiOtp");
  var sendOtpBtn = document.getElementById("changePinSendOtpBtn");
  if (!overlay || !openBtn || !form) return;

  wireSendOtpButton(sendOtpBtn, "pin", showMsg, function () {
    var next = newInput ? newInput.value : "";
    var confirmVal = confirmInput ? confirmInput.value : "";
    if (next.length !== 4 || !/^[0-9]+$/.test(next)) return "Enter your new 4-digit PIN first.";
    if (next !== confirmVal) return "New PIN and confirmation don't match.";
    return null;
  });

  function showMsg(text, type) {
    if (!msgEl) return;
    if (!text) {
      msgEl.hidden = true;
      msgEl.textContent = "";
      msgEl.classList.remove("regf-message--shake");
      return;
    }
    msgEl.textContent = text;
    msgEl.dataset.type = type || "info";
    msgEl.hidden = false;
    // Restart the shake even if a message is already showing - remove
    // the class, force a reflow, then add it back.
    msgEl.classList.remove("regf-message--shake");
    void msgEl.offsetWidth;
    msgEl.classList.add("regf-message--shake");
  }

  function showStep1() {
    if (step2) step2.hidden = true;
    if (currentInput) { currentInput.readOnly = false; currentInput.focus(); }
    if (verifyBtn) verifyBtn.hidden = false;
    if (submitBtn) submitBtn.hidden = true;
  }
  function showStep2() {
    if (step2) step2.hidden = false;
    if (currentInput) currentInput.readOnly = true;
    if (verifyBtn) verifyBtn.hidden = true;
    if (submitBtn) submitBtn.hidden = false;
    if (newInput) newInput.focus();
  }

  function resetForm() {
    form.reset();
    showMsg("");
    forgotPinUsed = false;
    showStep1();
  }

  function openOverlay() {
    resetForm();
    overlay.style.display = "flex";
    closeMenu();
  }
  function closeOverlay() {
    if ((verifyBtn && verifyBtn.disabled) || (submitBtn && submitBtn.disabled)) return; // request in flight
    overlay.style.display = "none";
  }

  openBtn.addEventListener("click", openOverlay);
  if (closeBtn) closeBtn.addEventListener("click", closeOverlay);
  if (cancelBtn) cancelBtn.addEventListener("click", closeOverlay);
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && overlay.style.display !== "none") closeOverlay();
  });

  if (forgotBtn) {
    forgotBtn.addEventListener("click", function () {
      forgotPinUsed = true;
      if (currentInput) {
        currentInput.value = "";
        currentInput.dispatchEvent(new Event("input", { bubbles: true }));
      }
      showMsg("No problem - enter a new PIN below and confirm it with the code we email you.", "success");
      showStep2();
    });
  }

  if (verifyBtn) {
    verifyBtn.addEventListener("click", function () {
      var current = currentInput ? currentInput.value : "";
      if (!current) {
        showMsg("Enter your current PIN.", "error");
        return;
      }

      var body = new URLSearchParams();
      body.append("currentPin", current);

      verifyBtn.disabled = true;
      verifyBtn.classList.add("is-loading");
      showMsg("");

      fetch("/Account/VerifyCurrentPin", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          verifyBtn.disabled = false;
          verifyBtn.classList.remove("is-loading");
          if (data && data.error) {
            showMsg(data.error, "error");
            // Wrong PIN - clear it instead of leaving the wrong digits
            // sitting there (also resets the dash meter back to empty).
            if (currentInput) {
              currentInput.value = "";
              currentInput.dispatchEvent(new Event("input", { bubbles: true }));
              currentInput.focus();
            }
            return;
          }
          showStep2();
        })
        .catch(function () {
          verifyBtn.disabled = false;
          verifyBtn.classList.remove("is-loading");
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }

  if (submitBtn) {
    submitBtn.addEventListener("click", function () {
      var current = currentInput ? currentInput.value : "";
      var next = newInput ? newInput.value : "";
      var confirmVal = confirmInput ? confirmInput.value : "";

      if (next.length !== 4 || !/^[0-9]+$/.test(next)) {
        showMsg("New PIN must be exactly 4 digits.", "error");
        return;
      }
      if (next !== confirmVal) {
        showMsg("New PIN and confirmation don't match.", "error");
        return;
      }

      var otpVal = otpInput ? otpInput.value.trim() : "";
      if (!otpVal) {
        showMsg("Enter the code we emailed you.", "error");
        return;
      }

      var body = new URLSearchParams();
      body.append("currentPin", current);
      body.append("newPin", next);
      body.append("otp", otpVal);
      if (forgotPinUsed) body.append("forgotPin", "true");

      submitBtn.disabled = true;
      submitBtn.classList.add("is-loading");
      showMsg("");

      fetch("/Account/ChangePin", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          submitBtn.disabled = false;
          submitBtn.classList.remove("is-loading");
          if (data && data.error) {
            showMsg(data.error, "error");
            return;
          }
          showMsg("PIN changed.", "success");
          setTimeout(closeOverlay, 900);
        })
        .catch(function () {
          submitBtn.disabled = false;
          submitBtn.classList.remove("is-loading");
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }
})();

// Sidebar profile menu - Link/Unlink Google Account, both OTP-gated
// (see wireSendOtpButton above and AccountController's RequestOtp /
// VerifyGoogleOtp / GoogleUnlink). Linking can't just POST and be done
// the way Change Password/PIN do - it has to hand off to a full-page
// redirect to Google - so on success this navigates the browser to
// GoogleLink with the one-time token VerifyGoogleOtp hands back,
// instead of showing a success message itself.
(function () {
  "use strict";

  function closeMenu() {
    var menu = document.getElementById("sidebarProfileMenu");
    if (menu) menu.hidden = true;
    var trigger = document.getElementById("sidebarProfileTrigger");
    if (trigger) trigger.setAttribute("aria-expanded", "false");
  }

  function setupSimpleModal(cfg) {
    var overlay = document.getElementById(cfg.overlayId);
    var openBtn = document.getElementById(cfg.openBtnId);
    var closeBtn = document.getElementById(cfg.closeBtnId);
    var cancelBtn = document.getElementById(cfg.cancelBtnId);
    var submitBtn = document.getElementById(cfg.submitBtnId);
    var msgEl = document.getElementById(cfg.msgId);
    var otpInput = document.getElementById(cfg.otpInputId);
    var sendOtpBtn = document.getElementById(cfg.sendOtpBtnId);
    if (!overlay || !openBtn) return;

    function showMsg(text, type) {
      if (!msgEl) return;
      if (!text) {
        msgEl.hidden = true;
        msgEl.textContent = "";
        msgEl.classList.remove("regf-message--shake");
        return;
      }
      msgEl.textContent = text;
      msgEl.dataset.type = type || "info";
      msgEl.hidden = false;
      // Restart the shake even if a message is already showing - remove
      // the class, force a reflow, then add it back.
      msgEl.classList.remove("regf-message--shake");
      void msgEl.offsetWidth;
      msgEl.classList.add("regf-message--shake");
    }

    wireSendOtpButton(sendOtpBtn, "google", showMsg);

    function openOverlay() {
      if (otpInput) otpInput.value = "";
      showMsg("");
      overlay.style.display = "flex";
      closeMenu();
      if (otpInput) otpInput.focus();
    }
    function closeOverlay() {
      if (submitBtn && submitBtn.disabled) return; // request in flight
      overlay.style.display = "none";
    }

    openBtn.addEventListener("click", openOverlay);
    if (closeBtn) closeBtn.addEventListener("click", closeOverlay);
    if (cancelBtn) cancelBtn.addEventListener("click", closeOverlay);
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && overlay.style.display !== "none") closeOverlay();
    });

    if (submitBtn) {
      submitBtn.addEventListener("click", function () {
        var otpVal = otpInput ? otpInput.value.trim() : "";
        if (!otpVal) {
          showMsg("Enter the code we emailed you.", "error");
          return;
        }
        submitBtn.disabled = true;
        submitBtn.classList.add("is-loading");
        showMsg("");
        cfg.onSubmit(otpVal, showMsg, function () {
          submitBtn.disabled = false;
          submitBtn.classList.remove("is-loading");
        });
      });
    }
  }

  setupSimpleModal({
    overlayId: "googleLinkOverlay",
    openBtnId: "openGoogleLinkBtn",
    closeBtnId: "googleLinkClose",
    cancelBtnId: "googleLinkCancelBtn",
    submitBtnId: "googleLinkSubmitBtn",
    msgId: "googleLinkMsg",
    otpInputId: "glOtp",
    sendOtpBtnId: "googleLinkSendOtpBtn",
    onSubmit: function (otpVal, showMsg, reenable) {
      var body = new URLSearchParams();
      body.append("otp", otpVal);
      fetch("/Account/VerifyGoogleOtp", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          if (data && data.error) {
            reenable();
            showMsg(data.error, "error");
            return;
          }
          // Full-page navigation on purpose - this has to leave the
          // AJAX nav and actually redirect to Google next.
          window.location.href = "/Account/GoogleLink?token=" + encodeURIComponent(data.token);
        })
        .catch(function () {
          reenable();
          showMsg("Network error - could not reach the server.", "error");
        });
    }
  });

  setupSimpleModal({
    overlayId: "googleUnlinkOverlay",
    openBtnId: "openGoogleUnlinkBtn",
    closeBtnId: "googleUnlinkClose",
    cancelBtnId: "googleUnlinkCancelBtn",
    submitBtnId: "googleUnlinkSubmitBtn",
    msgId: "googleUnlinkMsg",
    otpInputId: "guOtp",
    sendOtpBtnId: "googleUnlinkSendOtpBtn",
    onSubmit: function (otpVal, showMsg, reenable) {
      var body = new URLSearchParams();
      body.append("otp", otpVal);
      fetch("/Account/GoogleUnlink", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          if (data && data.error) {
            reenable();
            showMsg(data.error, "error");
            return;
          }
          showMsg("Google account unlinked.", "success");
          // Reload so the sidebar menu re-renders back to "Link Google
          // Account" (googleLinked is computed server-side on page load).
          setTimeout(function () { window.location.reload(); }, 900);
        })
        .catch(function () {
          reenable();
          showMsg("Network error - could not reach the server.", "error");
        });
    }
  });
})();

// Rare edge case: GoogleLink's one-time token can expire/get reused if
// the redirect to Google is interrupted between the OTP step and the
// actual redirect. Program.cs's AttendanceController etc. don't know
// about this, so it's surfaced with a plain query flag instead of a
// server-rendered message.
// App notice dialog (see #appNotice in _AdminLayout.cshtml) - replaces
// the browser's plain alert() with the same design as the delete confirms.
window.CLabTapNotice = function (title, text) {
  var box = document.getElementById("appNotice");
  if (!box) return;
  var ok = document.getElementById("appNoticeOk");
  document.getElementById("appNoticeTitle").textContent = title;
  document.getElementById("appNoticeText").textContent = text;
  function close() { box.hidden = true; ok.onclick = null; box.onclick = null; box.onkeydown = null; }
  ok.onclick = close;
  box.onclick = function (e) { if (e.target === box) close(); };
  box.onkeydown = function (e) { if (e.key === "Escape") close(); };
  box.hidden = false;
  ok.focus();
};

(function () {
  "use strict";
  var params = new URLSearchParams(window.location.search);
  if (params.get("notice") === "linkexpired") {
    window.CLabTapNotice("Link expired", "That link expired or was already used. Open the profile menu and try linking Google again.");
    params.delete("notice");
    var newQuery = params.toString();
    var newUrl = window.location.pathname + (newQuery ? "?" + newQuery : "");
    window.history.replaceState({}, "", newUrl);
  }
})();
