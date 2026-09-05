/**
 * GPU backdrop — the single compositing layer behind the whole app, and the capability
 * ladder that picks how to draw it.
 *
 * This file is the orchestrator only. Pixels belong to the renderers:
 *   js/gfx-webgpu.js   compute-advected particles, lazily fetched, `navigator.gpu` only
 *   js/gfx-webgl.js    WebGL2 (particles + dual-Kawase glass) with a WebGL1 field fallback
 *
 *      WebGPU  →  WebGL2  →  WebGL1  →  the CSS grid in modern-ui.css
 *
 * Each rung degrades to the next on any failure — adapter refusal, missing WebGL2, a
 * shader that will not compile, a lost context. The bottom rung is not a rung at all: if
 * nothing initialises, `data-gpu-backdrop` is never set on <html> and the static CSS grid
 * backdrop stays visible, exactly as before this layer existed.
 *
 * WebGPU is fetched lazily for the same reason Three.js is (see js/starfield-backdrop.js):
 * a browser that cannot use a single line of it should not download it. Here the gate is a
 * feature (`navigator.gpu`) rather than a route.
 *
 * ── What this file owns ─────────────────────────────────────────────────────────────────
 *   • Tier selection and fallback.
 *   • The accent colours, read from CSS custom properties and re-sampled on theme flip, so
 *     the backdrop can never drift from the palette.
 *   • The glass rect set (#6): the on-screen geometry of every `[data-glass]` surface,
 *     collected on scroll/resize and handed to the renderer, which blurs and refracts the
 *     backdrop beneath them. Collected at most once per two frames and skipped entirely
 *     when the signature has not changed — `getBoundingClientRect` is a forced layout, and
 *     doing it per element per frame is exactly the cost this layer exists to avoid.
 *   • The audio coupling (#4): one float per frame, PULLED from js/audio-kit.js. Pull, not
 *     push, so nothing is computed on frames the governor never serves, and the graphics
 *     have no reference to the audio graph.
 *
 * ── What this file no longer owns ───────────────────────────────────────────────────────
 * The rAF loop, the frame cap, the hidden/blur/reduced-motion pauses and the suppression
 * handshake with the catalog starfield all moved to js/motion-kit.js. This layer is now
 * just a named subscriber; the catalog page suppresses it by name rather than by setting a
 * dataset attribute that this file watched with a MutationObserver.
 */
(function () {
    'use strict';

    var MAX_GLASS_RECTS = 24;
    var GLASS_INTERVAL_MS = 66;      // at most ~15 collections/second

    /** Read a CSS custom property and normalise it to [r,g,b] in 0..1. */
    function readColor(name, fallback) {
        var raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        if (!raw) return fallback;
        if (raw[0] === '#') {
            var hex = raw.slice(1);
            if (hex.length === 3) hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
            if (hex.length < 6) return fallback;
            return [
                parseInt(hex.slice(0, 2), 16) / 255,
                parseInt(hex.slice(2, 4), 16) / 255,
                parseInt(hex.slice(4, 6), 16) / 255
            ];
        }
        var m = raw.match(/-?\d+(\.\d+)?/g);
        if (!m || m.length < 3) return fallback;
        return [m[0] / 255, m[1] / 255, m[2] / 255];
    }

    function palette() {
        return [
            readColor('--app-accent-strong', [0.357, 0.486, 0.839]),
            readColor('--app-accent-warm', [0.945, 0.714, 0.388]),
            // The third band reuses the plain `--app-accent` token rather than requiring a
            // new CSS variable — it already exists in both themes, so no theme can forget it.
            readColor('--app-accent', [0.576, 0.706, 0.961])
        ];
    }

    var webgpuScript = null;
    function loadWebgpuModule() {
        if (window.gfxWebgpu) return Promise.resolve(true);
        if (!navigator.gpu) return Promise.resolve(false);
        if (webgpuScript) return webgpuScript;
        webgpuScript = new Promise(function (resolve) {
            var s = document.createElement('script');
            s.src = 'js/gfx-webgpu.js';
            s.dataset.lib = 'gfx-webgpu';
            s.onload = function () { resolve(!!window.gfxWebgpu); };
            s.onerror = function () { resolve(false); };
            document.head.appendChild(s);
        });
        return webgpuScript;
    }

    function start() {
        var canvas = document.getElementById('app-gpu-backdrop');
        if (!canvas) return;

        // Records why the layer is (not) running. Read it in devtools or in tests:
        // webgpu | webgl2 | webgl1 | reduced-motion | no-gpu | context-lost | disposed
        var status = function (s) { canvas.dataset.gpu = s; };

        var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
        var renderer = null;
        var subscription = 0;
        var qualitySub = 0;
        var building = false;
        var token = 0;

        // ── Glass rects ─────────────────────────────────────────────────────────────────
        var glassBuf = new Float32Array(MAX_GLASS_RECTS * 5);
        var glassSig = '';
        var glassAt = 0;

        function collectGlass(nowMs) {
            if (!renderer || !renderer.setGlass) return;
            if (nowMs - glassAt < GLASS_INTERVAL_MS) return;
            glassAt = nowMs;

            var nodes = document.querySelectorAll('[data-glass]');
            var vw = window.innerWidth, vh = window.innerHeight;
            var n = 0, sig = '';
            for (var i = 0; i < nodes.length && n < MAX_GLASS_RECTS; i++) {
                var r = nodes[i].getBoundingClientRect();
                // Off-screen surfaces contribute nothing and would waste an SDF slot that a
                // visible one needs — the cap is 24 and a long catalog has far more cards.
                if (r.bottom < 0 || r.top > vh || r.width < 2 || r.height < 2) continue;
                // Cached per element. getBoundingClientRect reads already-computed layout,
                // but getComputedStyle forces a style resolution, and doing that for every
                // visible surface fifteen times a second was the single largest contributor
                // to the governor's dispatch cost. A corner radius is a design token; it does
                // not change between frames.
                var radius = nodes[i]._glassRadius;
                if (radius === undefined) {
                    radius = parseFloat(getComputedStyle(nodes[i]).borderTopLeftRadius) || 0;
                    nodes[i]._glassRadius = radius;
                }
                glassBuf[n * 5] = r.left + r.width / 2;
                glassBuf[n * 5 + 1] = r.top + r.height / 2;
                glassBuf[n * 5 + 2] = r.width / 2;
                glassBuf[n * 5 + 3] = r.height / 2;
                glassBuf[n * 5 + 4] = Math.min(radius, Math.min(r.width, r.height) / 2);
                sig += (r.left | 0) + ',' + (r.top | 0) + ',' + (r.width | 0) + ',' + (r.height | 0) + ';';
                n++;
            }
            if (sig === glassSig) return;      // nothing moved — skip the upload entirely
            glassSig = sig;
            renderer.setGlass(glassBuf, n, vw, vh);
        }

        // ── Sizing ──────────────────────────────────────────────────────────────────────
        function applySize() {
            if (!renderer) return;
            var q = window.motionKit ? window.motionKit.quality : { scale: 0.5 };
            renderer.resize(
                Math.max(1, Math.floor(window.innerWidth * q.scale)),
                Math.max(1, Math.floor(window.innerHeight * q.scale)));
            glassSig = '';                     // force a re-upload against the new resolution
        }

        // ── Build / teardown ────────────────────────────────────────────────────────────
        function attach(r) {
            renderer = r;
            renderer.setColors(palette());
            if (window.motionKit) {
                renderer.setQuality(window.motionKit.quality);
                qualitySub = window.motionKit.onQuality(function (q) {
                    if (!renderer) return;
                    renderer.setQuality(q);
                    applySize();
                });
            }
            applySize();

            document.documentElement.setAttribute('data-gpu-backdrop', '');
            canvas.classList.add('is-ready');
            status(renderer.tier);

            subscription = window.motionKit.subscribe('app-gpu-backdrop', function (dt, elapsed) {
                var energy = window.audioKit ? window.audioKit.energy() : 0;
                collectGlass(performance.now());
                renderer.frame(dt, elapsed, energy);
            }, { fps: 30 });

            canvas.addEventListener('webglcontextlost', onContextLost);
        }

        function onContextLost(e) {
            e.preventDefault();
            teardown();
            status('context-lost');
        }

        function teardown() {
            token++;
            if (subscription) { window.motionKit.unsubscribe(subscription); subscription = 0; }
            if (qualitySub) { window.motionKit.offQuality(qualitySub); qualitySub = 0; }
            canvas.removeEventListener('webglcontextlost', onContextLost);
            if (renderer) { try { renderer.dispose(); } catch (err) { } renderer = null; }
            canvas.classList.remove('is-ready');
            document.documentElement.removeAttribute('data-gpu-backdrop');
        }

        /**
         * Walk the ladder. Deliberately deferred while reduced-motion matches: building a
         * GPU context, compiling shaders and allocating particle buffers for a layer that
         * is never going to draw a frame is pure waste, so construction waits for the
         * opt-out rather than merely pausing afterwards.
         */
        function build() {
            if (renderer || building) return;
            if (motion.matches) { status('reduced-motion'); return; }
            if (!window.motionKit) { status('no-gpu'); return; }

            building = true;
            var mine = ++token;

            loadWebgpuModule().then(function (haveWebgpu) {
                if (mine !== token) return null;
                if (!haveWebgpu) return null;
                return window.gfxWebgpu.create(canvas).catch(function (err) {
                    console.warn('gpu-backdrop: WebGPU init failed, falling back', err);
                    return null;
                });
            }).then(function (r) {
                if (mine !== token) { if (r) r.dispose(); return; }
                if (r) { building = false; attach(r); return; }

                // WebGPU declined. A canvas can only ever have one context type, so if
                // `getContext('webgpu')` was reached at all it has poisoned this element for
                // WebGL — hence the swap rather than reusing it.
                //
                // `canvas` is reassigned BEFORE anything else touches it: `status()` closes
                // over this variable, and writing the outcome to the pre-swap node put the
                // whole diagnostic on an element that had already been removed from the DOM.
                // Every reader — devtools, the E2E tier assertion — then saw no state at all.
                canvas = replaceCanvas();
                var gl = window.gfxWebgl ? window.gfxWebgl.create(canvas) : null;
                building = false;
                if (!gl) { status('no-gpu'); return; }
                attach(gl);
            });
        }

        /**
         * Replace the canvas element in place. `getContext` is sticky per element: once a
         * canvas has been offered to WebGPU it cannot yield a WebGL context, even if the
         * WebGPU request failed. Swapping the node is the only way back down the ladder.
         */
        function replaceCanvas() {
            var fresh = canvas.cloneNode(false);
            canvas.parentNode.replaceChild(fresh, canvas);
            return fresh;
        }

        // ── Signals ─────────────────────────────────────────────────────────────────────
        var resizeQueued = false;
        window.addEventListener('resize', function () {
            if (resizeQueued) return;
            resizeQueued = true;
            requestAnimationFrame(function () { resizeQueued = false; applySize(); });
        }, { passive: true });

        // Scroll only invalidates the glass geometry; the collector itself is rate-limited
        // and signature-guarded, so this listener costs a single timestamp comparison.
        window.addEventListener('scroll', function () { glassAt = 0; }, { passive: true });

        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        if (scheme.addEventListener) {
            scheme.addEventListener('change', function () { if (renderer) renderer.setColors(palette()); });
        }

        // Registered whether or not the query matches at load — the previous version
        // returned early before reaching this line, so a visitor who arrived with
        // reduced-motion on could never get the backdrop back by turning it off.
        if (motion.addEventListener) {
            motion.addEventListener('change', function () {
                if (motion.matches) { teardown(); status('reduced-motion'); }
                else build();
            });
        }

        build();

        // Diagnostics for devtools and the E2E assertions.
        window.gpuBackdrop = {
            get tier() { return renderer ? renderer.tier : null; },
            get status() { return canvas.dataset.gpu || null; },
            get glassRects() { return glassSig ? glassSig.split(';').length - 1 : 0; }
        };
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
