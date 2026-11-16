// Adds the anti-forgery (CSRF) token to every state-changing request this
// page sends with fetch().
//
// The server rejects any POST that doesn't carry the token it issued to
// this browser (Program.cs: AutoValidateAntiforgeryToken). That stops a
// different website from quietly submitting requests to C-LabTap using a
// logged-in admin's cookie - the other site can make the browser send the
// request, but it can't read this page to learn the token.
//
// Plain <form method="post"> elements already carry the token as a hidden
// field (ASP.NET adds it). Everything else here goes through fetch(), so
// rather than editing each call, this wraps fetch once: same-origin
// requests that aren't GET/HEAD get an "X-CSRF-TOKEN" header, read from
// the <meta name="csrf-token"> tag the layout renders. Must load before
// any script that calls fetch().
(function () {
    var meta = document.querySelector('meta[name="csrf-token"]');
    var token = meta ? meta.getAttribute("content") : "";
    if (!token || typeof window.fetch !== "function") return;

    var originalFetch = window.fetch;

    window.fetch = function (input, init) {
        init = init || {};
        var isRequest = typeof Request !== "undefined" && input instanceof Request;
        var method = String(init.method || (isRequest ? input.method : "GET")).toUpperCase();
        var url = isRequest ? input.url : String(input);

        var sameOrigin;
        try {
            sameOrigin = new URL(url, window.location.href).origin === window.location.origin;
        } catch (e) {
            sameOrigin = false;
        }

        if (sameOrigin && method !== "GET" && method !== "HEAD") {
            var headers = new Headers(init.headers || (isRequest ? input.headers : undefined));
            if (!headers.has("X-CSRF-TOKEN")) headers.set("X-CSRF-TOKEN", token);
            init.headers = headers;
        }

        return originalFetch.call(window, input, init);
    };
})();
