// Type character-by-character-ish then press Enter on the editor, re-querying the editor each step.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ed = document.querySelector('div.ql-editor[contenteditable="true"]');
  if (!ed) return JSON.stringify({ error: 'no editor' });
  ed.focus();
  const before = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  const opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true, composed: true };
  ed.dispatchEvent(new KeyboardEvent('keydown', opts));
  ed.dispatchEvent(new KeyboardEvent('keypress', opts));
  ed.dispatchEvent(new KeyboardEvent('keyup', opts));
  await sleep(3000);
  const after = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  return JSON.stringify({ before, after, editor: (ed.textContent||'').slice(0,50) });
})()
