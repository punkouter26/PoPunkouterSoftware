/**
 * gfx-webgl — the WebGL2 (and WebGL1 fallback) renderer behind #app-gpu-backdrop.
 *
 * Loaded by js/gpu-backdrop.js, which owns tier selection, the colour tokens, the glass
 * rect collection and the motion-kit subscription. This file owns pixels only. It exposes
 * one factory, `gfxWebgl.create(canvas)`, returning the renderer contract that
 * gpu-backdrop.js drives (resize / setColors / setQuality / setGlass / frame / dispose).
 *
 * ── WebGL2 pipeline ─────────────────────────────────────────────────────────────────────
 *   1. FIELD     fullscreen → sceneFBO.  Aurora blobs + 3-octave curl-noise flow, dithered.
 *   2. PARTICLES transform feedback advects N points through the same curl field; drawn as
 *                additive soft sprites into sceneFBO.
 *   3. DOWN/UP   dual-Kawase: sceneFBO → half-res → back up. Two 4-tap passes total.
 *   4. COMPOSITE sceneFBO + blur + glass mask → canvas, with edge refraction and dither.
 *
 * ── Why dual-Kawase and not `backdrop-filter` ───────────────────────────────────────────
 * The 23 per-card `backdrop-filter` roots this layer replaced were the single largest
 * mobile GPU cost in the app, because each one forces its own backdrop snapshot and blur.
 * Dual-Kawase is 8 texture taps total, at quarter the pixels, for the WHOLE page — cheaper
 * than one native blur root, and it composites in the same pass that already runs. The
 * result is a genuinely frosted backdrop behind translucent surfaces rather than a flat
 * tint pretending to be glass.
 *
 * ── Why transform feedback and not a ping-pong FBO ──────────────────────────────────────
 * GPGPU-on-textures needs EXT_color_buffer_float to be renderable, which a meaningful slice
 * of mobile GL drivers do not expose. Transform feedback is core WebGL2, needs no float
 * render target, and keeps particle state in a plain vertex buffer.
 *
 * ── Banding ─────────────────────────────────────────────────────────────────────────────
 * A full-viewport gradient quantised to 8 bits bands visibly — it was the most obvious
 * defect in the previous shader. Both the field pass and the composite apply an ordered
 * dither of ±0.5/255 from a cheap hash. This costs three ALU ops and removes the artefact
 * completely; without it the curl-noise upgrade below would only have made it more obvious.
 *
 * ── WebGL1 fallback ─────────────────────────────────────────────────────────────────────
 * Field pass only, straight to the canvas: no FBOs, no particles, no glass. Same visual
 * identity, none of the cost. It exists so that a driver without WebGL2 still gets a
 * backdrop rather than the flat CSS grid.
 *
 * Simplex noise is Ashima Arts / Stefan Gustavson's public-domain `snoise` (webgl-noise),
 * inlined rather than imported — it is 20 lines and this file has no build step.
 */
(function () {
    'use strict';

    var MAX_GLASS_RECTS = 24;
    var BASE_PARTICLES = 20000;

    // ── Shared GLSL ─────────────────────────────────────────────────────────────────────

    var SNOISE = `
    vec3 mod289(vec3 x){return x-floor(x*(1.0/289.0))*289.0;}
    vec2 mod289(vec2 x){return x-floor(x*(1.0/289.0))*289.0;}
    vec3 permute(vec3 x){return mod289(((x*34.0)+1.0)*x);}
    float snoise(vec2 v){
      const vec4 C=vec4(0.211324865,0.366025403,-0.577350269,0.024390243);
      vec2 i=floor(v+dot(v,C.yy));
      vec2 x0=v-i+dot(i,C.xx);
      vec2 i1=(x0.x>x0.y)?vec2(1.0,0.0):vec2(0.0,1.0);
      vec4 x12=x0.xyxy+C.xxzz; x12.xy-=i1;
      i=mod289(i);
      vec3 p=permute(permute(i.y+vec3(0.0,i1.y,1.0))+i.x+vec3(0.0,i1.x,1.0));
      vec3 m=max(0.5-vec3(dot(x0,x0),dot(x12.xy,x12.xy),dot(x12.zw,x12.zw)),0.0);
      m=m*m; m=m*m;
      vec3 x=2.0*fract(p*C.www)-1.0;
      vec3 h=abs(x)-0.5;
      vec3 ox=floor(x+0.5);
      vec3 a0=x-ox;
      m*=1.79284291400159-0.85373472095314*(a0*a0+h*h);
      vec3 g;
      g.x=a0.x*x0.x+h.x*x0.y;
      g.yz=a0.yz*x12.xz+h.yz*x12.yw;
      return 130.0*dot(m,g);
    }`;

    // Curl of a scalar potential built from three octaves. Divergence-free by construction,
    // which is what makes the particles swirl instead of piling up in sinks.
    var CURL = `
    float potential(vec2 p, float t){
      return snoise(p * 1.0 + vec2(t * 0.10, 0.0)) * 1.00
           + snoise(p * 2.1 + vec2(0.0, t * 0.14)) * 0.45
           + snoise(p * 4.3 - vec2(t * 0.07, t * 0.05)) * 0.20;
    }
    vec2 curl(vec2 p, float t){
      float e = 0.035;
      float dx = potential(p + vec2(e, 0.0), t) - potential(p - vec2(e, 0.0), t);
      float dy = potential(p + vec2(0.0, e), t) - potential(p - vec2(0.0, e), t);
      return vec2(dy, -dx) / (2.0 * e);
    }`;

    // ±0.5/255 of triangular-ish noise. Enough to break 8-bit gradient banding, far below
    // the visible-grain threshold.
    var DITHER = `
    float hash12(vec2 p){
      vec3 p3 = fract(vec3(p.xyx) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }
    vec3 dither(vec3 c, vec2 fragCoord){
      return c + (hash12(fragCoord) - 0.5) / 255.0;
    }`;

    // The aurora identity of the original layer, preserved deliberately: two drifting round
    // blobs plus a sheared band. The curl-noise term modulates them rather than replacing
    // them, so this reads as the same backdrop with more life in it, not a different app.
    var FIELD_BODY = `
      vec2 uv = gl_FragCoord.xy / u_res;
      vec2 p = (uv - 0.5) * vec2(u_res.x / u_res.y, 1.0);
      float t = u_time * 0.05;

      // curl() returns a true derivative (the finite difference is divided by 2*epsilon),
      // so its magnitude is roughly 7 for this 3-octave potential — NOT the ~1 an amplitude
      // reads like. The visible half-extent of p is 0.72, so a multiplier of 0.085 displaces
      // every sample point clean outside its own gaussian falloff and the entire field
      // evaluates to zero. It fails silently and uniformly: no error, no artefact, just a
      // layer that composites to nothing. Keep this term small enough to WARP the blobs
      // rather than teleport them.
      vec2 flow = curl(p * 1.35, u_time * 0.35) * 0.012;
      vec2 pf = p + flow;

      vec2 o1 = vec2(sin(t * 1.1) * 0.35, cos(t * 0.9) * 0.25);
      vec2 o2 = vec2(cos(t * 0.7) * -0.40, sin(t * 1.3) * 0.30);
      float d1 = length(pf - o1);
      float d2 = length(pf - o2);
      float g1 = exp(-d1 * d1 * 3.5);
      float g2 = exp(-d2 * d2 * 4.0);

      float t3 = u_time * 0.021;
      vec2 o3 = vec2(sin(t3 * 0.8) * 0.55, cos(t3 * 0.5) * 0.18 - 0.08);
      vec2 p3 = pf - o3;
      p3.x += p3.y * 0.4;
      float d3 = length(p3 * vec2(2.8, 1.0));
      float g3 = exp(-d3 * d3 * 2.2) * 0.65;

      // Fine filament structure, only where the blobs already are, so it never turns the
      // backdrop into visible noise on a flat area.
      float fil = snoise(pf * 5.5 + flow * 6.0 + vec2(0.0, u_time * 0.06));
      float mask = g1 + g2 + g3;
      float detail = fil * 0.16 * mask;

      // Audio coupling: output level brightens the field and widens the vignette slightly.
      // Bounded at +35% so a loud sonification cannot wash the page out.
      float pulse = 1.0 + u_energy * 0.35;

      // 1.0 - smoothstep(lo, hi, x), NOT smoothstep(hi, lo, x). Passing edge0 > edge1 is
      // explicitly undefined in both GLSL and WGSL, and it does not fail loudly — the
      // shader compiles, the layer reports itself healthy, and the driver is free to return
      // zero for every pixel. The original blob shader was written the reversed way, which
      // is why this whole layer composited to very nearly nothing; it went unnoticed because
      // an opaque background on <body> was painting over the canvas anyway (see modern-ui.css).
      float vig = 1.0 - smoothstep(0.15, 1.15 + u_energy * 0.06, length(p));
      // 0.42 / 0.30, not 1.0 / 0.55. Three overlapping gaussians already sum past 1
      // where they meet, and the additive particle pass lands on top of that — the centre
      // of the viewport clipped to pure white and the whole layer read as fog rather than
      // as an aurora behind the page. Headroom here is what keeps the particles legible.
      vec3 col = (u_c1 * g1 + u_c2 * g2 + u_c3 * g3) * pulse * 0.42 + u_c3 * detail * 0.5;
      float a = (mask + detail) * vig * 0.30;`;

    var VERT2 = `#version 300 es
    in vec2 a_pos;
    void main(){ gl_Position = vec4(a_pos, 0.0, 1.0); }`;

    var FIELD_FS2 = `#version 300 es
    precision highp float;
    uniform vec2 u_res; uniform float u_time; uniform float u_energy;
    uniform vec3 u_c1, u_c2, u_c3;
    out vec4 fragColor;
    ${SNOISE}
    ${CURL}
    ${DITHER}
    void main(){
      ${FIELD_BODY}
      fragColor = vec4(dither(col * vig, gl_FragCoord.xy), a);
    }`;

    // ── Particles (WebGL2 only) ─────────────────────────────────────────────────────────

    var PARTICLE_UPDATE_VS = `#version 300 es
    precision highp float;
    in vec2 a_pos; in vec2 a_vel; in float a_seed;
    out vec2 v_pos; out vec2 v_vel; out float v_seed;
    uniform float u_dt; uniform float u_time; uniform float u_aspect; uniform float u_energy;
    ${SNOISE}
    ${CURL}
    void main(){
      vec2 f = curl(a_pos * 1.6, u_time * 0.35);
      // Audio raises the drive term, so the field visibly accelerates on a loud transient.
      float drive = 0.18 + u_energy * 0.35;
      vec2 vel = a_vel * 0.94 + f * drive * u_dt;
      vec2 pos = a_pos + vel * u_dt;

      // Toroidal wrap in aspect-corrected clip space. Respawning instead would make the
      // density visibly pulse; wrapping keeps the field statistically stationary.
      vec2 lim = vec2(u_aspect, 1.0) * 1.05;
      pos = mod(pos + lim, 2.0 * lim) - lim;

      v_pos = pos; v_vel = vel; v_seed = a_seed;
      gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
    }`;

    var PARTICLE_DRAW_VS = `#version 300 es
    precision highp float;
    in vec2 a_pos; in vec2 a_vel; in float a_seed;
    out float v_alpha; out float v_warm;
    uniform float u_aspect; uniform float u_size; uniform float u_energy;
    void main(){
      gl_Position = vec4(a_pos / vec2(u_aspect, 1.0), 0.0, 1.0);
      float speed = clamp(length(a_vel) * 1.6, 0.0, 1.0);
      gl_PointSize = u_size * (0.55 + a_seed * 0.9) * (1.0 + u_energy * 0.5);
      v_alpha = (0.025 + speed * 0.085) * (0.4 + a_seed * 0.6);
      v_warm = a_seed;
    }`;

    var PARTICLE_DRAW_FS = `#version 300 es
    precision highp float;
    in float v_alpha; in float v_warm;
    uniform vec3 u_c1, u_c2;
    out vec4 fragColor;
    void main(){
      // Gaussian-ish sprite. A hard disc reads as confetti; the falloff is what makes a
      // few thousand points read as a luminous field.
      float d = length(gl_PointCoord - 0.5) * 2.0;
      float fall = exp(-d * d * 3.2) * step(d, 1.0);
      vec3 col = mix(u_c1, u_c2, smoothstep(0.62, 1.0, v_warm));
      fragColor = vec4(col * fall * v_alpha, fall * v_alpha);
    }`;

    // ── Dual-Kawase ─────────────────────────────────────────────────────────────────────
    // Four bilinear taps per pass, offsets chosen so the two passes together approximate a
    // much wider Gaussian than eight taps normally buy.

    var KAWASE_DOWN_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_src; uniform vec2 u_texel; uniform float u_offset;
    out vec4 fragColor;
    void main(){
      vec2 uv = gl_FragCoord.xy * u_texel;
      vec2 o = u_texel * u_offset * 0.5;
      vec4 s = texture(u_src, uv + vec2(-o.x, -o.y));
      s += texture(u_src, uv + vec2( o.x, -o.y));
      s += texture(u_src, uv + vec2(-o.x,  o.y));
      s += texture(u_src, uv + vec2( o.x,  o.y));
      fragColor = s * 0.25;
    }`;

    var KAWASE_UP_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_src; uniform vec2 u_texel; uniform float u_offset;
    out vec4 fragColor;
    void main(){
      vec2 uv = gl_FragCoord.xy * u_texel;
      vec2 o = u_texel * u_offset;
      vec4 s = texture(u_src, uv + vec2(-o.x, 0.0)) + texture(u_src, uv + vec2(o.x, 0.0));
      s += texture(u_src, uv + vec2(0.0, -o.y)) + texture(u_src, uv + vec2(0.0, o.y));
      s += texture(u_src, uv + o * 0.7) + texture(u_src, uv - o * 0.7);
      fragColor = s / 6.0;
    }`;

    // ── Glass mask ──────────────────────────────────────────────────────────────────────
    // One fullscreen pass over up to MAX_GLASS_RECTS rounded-rect SDFs, rendered ONLY when
    // the rect set changes (scroll, resize, layout) — never per frame. The composite then
    // reads the mask's gradient for refraction, which is why a smooth SDF is stored rather
    // than a binary coverage value.

    var MASK_FS = `#version 300 es
    precision highp float;
    uniform vec2 u_res;
    uniform int u_count;
    uniform vec4 u_rects[${MAX_GLASS_RECTS}];   // xy = centre, zw = half-extent (px)
    uniform float u_radius[${MAX_GLASS_RECTS}];
    out vec4 fragColor;
    float sdRoundRect(vec2 p, vec2 b, float r){
      vec2 q = abs(p) - b + r;
      return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
    }
    void main(){
      vec2 p = gl_FragCoord.xy;
      float d = 1e9;
      for (int i = 0; i < ${MAX_GLASS_RECTS}; i++){
        if (i >= u_count) break;
        vec4 r = u_rects[i];
        d = min(d, sdRoundRect(p - r.xy, r.zw, u_radius[i]));
      }
      // Smooth 6px band across the boundary: inside → 1, outside → 0, and a usable
      // gradient in between for the refraction term.
      fragColor = vec4(1.0 - smoothstep(-3.0, 3.0, d), 0.0, 0.0, 1.0);
    }`;

    var COMPOSITE_FS = `#version 300 es
    precision highp float;
    uniform sampler2D u_scene; uniform sampler2D u_blur; uniform sampler2D u_mask;
    uniform vec2 u_res; uniform vec3 u_tint; uniform float u_energy; uniform float u_glass;
    out vec4 fragColor;
    ${DITHER}
    void main(){
      vec2 uv = gl_FragCoord.xy / u_res;
      vec2 e = 1.5 / u_res;

      float m = texture(u_mask, uv).r * u_glass;
      // Central-difference gradient of the mask. Zero in the flat interior and outside, so
      // the refraction offset only bends light near a glass edge — which is exactly where
      // real glass bends it.
      vec2 g = vec2(
        texture(u_mask, uv + vec2(e.x, 0.0)).r - texture(u_mask, uv - vec2(e.x, 0.0)).r,
        texture(u_mask, uv + vec2(0.0, e.y)).r - texture(u_mask, uv - vec2(0.0, e.y)).r);

      vec2 refr = g * 0.020 * u_glass;
      vec4 sharp = texture(u_scene, uv + refr);
      vec4 soft = texture(u_blur, uv + refr);
      vec4 col = mix(sharp, soft, clamp(m, 0.0, 1.0));

      // Rim light along the glass edge, lifted slightly by output level so the frame
      // catches the beat of a sonification.
      float rim = clamp(length(g) * 22.0, 0.0, 1.0);
      col.rgb += u_tint * rim * (0.045 + u_energy * 0.05);

      fragColor = vec4(dither(col.rgb, gl_FragCoord.xy), col.a);
    }`;

    // ── WebGL1 fallback shaders ─────────────────────────────────────────────────────────

    var VERT1 = `attribute vec2 a_pos; void main(){ gl_Position = vec4(a_pos, 0.0, 1.0); }`;

    var FIELD_FS1 = `precision highp float;
    uniform vec2 u_res; uniform float u_time; uniform float u_energy;
    uniform vec3 u_c1, u_c2, u_c3;
    ${SNOISE}
    ${CURL}
    ${DITHER}
    void main(){
      ${FIELD_BODY}
      gl_FragColor = vec4(dither(col * vig, gl_FragCoord.xy), a);
    }`;

    // ── GL plumbing ─────────────────────────────────────────────────────────────────────

    function compile(gl, type, src) {
        var s = gl.createShader(type);
        gl.shaderSource(s, src);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
            console.warn('gfx-webgl: shader compile failed\n' + gl.getShaderInfoLog(s));
            gl.deleteShader(s);
            return null;
        }
        return s;
    }

    function link(gl, vsSrc, fsSrc, feedback) {
        var vs = compile(gl, gl.VERTEX_SHADER, vsSrc);
        var fs = compile(gl, gl.FRAGMENT_SHADER, fsSrc);
        if (!vs || !fs) return null;
        var p = gl.createProgram();
        gl.attachShader(p, vs);
        gl.attachShader(p, fs);
        if (feedback) gl.transformFeedbackVaryings(p, feedback, gl.SEPARATE_ATTRIBS);
        gl.linkProgram(p);
        gl.deleteShader(vs);
        gl.deleteShader(fs);
        if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
            console.warn('gfx-webgl: link failed\n' + gl.getProgramInfoLog(p));
            return null;
        }
        return p;
    }

    /** Cache every active uniform location once — getUniformLocation is not free per frame. */
    function uniforms(gl, prog) {
        var out = {};
        var n = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
        for (var i = 0; i < n; i++) {
            var name = gl.getActiveUniform(prog, i).name.replace(/\[0\]$/, '');
            out[name] = gl.getUniformLocation(prog, name);
        }
        return out;
    }

    function makeTarget(gl, w, h) {
        var tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        var fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        return { tex: tex, fbo: fbo, w: w, h: h };
    }

    function resizeTarget(gl, t, w, h) {
        if (t.w === w && t.h === h) return;
        t.w = w; t.h = h;
        gl.bindTexture(gl.TEXTURE_2D, t.tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    }

    // ── Renderer ────────────────────────────────────────────────────────────────────────

    function create(canvas) {
        var attrs = {
            alpha: true, antialias: false, depth: false, stencil: false,
            premultipliedAlpha: false, powerPreference: 'low-power',
            failIfMajorPerformanceCaveat: true
        };

        var gl = canvas.getContext('webgl2', attrs);
        var isGL2 = !!gl;
        if (!gl) gl = canvas.getContext('webgl', attrs);
        if (!gl) return null;

        var colors = [[0.357, 0.486, 0.839], [0.945, 0.714, 0.388], [0.576, 0.706, 0.961]];
        var quality = { scale: 0.5, particles: 0.55, effects: false };
        var W = 1, H = 1;
        var energy = 0;

        // Fullscreen triangle. One draw, no index buffer, no wasted diagonal fragments.
        var quad = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, quad);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);

        var fieldProg = link(gl, isGL2 ? VERT2 : VERT1, isGL2 ? FIELD_FS2 : FIELD_FS1);
        if (!fieldProg) return null;
        var fieldU = uniforms(gl, fieldProg);
        var fieldPos = gl.getAttribLocation(fieldProg, 'a_pos');

        var g2 = null;
        if (isGL2) g2 = buildGl2(gl, quad);

        function bindQuad(prog, attrLoc) {
            gl.bindBuffer(gl.ARRAY_BUFFER, quad);
            gl.enableVertexAttribArray(attrLoc);
            gl.vertexAttribPointer(attrLoc, 2, gl.FLOAT, false, 0, 0);
        }

        function drawFullscreen() { gl.drawArrays(gl.TRIANGLES, 0, 3); }

        // ── WebGL2-only sub-pipeline ────────────────────────────────────────────────────
        function buildGl2(gl, quad) {
            var update = link(gl, PARTICLE_UPDATE_VS,
                '#version 300 es\nprecision mediump float;\nout vec4 c;\nvoid main(){ c = vec4(0.0); }',
                ['v_pos', 'v_vel', 'v_seed']);
            var draw = link(gl, PARTICLE_DRAW_VS, PARTICLE_DRAW_FS);
            var down = link(gl, VERT2, KAWASE_DOWN_FS);
            var up = link(gl, VERT2, KAWASE_UP_FS);
            var mask = link(gl, VERT2, MASK_FS);
            var comp = link(gl, VERT2, COMPOSITE_FS);
            if (!update || !draw || !down || !up || !mask || !comp) {
                console.warn('gfx-webgl: WebGL2 pipeline unavailable, falling back to field-only');
                return null;
            }

            return {
                update: update, updateU: uniforms(gl, update),
                draw: draw, drawU: uniforms(gl, draw),
                down: down, downU: uniforms(gl, down),
                up: up, upU: uniforms(gl, up),
                mask: mask, maskU: uniforms(gl, mask),
                comp: comp, compU: uniforms(gl, comp),
                scene: null, blurA: null, blurB: null, maskTarget: null,
                bufs: null, tf: null, count: 0, capacity: 0,
                maskDirty: true, glassCount: 0,
                rects: new Float32Array(MAX_GLASS_RECTS * 4),
                radii: new Float32Array(MAX_GLASS_RECTS)
            };
        }

        /** (Re)allocate the particle state buffers for the current quality tier. */
        function allocParticles(count) {
            if (!g2 || count === g2.capacity) return;

            if (g2.bufs) g2.bufs.forEach(function (b) { gl.deleteBuffer(b.pos); gl.deleteBuffer(b.vel); gl.deleteBuffer(b.seed); });
            if (g2.tf) gl.deleteTransformFeedback(g2.tf);

            var pos = new Float32Array(count * 2);
            var vel = new Float32Array(count * 2);
            var seed = new Float32Array(count);
            var aspect = W / Math.max(1, H);
            for (var i = 0; i < count; i++) {
                pos[i * 2] = (Math.random() * 2 - 1) * aspect;
                pos[i * 2 + 1] = Math.random() * 2 - 1;
                seed[i] = Math.random();
            }

            function pair() {
                var b = { pos: gl.createBuffer(), vel: gl.createBuffer(), seed: gl.createBuffer() };
                gl.bindBuffer(gl.ARRAY_BUFFER, b.pos); gl.bufferData(gl.ARRAY_BUFFER, pos, gl.DYNAMIC_COPY);
                gl.bindBuffer(gl.ARRAY_BUFFER, b.vel); gl.bufferData(gl.ARRAY_BUFFER, vel, gl.DYNAMIC_COPY);
                gl.bindBuffer(gl.ARRAY_BUFFER, b.seed); gl.bufferData(gl.ARRAY_BUFFER, seed, gl.DYNAMIC_COPY);
                return b;
            }
            g2.bufs = [pair(), pair()];
            g2.tf = gl.createTransformFeedback();
            g2.capacity = count;
            g2.count = count;
        }

        function bindParticleAttribs(prog, src) {
            var l = {
                pos: gl.getAttribLocation(prog, 'a_pos'),
                vel: gl.getAttribLocation(prog, 'a_vel'),
                seed: gl.getAttribLocation(prog, 'a_seed')
            };
            gl.bindBuffer(gl.ARRAY_BUFFER, src.pos);
            gl.enableVertexAttribArray(l.pos); gl.vertexAttribPointer(l.pos, 2, gl.FLOAT, false, 0, 0);
            gl.bindBuffer(gl.ARRAY_BUFFER, src.vel);
            gl.enableVertexAttribArray(l.vel); gl.vertexAttribPointer(l.vel, 2, gl.FLOAT, false, 0, 0);
            gl.bindBuffer(gl.ARRAY_BUFFER, src.seed);
            gl.enableVertexAttribArray(l.seed); gl.vertexAttribPointer(l.seed, 1, gl.FLOAT, false, 0, 0);
        }

        function renderMask() {
            if (!g2 || !g2.maskDirty) return;
            g2.maskDirty = false;
            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.maskTarget.fbo);
            gl.viewport(0, 0, g2.maskTarget.w, g2.maskTarget.h);
            gl.disable(gl.BLEND);
            gl.useProgram(g2.mask);
            bindQuad(g2.mask, gl.getAttribLocation(g2.mask, 'a_pos'));
            gl.uniform2f(g2.maskU.u_res, g2.maskTarget.w, g2.maskTarget.h);
            gl.uniform1i(g2.maskU.u_count, g2.glassCount);
            if (g2.glassCount > 0) {
                gl.uniform4fv(g2.maskU.u_rects, g2.rects);
                gl.uniform1fv(g2.maskU.u_radius, g2.radii);
            }
            gl.clearColor(0, 0, 0, 1);
            gl.clear(gl.COLOR_BUFFER_BIT);
            drawFullscreen();
        }

        function frame(dt, elapsed, level) {
            energy = level || 0;

            // ── Field ───────────────────────────────────────────────────────────────────
            var target = g2 ? g2.scene : null;
            gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fbo : null);
            gl.viewport(0, 0, W, H);
            gl.disable(gl.BLEND);
            gl.useProgram(fieldProg);
            bindQuad(fieldProg, fieldPos);
            gl.uniform2f(fieldU.u_res, W, H);
            gl.uniform1f(fieldU.u_time, elapsed);
            gl.uniform1f(fieldU.u_energy, energy);
            gl.uniform3fv(fieldU.u_c1, colors[0]);
            gl.uniform3fv(fieldU.u_c2, colors[1]);
            gl.uniform3fv(fieldU.u_c3, colors[2]);
            drawFullscreen();

            if (!g2) return;

            var aspect = W / Math.max(1, H);

            // ── Particle advection (transform feedback) ──────────────────────────────────
            if (g2.count > 0 && dt > 0) {
                gl.useProgram(g2.update);
                bindParticleAttribs(g2.update, g2.bufs[0]);
                gl.uniform1f(g2.updateU.u_dt, dt);
                gl.uniform1f(g2.updateU.u_time, elapsed);
                gl.uniform1f(g2.updateU.u_aspect, aspect);
                gl.uniform1f(g2.updateU.u_energy, energy);

                gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, g2.tf);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, g2.bufs[1].pos);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 1, g2.bufs[1].vel);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 2, g2.bufs[1].seed);
                gl.enable(gl.RASTERIZER_DISCARD);
                gl.beginTransformFeedback(gl.POINTS);
                gl.drawArrays(gl.POINTS, 0, g2.count);
                gl.endTransformFeedback();
                gl.disable(gl.RASTERIZER_DISCARD);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, null);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 1, null);
                gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 2, null);
                gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);

                var t = g2.bufs[0]; g2.bufs[0] = g2.bufs[1]; g2.bufs[1] = t;

                // ── Particle draw (additive into the scene) ──────────────────────────────
                gl.useProgram(g2.draw);
                bindParticleAttribs(g2.draw, g2.bufs[0]);
                gl.uniform1f(g2.drawU.u_aspect, aspect);
                gl.uniform1f(g2.drawU.u_size, Math.max(1.5, 2.6 * quality.scale * 2.0));
                gl.uniform1f(g2.drawU.u_energy, energy);
                gl.uniform3fv(g2.drawU.u_c1, colors[0]);
                gl.uniform3fv(g2.drawU.u_c2, colors[1]);
                gl.enable(gl.BLEND);
                gl.blendFunc(gl.SRC_ALPHA, gl.ONE);
                gl.drawArrays(gl.POINTS, 0, g2.count);
                gl.disable(gl.BLEND);
            }

            renderMask();

            // ── Dual-Kawase ─────────────────────────────────────────────────────────────
            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.blurA.fbo);
            gl.viewport(0, 0, g2.blurA.w, g2.blurA.h);
            gl.useProgram(g2.down);
            bindQuad(g2.down, gl.getAttribLocation(g2.down, 'a_pos'));
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, g2.scene.tex);
            gl.uniform1i(g2.downU.u_src, 0);
            gl.uniform2f(g2.downU.u_texel, 1 / g2.blurA.w, 1 / g2.blurA.h);
            gl.uniform1f(g2.downU.u_offset, 1.25);
            drawFullscreen();

            gl.bindFramebuffer(gl.FRAMEBUFFER, g2.blurB.fbo);
            gl.viewport(0, 0, g2.blurB.w, g2.blurB.h);
            gl.useProgram(g2.up);
            bindQuad(g2.up, gl.getAttribLocation(g2.up, 'a_pos'));
            gl.bindTexture(gl.TEXTURE_2D, g2.blurA.tex);
            gl.uniform1i(g2.upU.u_src, 0);
            gl.uniform2f(g2.upU.u_texel, 1 / g2.blurB.w, 1 / g2.blurB.h);
            gl.uniform1f(g2.upU.u_offset, 2.0);
            drawFullscreen();

            // ── Composite to canvas ─────────────────────────────────────────────────────
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, W, H);
            gl.useProgram(g2.comp);
            bindQuad(g2.comp, gl.getAttribLocation(g2.comp, 'a_pos'));
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, g2.scene.tex);
            gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, g2.blurB.tex);
            gl.activeTexture(gl.TEXTURE2); gl.bindTexture(gl.TEXTURE_2D, g2.maskTarget.tex);
            gl.uniform1i(g2.compU.u_scene, 0);
            gl.uniform1i(g2.compU.u_blur, 1);
            gl.uniform1i(g2.compU.u_mask, 2);
            gl.uniform2f(g2.compU.u_res, W, H);
            gl.uniform1f(g2.compU.u_energy, energy);
            gl.uniform1f(g2.compU.u_glass, quality.effects ? 1.0 : 0.55);
            gl.uniform3fv(g2.compU.u_tint, colors[2]);
            drawFullscreen();
        }

        function resize(w, h) {
            W = Math.max(1, w); H = Math.max(1, h);
            canvas.width = W; canvas.height = H;
            gl.viewport(0, 0, W, H);
            if (!g2) return;
            var half = { w: Math.max(1, W >> 1), h: Math.max(1, H >> 1) };
            var quarter = { w: Math.max(1, W >> 1), h: Math.max(1, H >> 1) };
            if (!g2.scene) {
                g2.scene = makeTarget(gl, W, H);
                g2.blurA = makeTarget(gl, half.w, half.h);
                g2.blurB = makeTarget(gl, W, H);
                g2.maskTarget = makeTarget(gl, quarter.w, quarter.h);
            } else {
                resizeTarget(gl, g2.scene, W, H);
                resizeTarget(gl, g2.blurA, half.w, half.h);
                resizeTarget(gl, g2.blurB, W, H);
                resizeTarget(gl, g2.maskTarget, quarter.w, quarter.h);
            }
            g2.maskDirty = true;
        }

        return {
            tier: isGL2 ? (g2 ? 'webgl2' : 'webgl2-field') : 'webgl1',
            gl: gl,

            resize: resize,

            setColors: function (c) { colors = c; },

            setQuality: function (q) {
                quality = q;
                if (g2) allocParticles(Math.round(BASE_PARTICLES * q.particles));
            },

            /**
             * rects: Float32Array of [centreX, centreY, halfW, halfH, radius] in CSS pixels,
             * already clipped to the viewport by the caller. Uploaded lazily — the mask pass
             * only re-runs when this changes, not every frame.
             */
            setGlass: function (list, count, vw, vh) {
                if (!g2) return;
                var n = Math.min(count, MAX_GLASS_RECTS);
                var sx = g2.maskTarget ? g2.maskTarget.w / vw : 1;
                var sy = g2.maskTarget ? g2.maskTarget.h / vh : 1;
                for (var i = 0; i < n; i++) {
                    // Y flips: DOM origin is top-left, gl_FragCoord is bottom-left.
                    g2.rects[i * 4] = list[i * 5] * sx;
                    g2.rects[i * 4 + 1] = (vh - list[i * 5 + 1]) * sy;
                    g2.rects[i * 4 + 2] = list[i * 5 + 2] * sx;
                    g2.rects[i * 4 + 3] = list[i * 5 + 3] * sy;
                    g2.radii[i] = list[i * 5 + 4] * sx;
                }
                g2.glassCount = n;
                g2.maskDirty = true;
            },

            frame: frame,

            dispose: function () {
                var lose = gl.getExtension('WEBGL_lose_context');
                if (lose) lose.loseContext();
            }
        };
    }

    window.gfxWebgl = { create: create };
})();
