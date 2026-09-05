/**
 * gfx-webgpu — the top rung of the backdrop capability ladder.
 *
 * Loaded LAZILY by js/gpu-backdrop.js, and only when `navigator.gpu` exists, so browsers
 * that cannot use a line of it never download it. That mirrors how starfield-backdrop.js
 * keeps Three.js off every route but "/" — the same cost discipline, applied to a feature
 * gate instead of a route gate.
 *
 * What it buys over the WebGL2 tier: particle advection moves from a transform-feedback
 * vertex pass to a real compute shader over a storage buffer, which is both faster per
 * particle and no longer abuses the rasteriser to do arithmetic. The count rises with it.
 *
 * ── Passes ──────────────────────────────────────────────────────────────────────────────
 *   1. COMPUTE    advect every particle through a curl-noise field, 64 per workgroup.
 *   2. SCENE      one render pass into an offscreen texture: fullscreen gradient (blend
 *                 off) then the particles as instanced soft quads (additive).
 *   3. COMPOSITE  scene → canvas, with the frosted-glass treatment applied.
 *
 * ── Glass without a mask texture ────────────────────────────────────────────────────────
 * The WebGL2 tier renders card rects into a quarter-res mask FBO and reads its gradient.
 * Here the rounded-rect SDFs are evaluated analytically in the composite shader straight
 * from a storage buffer, so there is no mask texture, no extra render pass, and no
 * resolution loss on the refraction gradient. Same visual contract, fewer moving parts —
 * the two tiers differ in mechanism, not in what a visitor sees.
 *
 * ── Noise parity ────────────────────────────────────────────────────────────────────────
 * This file uses hash-based value noise where gfx-webgl.js uses Ashima simplex. Both are
 * smooth divergence-free curl fields at the scales used here and the motion reads
 * identically; porting simplex to WGSL would have tripled the shader for no visible gain.
 *
 * ── Failure is silent and total ─────────────────────────────────────────────────────────
 * Adapter refusal, device loss, pipeline compilation failure and an uncaptured device error
 * all resolve to `null` (or a one-shot dispose), and gpu-backdrop.js falls to the WebGL
 * tier. Nothing here ever throws to the caller.
 */
(function () {
    'use strict';

    // 40k, not the 60k this started at. On a discrete GPU the extra 20k was free in GPU time
    // and not free in CPU: the per-frame encoder work plus the composite pass pushed the
    // governor's measured dispatch cost to ~5.8ms against a 6ms budget, which is a tier
    // demotion waiting to happen on any machine slower than the one it was tuned on. This is
    // still nearly 3x the WebGL2 tier's particle count.
    var BASE_PARTICLES = 40000;
    var MAX_GLASS_RECTS = 24;
    var PARTICLE_BYTES = 24;        // vec2 pos, vec2 vel, f32 seed, f32 pad
    var RECT_BYTES = 24;            // vec2 centre, vec2 half, f32 radius, f32 pad
    var PARAMS_BYTES = 80;

    var NOISE = `
    fn hash2(p: vec2f) -> f32 {
      var p3 = fract(vec3f(p.x, p.y, p.x) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }
    fn vnoise(p: vec2f) -> f32 {
      let i = floor(p);
      let f = fract(p);
      let u = f * f * (3.0 - 2.0 * f);
      let a = hash2(i);
      let b = hash2(i + vec2f(1.0, 0.0));
      let c = hash2(i + vec2f(0.0, 1.0));
      let d = hash2(i + vec2f(1.0, 1.0));
      return (mix(mix(a, b, u.x), mix(c, d, u.x), u.y)) * 2.0 - 1.0;
    }
    fn potential(p: vec2f, t: f32) -> f32 {
      return vnoise(p + vec2f(t * 0.10, 0.0)) * 1.00
           + vnoise(p * 2.1 + vec2f(0.0, t * 0.14)) * 0.45
           + vnoise(p * 4.3 - vec2f(t * 0.07, t * 0.05)) * 0.20;
    }
    fn curl(p: vec2f, t: f32) -> vec2f {
      let e = 0.035;
      let dx = potential(p + vec2f(e, 0.0), t) - potential(p - vec2f(e, 0.0), t);
      let dy = potential(p + vec2f(0.0, e), t) - potential(p - vec2f(0.0, e), t);
      return vec2f(dy, -dx) / (2.0 * e);
    }`;

    var PARAMS = `
    struct Params {
      res: vec2f,
      time: f32,
      dt: f32,
      aspect: f32,
      energy: f32,
      count: f32,
      glassCount: f32,
      c1: vec4f,
      c2: vec4f,
      c3: vec4f,
    };
    struct Particle { pos: vec2f, vel: vec2f, seed: f32, pad: f32 };`;

    var COMPUTE_WGSL = `
    ${PARAMS}
    ${NOISE}
    @group(0) @binding(0) var<uniform> P: Params;
    @group(0) @binding(1) var<storage, read_write> parts: array<Particle>;

    @compute @workgroup_size(64)
    fn main(@builtin(global_invocation_id) gid: vec3u) {
      let i = gid.x;
      if (i >= u32(P.count)) { return; }
      var p = parts[i];
      let f = curl(p.pos * 1.6, P.time * 0.35);
      let drive = 0.18 + P.energy * 0.35;
      var vel = p.vel * 0.94 + f * drive * P.dt;
      var pos = p.pos + vel * P.dt;
      // Toroidal wrap keeps the field statistically stationary; respawning would make the
      // density visibly pulse.
      let lim = vec2f(P.aspect, 1.0) * 1.05;
      pos = ((pos + lim) % (2.0 * lim)) - lim;
      parts[i] = Particle(pos, vel, p.seed, 0.0);
    }`;

    var SCENE_WGSL = `
    ${PARAMS}
    ${NOISE}
    @group(0) @binding(0) var<uniform> P: Params;
    @group(0) @binding(1) var<storage, read> parts: array<Particle>;

    struct VOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f };

    // Fullscreen triangle — one draw, no vertex buffer.
    @vertex fn vsField(@builtin(vertex_index) vi: u32) -> VOut {
      var xy = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
      var o: VOut;
      o.pos = vec4f(xy[vi], 0.0, 1.0);
      o.uv = xy[vi] * 0.5 + 0.5;
      return o;
    }

    @fragment fn fsField(in: VOut) -> @location(0) vec4f {
      let uv = vec2f(in.uv.x, 1.0 - in.uv.y);
      let p = (uv - 0.5) * vec2f(P.res.x / P.res.y, 1.0);
      let t = P.time * 0.05;

      // See the matching note in gfx-webgl.js: curl() returns a true derivative (~7 in
      // magnitude), so 0.085 displaced every sample outside its own falloff and the field
      // composited to nothing.
      let flow = curl(p * 1.35, P.time * 0.35) * 0.012;
      let pf = p + flow;

      let o1 = vec2f(sin(t * 1.1) * 0.35, cos(t * 0.9) * 0.25);
      let o2 = vec2f(cos(t * 0.7) * -0.40, sin(t * 1.3) * 0.30);
      let d1 = length(pf - o1);
      let d2 = length(pf - o2);
      let g1 = exp(-d1 * d1 * 3.5);
      let g2 = exp(-d2 * d2 * 4.0);

      let t3 = P.time * 0.021;
      let o3 = vec2f(sin(t3 * 0.8) * 0.55, cos(t3 * 0.5) * 0.18 - 0.08);
      var p3 = pf - o3;
      p3.x += p3.y * 0.4;
      let d3 = length(p3 * vec2f(2.8, 1.0));
      let g3 = exp(-d3 * d3 * 2.2) * 0.65;

      let fil = vnoise(pf * 5.5 + flow * 6.0 + vec2f(0.0, P.time * 0.06));
      let mask = g1 + g2 + g3;
      let detail = fil * 0.16 * mask;
      let pulse = 1.0 + P.energy * 0.35;
      // See the matching note in gfx-webgl.js: smoothstep with edge0 > edge1 is undefined
      // and silently returns zero on some drivers.
      let vig = 1.0 - smoothstep(0.15, 1.15 + P.energy * 0.06, length(p));

      // See the matching note in gfx-webgl.js: unscaled, three overlapping gaussians
      // plus the additive particle pass clip to white in the centre of the viewport.
      let col = (P.c1.rgb * g1 + P.c2.rgb * g2 + P.c3.rgb * g3) * pulse * 0.42 + P.c3.rgb * detail * 0.5;
      return vec4f(col * vig, (mask + detail) * vig * 0.30);
    }

    struct POut { @builtin(position) pos: vec4f, @location(0) local: vec2f, @location(1) tint: vec2f };

    @vertex fn vsPart(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> POut {
      var corner = array<vec2f, 6>(
        vec2f(-1.0, -1.0), vec2f(1.0, -1.0), vec2f(-1.0, 1.0),
        vec2f(-1.0, 1.0), vec2f(1.0, -1.0), vec2f(1.0, 1.0));
      let c = corner[vi];
      let pt = parts[ii];
      let sizePx = (2.2 + pt.seed * 2.4) * (1.0 + P.energy * 0.5);
      let half = sizePx / P.res;
      let clip = pt.pos / vec2f(P.aspect, 1.0);
      var o: POut;
      o.pos = vec4f(clip + c * half * 2.0, 0.0, 1.0);
      o.local = c;
      let speed = clamp(length(pt.vel) * 1.6, 0.0, 1.0);
      o.tint = vec2f(pt.seed, (0.025 + speed * 0.085) * (0.4 + pt.seed * 0.6));
      return o;
    }

    @fragment fn fsPart(in: POut) -> @location(0) vec4f {
      let d = length(in.local);
      // Gaussian falloff: a hard disc reads as confetti, the falloff is what makes tens of
      // thousands of points read as one luminous field.
      let fall = exp(-d * d * 3.2) * step(d, 1.0);
      let col = mix(P.c1.rgb, P.c2.rgb, smoothstep(0.62, 1.0, in.tint.x));
      let a = fall * in.tint.y;
      return vec4f(col * a, a);
    }`;

    var COMPOSITE_WGSL = `
    ${PARAMS}
    struct Rect { c: vec2f, h: vec2f, r: f32, pad: f32 };
    @group(0) @binding(0) var<uniform> P: Params;
    @group(0) @binding(1) var samp: sampler;
    @group(0) @binding(2) var scene: texture_2d<f32>;
    @group(0) @binding(3) var<storage, read> rects: array<Rect>;

    struct VOut { @builtin(position) pos: vec4f, @location(0) uv: vec2f };
    @vertex fn vs(@builtin(vertex_index) vi: u32) -> VOut {
      var xy = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
      var o: VOut;
      o.pos = vec4f(xy[vi], 0.0, 1.0);
      o.uv = vec2f(xy[vi].x * 0.5 + 0.5, 0.5 - xy[vi].y * 0.5);
      return o;
    }

    fn sdRoundRect(p: vec2f, b: vec2f, r: f32) -> f32 {
      let q = abs(p) - b + r;
      return min(max(q.x, q.y), 0.0) + length(max(q, vec2f(0.0))) - r;
    }

    /** Coverage of the glass surfaces at a pixel, smoothed across a 3px band. */
    fn glassAt(px: vec2f) -> f32 {
      var d = 1e9;
      let n = u32(P.glassCount);
      for (var i: u32 = 0u; i < n; i = i + 1u) {
        let r = rects[i];
        d = min(d, sdRoundRect(px - r.c, r.h, r.r));
      }
      return 1.0 - smoothstep(-3.0, 3.0, d);
    }

    /** Eight-tap ring blur. Cheap because it only has to sell "frosted", not "Gaussian". */
    fn blurred(uv: vec2f, radius: vec2f) -> vec4f {
      var acc = textureSample(scene, samp, uv) * 0.28;
      let k = 0.09;
      acc += textureSample(scene, samp, uv + vec2f( radius.x, 0.0)) * k;
      acc += textureSample(scene, samp, uv + vec2f(-radius.x, 0.0)) * k;
      acc += textureSample(scene, samp, uv + vec2f(0.0,  radius.y)) * k;
      acc += textureSample(scene, samp, uv + vec2f(0.0, -radius.y)) * k;
      acc += textureSample(scene, samp, uv + radius * 0.7) * k;
      acc += textureSample(scene, samp, uv - radius * 0.7) * k;
      acc += textureSample(scene, samp, uv + vec2f(radius.x, -radius.y) * 0.7) * k;
      acc += textureSample(scene, samp, uv + vec2f(-radius.x, radius.y) * 0.7) * k;
      return acc;
    }

    fn hash12(p: vec2f) -> f32 {
      var p3 = fract(vec3f(p.x, p.y, p.x) * 0.1031);
      p3 += dot(p3, p3.yzx + 33.33);
      return fract((p3.x + p3.y) * p3.z);
    }

    @fragment fn fs(in: VOut) -> @location(0) vec4f {
      let px = in.uv * P.res;
      let m = glassAt(px);
      // Central differences on the analytic SDF: zero in the flat interior and outside, so
      // the refraction offset only bends light where real glass would.
      let e = 1.5;
      let g = vec2f(
        glassAt(px + vec2f(e, 0.0)) - glassAt(px - vec2f(e, 0.0)),
        glassAt(px + vec2f(0.0, e)) - glassAt(px - vec2f(0.0, e)));

      let refr = g * 0.02;
      let uv = in.uv + refr;
      let sharp = textureSample(scene, samp, uv);
      let soft = blurred(uv, vec2f(3.5) / P.res);
      var col = mix(sharp, soft, clamp(m, 0.0, 1.0));

      let rim = clamp(length(g) * 22.0, 0.0, 1.0);
      col = vec4f(col.rgb + P.c3.rgb * rim * (0.045 + P.energy * 0.05), col.a);

      // ±0.5/255 dither: an 8-bit full-viewport gradient bands visibly without it.
      let d = (hash12(px) - 0.5) / 255.0;
      return vec4f(col.rgb + d, col.a);
    }`;

    async function create(canvas) {
        if (!navigator.gpu) return null;

        var adapter, device;
        try {
            adapter = await navigator.gpu.requestAdapter({ powerPreference: 'low-power' });
            if (!adapter) return null;
            device = await adapter.requestDevice();
        } catch (err) {
            console.warn('gfx-webgpu: no device', err);
            return null;
        }

        var context = canvas.getContext('webgpu');
        if (!context) return null;

        var format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device: device, format: format, alphaMode: 'premultiplied' });

        var dead = false;
        device.lost.then(function (info) {
            // Not necessarily an error: a normal dispose() destroys the device too.
            if (!dead) console.warn('gfx-webgpu: device lost —', info.message);
            dead = true;
        });
        device.onuncapturederror = function (e) {
            console.warn('gfx-webgpu: uncaptured error', e.error);
            dead = true;
        };

        var W = 1, H = 1;
        var colors = [[0.357, 0.486, 0.839], [0.945, 0.714, 0.388], [0.576, 0.706, 0.961]];
        var quality = { scale: 0.62, particles: 1, effects: true };
        var count = 0;

        var paramsBuf = device.createBuffer({ size: PARAMS_BYTES, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        var rectBuf = device.createBuffer({ size: RECT_BYTES * MAX_GLASS_RECTS, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
        var paramsData = new Float32Array(PARAMS_BYTES / 4);
        var rectData = new Float32Array(MAX_GLASS_RECTS * RECT_BYTES / 4);
        var glassCount = 0;

        var particleBuf = null;
        var sceneTex = null, sceneView = null;
        var sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear', addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge' });

        var computeMod, sceneMod, compositeMod;
        try {
            computeMod = device.createShaderModule({ code: COMPUTE_WGSL });
            sceneMod = device.createShaderModule({ code: SCENE_WGSL });
            compositeMod = device.createShaderModule({ code: COMPOSITE_WGSL });
        } catch (err) {
            console.warn('gfx-webgpu: shader module creation failed', err);
            return null;
        }

        var computePipe, fieldPipe, partPipe, compositePipe;
        try {
            computePipe = device.createComputePipeline({ layout: 'auto', compute: { module: computeMod, entryPoint: 'main' } });

            fieldPipe = device.createRenderPipeline({
                layout: 'auto',
                vertex: { module: sceneMod, entryPoint: 'vsField' },
                fragment: { module: sceneMod, entryPoint: 'fsField', targets: [{ format: 'rgba8unorm' }] },
                primitive: { topology: 'triangle-list' }
            });

            partPipe = device.createRenderPipeline({
                layout: 'auto',
                vertex: { module: sceneMod, entryPoint: 'vsPart' },
                fragment: {
                    module: sceneMod, entryPoint: 'fsPart',
                    targets: [{
                        format: 'rgba8unorm',
                        blend: {
                            // Additive: the sprites are already premultiplied by their alpha.
                            color: { srcFactor: 'one', dstFactor: 'one', operation: 'add' },
                            alpha: { srcFactor: 'one', dstFactor: 'one', operation: 'add' }
                        }
                    }]
                },
                primitive: { topology: 'triangle-list' }
            });

            compositePipe = device.createRenderPipeline({
                layout: 'auto',
                vertex: { module: compositeMod, entryPoint: 'vs' },
                fragment: { module: compositeMod, entryPoint: 'fs', targets: [{ format: format }] },
                primitive: { topology: 'triangle-list' }
            });
        } catch (err) {
            console.warn('gfx-webgpu: pipeline creation failed', err);
            return null;
        }

        var fieldBind = null, partBind = null, computeBind = null, compositeBind = null;

        function allocParticles(n) {
            if (n === count) return;
            count = n;
            if (particleBuf) particleBuf.destroy();
            particleBuf = device.createBuffer({
                size: Math.max(PARTICLE_BYTES, n * PARTICLE_BYTES),
                usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
            });
            var seed = new Float32Array(n * 6);
            var aspect = W / Math.max(1, H);
            for (var i = 0; i < n; i++) {
                seed[i * 6] = (Math.random() * 2 - 1) * aspect;
                seed[i * 6 + 1] = Math.random() * 2 - 1;
                seed[i * 6 + 4] = Math.random();
            }
            device.queue.writeBuffer(particleBuf, 0, seed);

            computeBind = device.createBindGroup({
                layout: computePipe.getBindGroupLayout(0),
                entries: [{ binding: 0, resource: { buffer: paramsBuf } }, { binding: 1, resource: { buffer: particleBuf } }]
            });
            partBind = device.createBindGroup({
                layout: partPipe.getBindGroupLayout(0),
                entries: [{ binding: 0, resource: { buffer: paramsBuf } }, { binding: 1, resource: { buffer: particleBuf } }]
            });
        }

        function resize(w, h) {
            W = Math.max(1, w); H = Math.max(1, h);
            canvas.width = W; canvas.height = H;
            if (sceneTex) sceneTex.destroy();
            sceneTex = device.createTexture({
                size: [W, H],
                format: 'rgba8unorm',
                usage: GPUTextureUsage.RENDER_ATTACHMENT | GPUTextureUsage.TEXTURE_BINDING
            });
            sceneView = sceneTex.createView();
            fieldBind = device.createBindGroup({
                layout: fieldPipe.getBindGroupLayout(0),
                entries: [{ binding: 0, resource: { buffer: paramsBuf } }]
            });
            compositeBind = device.createBindGroup({
                layout: compositePipe.getBindGroupLayout(0),
                entries: [
                    { binding: 0, resource: { buffer: paramsBuf } },
                    { binding: 1, resource: sampler },
                    { binding: 2, resource: sceneView },
                    { binding: 3, resource: { buffer: rectBuf } }
                ]
            });
        }

        function writeParams(dt, elapsed, energy) {
            paramsData[0] = W; paramsData[1] = H;
            paramsData[2] = elapsed; paramsData[3] = dt;
            paramsData[4] = W / Math.max(1, H); paramsData[5] = energy;
            paramsData[6] = count; paramsData[7] = glassCount;
            paramsData[8] = colors[0][0]; paramsData[9] = colors[0][1]; paramsData[10] = colors[0][2]; paramsData[11] = 1;
            paramsData[12] = colors[1][0]; paramsData[13] = colors[1][1]; paramsData[14] = colors[1][2]; paramsData[15] = 1;
            paramsData[16] = colors[2][0]; paramsData[17] = colors[2][1]; paramsData[18] = colors[2][2]; paramsData[19] = 1;
            device.queue.writeBuffer(paramsBuf, 0, paramsData);
        }

        function frame(dt, elapsed, energy) {
            if (dead || !sceneView || !particleBuf) return;
            writeParams(dt, elapsed, energy || 0);

            var enc = device.createCommandEncoder();

            var cp = enc.beginComputePass();
            cp.setPipeline(computePipe);
            cp.setBindGroup(0, computeBind);
            cp.dispatchWorkgroups(Math.ceil(count / 64));
            cp.end();

            var scenePass = enc.beginRenderPass({
                colorAttachments: [{
                    view: sceneView,
                    clearValue: { r: 0, g: 0, b: 0, a: 0 },
                    loadOp: 'clear', storeOp: 'store'
                }]
            });
            scenePass.setPipeline(fieldPipe);
            scenePass.setBindGroup(0, fieldBind);
            scenePass.draw(3);
            scenePass.setPipeline(partPipe);
            scenePass.setBindGroup(0, partBind);
            scenePass.draw(6, count);
            scenePass.end();

            var out = enc.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0, g: 0, b: 0, a: 0 },
                    loadOp: 'clear', storeOp: 'store'
                }]
            });
            out.setPipeline(compositePipe);
            out.setBindGroup(0, compositeBind);
            out.draw(3);
            out.end();

            device.queue.submit([enc.finish()]);
        }

        return {
            tier: 'webgpu',

            resize: function (w, h) {
                resize(w, h);
                if (!particleBuf) allocParticles(Math.round(BASE_PARTICLES * quality.particles));
            },

            setColors: function (c) { colors = c; },

            setQuality: function (q) {
                quality = q;
                allocParticles(Math.max(64, Math.round(BASE_PARTICLES * q.particles)));
            },

            setGlass: function (list, n, vw, vh) {
                glassCount = Math.min(n, MAX_GLASS_RECTS);
                var sx = W / vw, sy = H / vh;
                for (var i = 0; i < glassCount; i++) {
                    rectData[i * 6] = list[i * 5] * sx;
                    rectData[i * 6 + 1] = list[i * 5 + 1] * sy;
                    rectData[i * 6 + 2] = list[i * 5 + 2] * sx;
                    rectData[i * 6 + 3] = list[i * 5 + 3] * sy;
                    rectData[i * 6 + 4] = list[i * 5 + 4] * sx;
                    rectData[i * 6 + 5] = 0;
                }
                device.queue.writeBuffer(rectBuf, 0, rectData);
            },

            frame: frame,

            dispose: function () {
                dead = true;
                try {
                    if (particleBuf) particleBuf.destroy();
                    if (sceneTex) sceneTex.destroy();
                    paramsBuf.destroy();
                    rectBuf.destroy();
                    device.destroy();
                } catch (err) { /* teardown is best-effort */ }
            }
        };
    }

    window.gfxWebgpu = { create: create };
})();
