// Wait (up to ~120 s) for Gemini to finish its chess move, then report the board banner, the piece count
// and the tool traffic in the chat. Pass: the banner returns to "White's turn" and no tool error appears.
(async () => {
  function deepAll(root) {
    const acc = [];
    const walk = (node) => { for (const el of node.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } };
    walk(root); return acc;
  }
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));
  const banner = () => (deepAll(document).find(e => e.classList?.contains('status-banner'))?.textContent || '').trim();
  let status = banner();
  let waited = 0;
  for (; waited < 120 && !/White's turn/.test(status); waited += 2) { await sleep(2000); status = banner(); }
  const pieces = deepAll(document).filter(e => e.classList?.contains('piece')).length;
  const tool = [...document.querySelectorAll('[data-gemineachy-tool]')].map(e => (e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 160));
  return JSON.stringify({ waitedSeconds: waited, status, pieces, toolTraffic: tool.slice(-6) }, null, 1);
})()
