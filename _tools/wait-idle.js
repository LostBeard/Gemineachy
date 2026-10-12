// Wait until the chat has been idle (nothing generating, user-turn count unchanged) for 15 consecutive
// seconds - i.e. the whole tool loop (call -> results -> follow-up) has settled. Max ~170 s.
// Site-aware: Gemini (Stop button / aria-busy) and Duck.ai (visible Stop, data-activeresponse="false").
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const duck = location.host === 'duck.ai';
  const turns = () => document.querySelectorAll(duck ? '[data-testid="user-message"]' : 'chat-window user-query').length;
  const busy = () => {
    if (duck) {
      const stop = document.querySelector('button[aria-label="Stop generating"]');
      const r = stop ? stop.getBoundingClientRect() : null;
      return !!(r && r.width > 0) || !!document.querySelector('[data-activeresponse="false"]');
    }
    return !!document.querySelector('chat-window button[aria-label*="Stop"]')
        || !!document.querySelector('chat-window model-response [aria-busy="true"]');
  };
  let quiet = 0, last = turns(), t = 0;
  for (; t < 170 && quiet < 15; t++) {
    await sleep(1000);
    const n = turns();
    if (busy() || n !== last) { quiet = 0; last = n; } else quiet++;
  }
  return JSON.stringify({ waitedSeconds: t, turns: turns(), settled: quiet >= 15 });
})()
