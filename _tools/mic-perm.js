(async () => {
  let mic = 'unknown';
  try { mic = (await navigator.permissions.query({ name: 'microphone' })).state; } catch (e) { mic = 'query failed: ' + e; }
  const devs = await navigator.mediaDevices.enumerateDevices();
  const inputs = devs.filter(d => d.kind === 'audioinput');
  return JSON.stringify({
    micPermission: mic,
    audioInputs: inputs.length,
    // labels are only exposed once permission has been granted - a good second signal
    labelsVisible: inputs.some(d => !!d.label),
    firstLabels: inputs.slice(0, 3).map(d => d.label || '(hidden)')
  }, null, 1);
})()
