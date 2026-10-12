// Prototype: collect EVERY line of the last code block's virtualized editor (Monaco on Qwen, CodeMirror 6 on Z.ai)
// by scrolling it through and keying rendered lines by their absolute vertical position.
// window.__hideReply = true first applies the extension's exact tool-traffic hiding CSS to the reply, to measure
// whether a HIDDEN reply's editor still renders and scrolls. Reports lines collected, first/last, gaps.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const frame = () => new Promise(r => requestAnimationFrame(() => r()));
  const host = location.host;
  const monaco = host === 'chat.qwen.ai';
  const blocks = monaco ? [...document.querySelectorAll('pre.qwen-markdown-code')] : [...document.querySelectorAll('.cm-editor')];
  const block = blocks[blocks.length - 1];
  if (!block) return 'no code block';
  const reply = block.closest(monaco ? '.qwen-chat-message-assistant' : '.chat-assistant');
  if (window.__hideReply === 'offscreen' && reply) {
    // off-screen at FULL size: keeps the editor's own layout (its width/height), so it keeps rendering + scrolling
    const w = reply.getBoundingClientRect().width;
    Object.assign(reply.style, { position: 'absolute', left: '-100000px', top: '0', width: w + 'px', opacity: '0', pointerEvents: 'none' });
    await sleep(300);
  } else if (window.__hideReply && reply) {
    // identical to ChatAgentService.InjectToolTrafficStyle's rule
    Object.assign(reply.style, { position: 'absolute', width: '1px', height: '1px', padding: '0', margin: '-1px',
      overflow: 'hidden', clip: 'rect(0,0,0,0)', clipPath: 'inset(50%)', whiteSpace: 'nowrap', border: '0', opacity: '0', pointerEvents: 'none' });
    await sleep(300);
  }
  const got = new Map();
  const read = () => {
    if (monaco) for (const l of block.querySelectorAll('.view-line')) got.set(parseFloat(l.style.top), l.textContent.replace(/ /g, ' '));
    else { const content = block.querySelector('.cm-content'); for (const l of block.querySelectorAll('.cm-line')) got.set(l.offsetTop, l.textContent); }
  };
  let scroller;
  if (monaco) scroller = block.querySelector('.monaco-editor .overflow-guard');
  else { scroller = block.closest('[class*="overflow-y-auto"], [style*="overflow"]'); for (let e = block.parentElement; e && !scroller; e = e.parentElement) { const s = getComputedStyle(e); if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight) scroller = e; } }
  // Monaco routes wheel events by pointer COORDINATES (a coordinate-less event was ignored): aim at its centre
  const at = () => { const r = scroller.getBoundingClientRect(); return { clientX: r.left + Math.min(r.width / 2, 200), clientY: r.top + Math.min(r.height / 2, 200) }; };
  const startTop = scroller && !monaco ? scroller.scrollTop : 0;
  // start from the TOP of the block, wherever it was left scrolled
  if (monaco) { for (let k = 0; k < 20; k++) scroller.dispatchEvent(new WheelEvent('wheel', { deltaY: -100000, deltaMode: 0, bubbles: true, cancelable: true, ...at() })); await frame(); await sleep(50); }
  let stable = 0, last = -1;
  for (let i = 0; i < 200 && stable < 3; i++) {
    await frame(); await sleep(250);
    read();
    if (got.size === last) stable++; else { stable = 0; last = got.size; }
    if (monaco) scroller.dispatchEvent(new WheelEvent('wheel', { deltaY: 300, deltaMode: 0, bubbles: true, cancelable: true, ...at() }));
    else if (scroller) {
      if (i === 0) { const r = block.getBoundingClientRect(), sr = scroller.getBoundingClientRect(); scroller.scrollTop += r.top - sr.top; }
      else scroller.scrollTop += Math.max(200, scroller.clientHeight - 100);
    }
  }
  if (!monaco && scroller) scroller.scrollTop = startTop;
  if (window.__hideReply && reply) reply.removeAttribute('style');
  const lines = [...got.entries()].sort((a, b) => a[0] - b[0]).map(e => e[1]);
  const nums = lines.map(l => +(l.match(/line (\d+)/) || [])[1]).filter(n => n);
  const missing = []; for (let n = 1; n <= (window.__expect || 150); n++) if (!nums.includes(n)) missing.push(n);
  return JSON.stringify({ editor: monaco ? 'monaco' : 'codemirror', hidden: !!window.__hideReply, collected: lines.length, first: lines[0], last: lines[lines.length - 1], missing: missing.length > 12 ? missing.slice(0, 6).concat(['...'], missing.slice(-6)) : missing, scroller: !!scroller });
})()
