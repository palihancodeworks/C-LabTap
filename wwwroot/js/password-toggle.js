// Adds a show/hide "eye" button to every type="password" input on the
// page - the login form, Change Password/PIN, the Accounts form's
// password+PIN fields, and the Logbook kiosk's admin PIN gate. Works
// site-wide automatically instead of needing to be wired into each
// field by hand: a MutationObserver catches inputs added later too
// (modals that start hidden are already in the DOM at load and get
// caught by the initial scan; the Settings "Accounts" form is swapped
// in by the AJAX nav, which the observer catches as it's added).
(function () {
  "use strict";

  var EYE_OPEN =
    '<svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>' +
    '<circle cx="12" cy="12" r="3" stroke="currentColor" stroke-width="1.6"/></svg>';
  var EYE_CLOSED =
    '<svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
    '<path d="M3 3l18 18" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>' +
    '<path d="M10.6 5.2A10.8 10.8 0 0 1 12 5c6.5 0 10 7 10 7a15.6 15.6 0 0 1-3.2 4.1M6.6 6.6C4 8.3 2 12 2 12s3.5 7 10 7c1.4 0 2.6-.3 3.7-.8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>' +
    '<path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>';

  function wrapOne(input) {
    if (input.dataset.pwdToggled) return;
    input.dataset.pwdToggled = "1";

    var wrap = document.createElement("span");
    wrap.className = "pwd-wrap";
    input.parentNode.insertBefore(wrap, input);
    wrap.appendChild(input);

    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "pwd-toggle";
    btn.setAttribute("aria-label", "Show password");
    btn.innerHTML = EYE_OPEN;
    wrap.appendChild(btn);

    btn.addEventListener("click", function () {
      var showingText = input.type === "text";
      input.type = showingText ? "password" : "text";
      btn.innerHTML = showingText ? EYE_OPEN : EYE_CLOSED;
      btn.setAttribute("aria-label", showingText ? "Show password" : "Hide password");
    });
  }

  function scan(root) {
    var inputs = root.querySelectorAll ? root.querySelectorAll('input[type="password"]') : [];
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
          if (node.matches && node.matches('input[type="password"]')) wrapOne(node);
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
