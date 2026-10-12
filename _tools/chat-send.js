// Type window.__geminiMsg into the current chat site's compose box and click its Send button - the way a
// user does it (the extension's first-send hook may intercept the click). Site-aware (Gemini, Duck.ai).
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const text = window.__geminiMsg || 'hello';
  const duck = location.host === 'duck.ai';
  if (duck) {
    const ta = document.querySelector('textarea[name="user-prompt"]');
    if (!ta) return JSON.stringify({ error: 'no compose box' });
    ta.focus();
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(ta, text);
    ta.dispatchEvent(new Event('input', { bubbles: true }));
    await sleep(400);
    const send = document.querySelector('form[data-chat-footer] button[type="submit"]');
    if (!send || send.disabled) return JSON.stringify({ error: 'send not enabled' });
    send.click();
    await sleep(800);
    return JSON.stringify({ via: 'button', text, editorAfter: ta.value.slice(0, 80) });
  }
  const ed = document.querySelector('chat-window div[contenteditable="true"]');
  if (!ed) return JSON.stringify({ error: 'no editor' });
  ed.focus();
  document.execCommand('selectAll', false, null);
  document.execCommand('insertText', false, text);
  await sleep(600);
  const send = document.querySelector('chat-window button[aria-label*="Send"]');
  if (!send) return JSON.stringify({ error: 'no send button' });
  send.click();
  await sleep(800);
  return JSON.stringify({ via: 'button', text, editorAfter: ed.textContent.slice(0, 80) });
})()
