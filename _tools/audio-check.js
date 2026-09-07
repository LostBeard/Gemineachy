(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const el = document.querySelector('audio[data-gemineachy-reachy-audio]');
  if (!el) return JSON.stringify({ error: 'no <audio data-gemineachy-reachy-audio> in the document' });
  const snap = () => {
    let tracks = null;
    try {
      const so = el.srcObject;
      tracks = so && so.getAudioTracks ? so.getAudioTracks().map(t => ({ id: t.id, kind: t.kind, readyState: t.readyState, muted: t.muted, enabled: t.enabled })) : 'srcObject not readable from page world';
    } catch (e) { tracks = 'srcObject threw: ' + e; }
    return { paused: el.paused, currentTime: el.currentTime, readyState: el.readyState, muted: el.muted, volume: el.volume, tracks };
  };
  const a = snap();
  await sleep(3000);
  const b = snap();
  return JSON.stringify({
    robot: el.getAttribute('data-gemineachy-reachy-audio'),
    first: a, afterThreeSeconds: b,
    // currentTime advancing is the browser actually rendering the received stream
    clockAdvanced: +(b.currentTime - a.currentTime).toFixed(3)
  }, null, 1);
})()
