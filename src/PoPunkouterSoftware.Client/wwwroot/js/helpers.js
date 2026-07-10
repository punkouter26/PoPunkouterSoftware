/**
 * Trigger a browser file download with the given text content.
 * @param {string} filename  - suggested filename (e.g. "azure-report-2026-05-14.json")
 * @param {string} content   - file content (JSON string)
 * @param {string} mimeType  - MIME type (default: application/json)
 */
window.downloadTextFile = function (filename, content, mimeType) {
    mimeType = mimeType || 'application/json';
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

/* ═════════════════════════════════════════════════════════════════════════════
   Topbar behaviour.

   MainLayout is statically server-rendered (`<Routes />` carries no render mode;
   only the page components are InteractiveWebAssembly). Its OnAfterRenderAsync
   therefore never runs, so the previous `JS.InvokeVoidAsync("initNavbarScroll")`
   was never invoked and the floating navbar was inert. Both behaviours now
   self-initialise from this file, which the host page always loads.
   ═════════════════════════════════════════════════════════════════════════════ */

/**
 * Nav drawer: open/close the mobile topbar drawer.
 *
 * Replaces a ~200-character inline `onclick` that mutated classList, aria-expanded,
 * and textContent directly in the markup. Adds Escape-to-close and click-outside,
 * which the inline version had no room for.
 */
window.initTopbarDrawer = function () {
    const trigger = document.querySelector('.app-topbar-menu');
    const drawer = document.getElementById('app-topbar-drawer');
    if (!trigger || !drawer) return;
    if (trigger.dataset.wired === '1') return;
    trigger.dataset.wired = '1';

    const setOpen = (open) => {
        document.body.classList.toggle('topbar-open', open);
        trigger.setAttribute('aria-expanded', String(open));
        const icon = trigger.firstElementChild;
        if (icon) icon.textContent = open ? 'close' : 'menu';
    };
    const isOpen = () => document.body.classList.contains('topbar-open');

    window.closeTopbarDrawer = () => setOpen(false);

    trigger.addEventListener('click', (e) => {
        e.stopPropagation();
        setOpen(!isOpen());
    });

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && isOpen()) {
            setOpen(false);
            trigger.focus();
        }
    });

    document.addEventListener('click', (e) => {
        if (isOpen() && !drawer.contains(e.target) && !trigger.contains(e.target)) {
            setOpen(false);
        }
    });
};

/**
 * Track page scroll state to float/hide navbar navigation bar.
 *
 * The listener is registered passive so the compositor never waits on it, and the
 * class writes are coalesced into one rAF callback and only issued when the state
 * actually flips. The header carries `backdrop-filter` plus a `box-shadow`
 * transition, so an unconditional classList write per scroll event forced a blur
 * repaint on every frame.
 */
window.initNavbarScroll = function () {
    const header = document.querySelector('.rz-header');
    if (!header) return;
    if (window.navbarScrollHeader === header) return;

    if (window.navbarScrollHandler) {
        window.removeEventListener('scroll', window.navbarScrollHandler);
    }

    window.navbarScrollHeader = header;
    let lastScrollY = window.scrollY;
    let hidden = false;
    let scrolled = false;
    let queued = false;

    const apply = () => {
        queued = false;
        if (window.navbarScrollHeader !== header) return;

        const currentScrollY = window.scrollY;
        const nextHidden = currentScrollY > lastScrollY && currentScrollY > 80;
        const nextScrolled = currentScrollY > 20;

        if (nextHidden !== hidden) {
            header.classList.toggle('nav-hidden', nextHidden);
            hidden = nextHidden;
        }
        if (nextScrolled !== scrolled) {
            header.classList.toggle('nav-scrolled', nextScrolled);
            scrolled = nextScrolled;
        }
        lastScrollY = currentScrollY;
    };

    window.navbarScrollHandler = () => {
        if (queued) return;
        queued = true;
        requestAnimationFrame(apply);
    };
    window.addEventListener('scroll', window.navbarScrollHandler, { passive: true });
};

/**
 * One-tap copy clipboard helper. Returns a promise resolving to true/false so the
 * caller (C#) can show honest feedback — the previous version swallowed failures
 * (the UI said "Copied" with nothing on the clipboard) and threw synchronously on
 * non-secure origins where navigator.clipboard is undefined, which crashed the page
 * through the Blazor error boundary. The single toast is now owned by the C# side.
 */
window.copyToClipboard = function (text) {
    if (!navigator.clipboard) {
        console.error('Clipboard API unavailable (non-secure context?)');
        return Promise.resolve(false);
    }
    return navigator.clipboard.writeText(text)
        .then(() => true)
        .catch(err => {
            console.error('Clipboard copy failed: ', err);
            return false;
        });
};

/**
 * Media-query bridge.
 *
 * Lets a component render EITHER the desktop grid OR the mobile card list instead of
 * rendering both and hiding one with CSS. Only primitives cross the interop boundary
 * (string in, int out, bool on the callback), so no reflection-based serialisation is
 * pulled in and the WASM trim analyzer stays clean.
 *
 * `register` invokes the callback once with the current value before returning, so the
 * component never has to guess an initial breakpoint state.
 */
window.appMedia = (function () {
    var registry = {};
    var nextId = 1;

    return {
        register: function (query, dotNetRef, method) {
            var mql = window.matchMedia(query);
            var handler = function (e) { dotNetRef.invokeMethodAsync(method, e.matches); };
            mql.addEventListener('change', handler);
            var id = nextId++;
            registry[id] = { mql: mql, handler: handler };
            dotNetRef.invokeMethodAsync(method, mql.matches);
            return id;
        },
        unregister: function (id) {
            var entry = registry[id];
            if (!entry) return;
            entry.mql.removeEventListener('change', entry.handler);
            delete registry[id];
        }
    };
})();

/**
 * Force a cache-busting reload when the server BuildId no longer matches the loaded
 * WASM bundle. Replaces a JS `eval` string that was assembled in C#.
 */
window.appHardReload = function () {
    if (window.location.search.indexOf('reload=1') === -1) {
        window.location.replace(window.location.pathname + '?reload=1&t=' + Date.now());
    } else {
        window.location.reload();
    }
};

/* Self-initialise the topbar. The header is present in the static SSR payload, so
   it exists by the time this script executes; the DOMContentLoaded guard covers the
   case where the script is ever moved into <head>. */
(function bootstrapTopbar() {
    const start = () => {
        window.initTopbarDrawer();
        window.initNavbarScroll();
    };
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
