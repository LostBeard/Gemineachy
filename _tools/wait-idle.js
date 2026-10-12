// Wait until the chat has been idle (nothing generating, user-turn count unchanged) for 15 consecutive
// seconds - i.e. the whole tool loop (call -> results -> follow-up) has settled. Max ~170 s.
// Needs site-table.js prepended:  cdp.cs <site> file:site-table.js+wait-idle.js
(async () => {
  const S = window.__gemSite;
  if (!S) return JSON.stringify({ error: 'no site-table entry for ' + location.host });
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const turns = () => document.querySelectorAll(S.user).length;
  let quiet = 0, last = turns(), t = 0;
  for (; t < 170 && quiet < 15; t++) {
    await sleep(1000);
    const n = turns();
    if (S.busy() || n !== last) { quiet = 0; last = n; } else quiet++;
  }
  return JSON.stringify({ waitedSeconds: t, turns: turns(), settled: quiet >= 15 });
})()
