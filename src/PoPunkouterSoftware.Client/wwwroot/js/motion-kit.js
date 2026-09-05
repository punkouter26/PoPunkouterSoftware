/**
 * Motion kit — the single frame governor for every animated layer in the app.
 *
 * Before this file, each decorative layer owned a private `requestAnimationFrame` loop, a
 * private 30fps cap, a private `prefers-reduced-motion` listener, a private hidden/blur
 * pause, and the two of them coordinated through a `data-suppress-gpu-backdrop` attribute
 * plus a MutationObserver. That pattern works for exactly two layers. With the
 * audio-reactive backdrop, the glass mask and the catalog physics field it collapses: N
 * independent rAF loops each re-enter the frame callback, each re-read `matchMedia`, and
 * nothing can see the total cost in order to back off.
 *
 * So: ONE rAF loop, a subscriber list, one shared gate, and — the part no per-layer loop
 * could ever do — one adaptive quality controller that measures what the layers actually
 * cost and turns them down when the device cannot afford them.
 *
 * ── The gate ────────────────────────────────────────────────────────────────────────────
 * The loop is fully stopped (cancelAnimationFrame, no callback registered at all — not an
 * opacity fade, not a `return` inside a still-scheduled frame) whenever ANY of these hold:
 *   • `prefers-reduced-motion: reduce` matches
 *   • the tab is hidden
 *   • the window has lost focus
 *   • no subscriber is currently runnable
 * Unlike the loops it replaces, the reduced-motion listener stays registered even when the
 * page loads with the query already matching, so a mid-session opt-OUT starts the layers.
 * gpu-backdrop.js used to return early before registering its listener, which meant a
 * visitor who had reduced-motion on at load could never get the backdrop back.
 *
 * ── Per-subscriber frame caps ───────────────────────────────────────────────────────────
 * Each subscriber declares its own `fps`. The governor owns the clock and calls the
 * callback with a real elapsed `dt` in seconds, so a layer that integrates physics stays
 * correct whether it is served at 30fps, throttled by the quality controller, or resumed
 * after a long pause (dt is clamped — see MAX_DT — so a backgrounded tab does not teleport
 * every particle on the first frame back).
 *
 * ── Adaptive quality ────────────────────────────────────────────────────────────────────
 * The governor times its own dispatch: `cost` is the wall-clock spent inside subscriber
 * callbacks, EMA-smoothed. That is the honest signal. Frame *delta* is not — every
 * subscriber is deliberately capped below display refresh, so delta mostly measures the cap
 * we chose, and it is polluted by whatever else the page is doing. Cost measures us.
 *
 * Sustained cost over budget steps the tier down; a long calm window steps it back up.
 * Hysteresis is deliberately asymmetric (fast down, slow up): a device that just dropped
 * frames is more likely to drop them again than to have gotten faster, and an oscillating
 * render scale is more distracting than a permanently lower one.
 *
 * ── Suppression ─────────────────────────────────────────────────────────────────────────
 * `suppress(name)` / `release(name)` replace the old dataset-attribute + MutationObserver
 * contract between the two backdrops. The catalog's Three.js layer claims exclusivity over
 * the shared gradient layer directly, by name, and the governor simply stops dispatching to
 * the suppressed subscriber. Claims are counted, so two independent claimants releasing in
 * either order cannot resurrect a layer the other still wants suppressed.
 *
 * This file must load BEFORE any layer that subscribes to it, and it has no dependencies.
 */
(function () {
    'use strict';

    // Longest dt a subscriber will ever be handed, in seconds. Resuming from a hidden tab
    // otherwise delivers a multi-second dt that flings every integrated particle out of the
    // frustum in a single step.
    var MAX_DT = 1 / 15;

    // Dispatch-cost budget in milliseconds. All layers together get this much main-thread
    // time per tick before the quality controller starts taking things away. 6ms out of a
    // 33ms (30fps) budget leaves the rest of the frame to Blazor, layout and paint.
    var COST_BUDGET_MS = 6.0;
    var COST_CALM_MS = 3.0;
    var DOWN_TICKS = 45;    // ~1.5s at 30fps before stepping down
    var UP_TICKS = 300;     // ~10s of calm before stepping back up

    // Render scale is deliberately capped well below devicePixelRatio at every tier: these
    // are soft, out-of-focus gradients, so upscaling is free and 1:1 pixels buy nothing.
    var TIERS = [
        { name: 'low', scale: 0.34, particles: 0.25, effects: false },
        { name: 'medium', scale: 0.50, particles: 0.55, effects: false },
        { name: 'high', scale: 0.62, particles: 1.00, effects: true }
    ];

    /**
     * Starting tier. A phone that begins at `high` and falls to `low` has already shown the
     * visitor two seconds of dropped frames, so guess conservatively from the device hints
     * and let the controller earn its way up instead.
     */
    function initialTier() {
        var cores = navigator.hardwareConcurrency || 4;
        var mem = navigator.deviceMemory || 4;
        var coarse = window.matchMedia('(pointer: coarse)').matches;
        if (cores <= 4 || mem <= 4) return 0;
        if (coarse || cores <= 6) return 1;
        return 2;
    }

    /**
     * "Do not run decorative layers on this device at all."
     *
     * The tier ladder above answers HOW WELL to draw; this answers WHETHER TO. They are
     * different questions and the ladder cannot express the second one — its floor is still
     * a running WebGL context, a compositing layer and a per-frame dispatch, and on the
     * hardware below it is exactly wrong to pay any of that for a background gradient.
     *
     * Three signals, each of which is a statement by the device or the visitor rather than a
     * guess about them:
     *
     *   · Save-Data. The visitor has explicitly asked every site to spend less. Fetching
     *     ~600KB of Three.js for a decorative starfield straight past that request is not
     *     defensible, whatever the GPU can manage.
     *   · deviceMemory <= 2 / hardwareConcurrency <= 2. Below the ladder's floor, where a
     *     backdrop competes with the WASM runtime for the only thread that matters.
     *   · A 2G/slow-2G effective connection, for the same reason as Save-Data.
     *
     * Both hint APIs are Chromium-only and `undefined` elsewhere; each check is written so a
     * missing hint reads as "no objection", never as "minimal". The default is to draw.
     */
    function computeMinimal() {
        var conn = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
        if (conn) {
            if (conn.saveData === true) return true;
            var effective = conn.effectiveType || '';
            if (effective === 'slow-2g' || effective === '2g') return true;
        }
        if (typeof navigator.deviceMemory === 'number' && navigator.deviceMemory <= 2) return true;
        if (typeof navigator.hardwareConcurrency === 'number' && navigator.hardwareConcurrency <= 2) return true;
        return false;
    }

    var minimal = computeMinimal();

    var subs = [];
    var nextId = 1;
    var qualityListeners = [];
    var suppressed = Object.create(null);   // name -> claim count

    var tier = initialTier();
    var costEma = 0;
    var overTicks = 0;
    var calmTicks = 0;
    var fpsEma = 0;

    var raf = 0;
    var lastFrame = 0;
    var origin = performance.now();

    var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
    var blurred = false;

    function quality() { return TIERS[tier]; }

    function isRunnable(s) {
        return !s.paused && !suppressed[s.name];
    }

    function anyRunnable() {
        for (var i = 0; i < subs.length; i++) if (isRunnable(subs[i])) return true;
        return false;
    }

    /** The single gate. Every pause/resume signal in the app funnels through this. */
    function gateOpen() {
        return !motion.matches && !document.hidden && !blurred && anyRunnable();
    }

    function emitQuality() {
        var q = quality();
        for (var i = 0; i < qualityListeners.length; i++) {
            try { qualityListeners[i].fn(q); }
            catch (err) { console.warn('motion-kit: quality listener failed', err); }
        }
    }

    function setTier(next) {
        next = Math.max(0, Math.min(TIERS.length - 1, next));
        if (next === tier) return;
        tier = next;
        overTicks = 0;
        calmTicks = 0;
        emitQuality();
    }

    /**
     * Fold this tick's dispatch cost into the controller. Called once per served frame,
     * after every due subscriber has run.
     */
    function govern(cost) {
        costEma = costEma ? costEma * 0.88 + cost * 0.12 : cost;

        if (costEma > COST_BUDGET_MS) {
            calmTicks = 0;
            if (++overTicks >= DOWN_TICKS) setTier(tier - 1);
        } else if (costEma < COST_CALM_MS) {
            overTicks = 0;
            if (++calmTicks >= UP_TICKS) setTier(tier + 1);
        } else {
            overTicks = 0;
            calmTicks = 0;
        }
    }

    function frame(now) {
        raf = requestAnimationFrame(frame);

        var delta = lastFrame ? now - lastFrame : 0;
        lastFrame = now;
        if (delta > 0) fpsEma = fpsEma ? fpsEma * 0.9 + (1000 / delta) * 0.1 : 1000 / delta;

        var t0 = performance.now();
        var served = false;
        var elapsed = (now - origin) / 1000;

        for (var i = 0; i < subs.length; i++) {
            var s = subs[i];
            if (!isRunnable(s)) continue;
            if (now - s.last < s.interval) continue;

            var dt = s.last ? Math.min((now - s.last) / 1000, MAX_DT) : 0;
            s.last = now;
            served = true;
            try {
                s.fn(dt, elapsed, quality());
            } catch (err) {
                // One misbehaving decorative layer must not take the whole governor — and
                // with it every other layer — down. Drop the offender and keep going.
                console.warn('motion-kit: subscriber "' + s.name + '" threw; unsubscribing', err);
                subs.splice(i--, 1);
            }
        }

        if (served) govern(performance.now() - t0);
    }

    function play() {
        if (raf || !gateOpen()) return;
        lastFrame = 0;
        for (var i = 0; i < subs.length; i++) subs[i].last = 0;
        raf = requestAnimationFrame(frame);
    }

    function pause() {
        if (!raf) return;
        cancelAnimationFrame(raf);
        raf = 0;
    }

    /** Re-evaluate the gate after any signal change. The only way the loop starts or stops. */
    function sync() {
        if (gateOpen()) play(); else pause();
    }

    document.addEventListener('visibilitychange', sync);
    window.addEventListener('blur', function () { blurred = true; sync(); });
    window.addEventListener('focus', function () { blurred = false; sync(); });
    // Registered unconditionally, including when the query already matches at load — that
    // is the whole point (see the header note about the early-return bug this replaces).
    if (motion.addEventListener) motion.addEventListener('change', sync);

    window.motionKit = {
        /**
         * Register a per-frame callback.
         * @param {string} name    stable identity, also the handle for suppress()/release()
         * @param {function} fn    fn(dtSeconds, elapsedSeconds, quality)
         * @param {object} [opts]  { fps: 30 }
         * @returns {number} subscription id for unsubscribe()
         */
        subscribe: function (name, fn, opts) {
            opts = opts || {};
            var fps = opts.fps || 30;
            var s = {
                id: nextId++, name: name, fn: fn,
                interval: 1000 / fps, last: 0, paused: false
            };
            subs.push(s);
            sync();
            return s.id;
        },

        unsubscribe: function (id) {
            for (var i = 0; i < subs.length; i++) {
                if (subs[i].id === id) { subs.splice(i, 1); break; }
            }
            sync();
        },

        /** Stop dispatching to `name` until a matching release(). Reference-counted. */
        suppress: function (name) {
            suppressed[name] = (suppressed[name] || 0) + 1;
            sync();
        },

        release: function (name) {
            if (!suppressed[name]) return;
            if (--suppressed[name] <= 0) delete suppressed[name];
            sync();
        },

        isSuppressed: function (name) { return !!suppressed[name]; },

        /** Current tier descriptor: { name, scale, particles, effects }. */
        get quality() { return quality(); },

        /** Subscribe to tier changes; called immediately with the current tier. */
        onQuality: function (fn) {
            var entry = { id: nextId++, fn: fn };
            qualityListeners.push(entry);
            try { fn(quality()); } catch (err) { console.warn('motion-kit: quality listener failed', err); }
            return entry.id;
        },

        offQuality: function (id) {
            for (var i = 0; i < qualityListeners.length; i++) {
                if (qualityListeners[i].id === id) { qualityListeners.splice(i, 1); break; }
            }
        },

        get reduced() { return motion.matches; },
        get running() { return !!raf; },

        /**
         * True when decorative layers should not initialise AT ALL on this device — see
         * computeMinimal(). Layers treat it exactly like `reduced`: skip the download and the
         * context, keep the CSS fallback, do not error. Unlike `reduced` it cannot change
         * mid-session, so there is no listener to match.
         */
        get minimal() { return minimal; },

        /**
         * Diagnostics. Read it in devtools, or from a Playwright assertion that the loop is
         * genuinely stopped rather than merely invisible — `running` false with subscribers
         * present is the observable form of "reduced motion is honoured".
         */
        stats: function () {
            return {
                running: !!raf,
                reduced: motion.matches,
                minimal: minimal,
                hidden: document.hidden,
                blurred: blurred,
                tier: quality().name,
                scale: quality().scale,
                costMs: Math.round(costEma * 100) / 100,
                fps: Math.round(fpsEma),
                subscribers: subs.map(function (s) {
                    return { name: s.name, suppressed: !!suppressed[s.name] };
                })
            };
        }
    };
})();
