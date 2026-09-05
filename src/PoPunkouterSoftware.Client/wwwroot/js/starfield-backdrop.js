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
 *   • The global GPU backdrop is suppressed for the lifetime of this layer via
 *     `motionKit.suppress('app-gpu-backdrop')` so the two WebGL loops never both animate.
 *     That used to be a `data-suppress-gpu-backdrop` attribute on <html> which
 *     gpu-backdrop.js watched with a MutationObserver; the claim is now made directly, by
 *     name, and is reference-counted by the governor.
 *   • `failIfMajorPerformanceCaveat: true` rejects software rasterisers.
 *   • Graceful degradation: any failure (script load, context creation, Three.js) leaves
 *     the canvas hidden and logs at most one console.warn — never throws to the caller,
 *     so a failed JS interop call never surfaces as a Blazor error boundary.
 *   • Reads --app-accent-strong / --app-accent-warm so it stays theme-consistent.
 *
 * ── The orbit field (telemetry physics) ─────────────────────────────────────────────────
 * `setTelemetry()` turns the live catalog into a physical system: one body per app,
 * Verlet-ish integrated against a spring toward its own slow orbit around the globe.
 *
 *   healthy      strong spring, tight orbit — the body sits where it belongs
 *   degraded     weak spring plus random impulses — it visibly cannot hold station
 *   unreachable  spring released entirely, outward drift, colour dimmed — it falls away
 *
 * Two constraints made this worth building rather than faking:
 *   • It is DECORATION, and `wwwroot/data/apps.json` remains authoritative for the card
 *     list. A body may drift off screen; the card it corresponds to never moves, never
 *     dims, and never disappears. A probe failure must not remove an app from the
 *     portfolio, and that rule extends to anything a visitor could read as removal.
 *   • It is hand-rolled Verlet, NOT a physics engine. Rapier's WASM build is several
 *     hundred KB — in a file whose entire header is about not shipping bytes to routes
 *     that do not need them. Thirty bodies with one spring each is thirty lines.
 *
 * ── Frame ownership ─────────────────────────────────────────────────────────────────────
 * There is no rAF loop, no frame cap and no visibility/blur/reduced-motion listener in this
 * file any more. js/motion-kit.js owns all of it; this layer is a named subscriber that
 * receives a real `dt` and an audio level, and the governor's quality tier scales the star
 * count. See that file for why one shared loop replaced the per-layer ones.
 */
(function () {
    'use strict';

    var THREE_SRC = 'lib/three/three.min.js';
    var BASE_STARS = 1400;
    var GLOBE_RADIUS = 2.35;

    // Four states, not three. `unknown` exists because the portfolio's third status value is
    // "not-monitored" — no probe covers that app — and neither of the obvious shortcuts is
    // honest: rendering it as healthy claims an observation nobody made, and rendering it as
    // degraded reports a fault from an absence of data. It holds a calm orbit like a healthy
    // body and is simply dimmer, which is the same rule the pinger applies when it declines
    // to count a timeout as either up or down.
    var STATE_UP = 0, STATE_WARN = 1, STATE_DOWN = 2, STATE_UNKNOWN = 3;

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

    // Module-scoped instance state. Rebuilt fresh on every init() so repeated navigation to
    // "/" (Index.razor mounts a brand-new <canvas> each time) never reuses a torn-down
    // THREE.WebGLRenderer.
    var inst = null;
    // Bumped on every init()/dispose(); guards the async loadThree() continuation against
    // resurrecting a render loop after the page has already navigated away.
    var currentToken = 0;
    // Telemetry can arrive before OR after init() — Index.razor loads the catalog
    // asynchronously — so the latest value is held here and applied whenever both exist.
    var pendingTelemetry = '';

    /**
     * Parse the wire format: "Name|state;Name|state;…" with state in up|warn|down|unknown.
     * A single delimited string keeps the interop boundary to primitives, the same
     * discipline every interop call in this app follows — no DTO, nothing for the WASM trim
     * analyzer to reflect over.
     */
    var STATES = { up: STATE_UP, warn: STATE_WARN, down: STATE_DOWN, unknown: STATE_UNKNOWN };

    function parseTelemetry(spec) {
        var out = [];
        if (!spec) return out;
        var parts = spec.split(';');
        for (var i = 0; i < parts.length; i++) {
            if (!parts[i]) continue;
            var kv = parts[i].split('|');
            var state = STATES[kv[1]];
            out.push({ name: kv[0], state: state === undefined ? STATE_UNKNOWN : state });
        }
        return out;
    }

    /**
     * A 32×32 radial-falloff sprite for the orbit points, drawn once into an offscreen 2D
     * canvas. Generated rather than shipped: it is four lines of gradient stops against a
     * PNG that would need a network request, a cache header and a file in wwwroot — the
     * same reasoning that keeps js/audio-kit.js free of .mp3 assets.
     */
    var spriteTexture = null;
    function makeSprite() {
        if (spriteTexture) return spriteTexture;
        var c = document.createElement('canvas');
        c.width = c.height = 32;
        var g = c.getContext('2d');
        var grad = g.createRadialGradient(16, 16, 0, 16, 16, 16);
        grad.addColorStop(0, 'rgba(255,255,255,1)');
        grad.addColorStop(0.35, 'rgba(255,255,255,0.55)');
        grad.addColorStop(1, 'rgba(255,255,255,0)');
        g.fillStyle = grad;
        g.fillRect(0, 0, 32, 32);
        spriteTexture = new THREE.CanvasTexture(c);
        return spriteTexture;
    }

    function buildScene(canvas, starCount) {
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

        // Starfield: points scattered through a spherical volume, denser toward the centre
        // so it reads as depth rather than a flat shell.
        var starGeo = new THREE.BufferGeometry();
        var positions = new Float32Array(starCount * 3);
        var colors = new Float32Array(starCount * 3);
        var warm = new THREE.Color();
        var cool = new THREE.Color();
        var bright = new THREE.Color(1, 1, 1);
        for (var i = 0; i < starCount; i++) {
            var radius = 3 + Math.pow(Math.random(), 0.5) * 22;
            var theta = Math.random() * Math.PI * 2;
            var phi = Math.acos(2 * Math.random() - 1);
            var idx = i * 3;
            positions[idx] = radius * Math.sin(phi) * Math.cos(theta);
            positions[idx + 1] = radius * Math.sin(phi) * Math.sin(theta);
            positions[idx + 2] = radius * Math.cos(phi);

            var pick = Math.random();
            var c = pick < 0.72 ? cool : (pick < 0.92 ? warm : bright);
            colors[idx] = c.r; colors[idx + 1] = c.g; colors[idx + 2] = c.b;
        }
        starGeo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        starGeo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
        var starMat = new THREE.PointsMaterial({
            size: 0.055, sizeAttenuation: true, vertexColors: true,
            transparent: true, opacity: 0.85, depthWrite: false, blending: THREE.AdditiveBlending
        });
        var stars = new THREE.Points(starGeo, starMat);
        scene.add(stars);

        // Globe: a low-poly wireframe sphere, slowly rotating — reads as an orbiting
        // planet/network without the triangle-fill cost of a shaded sphere.
        var globeGeo = new THREE.EdgesGeometry(new THREE.IcosahedronGeometry(GLOBE_RADIUS, 1));
        var globeMat = new THREE.LineBasicMaterial({ transparent: true, opacity: 0.4 });
        var globe = new THREE.LineSegments(globeGeo, globeMat);
        scene.add(globe);

        // Orbit bodies. Allocated lazily at the first setTelemetry() so a visitor who never
        // gets a catalog response pays nothing for them.
        //
        // The sprite map is not optional. A THREE.PointsMaterial with no map rasterises each
        // point as a hard SQUARE, and at the size these need to be readable that renders as
        // a scatter of solid blocks over the catalog — it reads as a rendering fault, not as
        // a constellation. The stars get away with it only because they are 0.055 across.
        var orbitMat = new THREE.PointsMaterial({
            size: 0.22, sizeAttenuation: true, vertexColors: true, map: makeSprite(),
            transparent: true, opacity: 0.95, depthWrite: false, blending: THREE.AdditiveBlending
        });
        var orbitGeo = new THREE.BufferGeometry();
        var orbits = new THREE.Points(orbitGeo, orbitMat);
        orbits.visible = false;
        scene.add(orbits);

        function pushColors() {
            var c1 = readColor('--app-accent-strong', [0.357, 0.486, 0.839]);
            var c2 = readColor('--app-accent-warm', [0.945, 0.714, 0.388]);
            cool.setRGB(c1[0], c1[1], c1[2]);
            warm.setRGB(c2[0], c2[1], c2[2]);
            // The globe wireframe is the dominant colour cue and re-tints immediately.
            // Per-star colour identity is not retained after the initial buffer upload, so a
            // theme flip does not retroactively re-tint already-placed points — an acceptable
            // trade against keeping a second 1400-entry buffer alive just for this.
            globeMat.color.setRGB(c1[0], c1[1], c1[2]);
        }

        function resize(scale) {
            // window.innerWidth/innerHeight rather than clientWidth/clientHeight — avoids
            // depending on the scoped stylesheet having already applied layout.
            var w = Math.max(1, Math.floor(window.innerWidth * scale));
            var h = Math.max(1, Math.floor(window.innerHeight * scale));
            if (canvas.width === w && canvas.height === h) return;
            renderer.setPixelRatio(1);
            renderer.setSize(w, h, false);
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
        }

        return {
            canvas: canvas, gl: gl, renderer: renderer, scene: scene, camera: camera,
            stars: stars, globe: globe, orbits: orbits,
            cool: cool, warm: warm,
            pushColors: pushColors, resize: resize
        };
    }

    function start(canvas) {
        var status = function (s) { canvas.dataset.starfield = s; };

        var quality = window.motionKit ? window.motionKit.quality : { scale: 0.75, particles: 1 };
        var built = null;
        var bodies = [];
        var orbitPos = null;
        var orbitCol = null;
        var sub = 0, qualitySub = 0;
        var resizeQueued = false;
        var onResize;

        try {
            built = buildScene(canvas, Math.max(200, Math.round(BASE_STARS * quality.particles)));
        } catch (err) {
            console.warn('starfield-backdrop: scene build failed', err);
            status('error');
            return { dispose: function () { }, setTelemetry: function () { } };
        }
        if (!built) return { dispose: function () { }, setTelemetry: function () { } };

        /**
         * Build (or rebuild) the orbit field from a telemetry spec. Each body gets a stable
         * orbit derived from its index, so a re-scan that changes only health does not
         * reshuffle the constellation — the visitor sees a body change behaviour, not the
         * whole field re-deal itself.
         */
        function setTelemetry(spec) {
            var apps = parseTelemetry(spec);
            if (!apps.length) { built.orbits.visible = false; bodies = []; return; }

            var n = apps.length;
            if (!orbitPos || orbitPos.length !== n * 3) {
                orbitPos = new Float32Array(n * 3);
                orbitCol = new Float32Array(n * 3);
                built.orbits.geometry.setAttribute('position', new THREE.BufferAttribute(orbitPos, 3));
                built.orbits.geometry.setAttribute('color', new THREE.BufferAttribute(orbitCol, 3));
            }

            var next = [];
            for (var i = 0; i < n; i++) {
                var prev = bodies[i];
                // Golden-angle distribution: evenly spread inclinations without clumping,
                // and deterministic, so the layout is identical between scans.
                var golden = i * 2.399963;
                var body = prev && prev.name === apps[i].name ? prev : {
                    name: apps[i].name,
                    radius: GLOBE_RADIUS + 1.15 + (i % 4) * 0.42,
                    incl: Math.asin(Math.min(0.95, ((i % 7) / 7) * 1.6 - 0.8)),
                    phase: golden % (Math.PI * 2),
                    speed: 0.16 + (i % 5) * 0.026,
                    px: 0, py: 0, pz: 0, vx: 0, vy: 0, vz: 0,
                    fall: 0, seeded: false
                };
                body.state = apps[i].state;
                next.push(body);
            }
            bodies = next;
            built.orbits.visible = true;
        }

        /** Where body `b` belongs right now, if it were perfectly healthy. */
        function orbitTarget(b, t, out) {
            var a = b.phase + t * b.speed;
            var r = b.radius + b.fall;
            out[0] = Math.cos(a) * r;
            out[1] = Math.sin(a) * r * Math.sin(b.incl);
            out[2] = Math.sin(a) * r * Math.cos(b.incl);
        }

        var target = [0, 0, 0];

        function integrate(dt, t, energy) {
            if (!bodies.length || !orbitPos) return;

            for (var i = 0; i < bodies.length; i++) {
                var b = bodies[i];
                orbitTarget(b, t, target);

                if (!b.seeded) {
                    b.px = target[0]; b.py = target[1]; b.pz = target[2];
                    b.seeded = true;
                }

                if (b.state === STATE_DOWN) {
                    // Spring released. The body keeps its angular motion but the orbit radius
                    // grows without bound, so it drifts out of frame — and `fall` is never
                    // reset, so a body that recovers eases back in rather than snapping.
                    b.fall = Math.min(b.fall + dt * 0.55, 14);
                    var k = 0.6;
                    b.vx += (target[0] - b.px) * k * dt;
                    b.vy += (target[1] - b.py) * k * dt;
                    b.vz += (target[2] - b.pz) * k * dt;
                } else {
                    b.fall *= Math.max(0, 1 - dt * 0.8);
                    // Degraded bodies get a weak spring plus random impulses, so they visibly
                    // cannot hold station. Healthy ones are critically damped and look still.
                    var stiff = b.state === STATE_WARN ? 6.0 : 22.0;
                    b.vx += (target[0] - b.px) * stiff * dt;
                    b.vy += (target[1] - b.py) * stiff * dt;
                    b.vz += (target[2] - b.pz) * stiff * dt;
                    if (b.state === STATE_WARN) {
                        var jitter = 2.4 * dt;
                        b.vx += (Math.random() - 0.5) * jitter;
                        b.vy += (Math.random() - 0.5) * jitter;
                        b.vz += (Math.random() - 0.5) * jitter;
                    }
                }

                var settled = b.state === STATE_UP || b.state === STATE_UNKNOWN;
                var damp = Math.max(0, 1 - dt * (settled ? 7.5 : 3.0));
                b.vx *= damp; b.vy *= damp; b.vz *= damp;
                b.px += b.vx * dt; b.py += b.vy * dt; b.pz += b.vz * dt;

                var o = i * 3;
                orbitPos[o] = b.px; orbitPos[o + 1] = b.py; orbitPos[o + 2] = b.pz;

                // Audio brightens the healthy bodies only — a louder page must never make an
                // outage, or an app nobody is watching, look better than it is.
                var lift = b.state === STATE_UP ? 1 + energy * 0.6 : 1;
                var fade = b.state === STATE_DOWN ? Math.max(0.12, 1 - b.fall / 14)
                    : (b.state === STATE_UNKNOWN ? 0.34 : 1);
                var warmth = b.state === STATE_WARN ? 1 : 0;
                var c = warmth ? built.warm : built.cool;
                // Unreachable bodies lose their green/blue channels rather than just dimming:
                // at a glance the field reads as "one of these is a different kind of thing",
                // which a uniform fade does not communicate.
                var chill = b.state === STATE_DOWN ? 0.45 : 1;
                orbitCol[o] = c.r * lift * fade;
                orbitCol[o + 1] = c.g * lift * fade * chill;
                orbitCol[o + 2] = c.b * lift * fade * chill;
            }

            built.orbits.geometry.attributes.position.needsUpdate = true;
            built.orbits.geometry.attributes.color.needsUpdate = true;
        }

        built.resize(quality.scale);
        built.pushColors();
        canvas.classList.add('is-ready');
        status('ready');

        if (pendingTelemetry) setTelemetry(pendingTelemetry);

        // The shared gradient backdrop stands down for as long as this layer is alive: two
        // always-on WebGL loops fighting for the same GPU budget is exactly what the cost
        // comments in gpu-backdrop.js rule out.
        window.motionKit.suppress('app-gpu-backdrop');

        sub = window.motionKit.subscribe('catalog-starfield', function (dt, elapsed) {
            var energy = window.audioKit ? window.audioKit.energy() : 0;
            built.globe.rotation.y += dt * 0.12;
            built.globe.rotation.x += dt * 0.03;
            built.stars.rotation.y += dt * 0.008;
            // The globe is the one element that reacts to sound directly; a subtle scale
            // pulse reads as breathing rather than as a meter.
            var s = 1 + energy * 0.06;
            built.globe.scale.set(s, s, s);
            integrate(dt, elapsed, energy);
            built.renderer.render(built.scene, built.camera);
        }, { fps: 30 });

        qualitySub = window.motionKit.onQuality(function (q) {
            quality = q;
            built.resize(q.scale);
        });

        onResize = function () {
            if (resizeQueued) return;
            resizeQueued = true;
            requestAnimationFrame(function () { resizeQueued = false; built && built.resize(quality.scale); });
        };
        window.addEventListener('resize', onResize, { passive: true });

        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        var onScheme = function () { built && built.pushColors(); };
        if (scheme.addEventListener) scheme.addEventListener('change', onScheme);

        canvas.addEventListener('webglcontextlost', function (e) {
            e.preventDefault();
            if (sub) { window.motionKit.unsubscribe(sub); sub = 0; }
            status('context-lost');
        });

        function teardown() {
            if (sub) { window.motionKit.unsubscribe(sub); sub = 0; }
            if (qualitySub) { window.motionKit.offQuality(qualitySub); qualitySub = 0; }
            window.motionKit.release('app-gpu-backdrop');
            window.removeEventListener('resize', onResize);
            if (scheme.removeEventListener) scheme.removeEventListener('change', onScheme);
            if (built) {
                built.stars.geometry.dispose();
                built.stars.material.dispose();
                built.globe.geometry.dispose();
                built.globe.material.dispose();
                built.orbits.geometry.dispose();
                built.orbits.material.dispose();
                // Material.dispose() does not release the map, and the cached sprite would
                // otherwise outlive the renderer whose context uploaded it.
                if (spriteTexture) { spriteTexture.dispose(); spriteTexture = null; }
                built.renderer.dispose();
                var loseCtx = built.gl.getExtension('WEBGL_lose_context');
                if (loseCtx) loseCtx.loseContext();
            }
            built = null;
            bodies = [];
        }

        return { dispose: teardown, setTelemetry: setTelemetry };
    }

    /** Fetch three.js (if not already cached) and build the live instance. */
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
        /** Called from Index.razor's OnAfterRenderAsync(firstRender). Idempotent. */
        init: function () {
            currentToken++;
            var token = currentToken;
            if (inst) { inst.dispose(); inst = null; }

            var canvas = document.getElementById('catalog-starfield');
            if (!canvas) return Promise.resolve();
            if (!window.motionKit) { canvas.dataset.starfield = 'no-governor'; return Promise.resolve(); }

            // The heaviest optional thing the app does — a lazily fetched ~600KB of Three.js
            // and a second WebGL context — and it is on "/", the route with the LCP that
            // matters. On a device or a connection that has told us to spend less (see
            // motionKit.minimal) it is not drawn at all: no fetch, no context, no listener.
            // Unlike the reduced-motion path below there is nothing to opt back into, because
            // none of these signals change mid-session.
            if (window.motionKit.minimal) {
                canvas.dataset.starfield = 'minimal';
                return Promise.resolve();
            }

            var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
            if (!motion.matches) return buildAndStart(canvas, token);

            // Skip the three.js fetch and all WebGL cost entirely while reduced-motion
            // matches, but keep one lightweight listener alive so turning it off
            // mid-session still starts the layer.
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
                },
                setTelemetry: function () { }
            };
            return Promise.resolve();
        },

        /**
         * Push the live catalog into the orbit field. Format: "Name|state;Name|state;…"
         * with state in up|warn|down. Held until init() completes if it arrives first —
         * Index.razor fetches the catalog and renders the canvas on independent schedules.
         */
        setTelemetry: function (spec) {
            pendingTelemetry = spec || '';
            if (inst && inst.setTelemetry) inst.setTelemetry(pendingTelemetry);
        },

        /** Called from Index.razor's DisposeAsync. */
        dispose: function () {
            currentToken++;
            pendingTelemetry = '';
            if (inst) { inst.dispose(); inst = null; }
        }
    };
})();
