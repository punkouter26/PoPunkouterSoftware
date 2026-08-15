/**
 * Catalog-page 3D starfield/globe backdrop — Three.js, rendered into
 * `<canvas id="catalog-starfield">` (Index.razor, catalog page only).
 *
 * This is the deliberately "heavy" decorative layer for `/` — unlike the rest of this
 * trim-conscious app it costs real bundle size, so the cost is scoped as tightly as
 * possible:
 *   • Three.js itself (~600KB) is NEVER declared as a static <script> in the shared host
 *     page. This file (a small wrapper with no THREE dependency at parse time) is the
 *     only thing loaded on every route; it fetches lib/three/three.min.js lazily, and
 *     only `init()` — called from Index.razor's OnAfterRenderAsync — ever triggers that
 *     fetch. Visiting /azure, or any page other than the catalog, never downloads it.
 *   • The global GPU backdrop (js/gpu-backdrop.js) is suppressed for the lifetime of this
 *     layer via `document.documentElement.dataset.suppressGpuBackdrop` (set by
 *     Index.razor, cleared on dispose) so the two WebGL render loops never run
 *     concurrently — see gpu-backdrop.js for the consuming half of that contract.
 *
 * Same cost-discipline patterns as gpu-backdrop.js:
 *   • Internal render resolution capped below the CSS viewport size (STARFIELD_SCALE),
 *     upscaled for free via CSS — no devicePixelRatio multiplication.
 *   • Frame rate capped at 30fps; the rAF loop fully stops (not just visually pauses)
 *     when the tab is hidden, the window loses focus, or reduced-motion matches.
 *   • `failIfMajorPerformanceCaveat: true` rejects software rasterisers.
 *   • Graceful degradation: any failure (script load, context creation, Three.js) leaves
 *     the canvas hidden and logs at most one console.warn — never throws to the caller,
 *     so a failed JS interop call never surfaces as a Blazor error boundary.
 *   • Reads --app-accent-strong / --app-accent-warm so it stays theme-consistent, and
 *     re-samples them on both OS theme change and mid-session reduced-motion opt-in.
 */
(function () {
    'use strict';

    var THREE_SRC = 'lib/three/three.min.js';
    var STARFIELD_SCALE = 0.75;
    var FRAME_MS = 1000 / 30;
    var STAR_COUNT = 1400;
    var GLOBE_RADIUS = 2.35;

    /** Read a CSS custom property and normalise it to [r,g,b] in 0..1. Mirrors the
     *  identical helper in gpu-backdrop.js — kept local rather than shared so the two
     *  cost-controlled layers stay independently deployable. */
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

    var scriptPromise = null;
    /** Lazily inject the vendored Three.js UMD build. Resolves once, cached, so repeat
     *  navigation back to "/" within the same session does not re-fetch. */
    function loadThree() {
        if (window.THREE) return Promise.resolve();
        if (scriptPromise) return scriptPromise;
        scriptPromise = new Promise(function (resolve, reject) {
            var existing = document.querySelector('script[data-lib="three"]');
            if (existing) {
                existing.addEventListener('load', resolve, { once: true });
                existing.addEventListener('error', function () { reject(new Error('three.js failed to load')); }, { once: true });
                return;
            }
            var s = document.createElement('script');
            s.src = THREE_SRC;
            s.dataset.lib = 'three';
            s.onload = resolve;
            s.onerror = function () { reject(new Error('three.js failed to load')); };
            document.head.appendChild(s);
        });
        return scriptPromise;
    }

    // Module-scoped instance state. Rebuilt fresh on every init() so repeated
    // navigation to "/" (Index.razor mounts a brand-new <canvas> each time) never
    // reuses a torn-down THREE.WebGLRenderer.
    var inst = null;
    // Bumped on every init()/dispose() call; guards the async loadThree().then(...)
    // continuation below against resurrecting a render loop after the page has
    // already navigated away (dispose() can land while a lazy reduced-motion
    // opt-in's script fetch is still in flight).
    var currentToken = 0;

    function buildScene(canvas) {
        var gl = canvas.getContext('webgl', {
            alpha: true,
            antialias: false,
            depth: true,
            stencil: false,
            powerPreference: 'low-power',
            failIfMajorPerformanceCaveat: true
        });
        if (!gl) { canvas.dataset.starfield = 'no-webgl'; return null; }

        var renderer;
        try {
            renderer = new THREE.WebGLRenderer({ canvas: canvas, context: gl, antialias: false, alpha: true, powerPreference: 'low-power' });
        } catch (err) {
            console.warn('starfield-backdrop: renderer init failed', err);
            canvas.dataset.starfield = 'renderer-error';
            return null;
        }

        var scene = new THREE.Scene();
        var camera = new THREE.PerspectiveCamera(55, 1, 0.1, 100);
        camera.position.z = 7;

        // Starfield: points scattered through a spherical volume, denser toward the
        // centre so it reads as depth rather than a flat shell.
        var starGeo = new THREE.BufferGeometry();
        var positions = new Float32Array(STAR_COUNT * 3);
        var colors = new Float32Array(STAR_COUNT * 3);
        var warm = new THREE.Color();
        var cool = new THREE.Color();
        var bright = new THREE.Color(1, 1, 1);
        for (var i = 0; i < STAR_COUNT; i++) {
            var radius = 3 + Math.pow(Math.random(), 0.5) * 22;
            var theta = Math.random() * Math.PI * 2;
            var phi = Math.acos(2 * Math.random() - 1);
            var idx = i * 3;
            positions[idx] = radius * Math.sin(phi) * Math.cos(theta);
            positions[idx + 1] = radius * Math.sin(phi) * Math.sin(theta);
            positions[idx + 2] = radius * Math.cos(phi);

            // Mostly cool/neutral points with occasional warm and bright-white ones.
            var pick = Math.random();
            var c = pick < 0.72 ? cool : (pick < 0.92 ? warm : bright);
            colors[idx] = c.r; colors[idx + 1] = c.g; colors[idx + 2] = c.b;
        }
        starGeo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        starGeo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
        var starMat = new THREE.PointsMaterial({
            size: 0.055,
            sizeAttenuation: true,
            vertexColors: true,
            transparent: true,
            opacity: 0.85,
            depthWrite: false,
            blending: THREE.AdditiveBlending
        });
        var stars = new THREE.Points(starGeo, starMat);
        scene.add(stars);

        // Globe: a low-poly wireframe sphere, slowly rotating — reads as an orbiting
        // planet/network without the triangle-fill cost of a shaded sphere.
        var globeGeo = new THREE.EdgesGeometry(new THREE.IcosahedronGeometry(GLOBE_RADIUS, 1));
        var globeMat = new THREE.LineBasicMaterial({ transparent: true, opacity: 0.4 });
        var globe = new THREE.LineSegments(globeGeo, globeMat);
        scene.add(globe);

        function pushColors() {
            var c1 = readColor('--app-accent-strong', [0.357, 0.486, 0.839]);
            var c2 = readColor('--app-accent-warm', [0.945, 0.714, 0.388]);
            cool.setRGB(c1[0], c1[1], c1[2]);
            warm.setRGB(c2[0], c2[1], c2[2]);
            // The globe wireframe is the dominant colour cue and re-tints immediately.
            // Per-star colour identity (cool/warm/bright) is not retained after the
            // initial buffer upload, so a theme flip does not retroactively re-tint
            // already-placed points — an acceptable trade against keeping a second
            // 1400-entry buffer alive just for this. The next full init() (i.e. the
            // next visit to "/") picks up the new palette for stars too.
            globeMat.color.setRGB(c1[0], c1[1], c1[2]);
        }

        function resize() {
            // window.innerWidth/innerHeight rather than canvas.clientWidth/clientHeight —
            // mirrors gpu-backdrop.js's resize() and avoids depending on the scoped
            // stylesheet (which sizes the canvas to the viewport) having already applied
            // layout by the time this first runs.
            var w = Math.max(1, Math.floor(window.innerWidth * STARFIELD_SCALE));
            var h = Math.max(1, Math.floor(window.innerHeight * STARFIELD_SCALE));
            if (canvas.width === w && canvas.height === h) return;
            renderer.setPixelRatio(1);
            renderer.setSize(w, h, false);
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
        }

        return {
            canvas: canvas, gl: gl, renderer: renderer, scene: scene, camera: camera,
            stars: stars, globe: globe, pushColors: pushColors, resize: resize
        };
    }

    function start(canvas) {
        var status = function (s) { canvas.dataset.starfield = s; };

        var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
        var raf = 0;
        var last = 0;
        var built = null;
        var resizeQueued = false;

        var onResize, onVisibility, onBlur, onFocus, onScheme, onMotion;

        function frame(now) {
            raf = requestAnimationFrame(frame);
            if (now - last < FRAME_MS) return;
            var dt = last ? (now - last) / 1000 : 0;
            last = now;
            if (!built) return;
            built.globe.rotation.y += dt * 0.12;
            built.globe.rotation.x += dt * 0.03;
            built.stars.rotation.y += dt * 0.008;
            built.renderer.render(built.scene, built.camera);
        }

        function play() {
            if (motion.matches || document.hidden) return;
            if (!raf) { last = 0; raf = requestAnimationFrame(frame); }
        }

        function pause() {
            if (raf) { cancelAnimationFrame(raf); raf = 0; }
        }

        function teardown() {
            pause();
            window.removeEventListener('resize', onResize);
            document.removeEventListener('visibilitychange', onVisibility);
            window.removeEventListener('blur', onBlur);
            window.removeEventListener('focus', onFocus);
            if (motion.removeEventListener) motion.removeEventListener('change', onMotion);
            var scheme = window.matchMedia('(prefers-color-scheme: dark)');
            if (scheme.removeEventListener) scheme.removeEventListener('change', onScheme);
            if (built) {
                built.stars.geometry.dispose();
                built.stars.material.dispose();
                built.globe.geometry.dispose();
                built.globe.material.dispose();
                built.renderer.dispose();
                var loseCtx = built.gl.getExtension('WEBGL_lose_context');
                if (loseCtx) loseCtx.loseContext();
            }
            built = null;
        }

        try {
            built = buildScene(canvas);
        } catch (err) {
            console.warn('starfield-backdrop: scene build failed', err);
            status('error');
            return { dispose: function () { } };
        }
        if (!built) return { dispose: function () { } };

        built.resize();
        built.pushColors();
        canvas.classList.add('is-ready');
        status('ready');
        play();

        onResize = function () {
            if (resizeQueued) return;
            resizeQueued = true;
            requestAnimationFrame(function () { resizeQueued = false; built && built.resize(); });
        };
        window.addEventListener('resize', onResize, { passive: true });

        onVisibility = function () { if (document.hidden) pause(); else play(); };
        document.addEventListener('visibilitychange', onVisibility);
        onBlur = pause;
        onFocus = play;
        window.addEventListener('blur', onBlur);
        window.addEventListener('focus', onFocus);

        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        onScheme = function () { built && built.pushColors(); };
        if (scheme.addEventListener) scheme.addEventListener('change', onScheme);

        onMotion = function () {
            if (motion.matches) {
                pause();
                canvas.classList.remove('is-ready');
            } else {
                canvas.classList.add('is-ready');
                play();
            }
        };
        if (motion.addEventListener) motion.addEventListener('change', onMotion);

        canvas.addEventListener('webglcontextlost', function (e) {
            e.preventDefault();
            pause();
            status('context-lost');
        });

        return { dispose: teardown };
    }

    /** Fetch three.js (if not already cached) and build the live instance. Guarded by
     *  `token`: if dispose() bumped currentToken while the fetch was in flight (e.g. the
     *  page navigated away during a lazy reduced-motion opt-in), the continuation is a
     *  no-op instead of resurrecting a render loop for a canvas that is already gone. */
    function buildAndStart(canvas, token) {
        return loadThree().then(function () {
            if (token !== currentToken) return;
            inst = start(canvas);
        }).catch(function (err) {
            if (token !== currentToken) return;
            console.warn('starfield-backdrop: could not load three.js', err);
            canvas.dataset.starfield = 'load-error';
        });
    }

    window.starfieldBackdrop = {
        /** Called from Index.razor's OnAfterRenderAsync(firstRender). Idempotent:
         *  disposes any previous instance before building a new one. */
        init: function () {
            currentToken++;
            var token = currentToken;
            if (inst) { inst.dispose(); inst = null; }

            var canvas = document.getElementById('catalog-starfield');
            if (!canvas) return Promise.resolve();

            // Suppress the global gradient-blob backdrop for as long as this layer owns
            // the page — two independent always-on WebGL loops fighting for the same
            // GPU budget is exactly what gpu-backdrop.js's own cost comments rule out.
            document.documentElement.dataset.suppressGpuBackdrop = '1';

            var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
            if (!motion.matches) return buildAndStart(canvas, token);

            // Mirrors gpu-backdrop.js's reduced-motion early exit: skip the three.js
            // fetch and all WebGL cost entirely while the query matches. Unlike
            // gpu-backdrop.js — whose early return also skips registering its own
            // change listener, so a page that loads with reduced-motion already on can
            // never light up later — this keeps one lightweight listener alive so
            // turning reduced-motion off mid-session still starts the layer.
            canvas.dataset.starfield = 'reduced-motion';
            var onOptIn = function () {
                if (motion.matches || token !== currentToken) return;
                if (motion.removeEventListener) motion.removeEventListener('change', onOptIn);
                buildAndStart(canvas, token);
            };
            if (motion.addEventListener) motion.addEventListener('change', onOptIn);
            inst = {
                dispose: function () {
                    if (motion.removeEventListener) motion.removeEventListener('change', onOptIn);
                }
            };
            return Promise.resolve();
        },

        /** Called from Index.razor's DisposeAsync. Stops the render loop, frees the
         *  WebGL context, and un-suppresses the global backdrop for the next page. */
        dispose: function () {
            currentToken++;
            if (inst) { inst.dispose(); inst = null; }
            delete document.documentElement.dataset.suppressGpuBackdrop;
        }
    };
})();
