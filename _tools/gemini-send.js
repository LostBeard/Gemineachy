// Type a message into Gemini's compose box and send it via Enter. Set window.__geminiMsg to the text.
// Gemini uses a Quill editor (div.ql-editor) and sends on Enter (Shift+Enter = newline).
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const text = window.__geminiMsg || 'hello';
  const ed = document.querySelector('div.ql-editor[contenteditable="true"]') || document.querySelector('[contenteditable="true"]');
  if (!ed) return JSON.stringify({ error: 'no editor' });
  ed.focus();
  document.execCommand('selectAll', false, null);
  document.execCommand('insertText', false, text);
  ed.dispatchEvent(new InputEvent('input', { bubbles: true }));
  await sleep(600);

  // Prefer a send button if one is present; else press Enter.
  let clicked = null;
  const btns = [...document.querySelectorAll('button')].filter(b => !b.disabled);
  let send = btns.find(b => /send/i.test((b.getAttribute('aria-label') || '') + (b.getAttribute('mattooltip') || '')));
  if (!send) {
    const icon = [...document.querySelectorAll('mat-icon, span.material-symbols-outlined, span')].find(i => (i.textContent || '').trim() === 'send');
    if (icon) send = icon.closest('button');
  }
  if (send) { send.click(); clicked = 'button'; }
  else {
    const opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
    ed.dispatchEvent(new KeyboardEvent('keydown', opts));
    ed.dispatchEvent(new KeyboardEvent('keypress', opts));
    ed.dispatchEvent(new KeyboardEvent('keyup', opts));
    clicked = 'enter';
  }
  await sleep(800);
  const after = ed.textContent.trim();
  return JSON.stringify({ via: clicked, text, clearedAfterSend: after.length === 0, editorAfter: after });
})()
