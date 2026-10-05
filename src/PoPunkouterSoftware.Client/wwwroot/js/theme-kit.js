/*
 * Theme preference — the visitor's explicit light/dark choice, and the header toggle
 * that sets it.
 *
 * WHY THIS IS A FILE AND NOT A RADZEN COMPONENT
 * `RadzenTheme` + `RadzenAppearanceToggle` exist, but the app does not use them, and
 * adopting them here would mean two theme authorities: this app's own token block (which
 * paints the page, the cards, the backdrop grid) and Radzen's (which paints every badge,
 * button, chart legend and grid). Radzen's token bridge in modern-ui.css already has to
 * map one onto the other; a second switching mechanism on top of that is a second way
 * for the two to disagree. The choice is applied to both layers from one place here.
 *
 * App.razor scopes Radzen's two sheets with `media="(prefers-color-scheme: light|dark)"`,
 * which CSS cannot override — there is no selector that reaches a stylesheet's media
 * attribute. So the switch is a rewrite of those two `media` attributes: `all` on the
 * chosen sheet, `not all` on the other. When no choice is stored the original media
 * query is restored, and the app follows the OS again.
 *
 * MUST LOAD EARLY — before the splash is dismissed, and before Blazor renders the header.
 * Applies the stored choice synchronously at parse time, then re-applies after every
 * enhanced navigation, which re-inserts a header whose markup hardcodes the OS-following
 * default.
 *
 * A file rather than an inline <script> in App.razor's <head>: the CSP (`script-src 'self'
 * 'wasm-unsafe-eval'`, no 'unsafe-inline') blocks inline script, so the "set the attribute
 * before first paint" version of this is not available. The consequence is a splash that
 * may paint in the OS theme for the few milliseconds before this runs — the alternative
 * was not having the control at all.
 */
(function () {
    'use strict';

    var KEY = 'pops:theme';
    var DENSITY_KEY = 'pops:density';
    var root = document.documentElement;
    var darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

    // Two glyphs, one per state, swapped on the toggle. Both are in the icon-font subset —
    // adding a name here without adding it to SCRIPTS/Build-IconFontSubset.py renders the
    // literal word "dark_mode" in the button (see the icon-font note in CLAUDE.md).
    var ICON = { dark: 'dark_mode', light: 'light_mode' };

    function readDensity() {
        try {
            return localStorage.getItem(DENSITY_KEY) === 'true';
        } catch (e) {
            return false;
        }
    }

    function setDensity(isCompact) {
        try {
            if (isCompact) localStorage.setItem(DENSITY_KEY, 'true');
            else localStorage.removeItem(DENSITY_KEY);
        } catch (e) { }
        applyDensity();
    }

    function applyDensity() {
        if (readDensity()) {
            root.setAttribute('data-compact', 'true');
        } else {
            root.removeAttribute('data-compact');
        }
    }

    function read() {
        try {
            var v = localStorage.getItem(KEY);
            return v === 'light' || v === 'dark' ? v : null;
        } catch (e) {
            // Private-mode / storage-disabled browsers throw on access. No stored choice
            // is the correct reading of "cannot read one".
            return null;
        }
    }

    /** The theme actually in effect: the stored choice, else the OS. */
    function effective() {
        return read() || (darkQuery.matches ? 'dark' : 'light');
    }

    function write(pref) {
        try {
            if (pref) localStorage.setItem(KEY, pref);
            else localStorage.removeItem(KEY);
        } catch (e) { /* preference is then per-page; the theme still applies */ }
    }

    /** Push the current choice into CSS and into Radzen's two theme links. */
    function apply() {
        var pref = read();

        if (pref) root.setAttribute('data-app-theme', pref);
        else root.removeAttribute('data-app-theme');

        var want = effective();
        var links = document.querySelectorAll('link[href*="Radzen.Blazor/css/software"]');
        for (var i = 0; i < links.length; i++) {
            var el = links[i];
            var isDarkSheet = el.getAttribute('href').indexOf('software-dark') !== -1;
            if (!pref) {
                el.setAttribute('media', isDarkSheet
                    ? '(prefers-color-scheme: dark)'
                    : '(prefers-color-scheme: light)');
            } else {
                el.setAttribute('media', (isDarkSheet ? 'dark' : 'light') === want ? 'all' : 'not all');
            }
        }
    }

    /** Push the current choice onto the header toggle. Idempotent; safe after enhanced nav. */
    function syncToggle() {
        var nodes = document.querySelectorAll('[data-theme-toggle]');
        if (!nodes.length) return;
        var pref = read();
        var current = effective();
        var followingSystem = !pref;
        for (var i = 0; i < nodes.length; i++) {
            var el = nodes[i];
            var label = followingSystem
                ? 'Theme follows your system setting. Switch to ' + (current === 'dark' ? 'light' : 'dark') + ' theme'
                : 'Switch to ' + (current === 'dark' ? 'light' : 'dark') + ' theme';
            el.setAttribute('aria-label', label);
            el.setAttribute('title', label);
            // aria-pressed describes a two-state control whose state is ON/OFF, and here it
            // would have to mean "dark is on" — which is a lie while following the system.
            // The label carries the state instead.
            el.removeAttribute('aria-pressed');
            el.setAttribute('data-theme-state', followingSystem ? 'system' : current);
            var icon = el.querySelector('.material-symbols-outlined');
            if (icon) {
                var want = ICON[current];
                if (icon.textContent !== want) icon.textContent = want;
            }
        }
    }

    function set(pref) {
        write(pref);
        apply();
        syncToggle();
        // The GPU backdrop reads accent tokens once; tell it they changed.
        try { document.dispatchEvent(new CustomEvent('app:theme')); } catch (e) { }
    }

    /**
     * Flip the theme. With an `origin` (the toggle that was pressed) and View Transitions
     * support, the new palette wipes out from that point as a growing circle: the browser
     * snapshots the old page, set() runs, and modern-ui.css clips the new snapshot with a
     * circle keyed off the three --theme-reveal-* properties below. Without support, or
     * under reduced motion, it is the same instant flip as before — nothing else differs.
     */
    function toggle(origin) {
        var next = effective() === 'dark' ? 'light' : 'dark';
        var reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (!origin || reduce || !document.startViewTransition) { set(next); return; }

        var x = origin.x, y = origin.y;
        var r = Math.hypot(Math.max(x, window.innerWidth - x), Math.max(y, window.innerHeight - y));
        root.style.setProperty('--theme-reveal-x', x + 'px');
        root.style.setProperty('--theme-reveal-y', y + 'px');
        root.style.setProperty('--theme-reveal-r', r + 'px');
        root.classList.add('is-theme-reveal');
        var vt = document.startViewTransition(function () { set(next); });
        vt.finished.finally(function () { root.classList.remove('is-theme-reveal'); });
    }

    // Delegated rather than bound per element: the header is re-inserted by enhanced
    // navigation, so a direct listener would be lost or double-bound. Same reasoning as the
    // sound toggle in audio-kit.js.
    document.addEventListener('click', function (e) {
        if (!e.target || !e.target.closest) return;
        var btn = e.target.closest('[data-theme-toggle]');
        if (btn) {
            e.preventDefault();
            // The button's centre, not the click point: a keyboard activation reports 0,0.
            var r = btn.getBoundingClientRect();
            toggle({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
        }
    });

    // An OS theme change must be picked up while the visitor is following the system.
    // Not re-applied over an explicit choice — that is the whole point of the choice.
    var onSystemChange = function () { if (!read()) { apply(); syncToggle(); } };
    if (darkQuery.addEventListener) darkQuery.addEventListener('change', onSystemChange);
    else if (darkQuery.addListener) darkQuery.addListener(onSystemChange);

    // Re-apply to a header enhanced navigation has just re-inserted. Debounced through rAF
    // for the same reason audio-kit.js does it — this is a coalescer, not an animation
    // frame, and it keeps a busy render (the 30-second scan streams ~20 updates) from
    // running a full-document query per mutation batch.
    var queued = false;
    new MutationObserver(function () {
        if (queued) return;
        queued = true;
        requestAnimationFrame(function () { queued = false; syncToggle(); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    // Apply before anything else can paint content, then hydrate the toggle.
    apply();
    applyDensity();
    syncToggle();

    window.themeKit = {
        get preference() { return read(); },
        get effective() { return effective(); },
        get isCompact() { return readDensity(); },
        setDensity: setDensity,
        set: set,
        toggle: toggle,
        sync: syncToggle
    };
})();
