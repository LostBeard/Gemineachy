(async () => {
  const el = document.querySelector('audio[data-gemineachy-reachy-audio]');
  if (!el || !el.srcObject) return JSON.stringify({ error: 'no playing element' });
  const ctx = new (window.AudioContext || window.webkitAudioContext)();
  try { await ctx.resume(); } catch (e) {}
  const src = ctx.createMediaStreamSource(el.srcObject);
  const an = ctx.createAnalyser();
  an.fftSize = 2048;
  src.connect(an);                       // analyser only - not connected to destination, so no double-play
  const buf = new Float32Array(an.fftSize);
  let peak = 0, sumSq = 0, n = 0;
  const t0 = performance.now();
  while (performance.now() - t0 < 3000) {
    an.getFloatTimeDomainData(buf);
    for (let i = 0; i < buf.length; i++) { const v = Math.abs(buf[i]); if (v > peak) peak = v; sumSq += buf[i] * buf[i]; n++; }
    await new Promise(r => setTimeout(r, 50));
  }
  const rms = Math.sqrt(sumSq / Math.max(1, n));
  const out = { ctxState: ctx.state, sampleRate: ctx.sampleRate, peak: +peak.toFixed(5), rms: +rms.toFixed(5),
                rmsDbfs: rms > 0 ? +(20 * Math.log10(rms)).toFixed(1) : null };
  try { src.disconnect(); await ctx.close(); } catch (e) {}
  return JSON.stringify(out);
})()
