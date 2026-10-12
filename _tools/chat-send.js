// Type window.__geminiMsg into the current chat site's compose box and click its Send button - the way a
// user does it (the extension's first-send hook may intercept the click).
// Needs site-table.js prepended:  cdp.cs <site> file:site-table.js+chat-send.js
(async () => {
  const S = window.__gemSite;
  if (!S) return JSON.stringify({ error: 'no site-table entry for ' + location.host });
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const text = window.__geminiMsg || 'hello';
  const box = document.querySelector(S.compose);
  if (!box) return JSON.stringify({ error: 'no compose box' });
  box.focus();
  if (box.tagName === 'TEXTAREA') {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(box, text);
    box.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, text);
  }
  let send = null;
  for (let i = 0; i < 20 && !send; i++) {
    await sleep(200);
    const b = document.querySelector(S.send);
    if (b && !b.disabled && b.getAttribute('aria-disabled') !== 'true') send = b;
  }
  if (!send) return JSON.stringify({ error: 'send never enabled' });
  send.click();
  await sleep(800);
  return JSON.stringify({ via: 'button', text: text.slice(0, 80) });
})()
