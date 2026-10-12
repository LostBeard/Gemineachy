// Type window.__siteMsg into a chat site's main compose box (the biggest visible textarea/contenteditable that
// is not a search/login field) the way a page script can: native value setter + input event for a textarea,
// execCommand insertText for a contenteditable. Leaves focus in the box, so `cdp.cs --enter` can send it.
(() => {
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const skip = el => /search|phone|email|password/i.test((el.getAttribute('placeholder') || '') + ' ' + (el.getAttribute('type') || ''));
  const boxes = [...document.querySelectorAll('textarea, [contenteditable="true"]')].filter(e => vis(e) && !skip(e))
    .sort((a, b) => b.getBoundingClientRect().width - a.getBoundingClientRect().width);
  const box = boxes[0];
  if (!box) return 'no compose box';
  const text = window.__siteMsg || 'hello';
  box.focus();
  if (box.tagName === 'TEXTAREA') {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(box, text);
    box.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, text);
  }
  return `typed into ${box.tagName}.${(box.className || '').toString().split(' ')[0]} -> ${(box.value ?? box.textContent).length} chars`;
})()
