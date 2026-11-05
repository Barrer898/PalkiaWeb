// Only runs on pages that opted in via the <meta name="idle-timeout-minutes">
// tag (see Pages/_Host.cshtml) - i.e. only when the request was authenticated
// at prerender time. Deliberately independent of the Blazor circuit: it
// tracks real user input (mouse/keyboard/touch) and, once idle too long,
// does a real POST to /Account/Logout followed by a full navigation to the
// login page. This is what actually catches "user has the tab open but
// hasn't touched it in an hour" - the auth cookie's own sliding expiration
// only renews on real HTTP requests, which an idle Blazor Server session
// mostly doesn't make.
(function () {
    "use strict";

    var meta = document.querySelector('meta[name="idle-timeout-minutes"]');
    if (!meta) {
        return;
    }

    var timeoutMinutes = parseFloat(meta.content);
    if (!timeoutMinutes || timeoutMinutes <= 0) {
        return;
    }

    var timeoutMs = timeoutMinutes * 60 * 1000;
    var checkIntervalMs = 30 * 1000; // no need to check more often than this
    var lastActivity = Date.now();
    var loggedOut = false;

    var activityEvents = ["mousemove", "mousedown", "keydown", "touchstart", "scroll"];
    activityEvents.forEach(function (eventName) {
        document.addEventListener(eventName, function () {
            lastActivity = Date.now();
        }, { passive: true });
    });

    function idleLogout() {
        if (loggedOut) {
            return;
        }
        loggedOut = true;

        fetch("/Account/Logout", { method: "POST", credentials: "same-origin" })
            .catch(function () {
                // Even if the fetch itself fails (e.g. connection already
                // gone), still send them to the login page - worst case
                // they hit an already-expired cookie there and re-enter
                // credentials, which is the correct outcome either way.
            })
            .finally(function () {
                window.location.href = "/Account/Login?reason=idle";
            });
    }

    setInterval(function () {
        if (Date.now() - lastActivity >= timeoutMs) {
            idleLogout();
        }
    }, checkIntervalMs);
})();
