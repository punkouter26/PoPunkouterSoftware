/*
 * Enhanced-navigation hooks. Must load AFTER _framework/blazor.web.js, which is what
 * defines `Blazor`.
 *
 * These callbacks are delivered through Blazor's own listener registry, NOT as DOM events —
 * `document.addEventListener('enhancedload', ...)` never fires and silently does nothing.
 *
 * Without the splash hookup, clicking an in-app link leaves the re-inserted boot splash
 * covering a fully rendered page forever (the first-load MutationObserver in app-boot.js is
 * already disconnected by then), which looks exactly like the app hanging on load.
 *
 * A file rather than an inline <script> for the same CSP reason as app-boot.js — see
 * Host/SecurityHeaders.cs.
 */
Blazor.addEventListener('enhancednavigationstart', window.appNavStart);
Blazor.addEventListener('enhancedload', function () {
    window.dismissBootSplash(false);
    window.appNavEnd();
});
