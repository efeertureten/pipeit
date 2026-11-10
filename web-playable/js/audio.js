/*
 * Su Ustası — synthesized SFX (Web Audio API, no audio files).
 *   water(combo)  rising-pitch splash: +2 semitones per consecutive successful exit
 *   bonk()        metallic clank on a blocked tap (inharmonic partials + thud)
 *   drain()       gurgling drain sweep when the tank empties (win)
 *   + crack / ice / thaw / unlock / rotate / leak / hammer / patch / win / lose / coin / click
 */
(function (root) {
  'use strict';
  let ctx = null, master = null, noiseBuf = null;
  let enabled = true;

  function ensure() {
    if (!enabled) return null;
    if (!ctx) {
      const AC = root.AudioContext || root.webkitAudioContext;
      if (!AC) return null;
      ctx = new AC();
      master = ctx.createGain();
      master.gain.value = 0.55;
      const comp = ctx.createDynamicsCompressor();
      master.connect(comp);
      comp.connect(ctx.destination);
      const len = ctx.sampleRate * 2;
      noiseBuf = ctx.createBuffer(1, len, ctx.sampleRate);
      const d = noiseBuf.getChannelData(0);
      for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    }
    if (ctx.state === 'suspended') ctx.resume();
    return ctx;
  }

  function envGain(t0, attack, peak, decay, dest) {
    const g = ctx.createGain();
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(Math.max(peak, 0.0002), t0 + attack);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + attack + decay);
    g.connect(dest || master);
    return g;
  }

  function tone(type, f0, t0, dur, peak, f1, dest) {
    const o = ctx.createOscillator();
    o.type = type;
    o.frequency.setValueAtTime(f0, t0);
    if (f1) o.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
    const g = envGain(t0, Math.min(0.01, dur / 4), peak, dur, dest);
    o.connect(g);
    o.start(t0);
    o.stop(t0 + dur + 0.05);
    return o;
  }

  function noise(t0, dur, filterType, f0, f1, q, peak, dest) {
    const src = ctx.createBufferSource();
    src.buffer = noiseBuf;
    const f = ctx.createBiquadFilter();
    f.type = filterType;
    f.Q.value = q;
    f.frequency.setValueAtTime(f0, t0);
    if (f1) f.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
    const g = envGain(t0, Math.min(0.02, dur / 5), peak, dur, dest);
    src.connect(f); f.connect(g);
    src.start(t0, Math.random());
    src.stop(t0 + dur + 0.05);
  }

  const SFX = {
    setEnabled(v) { enabled = !!v; if (!enabled && ctx) ctx.suspend(); else if (enabled && ctx) ctx.resume(); },
    isEnabled() { return enabled; },
    unlock() { ensure(); },

    /** Successful exit. combo = 1,2,3... consecutive successes → pitch climbs 2 semitones each (cap 1 octave+). */
    water(combo) {
      if (!ensure()) return;
      const t = ctx.currentTime;
      const semis = Math.min(Math.max(combo - 1, 0), 7) * 2;
      const f = 330 * Math.pow(2, semis / 12);
      noise(t, 0.42, 'bandpass', 700, 2600, 1.2, 0.35);              // whoosh / spray
      tone('sine', f, t + 0.02, 0.16, 0.42, f * 1.7);                // rising "bloop"
      tone('triangle', f * 2, t + 0.09, 0.12, 0.16, f * 2.6);        // sparkle
      noise(t + 0.18, 0.25, 'lowpass', 1600, 500, 0.7, 0.12);         // splash tail
    },

    /** Blocked tap: metallic bonk. */
    bonk() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      const base = 165;
      [1, 2.62, 4.24, 6.08, 8.3].forEach((r, i) => tone(i % 2 ? 'square' : 'sine', base * r, t, 0.32 - i * 0.04, 0.22 / (i + 1)));
      tone('sine', 110, t, 0.12, 0.5, 55);                           // body thud
      noise(t, 0.05, 'highpass', 3000, null, 0.8, 0.18);              // impact click
    },

    /** Tank drains (win). */
    drain() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      noise(t, 1.7, 'lowpass', 2200, 180, 1.4, 0.32);
      const o = ctx.createOscillator();
      o.type = 'sine';
      o.frequency.setValueAtTime(320, t);
      o.frequency.exponentialRampToValueAtTime(70, t + 1.6);
      const lfo = ctx.createOscillator();
      const lfoGain = ctx.createGain();
      lfo.frequency.value = 9; lfoGain.gain.value = 40;
      lfo.connect(lfoGain); lfoGain.connect(o.frequency);
      const g = envGain(t, 0.08, 0.25, 1.6);
      o.connect(g);
      o.start(t); lfo.start(t);
      o.stop(t + 1.8); lfo.stop(t + 1.8);
    },

    crack() { // rust breaks
      if (!ensure()) return;
      const t = ctx.currentTime;
      [0, 0.045, 0.1].forEach((d, i) => noise(t + d, 0.06, 'highpass', 1800 + i * 600, null, 1, 0.35));
      tone('square', 240, t, 0.08, 0.12, 120);
    },
    ice() { // tapping frozen pipe / ice counter ticks
      if (!ensure()) return;
      const t = ctx.currentTime;
      [2093, 2637, 3136].forEach((f, i) => tone('sine', f * (1 + (Math.random() - 0.5) * 0.02), t + i * 0.03, 0.25, 0.12));
    },
    thaw() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      noise(t, 0.35, 'bandpass', 4000, 1200, 2, 0.22);
      [1568, 2093, 2637, 3520].forEach((f, i) => tone('sine', f, t + i * 0.05, 0.2, 0.1));
    },
    unlockChain() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      tone('square', 1200, t, 0.03, 0.15);
      tone('square', 900, t + 0.06, 0.03, 0.15);
      [0.1, 0.15, 0.21, 0.26].forEach((d) => noise(t + d, 0.05, 'bandpass', 5200, null, 4, 0.2));
    },
    rotate() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      [0, 0.05, 0.1].forEach((d, i) => tone('square', 900 - i * 80, t + d, 0.018, 0.12));
      tone('sine', 520, t, 0.18, 0.08, 780);
    },
    leak() { // leaking pipe burst
      if (!ensure()) return;
      const t = ctx.currentTime;
      tone('sine', 1500, t, 0.08, 0.3, 480);
      tone('sine', 1300, t + 0.11, 0.08, 0.25, 420);
      noise(t, 0.4, 'bandpass', 1500, 700, 1, 0.25);
    },
    hammer() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      tone('sine', 90, t, 0.25, 0.7, 40);
      noise(t, 0.18, 'lowpass', 2500, 300, 0.8, 0.5);
      [1, 2.76, 5.4].forEach((r, i) => tone('triangle', 300 * r, t, 0.2, 0.12 / (i + 1)));
    },
    patch() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      [523, 659, 784].forEach((f, i) => tone('triangle', f, t + i * 0.07, 0.14, 0.2));
    },
    xray() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      tone('sine', 400, t, 0.5, 0.18, 1600);
      tone('sine', 600, t + 0.05, 0.45, 0.1, 2400);
    },
    win() {
      if (!ensure()) return;
      const t = ctx.currentTime + 0.1;
      [523.25, 659.25, 783.99, 1046.5].forEach((f, i) => tone('triangle', f, t + i * 0.11, 0.22, 0.28));
      tone('sine', 1046.5, t + 0.44, 0.6, 0.2);
    },
    lose() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      const o = ctx.createOscillator();
      o.type = 'sawtooth';
      o.frequency.setValueAtTime(392, t);
      o.frequency.exponentialRampToValueAtTime(150, t + 0.9);
      const f = ctx.createBiquadFilter();
      f.type = 'lowpass'; f.frequency.value = 1200;
      const g = envGain(t, 0.02, 0.2, 0.9);
      o.connect(f); f.connect(g);
      o.start(t); o.stop(t + 1);
      noise(t + 0.1, 0.8, 'bandpass', 1200, 400, 1, 0.2);
    },
    coin() {
      if (!ensure()) return;
      const t = ctx.currentTime;
      tone('sine', 1318.5, t, 0.08, 0.2);
      tone('sine', 1760, t + 0.07, 0.18, 0.2);
    },
    click() {
      if (!ensure()) return;
      tone('triangle', 660, ctx.currentTime, 0.04, 0.1);
    },
  };

  root.SuAudio = SFX;
})(typeof self !== 'undefined' ? self : this);
