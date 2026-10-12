// One cell of the send A/B matrix. window.__sendHow = 'click' (script .click() on Send) | 'enter' (script
// KeyboardEvent Enter on the editor) | 'none' (type only - follow with `cdp.cs --enter` for a TRUSTED Enter).
// Types window.__composeText with execCommand insertText first. Reports compose length + turn count over 6 s.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ed = document.querySelector('chat-window div[contenteditable="true"]');
  if (!ed) return 'no editor';
  const how = window.__sendHow || 'click';
  ed.focus();
  document.execCommand('selectAll', false, null);
  document.execCommand('insertText', false, window.__composeText || 'Reply with just the word: pong');
  await sleep(500);
  const turns0 = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  const act = navigator.userActivation ? `${navigator.userActivation.isActive}/${navigator.userActivation.hasBeenActive}` : 'n/a';
  if (how === 'click') {
    const send = document.querySelector('chat-window button[aria-label*="Send"]');
    if (!send) return 'no send button';
    send.click();
  } else if (how === 'enter') {
    const o = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
    ed.dispatchEvent(new KeyboardEvent('keydown', o));
    ed.dispatchEvent(new KeyboardEvent('keyup', o));
  }
  if (how === 'none') return JSON.stringify({ how, typed: ed.textContent.length, turns0, activation: act });
  const r = [];
  for (let i = 0; i < 10; i++) {
    await sleep(600);
    r.push(`${(document.querySelector('chat-window div[contenteditable="true"]')?.textContent || '').length}/${document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length}`);
  }
  return JSON.stringify({ how, turns0, activation: act, timeline: r.join(' ') });
})()
