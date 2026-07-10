/**
 * GPU backdrop — a single WebGL compositing layer for the whole app.
 *
 * Replaces the 23 per-card `backdrop-filter` roots that previously dominated mobile
 * GPU cost. Two slow-drifting radial fields are blended in a fragment shader and
 * composited once, behind all content.
 *
 * Cost controls, in order of impact:
 *   • Renders at 50% resolution — the output is a soft gradient, so the upscale is free.
 *   • Frame rate capped at 30fps; the loop is fully stopped (no rAF) when the tab is
 *     hidden, when the document loses focus, or when the reduced-motion query matches.
 *   • Fragment shader is branch-free and uses six transcendentals per pixel.
 *
 * Degradation: if WebGL is unavailable the canvas stays hidden and `data-gpu-backdrop`
 * is never set on <html>, so the CSS grid fallback in modern-ui.base.css remains visible.
 */
(function () {
    'use strict';

    var VERT = [
        'attribute vec2 a_pos;',
        'void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }'
    ].join('\n');

    var FRAG = [
        'precision mediump float;',
        'uniform vec2 u_res;',
        'uniform float u_time;',
        'uniform vec3 u_c1;',
        'uniform vec3 u_c2;',
        'void main() {',
        '  vec2 uv = gl_FragCoord.xy / u_res;',
        '  vec2 p = (uv - 0.5) * vec2(u_res.x / u_res.y, 1.0);',
        '  float t = u_time * 0.05;',
        '  vec2 o1 = vec2(sin(t * 1.1) * 0.35, cos(t * 0.9) * 0.25);',
        '  vec2 o2 = vec2(cos(t * 0.7) * -0.40, sin(t * 1.3) * 0.30);',
        '  float d1 = length(p - o1);',
        '  float d2 = length(p - o2);',
        '  float g1 = exp(-d1 * d1 * 3.5);',
        '  float g2 = exp(-d2 * d2 * 4.0);',
        '  float vig = smoothstep(1.15, 0.15, length(p));',
        '  vec3 col = u_c1 * g1 + u_c2 * g2;',
        '  gl_FragColor = vec4(col * vig, (g1 + g2) * vig * 0.55);',
        '}'
    ].join('\n');

    var RENDER_SCALE = 0.5;
    var FRAME_MS = 1000 / 30;

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

    function compile(gl, type, src) {
        var s = gl.createShader(type);
        gl.shaderSource(s, src);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
            console.warn('gpu-backdrop: shader compile failed', gl.getShaderInfoLog(s));
            return null;
        }
        return s;
    }

    function start() {
        var canvas = document.getElementById('app-gpu-backdrop');
        if (!canvas) return;

        // Records why the layer is (not) running. Read it in devtools or in tests:
        // ready | reduced-motion | no-webgl | shader-error | link-error | context-lost
        var status = function (s) { canvas.dataset.gpu = s; };

        var motion = window.matchMedia('(prefers-reduced-motion: reduce)');
        if (motion.matches) { status('reduced-motion'); return; }

        // failIfMajorPerformanceCaveat rejects software rasterisers (SwiftShader): a
        // decorative shader is not worth burning CPU when there is no real GPU.
        var gl = canvas.getContext('webgl', {
            alpha: true,
            antialias: false,
            depth: false,
            stencil: false,
            powerPreference: 'low-power',
            failIfMajorPerformanceCaveat: true
        });
        if (!gl) { status('no-webgl'); return; }

        var vs = compile(gl, gl.VERTEX_SHADER, VERT);
        var fs = compile(gl, gl.FRAGMENT_SHADER, FRAG);
        if (!vs || !fs) { status('shader-error'); return; }

        var prog = gl.createProgram();
        gl.attachShader(prog, vs);
        gl.attachShader(prog, fs);
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
            console.warn('gpu-backdrop: link failed', gl.getProgramInfoLog(prog));
            status('link-error');
            return;
        }
        gl.useProgram(prog);

        var buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        var loc = gl.getAttribLocation(prog, 'a_pos');
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

        var uRes = gl.getUniformLocation(prog, 'u_res');
        var uTime = gl.getUniformLocation(prog, 'u_time');
        var uC1 = gl.getUniformLocation(prog, 'u_c1');
        var uC2 = gl.getUniformLocation(prog, 'u_c2');

        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

        function pushColors() {
            var c1 = readColor('--app-accent-strong', [0.357, 0.486, 0.839]);
            var c2 = readColor('--app-accent-warm', [0.945, 0.714, 0.388]);
            gl.uniform3f(uC1, c1[0], c1[1], c1[2]);
            gl.uniform3f(uC2, c2[0], c2[1], c2[2]);
        }

        function resize() {
            var w = Math.max(1, Math.floor(window.innerWidth * RENDER_SCALE));
            var h = Math.max(1, Math.floor(window.innerHeight * RENDER_SCALE));
            if (canvas.width === w && canvas.height === h) return;
            canvas.width = w;
            canvas.height = h;
            gl.viewport(0, 0, w, h);
            gl.uniform2f(uRes, w, h);
        }

        var raf = 0;
        var last = 0;
        var t0 = performance.now();

        function frame(now) {
            raf = requestAnimationFrame(frame);
            if (now - last < FRAME_MS) return;
            last = now;
            gl.uniform1f(uTime, (now - t0) / 1000);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }

        function play() {
            if (!raf) {
                last = 0;
                raf = requestAnimationFrame(frame);
            }
        }

        function pause() {
            if (raf) {
                cancelAnimationFrame(raf);
                raf = 0;
            }
        }

        resize();
        pushColors();
        document.documentElement.setAttribute('data-gpu-backdrop', '');
        canvas.classList.add('is-ready');
        status('ready');
        play();

        var resizeQueued = false;
        window.addEventListener('resize', function () {
            if (resizeQueued) return;
            resizeQueued = true;
            requestAnimationFrame(function () {
                resizeQueued = false;
                resize();
            });
        }, { passive: true });

        document.addEventListener('visibilitychange', function () {
            if (document.hidden) pause(); else play();
        });
        window.addEventListener('blur', pause);
        window.addEventListener('focus', play);

        // Re-sample the accent tokens when the OS theme flips.
        var scheme = window.matchMedia('(prefers-color-scheme: dark)');
        var onScheme = function () { pushColors(); };
        if (scheme.addEventListener) scheme.addEventListener('change', onScheme);

        // Honour a mid-session reduced-motion opt-in.
        var onMotion = function () {
            if (motion.matches) {
                pause();
                canvas.classList.remove('is-ready');
                document.documentElement.removeAttribute('data-gpu-backdrop');
            } else {
                document.documentElement.setAttribute('data-gpu-backdrop', '');
                canvas.classList.add('is-ready');
                play();
            }
        };
        if (motion.addEventListener) motion.addEventListener('change', onMotion);

        gl.canvas.addEventListener('webglcontextlost', function (e) {
            e.preventDefault();
            pause();
            status('context-lost');
            document.documentElement.removeAttribute('data-gpu-backdrop');
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
