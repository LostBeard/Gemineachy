// Duck.ai shape details for the adapter: (1) how a MULTI-LINE user message renders (can single lines be
// hidden?), (2) the reply's markdown root vs its header chrome, (3) Stop button visibility right now.
(() => {
  const users = document.querySelectorAll('[data-testid="user-message"]');
  const u = users[users.length - 1];
  const replies = document.querySelectorAll('[data-activeresponse]');
  const r = replies[replies.length - 1];
  const stop = document.querySelector('button[aria-label="Stop generating"]');
  const rect = stop ? stop.getBoundingClientRect() : null;
  return JSON.stringify({
    userHtml: u ? u.innerHTML.replace(/class="[^"]*"/g, '').slice(0, 700) : null,
    replyKids: r ? [...r.querySelectorAll('[data-dark-theme], .space-y-4')].map(e => e.tagName + '.' + (e.className || '').toString().slice(0, 50) + ' text=' + (e.textContent || '').slice(0, 60)) : null,
    replyTextStart: r ? (r.textContent || '').slice(0, 80) : null,
    stop: stop ? { w: rect.width, h: rect.height, display: getComputedStyle(stop).display, visibility: getComputedStyle(stop).visibility, hidden: stop.hidden, ariaHidden: stop.closest('[aria-hidden="true"]') ? 'in aria-hidden' : '' } : null,
  }, null, 1);
})()
