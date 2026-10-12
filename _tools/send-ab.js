// A/B the way text gets into Gemini's compose box, then click Send and report whether Gemini took it.
// window.__composeMode = 'textContent' (GeminiChatService's way) | 'insertText' (execCommand, user-like).
// window.__composeText = the message. Returns a timeline of compose length / turn count / path.
// Note: a fresh chat navigates (/app -> /app/<id>) only when signed in; signed out it stays on /app.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const ed = document.querySelector('chat-window div[contenteditable="true"]');
  if (!ed) return 'no editor';
  const text = window.__composeText || 'Reply with just the word: pong';
  const mode = window.__composeMode || 'textContent';
  ed.focus();
  if (mode === 'textContent') {
    ed.textContent = text;
    ed.dispatchEvent(new InputEvent('input', { bubbles: true, cancelable: true, inputType: 'insertText', data: text }));
  } else {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, text);
  }
  await sleep(500);
  const turns0 = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  const send = document.querySelector('chat-window button[aria-label*="Send"]');
  if (!send) return JSON.stringify({ mode, error: 'no send button after input' });
  setTimeout(() => send.click(), 0);   // click after this evaluate's microtask, so a navigation cannot strand it
  const r = [];
  for (let i = 0; i < 10; i++) {
    await sleep(600);
    const e2 = document.querySelector('chat-window div[contenteditable="true"]');
    r.push(`${(e2?.textContent || '').length}/${document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length}`);
  }
  return JSON.stringify({ mode, turns0, timeline: r.join(' '), path: location.pathname });
})()
