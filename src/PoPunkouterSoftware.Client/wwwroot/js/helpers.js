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
        // aria-expanded only describes reality when the drawer can actually collapse.
        // The trigger is `display: none` on desktop (>= 768px) and the centre nav is
        // always visible, so an "expanded" attribute there lied: a screen reader
        // heard "expanded" on a desktop where there was no drawer to expand. Hide
        // the attribute on desktop by clearing it; on mobile it tracks the state.
        const canCollapse = getComputedStyle(trigger).display !== 'none';
        if (canCollapse) {
            trigger.setAttribute('aria-expanded', String(open));
        } else {
            trigger.removeAttribute('aria-expanded');
        }
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

/**
 * Snap pager — the navigation affordance for the mobile-portrait viewport-fit layout.
 *
 * A page marked `[data-snap-pager]` becomes a one-viewport-tall scroll-snap container on
 * mobile portrait (see the [data-snap-pager] block in css/modern-ui.css) and each
 * `.app-pane` child fills a screen. CSS does all of the paging; this file only adds what
 * CSS cannot: a set of dots saying how many screens there are and which one you are on,
 * because a snap container with no visible affordance looks exactly like a page that has
 * mysteriously stopped scrolling.
 *
 * Everything here is driven off the COMPUTED `scroll-snap-type`, never off a viewport width
 * guess. The media query in the stylesheet is the single source of truth for when paging is
 * active — if it stops matching (rotate to landscape, resize a desktop window), the computed
 * value goes back to `none` and this tears itself down on the next sync.
 *
 * The panes are rendered by Blazor WASM after an HTTP fetch, and one of them appears and
 * disappears as Advanced diagnostics is toggled, so the pane set cannot be read once at load.
 * A childList MutationObserver watches for it. The callback is a flag flip and a
 * requestAnimationFrame — sync() itself early-exits unless the pane count or the snap state
 * actually changed, which is far less work per Blazor render than Blazor's own diff.
 */
window.appSnapPager = (function () {
    var dots = null;         // the injected dot strip, or null when paging is off
    var buttons = [];
    var panes = [];
    var observer = null;     // IntersectionObserver over the panes
    var mutations = null;
    var scheduled = false;
    var signature = '';

    function isPaging(el) {
        var type = getComputedStyle(el).scrollSnapType;
        return !!type && type !== 'none';
    }

    /** A pane's own heading is a better dot label than "Section 3". */
    function labelFor(pane, index) {
        var heading = pane.querySelector('h1, h2');
        var text = heading && heading.textContent ? heading.textContent.trim() : '';
        return text ? 'Go to ' + text : 'Go to section ' + (index + 1);
    }

    function teardown() {
        if (observer) { observer.disconnect(); observer = null; }
        if (dots && dots.parentNode) { dots.parentNode.removeChild(dots); }
        dots = null;
        buttons = [];
        panes = [];
        signature = '';
    }

    function markCurrent(pane) {
        var index = panes.indexOf(pane);
        if (index < 0) return;
        for (var i = 0; i < buttons.length; i++) {
            var on = i === index;
            buttons[i].classList.toggle('is-current', on);
            if (on) buttons[i].setAttribute('aria-current', 'true');
            else buttons[i].removeAttribute('aria-current');
        }
    }

    function build(pager, found) {
        teardown();
        panes = found;

        dots = document.createElement('div');
        dots.className = 'app-pager-dots';
        // role first: a bare <div> is role=generic and a generic element takes no accessible
        // name, so the aria-label on its own was inert.
        dots.setAttribute('role', 'group');
        dots.setAttribute('aria-label', 'Page sections');

        panes.forEach(function (pane, index) {
            var dot = document.createElement('button');
            dot.type = 'button';
            dot.className = 'app-pager-dot';
            dot.setAttribute('aria-label', labelFor(pane, index));
            dot.addEventListener('click', function () {
                // Honour reduced-motion here as well as in CSS: `scrollIntoView` with
                // `behavior: smooth` is script-driven motion that no stylesheet can suppress.
                var reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                pane.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
            });
            dots.appendChild(dot);
            buttons.push(dot);
        });

        pager.parentNode.insertBefore(dots, pager.nextSibling);

        // `root: pager` because the panes scroll inside the container, not the document.
        // A pane counts as current once most of it is on screen, which for full-height panes
        // in a mandatory-snap container is unambiguous.
        observer = new IntersectionObserver(function (entries) {
            for (var i = 0; i < entries.length; i++) {
                if (entries[i].isIntersecting) markCurrent(entries[i].target);
            }
        }, { root: pager, threshold: 0.55 });

        panes.forEach(function (pane) { observer.observe(pane); });
        markCurrent(panes[0]);
    }

    function sync() {
        var pager = document.querySelector('[data-snap-pager]');
        if (!pager || !isPaging(pager)) { teardown(); return; }

        var found = Array.prototype.slice.call(pager.querySelectorAll(':scope > .app-pane'));
        if (found.length < 2) { teardown(); return; }

        // Cheap idempotence check — this runs behind a MutationObserver, so it must do
        // nothing at all in the overwhelmingly common case where nothing relevant moved.
        var next = found.length + '|' + found.map(function (p) { return p.className; }).join(',');
        if (next === signature) return;
        signature = next;

        build(pager, found);
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(function () { scheduled = false; sync(); });
    }

    function start() {
        if (mutations) return;
        mutations = new MutationObserver(schedule);
        mutations.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('resize', schedule);
        window.addEventListener('orientationchange', schedule);
        schedule();
    }

    return { sync: sync, schedule: schedule, start: start };
})();

/* ═════════════════════════════════════════════════════════════════════════════
   Radzen markup repairs — accessibility only, no visual effect.

   Two defects in what Radzen 10.4.5 renders itself. Neither can be fixed from this
   repository's markup, because in both cases the library emits the wrong thing:

   1. ICON GLYPHS ARE REAL TEXT. Radzen renders an icon as
      `<i class="notranslate rzi">refresh</i>` — the ligature NAME, in the text flow,
      with no aria-hidden. So the accessible name of every Radzen icon button is the
      glyph name followed by its label, measured on the running app as
      "refresh Rescan Azure" and "graphic_eq Listen", and the two /users disclosures
      announce as "timelineSign-ins over time" and "historyMost recent sign-ins".
      Sixteen on "/", four on "/azure", two on "/users".

      The hand-written `<span class="material-symbols-outlined" aria-hidden="true">`
      glyphs in MainLayout were already correct, which is exactly why this went
      unnoticed for so long: it only affects the path that goes through Radzen.

   2. `aria-disabled` IS EMITTED UNEVALUATED. Chart legend items ship
      `aria-disabled="False.ToString().ToLowerInvariant()"` — a literal, from an
      expression that never ran. The value is neither "true" nor "false", so nothing
      can tell whether the item is usable — and a legend item IS a control
      (`role="button"`, `tabindex="0"`, clicking it hides that series).

   WHY A MutationObserver AND NOT 22 CALL SITES. aria-hidden cannot be set from CSS,
   and RadzenButton gives no way to reach the <i> it renders inside itself. Doing this
   per call site — an aria-label on every button, an aria-hidden wrapper span around
   every standalone icon — is 22 places to remember, in markup whose whole point is
   that adding a Radzen control works with no interop hook. This is the same reasoning
   as the delegated click listeners: survive any amount of DOM replacement, and ask
   nothing of the component rendering the markup.

   Cost is bounded: one observer on documentElement, one attribute write per icon, and
   a no-op for any added node carrying neither. Attribute writes do not produce
   childList records, so this cannot feed itself.
   ═════════════════════════════════════════════════════════════════════════════ */
window.appRepairRadzenA11y = (function () {
    'use strict';

    function hideIcons(node) {
        if (node.classList && node.classList.contains('rzi')) {
            node.setAttribute('aria-hidden', 'true');
        }
        if (!node.querySelectorAll) return;
        const icons = node.querySelectorAll('.rzi');
        for (let i = 0; i < icons.length; i++) {
            icons[i].setAttribute('aria-hidden', 'true');
        }
    }

    function fixAriaDisabled(node) {
        if (!node.querySelectorAll) return;
        const candidates = [];
        if (node.hasAttribute && node.hasAttribute('aria-disabled')) candidates.push(node);
        candidates.push.apply(candidates, node.querySelectorAll('[aria-disabled]'));
        for (let i = 0; i < candidates.length; i++) {
            const value = candidates[i].getAttribute('aria-disabled');
            // Only the malformed value is removed. A real "true"/"false" is Radzen
            // (or this app) working correctly and is left exactly as found.
            if (value !== 'true' && value !== 'false') {
                candidates[i].removeAttribute('aria-disabled');
            }
        }
    }

    function repair(node) {
        if (!node || node.nodeType !== 1) return;
        hideIcons(node);
        fixAriaDisabled(node);
    }

    function start() {
        if (!document.body) return;
        // The static SSR payload is already parsed, so sweep it once — the observer
        // below only sees what Blazor adds afterwards.
        repair(document.body);
        new MutationObserver(function (records) {
            for (const record of records) {
                for (const added of record.addedNodes) repair(added);
            }
        }).observe(document.documentElement, { childList: true, subtree: true });
    }

    return { start: start, repair: repair };
})();

/* Self-initialise the topbar. The header is present in the static SSR payload, so
   it exists by the time this script executes; the DOMContentLoaded guard covers the
   case where the script is ever moved into <head>. */
(function bootstrapTopbar() {
    const start = () => {
        window.initTopbarDrawer();
        window.initNavbarScroll();
        window.appSnapPager.start();
        window.appRepairRadzenA11y.start();
    };
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
