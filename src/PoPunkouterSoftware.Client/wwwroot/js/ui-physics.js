/**
 * ui-physics — physics applied to the DOM itself, not to a canvas.
 *
 * Two effects, both wired by delegation or observation so no Blazor component needs an
 * interop call, an OnAfterRender hook or a disposal path — the same contract audio-kit.js
 * follows for `[data-sfx]`:
 *
 *   ODOMETER  a KPI value (`.app-kpis strong`) counts up to its number on a damped spring
 *             the first time it appears. Only the text node's value changes; if Blazor
 *             writes the node mid-count the count stops and Blazor's value stands.
 *   TILT      a catalogue card leans toward a mouse pointer on an underdamped spring, so it
 *             wobbles into place rather than tracking rigidly.
 *
 * Bars springing into place (MetricBars) are CSS — a `linear()` spring curve on a keyframe
 * in modern-ui.css — because a native animation needs no script at all.
 *
 * Every per-frame effect is a named motionKit subscriber that unsubscribes when it settles,
 * so an idle page holds no subscription and the governor's gate stays closed. Reduced motion
 * and `motionKit.minimal` skip both; the page is identical without them.
 */
(function () {
    'use strict';

    var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    function off() { return reduce.matches || !window.motionKit || window.motionKit.minimal; }

    // ── Odometer ────────────────────────────────────────────────────────────────────────
    var odos = [], odoSub = 0;

    function format(v, o) {
        var s = v.toFixed(o.dec);
        if (o.commas) s = s.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
        return o.pre + s + o.post;
    }

    function tickOdos(dt) {
        for (var i = 0; i < odos.length; i++) {
            var o = odos[i];
            // Blazor rewrote the node: its value is the truth, stop counting.
            if (o.node.nodeValue !== o.last || !o.node.isConnected) { odos.splice(i--, 1); continue; }
            o.t += dt;
            var p = Math.min(1, o.t / 0.9);
            var e = 1 - Math.exp(-6 * p) * Math.cos(9 * p);
            o.last = o.node.nodeValue = p >= 1 ? o.final : format(o.target * e, o);
            if (p >= 1) odos.splice(i--, 1);
        }
        if (!odos.length && odoSub) { window.motionKit.unsubscribe(odoSub); odoSub = 0; }
    }

    function startOdo(el) {
        var node = el.firstChild;
        if (!node || node.nodeType !== 3 || node.nextSibling) return;
        var m = /^(\D*?)(-?[\d,]*\.?\d+)(.*)$/.exec(node.nodeValue);
        if (!m) return;
        var target = parseFloat(m[2].replace(/,/g, ''));
        if (!isFinite(target) || target === 0) return;
        var r = el.getBoundingClientRect();
        if (r.bottom < 0 || r.top > window.innerHeight) return;   // nobody would see it count
        odos.push({
            node: node, pre: m[1], post: m[3], target: target, final: node.nodeValue,
            dec: (m[2].split('.')[1] || '').length, commas: m[2].indexOf(',') >= 0,
            t: 0, last: node.nodeValue
        });
        if (!odoSub) odoSub = window.motionKit.subscribe('ui-odometer', tickOdos, { fps: 60 });
    }

    function scanOdos() {
        if (off()) return;
        var els = document.querySelectorAll('.app-kpis strong:not([data-odo])');
        for (var i = 0; i < els.length; i++) { els[i].setAttribute('data-odo', ''); startOdo(els[i]); }
    }

    // ── Tilt ────────────────────────────────────────────────────────────────────────────
    var tilt = { el: null, tx: 0, ty: 0, x: 0, y: 0, vx: 0, vy: 0, sub: 0 };

    function tickTilt(dt) {
        // Underdamped (ζ ≈ 0.5): it overshoots once and settles — a wobble, not a lag.
        var k = 170, c = 13;
        tilt.vx += ((tilt.tx - tilt.x) * k - tilt.vx * c) * dt;
        tilt.vy += ((tilt.ty - tilt.y) * k - tilt.vy * c) * dt;
        tilt.x += tilt.vx * dt; tilt.y += tilt.vy * dt;
        var el = tilt.el;
        if (!el) return;
        el.style.setProperty('--tilt-x', tilt.x.toFixed(2) + 'deg');
        el.style.setProperty('--tilt-y', tilt.y.toFixed(2) + 'deg');
        var settled = Math.abs(tilt.x) + Math.abs(tilt.y) + Math.abs(tilt.vx) + Math.abs(tilt.vy) < 0.02;
        if (settled && tilt.tx === 0 && tilt.ty === 0) release();
    }

    function release() {
        if (tilt.el) {
            tilt.el.classList.remove('is-tilting');
            tilt.el.style.removeProperty('--tilt-x');
            tilt.el.style.removeProperty('--tilt-y');
        }
        tilt.el = null;
        tilt.x = tilt.y = tilt.vx = tilt.vy = tilt.tx = tilt.ty = 0;
        if (tilt.sub) { window.motionKit.unsubscribe(tilt.sub); tilt.sub = 0; }
    }

    document.addEventListener('pointermove', function (e) {
        if (e.pointerType !== 'mouse' || off()) return;
        var card = e.target && e.target.closest && e.target.closest('.app-portfolio-card');
        if (!card) { tilt.tx = tilt.ty = 0; return; }        // spring home, then release
        if (card !== tilt.el) {
            release();
            tilt.el = card;
            card.classList.add('is-tilting');
        }
        var r = card.getBoundingClientRect();
        tilt.ty = ((e.clientX - r.left) / r.width - 0.5) * 7;
        tilt.tx = -((e.clientY - r.top) / r.height - 0.5) * 7;
        if (!tilt.sub) tilt.sub = window.motionKit.subscribe('ui-tilt', tickTilt, { fps: 60 });
    }, { passive: true });

    // ── Wiring ──────────────────────────────────────────────────────────────────────────
    // One observer for the odometer: KPI tiles arrive whenever the WASM page finishes its
    // fetch, which no navigation event announces. The callback is a single selector query.
    function boot() {
        scanOdos();
        new MutationObserver(scanOdos).observe(document.body, { childList: true, subtree: true });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true });
    else boot();
})();
