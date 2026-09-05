/*
 * Host-page boot behaviour: the loading splash and the navigation progress bar.
 *
 * Lives in a file rather than an inline <script> in App.razor because the app sends
 * `script-src 'self' 'wasm-unsafe-eval'` with no 'unsafe-inline' (see
 * Host/SecurityHeaders.cs). An inline block here would be blocked by the browser with
 * nothing but a console entry — and since its only job is to REMOVE the splash, the
 * visible symptom would be a permanent full-screen "Loading PoPunkouterSoftware…"
 * overlay over a fully rendered page.
 *
 * Loads before motion-kit.js and owns no animation frames: the progress bar is a CSS
 * transition, so the shared rAF governor is not involved.
 */
(function () {
    'use strict';

    // Suppress expected hot-reload errors in development.
    var originalError = console.error;
    console.error = function () {
        var msg = (arguments[0] && arguments[0].toString && arguments[0].toString()) || '';
        if (msg.includes('blazor-hotreload') || msg.includes('net::ERR_ABORTED')) {
            return; // Silently ignore hot-reload connection errors
        }
        originalError.apply(console, arguments);
    };

    // Remove the boot splash the moment Blazor renders real UI (the Radzen layout).
    //
    // Re-query on every call: an enhanced navigation re-inserts a *brand new* splash
    // element (the server renders one into every response, and the DOM diff restores what
    // we removed), so a captured reference goes stale. `fade` is for the cold load only —
    // on navigation the splash must vanish instantly or every page change flashes a
    // full-screen overlay for 300ms.
    window.dismissBootSplash = function (fade) {
        var splash = document.getElementById('app-boot-splash');
        if (!splash) return;
        if (!fade) { splash.remove(); return; }
        splash.classList.add('is-dismissed');
        setTimeout(function () { splash.remove(); }, 350);
    };

    var observer = new MutationObserver(function () {
        if (document.querySelector('.rz-layout, .app-topbar')) {
            observer.disconnect();
            window.dismissBootSplash(true);
        }
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    // Fail-safe: never trap the user behind the splash if boot detection misses.
    setTimeout(function () { observer.disconnect(); window.dismissBootSplash(true); }, 30000);

    // Navigation progress bar for Blazor enhanced navigation.
    var bar = null;
    function getBar() { return bar || (bar = document.getElementById('app-nav-progress')); }

    window.appNavStart = function () {
        var b = getBar(); if (!b) return;
        b.className = 'is-active';
        // Close the mobile drawer once a navigation begins, otherwise it stays open
        // across page changes and looks like a stuck overlay.
        if (window.closeTopbarDrawer) window.closeTopbarDrawer();
    };

    window.appNavEnd = function () {
        var b = getBar(); if (!b) return;
        b.className = 'is-done';
        setTimeout(function () {
            b.className = 'is-hidden';
            setTimeout(function () { b.className = ''; }, 300);
        }, 200);
    };
})();
