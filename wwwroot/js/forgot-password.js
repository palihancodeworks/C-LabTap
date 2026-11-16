// "Forgot password?" on the login page (see Views/Account/LoggedOut.cshtml
// and AccountController's ForgotPasswordRequestOtp / ForgotPasswordReset).
// Self-contained since this page doesn't load admin.js - just this file
// plus password-toggle.js (already loaded) for the New Password field's
// eye icon. Two steps: enter a username and request a code, then enter
// that code + a new password to actually reset it - no "resend" cooldown
// here since there's nothing to verify first the way Change Password's
// current-password step has.
(function () {
  "use strict";

  var overlay = document.getElementById("forgotPasswordOverlay");
  var openBtn = document.getElementById("openForgotPasswordBtn");
  var closeBtn = document.getElementById("forgotPasswordClose");
  var cancelBtn = document.getElementById("forgotPasswordCancelBtn");
  var sendBtn = document.getElementById("forgotPasswordSendBtn");
  var resetBtn = document.getElementById("forgotPasswordResetBtn");
  var form = document.getElementById("forgotPasswordForm");
  var msgEl = document.getElementById("forgotPasswordMsg");
  var step2 = document.getElementById("fpStep2");
  var usernameInput = document.getElementById("fpUsername");
  var otpInput = document.getElementById("fpOtp");
  var newInput = document.getElementById("fpNew");
  var confirmInput = document.getElementById("fpConfirm");
  if (!overlay || !openBtn || !form) return;

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
    msgEl.classList.remove("regf-message--shake");
    void msgEl.offsetWidth;
    msgEl.classList.add("regf-message--shake");
  }

  function showStep1() {
    if (step2) step2.hidden = true;
    if (usernameInput) usernameInput.readOnly = false;
    if (sendBtn) { sendBtn.hidden = false; sendBtn.disabled = false; sendBtn.textContent = "Send code"; sendBtn.classList.remove("is-loading"); }
    if (resetBtn) resetBtn.hidden = true;
  }
  function showStep2() {
    if (step2) step2.hidden = false;
    if (usernameInput) usernameInput.readOnly = true;
    if (sendBtn) sendBtn.hidden = true;
    if (resetBtn) resetBtn.hidden = false;
    if (otpInput) otpInput.focus();
  }

  function resetForm() {
    form.reset();
    showMsg("");
    showStep1();
  }

  function openOverlay(e) {
    if (e) e.preventDefault();
    resetForm();
    overlay.style.display = "flex";
    if (usernameInput) usernameInput.focus();
  }
  function closeOverlay() {
    if ((sendBtn && sendBtn.disabled && !sendBtn.hidden) || (resetBtn && resetBtn.disabled && !resetBtn.hidden)) return; // request in flight
    overlay.style.display = "none";
  }

  openBtn.addEventListener("click", openOverlay);
  if (closeBtn) closeBtn.addEventListener("click", closeOverlay);
  if (cancelBtn) cancelBtn.addEventListener("click", closeOverlay);
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && overlay.style.display !== "none") closeOverlay();
  });

  // Landed here via the signed-in Change Password modal's "Forgot your
  // current password?" link (AccountController.Logout(openForgot: true))
  // - open this overlay right away instead of making them find and click
  // "Forgot password?" again themselves.
  if (/(?:^|[?&])openForgot=true(?:&|$)/i.test(window.location.search)) {
    openOverlay();
  }

  if (sendBtn) {
    sendBtn.addEventListener("click", function () {
      var username = usernameInput ? usernameInput.value.trim() : "";
      if (!username) {
        showMsg("Enter your username.", "error");
        return;
      }

      var body = new URLSearchParams();
      body.append("username", username);

      sendBtn.disabled = true;
      sendBtn.classList.add("is-loading");
      sendBtn.textContent = "Sending...";
      showMsg("");

      fetch("/Account/ForgotPasswordRequestOtp", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          sendBtn.disabled = false;
          sendBtn.classList.remove("is-loading");
          sendBtn.textContent = "Send code";
          if (data && data.error) {
            showMsg(data.error, "error");
            return;
          }
          showMsg(data.message || "Code sent - check the email on file for that account.", "success");
          showStep2();
        })
        .catch(function () {
          sendBtn.disabled = false;
          sendBtn.classList.remove("is-loading");
          sendBtn.textContent = "Send code";
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }

  if (resetBtn) {
    resetBtn.addEventListener("click", function () {
      var username = usernameInput ? usernameInput.value.trim() : "";
      var otpVal = otpInput ? otpInput.value.trim() : "";
      var next = newInput ? newInput.value : "";
      var confirmVal = confirmInput ? confirmInput.value : "";

      if (!otpVal) { showMsg("Enter the code we emailed you.", "error"); return; }
      if (next.length < 8) { showMsg("New password must be at least 8 characters.", "error"); return; }
      if (next !== confirmVal) { showMsg("New password and confirmation don't match.", "error"); return; }

      var body = new URLSearchParams();
      body.append("username", username);
      body.append("otp", otpVal);
      body.append("newPassword", next);

      resetBtn.disabled = true;
      resetBtn.classList.add("is-loading");
      showMsg("");

      fetch("/Account/ForgotPasswordReset", {
        method: "POST",
        headers: { "X-Requested-With": "XMLHttpRequest" },
        body: body
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          resetBtn.disabled = false;
          resetBtn.classList.remove("is-loading");
          if (data && data.error) {
            showMsg(data.error, "error");
            return;
          }
          showMsg("Password reset - you can log in with your new password now.", "success");
          var loginUsername = document.getElementById("username");
          if (loginUsername) loginUsername.value = username;
          setTimeout(closeOverlay, 1400);
        })
        .catch(function () {
          resetBtn.disabled = false;
          resetBtn.classList.remove("is-loading");
          showMsg("Network error - could not reach the server.", "error");
        });
    });
  }
})();
