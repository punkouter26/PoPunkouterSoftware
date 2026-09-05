/**
 * Audio kit — programmatic sound for the app. Zero audio assets, zero dependencies.
 *
 * Every sound here is synthesised at runtime from oscillators, a procedurally filled noise
 * buffer, and filter/envelope automation. Nothing is fetched. That is a deliberate match
 * for a project that lazy-loads Three.js specifically to keep ~600KB off routes that do not
 * need it: a handful of .mp3 one-shots would have cost more bytes than this entire file,
 * and a sample cannot be retuned per-datum the way `sonifyOps()` below needs.
 *
 * ── Silence is the default, and it is an accessibility floor ─────────────────────────────
 * `enabled` starts false and is only ever true because a visitor turned it on; the choice
 * persists in localStorage. An ops dashboard that makes noise at a visitor who did not ask
 * for it reads as broken, not polished.
 *
 * The AudioContext is additionally never CONSTRUCTED until a real user gesture, even when
 * storage says the visitor already opted in. Constructing one during page load produces a
 * context stuck in `suspended` state under every current browser autoplay policy, which
 * then silently swallows the first few sounds and — worse — leaves an audio graph and a
 * hardware device claim alive on a page that may never make a sound. `unlock()` is wired to
 * the first pointerdown/keydown and to the toggle button itself (a click IS a gesture, so
 * turning sound on plays its own confirmation immediately).
 *
 * ── The bus ─────────────────────────────────────────────────────────────────────────────
 *     voices → masterGain → limiter (DynamicsCompressor) → analyser → destination
 *
 * The limiter is not decoration. `sonifyOps()` can stack a dozen simultaneous partials whose
 * count is driven by live Azure findings — an unbounded sum of oscillators clips hard and
 * sounds like a fault. The analyser sits AFTER the limiter so the visual coupling in
 * gpu-backdrop.js tracks what the visitor actually hears, not what was requested.
 *
 * ── Why the dashboard makes sound at all ────────────────────────────────────────────────
 * Two of the three consumers carry information the eye is not looking at:
 *   • `refreshStart/Progress/End` — a full ARM subscription scan takes ~30 seconds, which is
 *     precisely the duration after which someone switches tabs. The rising filter sweep and
 *     the resolving chord say "it finished, and it worked" without the page being on screen.
 *     A failure resolves to a minor second instead; the two are unmistakable.
 *   • `sonifyOps` — the whole fleet state as one four-second gesture. This is a second
 *     MODALITY on facts the page already renders, not a second copy of them, so it does not
 *     violate the "each fact appears once on /azure" rule.
 * The third (`play('tap')` and friends) is ordinary UI feedback and is the least important.
 */
(function () {
    'use strict';

    var STORAGE_KEY = 'pops:sound';
    var MASTER_LEVEL = 0.5;

    var ctx = null;
    var master = null;
    var limiter = null;
    var analyser = null;
    var noiseBuffer = null;
    var binData = null;
    var timeData = null;

    var enabled = readPreference();
    var energyEma = 0;

    // Live handle on the refresh drone so progress can retune it and End can resolve it.
    var drone = null;

    function readPreference() {
        try { return window.localStorage.getItem(STORAGE_KEY) === '1'; }
        catch (err) { return false; }   // private mode / blocked storage — stay silent
    }

    function writePreference(on) {
        try { window.localStorage.setItem(STORAGE_KEY, on ? '1' : '0'); }
        catch (err) { /* preference simply will not persist */ }
    }

    /**
     * Build the audio graph. Called only from a user-gesture handler — see the header.
     * Returns true when a running context is available.
     */
    function ensureContext() {
        if (!enabled) return false;
        if (ctx) {
            // Browsers re-suspend a context when the tab is backgrounded long enough.
            if (ctx.state === 'suspended') ctx.resume().catch(function () { });
            return ctx.state !== 'closed';
        }

        var Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return false;

        try {
            ctx = new Ctor({ latencyHint: 'interactive' });
        } catch (err) {
            console.warn('audio-kit: AudioContext unavailable', err);
            return false;
        }

        master = ctx.createGain();
        master.gain.value = MASTER_LEVEL;

        limiter = ctx.createDynamicsCompressor();
        limiter.threshold.value = -12;
        limiter.knee.value = 0;
        limiter.ratio.value = 20;
        limiter.attack.value = 0.003;
        limiter.release.value = 0.25;

        analyser = ctx.createAnalyser();
        analyser.fftSize = 256;           // 128 frequency bins; we downsample to 16
        analyser.smoothingTimeConstant = 0.7;
        binData = new Uint8Array(analyser.frequencyBinCount);
        timeData = new Uint8Array(analyser.fftSize);

        master.connect(limiter);
        limiter.connect(analyser);
        analyser.connect(ctx.destination);

        // One second of white noise, generated once and replayed at varying rates. Noise is
        // what makes a "tick" read as a tick rather than a tone; regenerating the buffer per
        // hit would allocate 44100 floats on every keystroke-sized interaction.
        noiseBuffer = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
        var data = noiseBuffer.getChannelData(0);
        for (var i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;

        if (ctx.state === 'suspended') ctx.resume().catch(function () { });
        return true;
    }

    function now() { return ctx ? ctx.currentTime : 0; }

    /**
     * One synth voice: oscillator → gain (ADSR) → optional lowpass → master.
     * Everything is scheduled against `ctx.currentTime`; nothing uses setTimeout, so a busy
     * main thread cannot smear the rhythm of a sonification.
     */
    function voice(opts) {
        if (!ensureContext()) return null;
        var t = now() + (opts.delay || 0);
        var dur = opts.duration || 0.2;
        var peak = opts.gain == null ? 0.2 : opts.gain;
        var attack = opts.attack == null ? 0.006 : opts.attack;

        var osc = ctx.createOscillator();
        osc.type = opts.type || 'sine';
        osc.frequency.setValueAtTime(opts.freq, t);
        if (opts.toFreq) osc.frequency.exponentialRampToValueAtTime(Math.max(1, opts.toFreq), t + dur);
        if (opts.detune) osc.detune.setValueAtTime(opts.detune, t);

        var g = ctx.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak), t + attack);
        // exponentialRamp cannot reach 0, so decay to a floor then hard-stop at the end.
        g.gain.exponentialRampToValueAtTime(0.0001, t + dur);

        var tail = g;
        if (opts.cutoff) {
            var lp = ctx.createBiquadFilter();
            lp.type = 'lowpass';
            lp.frequency.setValueAtTime(opts.cutoff, t);
            if (opts.toCutoff) lp.frequency.exponentialRampToValueAtTime(Math.max(60, opts.toCutoff), t + dur);
            lp.Q.value = opts.q == null ? 0.7 : opts.q;
            g.connect(lp);
            tail = lp;
        }

        osc.connect(g);
        tail.connect(master);
        osc.start(t);
        osc.stop(t + dur + 0.02);
        osc.onended = function () { try { tail.disconnect(); g.disconnect(); } catch (e) { } };
        return osc;
    }

    /** Filtered noise burst — ticks, clicks, and the "something is wrong" texture. */
    function noise(opts) {
        if (!ensureContext()) return;
        opts = opts || {};
        var t = now() + (opts.delay || 0);
        var dur = opts.duration || 0.09;
        var peak = opts.gain == null ? 0.12 : opts.gain;

        var src = ctx.createBufferSource();
        src.buffer = noiseBuffer;
        src.loop = true;
        src.playbackRate.value = opts.rate || 1;

        var bp = ctx.createBiquadFilter();
        bp.type = opts.filter || 'bandpass';
        bp.frequency.setValueAtTime(opts.freq || 2400, t);
        if (opts.toFreq) bp.frequency.exponentialRampToValueAtTime(Math.max(60, opts.toFreq), t + dur);
        bp.Q.value = opts.q == null ? 1.2 : opts.q;

        var g = ctx.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(peak, t + 0.004);
        g.gain.exponentialRampToValueAtTime(0.0001, t + dur);

        src.connect(bp);
        bp.connect(g);
        g.connect(master);
        src.start(t);
        src.stop(t + dur + 0.02);
        src.onended = function () { try { g.disconnect(); bp.disconnect(); } catch (e) { } };
    }

    /** A stack of voices sharing one envelope shape — used for every resolving cadence. */
    function chord(freqs, opts) {
        opts = opts || {};
        for (var i = 0; i < freqs.length; i++) {
            voice({
                freq: freqs[i],
                type: opts.type || 'triangle',
                duration: opts.duration || 0.5,
                gain: (opts.gain == null ? 0.16 : opts.gain) / Math.sqrt(freqs.length),
                delay: (opts.delay || 0) + i * (opts.stagger == null ? 0.035 : opts.stagger),
                cutoff: opts.cutoff || 3200,
                attack: opts.attack
            });
        }
    }

    // ── Semantic one-shots ──────────────────────────────────────────────────────────────
    // Named for the event, not the sound, so the palette can be retuned in one place.
    var SFX = {
        tap: function () { voice({ freq: 660, type: 'triangle', duration: 0.07, gain: 0.10, cutoff: 2600 }); },
        hover: function () { voice({ freq: 1180, type: 'sine', duration: 0.045, gain: 0.035, cutoff: 3400 }); },
        open: function () { voice({ freq: 420, toFreq: 720, type: 'triangle', duration: 0.16, gain: 0.10, cutoff: 2800 }); },
        close: function () { voice({ freq: 720, toFreq: 420, type: 'triangle', duration: 0.16, gain: 0.09, cutoff: 2400 }); },
        copy: function () { voice({ freq: 880, duration: 0.06, gain: 0.10, cutoff: 3000 }); voice({ freq: 1320, duration: 0.09, gain: 0.08, delay: 0.055, cutoff: 3400 }); },
        // A perfect fifth over the root, then the octave — unambiguously "finished, fine".
        success: function () { chord([440, 660, 880], { duration: 0.65, gain: 0.22, stagger: 0.055 }); },
        // Minor second. Deliberately unpleasant; it is reporting a failure.
        failure: function () { chord([220, 233.08], { duration: 0.75, gain: 0.20, stagger: 0, type: 'sawtooth', cutoff: 1200 }); },
        warn: function () { voice({ freq: 520, type: 'square', duration: 0.13, gain: 0.09, cutoff: 1600 }); voice({ freq: 392, type: 'square', duration: 0.18, gain: 0.09, delay: 0.14, cutoff: 1400 }); },
        cancel: function () { voice({ freq: 500, toFreq: 180, type: 'triangle', duration: 0.28, gain: 0.13, cutoff: 1800 }); },
        tick: function () { noise({ freq: 3200, duration: 0.035, gain: 0.06 }); }
    };

    // ── Refresh lifecycle (the SignalR-driven scan) ─────────────────────────────────────

    /**
     * Start the scan drone: two detuned sawtooths through a lowpass. `progress()` walks the
     * cutoff upward, so the sound literally opens up as the scan completes — the visitor
     * hears the percentage without reading it.
     */
    function refreshStart() {
        if (!ensureContext()) return;
        refreshStop(true);

        var t = now();
        var g = ctx.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(0.09, t + 0.35);

        var lp = ctx.createBiquadFilter();
        lp.type = 'lowpass';
        lp.frequency.setValueAtTime(220, t);
        lp.Q.value = 4;

        // Tremolo. When the hub is down the client shows an INDETERMINATE bar because
        // percent never arrives; `indeterminate()` deepens this LFO so the ear gets the
        // same "still working, no idea how far" message the bar is giving the eye.
        var lfo = ctx.createOscillator();
        var lfoGain = ctx.createGain();
        lfo.frequency.value = 0.7;
        lfoGain.gain.value = 0.012;
        lfo.connect(lfoGain);
        lfoGain.connect(g.gain);

        var a = ctx.createOscillator();
        var b = ctx.createOscillator();
        a.type = b.type = 'sawtooth';
        a.frequency.value = 55;
        b.frequency.value = 55;
        b.detune.value = 7;

        a.connect(lp); b.connect(lp);
        lp.connect(g);
        g.connect(master);
        a.start(t); b.start(t); lfo.start(t);

        drone = { a: a, b: b, lfo: lfo, lfoGain: lfoGain, gain: g, filter: lp };
        SFX.open();
    }

    /** percent 0..100 → filter cutoff 220Hz..2600Hz, plus a tick on each 10% boundary. */
    var lastTickBucket = -1;
    function refreshProgress(percent) {
        if (!drone || !ctx) return;
        var p = Math.max(0, Math.min(100, percent)) / 100;
        var target = 220 + Math.pow(p, 1.4) * 2400;
        drone.filter.frequency.cancelScheduledValues(now());
        drone.filter.frequency.setTargetAtTime(target, now(), 0.25);
        drone.a.frequency.setTargetAtTime(55 + p * 12, now(), 0.4);
        drone.b.frequency.setTargetAtTime(55 + p * 12, now(), 0.4);

        var bucket = Math.floor(p * 10);
        if (bucket !== lastTickBucket) {
            lastTickBucket = bucket;
            SFX.tick();
        }
    }

    /** No percent is arriving (hub down). Deepen and slow the tremolo instead of lying. */
    function refreshIndeterminate() {
        if (!drone || !ctx) return;
        drone.lfo.frequency.setTargetAtTime(0.45, now(), 0.3);
        drone.lfoGain.gain.setTargetAtTime(0.035, now(), 0.3);
    }

    function refreshStop(silent) {
        lastTickBucket = -1;
        if (!drone || !ctx) return;
        var t = now();
        var d = drone;
        drone = null;
        try {
            d.gain.gain.cancelScheduledValues(t);
            d.gain.gain.setValueAtTime(Math.max(0.0002, d.gain.gain.value), t);
            d.gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.25);
            d.a.stop(t + 0.3); d.b.stop(t + 0.3); d.lfo.stop(t + 0.3);
            d.a.onended = function () {
                try { d.gain.disconnect(); d.filter.disconnect(); d.lfoGain.disconnect(); } catch (e) { }
            };
        } catch (err) { /* already stopped */ }
        if (silent) return;
    }

    /** outcome: 'success' | 'failure' | 'cancelled' | 'timeout' */
    function refreshEnd(outcome) {
        refreshStop(true);
        if (!ensureContext()) return;
        if (outcome === 'success') SFX.success();
        else if (outcome === 'cancelled') SFX.cancel();
        else if (outcome === 'timeout') SFX.warn();
        else SFX.failure();
    }

    // ── Sonification of the fleet (#2) ──────────────────────────────────────────────────

    /**
     * Render the whole ops summary as one ~4.2 second gesture.
     *
     * Only primitives cross the interop boundary — seven numbers — matching the discipline
     * every interop call in this app already follows. Nothing here needs a serialised DTO, and the
     * WASM trim analyzer stays clean because no new type is reflected over.
     *
     * The mapping, and why each choice is the honest one:
     *   • health      → detune of the upper partials. A perfectly healthy fleet is a pure
     *                   stacked fifth; degradation literally puts the chord out of tune.
     *   • broken      → a tritone partial, gain scaled by count and hard-capped. The most
     *                   dissonant interval in the western system, reserved for the most
     *                   serious fact, and capped so 40 outages are not 40x louder than 4.
     *   • actionable  → the number of ticks, capped at 8. Countable by ear up to about that.
     *   • security    → a bright high partial; findings are "sharp", not "loud".
     *   • cleanup     → a soft low pulse; waste is dull weight, not an alarm.
     *   • forecast    → tremolo RATE. Over budget beats faster: urgency, not volume.
     *   • uptime      → master lowpass cutoff. A fleet with poor uptime sounds muffled and
     *                   distant; a perfect one is open and present.
     */
    function sonifyOps(health, broken, actionable, security, cleanup, forecastRatio, uptime) {
        if (!ensureContext()) return;

        var t = now() + 0.05;
        var root = 110;                                        // A2
        var detune = (100 - clamp(health, 0, 100)) * 0.9;      // cents, 0..90
        var cutoff = 500 + clamp(uptime, 0, 100) * 60;         // 500..6500 Hz
        var lfoHz = 1.2 + clamp(forecastRatio, 0, 2) * 3.9;    // 1.2..9 Hz

        var bus = ctx.createGain();
        bus.gain.setValueAtTime(0.0001, t);
        bus.gain.exponentialRampToValueAtTime(0.5, t + 0.5);
        bus.gain.setValueAtTime(0.5, t + 3.2);
        bus.gain.exponentialRampToValueAtTime(0.0001, t + 4.2);

        var lp = ctx.createBiquadFilter();
        lp.type = 'lowpass';
        lp.frequency.setValueAtTime(cutoff, t);
        lp.Q.value = 1.1;

        var lfo = ctx.createOscillator();
        var lfoGain = ctx.createGain();
        lfo.frequency.value = lfoHz;
        lfoGain.gain.value = 0.10;
        lfo.connect(lfoGain);
        lfoGain.connect(bus.gain);
        lfo.start(t);
        lfo.stop(t + 4.3);

        lp.connect(bus);
        bus.connect(master);

        var partials = [
            { mul: 1.0, gain: 0.22, cents: 0 },
            { mul: 1.5, gain: 0.15, cents: detune },
            { mul: 2.0, gain: 0.11, cents: -detune },
            { mul: 3.0, gain: 0.07, cents: detune * 1.5 }
        ];

        if (broken > 0) {
            partials.push({
                mul: 1.4142,                                  // tritone
                gain: Math.min(0.20, 0.05 + broken * 0.035),
                cents: 0, type: 'sawtooth'
            });
        }
        if (security > 0) {
            partials.push({ mul: 6.0, gain: Math.min(0.05, 0.012 * security), cents: 0 });
        }
        if (cleanup > 0) {
            partials.push({ mul: 0.5, gain: Math.min(0.09, 0.02 * cleanup), cents: 0 });
        }

        for (var i = 0; i < partials.length; i++) {
            var p = partials[i];
            var osc = ctx.createOscillator();
            osc.type = p.type || 'triangle';
            osc.frequency.value = root * p.mul;
            osc.detune.value = p.cents;
            var g = ctx.createGain();
            g.gain.value = p.gain;
            osc.connect(g);
            g.connect(lp);
            osc.start(t);
            osc.stop(t + 4.3);
        }

        // Countable ticks for the actionable items, inside the sustained section.
        var ticks = Math.min(8, Math.max(0, actionable | 0));
        for (var k = 0; k < ticks; k++) {
            noise({ delay: 0.9 + k * 0.22, freq: 2600, duration: 0.05, gain: 0.09 });
        }

        // Resolve. A healthy fleet ends on a consonant cadence; a broken one does not get
        // one — the drone simply fades, which is the point.
        if (broken === 0 && actionable === 0) {
            chord([root * 2, root * 3, root * 4], { delay: 3.3, duration: 0.9, gain: 0.16, stagger: 0.07 });
        }
    }

    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

    // ── Analyser tap for the audio-reactive shaders (#4) ────────────────────────────────

    /**
     * Smoothed output RMS in 0..1. Pull-based on purpose: the render loop asks when it is
     * about to draw a frame, so nothing is computed on frames the governor never serves and
     * there is no push subscription coupling audio to graphics.
     */
    function energy() {
        if (!analyser || !ctx || ctx.state !== 'running') {
            energyEma *= 0.85;                     // decay to silence rather than snapping
            return energyEma < 0.001 ? 0 : energyEma;
        }
        analyser.getByteTimeDomainData(timeData);
        var sum = 0;
        for (var i = 0; i < timeData.length; i++) {
            var v = (timeData[i] - 128) / 128;
            sum += v * v;
        }
        var rms = Math.sqrt(sum / timeData.length);
        energyEma = energyEma * 0.7 + Math.min(1, rms * 3.2) * 0.3;
        return energyEma;
    }

    /** Fill `out` (length 16) with a downsampled 0..1 spectrum. Returns false when silent. */
    function spectrum(out) {
        if (!analyser || !ctx || ctx.state !== 'running') return false;
        analyser.getByteFrequencyData(binData);
        var per = Math.floor(binData.length / out.length) || 1;
        for (var i = 0; i < out.length; i++) {
            var acc = 0;
            for (var j = 0; j < per; j++) acc += binData[i * per + j];
            out[i] = acc / per / 255;
        }
        return true;
    }

    // ── Gesture unlock + the toggle control ─────────────────────────────────────────────

    var unlockBound = false;
    function bindUnlock() {
        if (unlockBound) return;
        unlockBound = true;
        var fire = function () {
            document.removeEventListener('pointerdown', fire, true);
            document.removeEventListener('keydown', fire, true);
            unlockBound = false;
            ensureContext();
        };
        document.addEventListener('pointerdown', fire, true);
        document.addEventListener('keydown', fire, true);
    }

    /** Push `enabled` onto every toggle in the DOM. Idempotent; safe after enhanced nav. */
    function syncToggles() {
        var nodes = document.querySelectorAll('[data-sound-toggle]');
        for (var i = 0; i < nodes.length; i++) {
            var el = nodes[i];
            el.setAttribute('aria-pressed', String(enabled));
            el.setAttribute('title', enabled ? 'Sound on — click to mute' : 'Sound off — click to enable interface and status audio');
            el.setAttribute('aria-label', enabled ? 'Turn sound off' : 'Turn sound on');
            var icon = el.querySelector('.material-symbols-outlined');
            if (icon) icon.textContent = enabled ? 'volume_up' : 'volume_off';
            el.classList.toggle('is-on', enabled);
        }
    }

    function setEnabled(on) {
        on = !!on;
        if (on === enabled) { syncToggles(); return; }
        enabled = on;
        writePreference(on);

        if (!on) {
            refreshStop(true);
            if (ctx) ctx.suspend().catch(function () { });
        } else if (ensureContext()) {
            SFX.copy();     // confirmation: you turned it on, and here is what "on" sounds like
        }
        syncToggles();
    }

    // Delegated rather than bound per element: MainLayout is statically server-rendered and
    // Blazor's enhanced navigation re-inserts the header on every page change, so a direct
    // listener (or a `dataset.wired` guard) would be lost or double-bound. Delegation on
    // `document` survives any amount of DOM replacement — and, just as importantly, it
    // means a Blazor component that renders an SFX-annotated element needs no JS interop,
    // no OnAfterRender hook and no disposal path. It just writes the attribute.
    document.addEventListener('click', function (e) {
        if (!e.target || !e.target.closest) return;

        var toggle = e.target.closest('[data-sound-toggle]');
        if (toggle) {
            e.preventDefault();
            setEnabled(!enabled);
            return;
        }

        var sfx = e.target.closest('[data-sfx]');
        if (sfx && enabled) {
            var fn = SFX[sfx.getAttribute('data-sfx')];
            if (fn) fn();
        }
    });

    // Hover cues are pointer-type-gated: on a touch screen `pointerenter` fires as part of
    // the tap, so an un-gated hover sound would double up with the tap sound on every
    // press. A hover cue is only meaningful where hovering is a distinct act.
    document.addEventListener('pointerenter', function (e) {
        if (!enabled || e.pointerType !== 'mouse') return;
        if (!e.target || !e.target.closest) return;
        var el = e.target.closest('[data-sfx-hover]');
        if (!el) return;
        var fn = SFX[el.getAttribute('data-sfx-hover')];
        if (fn) fn();
    }, true);

    // Re-apply state to a header that enhanced navigation has just re-inserted.
    var syncQueued = false;
    new MutationObserver(function () {
        if (syncQueued) return;
        syncQueued = true;
        requestAnimationFrame(function () { syncQueued = false; syncToggles(); });
    }).observe(document.documentElement, { childList: true, subtree: true });

    // Hydrate the toggle(s) on first paint. The static SSR markup hardcodes the "sound off"
    // treatment (the only honest default — a JS-driven guess would flash a speaker icon at a
    // visitor who has never enabled sound), so without this call a returning user with
    // localStorage `pops:sound = '1'` saw a one-frame "volume_off" before the next
    // MutationObserver tick corrected it. Sync once at module load so the header reflects
    // the saved preference before the first paint settles.
    syncToggles();

    window.audioKit = {
        get enabled() { return enabled; },
        setEnabled: setEnabled,
        toggle: function () { setEnabled(!enabled); },

        /** True only when a context exists AND is actually running. */
        get live() { return !!ctx && ctx.state === 'running'; },

        /** Semantic one-shot by name; unknown names are ignored rather than throwing. */
        play: function (name) {
            if (!enabled) return;
            var fn = SFX[name];
            if (fn) fn();
        },

        refreshStart: function () { if (enabled) refreshStart(); },
        refreshProgress: function (percent) { if (enabled) refreshProgress(percent); },
        refreshIndeterminate: function () { if (enabled) refreshIndeterminate(); },
        refreshEnd: function (outcome) { if (enabled) refreshEnd(outcome); },

        sonifyOps: function (health, broken, actionable, security, cleanup, forecastRatio, uptime) {
            if (enabled) sonifyOps(health, broken, actionable, security, cleanup, forecastRatio, uptime);
        },

        energy: energy,
        spectrum: spectrum,

        /** Diagnostics, and the hook the E2E silence assertion reads. */
        stats: function () {
            return {
                enabled: enabled,
                contextCreated: !!ctx,
                state: ctx ? ctx.state : 'none',
                energy: Math.round(energyEma * 1000) / 1000
            };
        }
    };

    // Arm the gesture unlock only when the visitor has already opted in; otherwise the
    // context is not created until they press the toggle (which is itself the gesture).
    if (enabled) bindUnlock();
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', syncToggles, { once: true });
    } else {
        syncToggles();
    }
})();
