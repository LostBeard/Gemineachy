// Gemineachy page bridge - runs in the PAGE's world ("world": "MAIN"), on chat sites whose code blocks are
// virtualizing editors (Z.ai: CodeMirror 6, Qwen: Monaco). Such an editor keeps only the lines near the viewport in
// the DOM, so the content script cannot read a long block from the DOM. The full text lives in page JavaScript
// objects that only this world can reach.
//
// Protocol: the content script dispatches a 'gemineachy-read-code' event on a live code element. Events cross
// worlds and are delivered synchronously, so by the time dispatchEvent returns, this listener has written the
// block's exact text to the element's data-gemineachy-code attribute (or removed it when the text is unknown).
// Approved by TJ 2026-10-12. Keep it this small: read-only, no other messages.
document.addEventListener('gemineachy-read-code', e => {
  const el = e.target;
  if (!(el instanceof Element)) return;
  let text = null;
  try {
    // CodeMirror 6: the .cm-content element's view tree node links to its EditorView.
    const cm = el.cmView && el.cmView.view;
    if (cm && cm.state && cm.state.doc) text = cm.state.doc.toString();
    else {
      // Qwen: React passes the block's markdown token ({ type: 'code', text }) to the component that renders the <pre>.
      const key = Object.keys(el).find(k => k.startsWith('__reactFiber$'));
      for (let f = key ? el[key] : null, depth = 0; f && depth < 4 && text === null; f = f.return, depth++) {
        const token = f.memoizedProps && f.memoizedProps.token;
        if (token && token.type === 'code' && typeof token.text === 'string') text = token.text;
      }
    }
  } catch { text = null; }
  if (text === null) el.removeAttribute('data-gemineachy-code');
  else el.setAttribute('data-gemineachy-code', text);
}, true);
