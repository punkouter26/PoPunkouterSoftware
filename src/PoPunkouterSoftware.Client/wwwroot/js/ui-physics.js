/**
 * ui-physics — physics applied to the DOM itself, not to a canvas.
 *
 * Three effects, all wired by delegation or observation so no Blazor component needs an
 * interop call, an OnAfterRender hook or a disposal path — the same contract audio-kit.js
 * follows for `[data-sfx]`:
 *
 *   SHATTER   a click on `[data-fx-shatter]` breaks the enclosing card (the attribute's
 *             value is the selector; default `article`) into Voronoi shards that fall under
 *             gravity, spin and bounce off the bottom of the viewport. The shards are CLONES
 *             of the card cut with `clip-path: polygon()`, so they carry its real pixels —
 *             a shader cannot read the page, which is why this is not a GPU effect.
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
 * so an idle page holds no subscription and the governor's gate stays closed. Shard paths
 * are integrated up front and handed to the Web Animations API as keyframes: the browser
 * interpolates them off the main thread, and no rAF loop is added. Reduced motion and
 * `motionKit.minimal` skip all three; the page is identical without them.
 */
(function () {
    'use strict';

    var reduce = window.matchMedia('(prefers-reduced-motion: reduce)');
    function off() { return reduce.matches || !window.motionKit || window.motionKit.minimal; }

    // ── Shatter ─────────────────────────────────────────────────────────────────────────

    /** Keep the part of `poly` on the near side of the bisector between `a` and `b`. */
    function clip(poly, a, b) {
        var mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2, nx = b[0] - a[0], ny = b[1] - a[1];
        var out = [];
        for (var i = 0; i < poly.length; i++) {
            var p = poly[i], q = poly[(i + 1) % poly.length];
            var dp = (p[0] - mx) * nx + (p[1] - my) * ny, dq = (q[0] - mx) * nx + (q[1] - my) * ny;
            if (dp <= 0) out.push(p);
            if ((dp < 0) !== (dq < 0)) {
                var t = dp / (dp - dq);
                out.push([p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t]);
            }
        }
        return out;
    }

    /** Voronoi cells of `n` random sites in a w×h box: n² half-plane clips, n ≤ 12. */
    function voronoi(w, h, n) {
        var sites = [], cells = [];
        for (var i = 0; i < n; i++) sites.push([Math.random() * w, Math.random() * h]);
        for (var s = 0; s < n; s++) {
            var poly = [[0, 0], [w, 0], [w, h], [0, h]];
            for (var o = 0; o < n && poly.length; o++) if (o !== s) poly = clip(poly, sites[s], sites[o]);
            if (poly.length > 2) cells.push(poly);
        }
        return cells;
    }

    /**
     * Integrate one shard: gravity, spin, a lossy bounce off the viewport floor. 60Hz steps,
     * sampled every third into keyframes. ponytail: shards pass through each other — a
     * shard-shard collision pass would be O(n²) per step for an effect lasting 1.3s.
     */
    function trajectory(cx, cy, w, h, floor) {
        var x = 0, y = 0, a = 0;
        var vx = (cx - w / 2) * 2.4 + (Math.random() - 0.5) * 160;
        var vy = -(200 + Math.random() * 280) + (cy - h / 2) * 1.2;
        var va = (Math.random() - 0.5) * 9;
        var frames = [];
        for (var i = 0; i <= 78; i++) {
            if (i % 3 === 0) {
                frames.push({
                    transform: 'translate(' + x.toFixed(1) + 'px,' + y.toFixed(1) + 'px) rotate(' + a.toFixed(3) + 'rad)',
                    opacity: i < 50 ? 1 : 1 - (i - 50) / 28
                });
            }
            var dt = 1 / 60;
            vy += 2400 * dt;
            x += vx * dt; y += vy * dt; a += va * dt;
            if (cy + y > floor && vy > 0) { y = floor - cy; vy *= -0.35; vx *= 0.7; va *= 0.6; }
        }
        return frames;
    }

    function shatter(el) {
        if (off() || !el.parentElement) return;
        var w = el.offsetWidth, h = el.offsetHeight;
        if (w < 8 || h < 8) return;
        var top = el.getBoundingClientRect().top;
        var floor = window.innerHeight - top - 8;     // viewport bottom, in card-local px
        var cells = voronoi(w, h, Math.min(12, Math.max(6, Math.round(w * h / 9000))));
        var shards = [];
        cells.forEach(function (poly) {
            var cx = 0, cy = 0;
            poly.forEach(function (p) { cx += p[0]; cy += p[1]; });
            cx /= poly.length; cy /= poly.length;
            var s = el.cloneNode(true);
            s.removeAttribute('id');
            s.querySelectorAll('[id]').forEach(function (n) { n.removeAttribute('id'); });
            s.setAttribute('aria-hidden', 'true');
            s.inert = true;
            s.classList.add('app-shard');
            s.style.cssText += ';position:absolute;margin:0;pointer-events:none;z-index:5;'
                + 'left:' + el.offsetLeft + 'px;top:' + el.offsetTop + 'px;width:' + w + 'px;height:' + h + 'px;'
                + 'transform-origin:' + cx + 'px ' + cy + 'px;'
                + 'clip-path:polygon(' + poly.map(function (p) { return p[0].toFixed(1) + 'px ' + p[1].toFixed(1) + 'px'; }).join(',') + ')';
            // Inserted as a sibling so the clone stays inside the page's scoped-CSS ancestry
            // (`.azure-ops-page ::deep …` only matches under the page root).
            el.parentElement.appendChild(s);
            shards.push(s);
            s.animate(trajectory(cx, cy, w, h, floor), { duration: 1300, easing: 'linear', fill: 'forwards' });
        });
        // The real card hides while its shards fall. If the server refuses the snooze the
        // card was never removed, and it simply reappears.
        el.style.visibility = 'hidden';
        setTimeout(function () {
            shards.forEach(function (s) { s.remove(); });
            if (el.isConnected) el.style.visibility = '';
        }, 1350);
    }

    // Capture phase: runs before Blazor's own click handler, while the card is still whole.
    document.addEventListener('click', function (e) {
        var t = e.target && e.target.closest && e.target.closest('[data-fx-shatter]');
        if (!t) return;
        var card = t.closest(t.getAttribute('data-fx-shatter') || 'article');
        if (card) shatter(card);
    }, true);

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

    window.uiPhysics = { shatter: shatter };
})();
