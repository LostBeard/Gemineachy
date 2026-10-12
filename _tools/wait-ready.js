// Wait (max 40 s) until the chat page is usable AND the extension has booted on it: the site's compose box exists
// and the always-on scrape self-test has written its marker. Use after any new-chat that reloads the page.
// Needs site-table.js prepended:  cdp.cs <site> file:site-table.js+wait-ready.js
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  for (let i = 0; i < 80; i++) {
    const S = window.__gemSite;
    const ok = S && document.querySelector(S.compose) && document.documentElement.getAttribute('data-gem-scrapetest');
    if (ok) return JSON.stringify({ ready: true, waitedMs: i * 500, turns: document.querySelectorAll(S.user).length });
    await sleep(500);
  }
  return JSON.stringify({ ready: false });
})()
