// Draws a row of small dash segments under a PIN input that light up
// as each digit is typed - same look/pattern as Register Student's
// 12-digit LRN meter (.regf-meter), just reused for 4-digit PINs: the
// Settings "Starting PIN" field, the Change PIN modal's
// Current/New/Confirm fields, and the Logbook kiosk's admin PIN gate.
// Opt-in via data-pin-dots="<length>" on the <input> (NOT applied to
// OTP code fields - an emailed code is meant to be read, not entered
// blind like a PIN). The input itself is left completely alone - this
// only adds a sibling meter element and listens for input events.
(function () {
  "use strict";

  function wrapOne(input) {
    if (input.dataset.pinDotsWrapped) return;
    input.dataset.pinDotsWrapped = "1";

    var length = parseInt(input.dataset.pinDots, 10);
    if (!length || length < 1) length = 4;

    var meter = document.createElement("div");
    meter.className = "pin-meter";
    meter.setAttribute("aria-hidden", "true");
    var segs = [];
    for (var i = 0; i < length; i++) {
      var seg = document.createElement("i");
      meter.appendChild(seg);
      segs.push(seg);
    }
    // If the eye-toggle script (password-toggle.js) has already
    // wrapped this input in a .pwd-wrap span, put the meter after that
    // whole wrap instead of inside it - otherwise .pwd-wrap would grow
    // to contain the meter too, and the toggle button's "centered on
    // the wrap" math would center it over input+meter together instead
    // of just the input, throwing its vertical alignment off.
    var host = input.closest(".pwd-wrap") || input;
    host.insertAdjacentElement("afterend", meter);

    function render() {
      var filled = Math.min(input.value.length, segs.length);
      for (var i = 0; i < segs.length; i++) {
        segs[i].classList.toggle("on", i < filled);
      }
      meter.classList.toggle("is-full", filled === segs.length);
    }

    input.addEventListener("input", render);
    render();
  }

  function scan(root) {
    var inputs = root.querySelectorAll ? root.querySelectorAll("input[data-pin-dots]") : [];
    for (var i = 0; i < inputs.length; i++) wrapOne(inputs[i]);
  }

  function init() {
    scan(document);
    var observer = new MutationObserver(function (mutations) {
      for (var i = 0; i < mutations.length; i++) {
        var added = mutations[i].addedNodes;
        for (var j = 0; j < added.length; j++) {
          var node = added[j];
          if (node.nodeType !== 1) continue;
          if (node.matches && node.matches("input[data-pin-dots]")) wrapOne(node);
          scan(node);
        }
      }
    });
    observer.observe(document.body, { childList: true, subtree: true });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
