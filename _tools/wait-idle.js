// Wait until the Gemini chat has been idle (no Stop button, turn count unchanged) for 8 consecutive
// seconds - i.e. the whole tool loop (call -> results -> follow-up) has settled. Max ~170 s.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const turns = () => document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  const busy = () => !!document.querySelector('chat-window button[aria-label*="Stop"]');
  let quiet = 0, last = turns(), t = 0;
  for (; t < 170 && quiet < 8; t++) {
    await sleep(1000);
    const n = turns();
    if (busy() || n !== last) { quiet = 0; last = n; } else quiet++;
  }
  return JSON.stringify({ waitedSeconds: t, turns: turns(), settled: quiet >= 8 });
})()
