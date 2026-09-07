// Is the ring actually filling? Reads the robot card's status text for the ears state.
(() => {
  function deepAll(root) { const acc = []; const walk = n => { for (const el of n.querySelectorAll('*')) { acc.push(el); if (el.shadowRoot) walk(el.shadowRoot); } }; walk(root); return acc; }
  const btns = deepAll(document).filter(e => e.tagName === 'BUTTON').map(b => (b.textContent || '').trim());
  return JSON.stringify({ earsButtons: btns.filter(t => /Ears|Transcribe/.test(t)) });
})()
