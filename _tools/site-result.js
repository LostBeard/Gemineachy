// After sending the probe prompt: did a reply arrive, and how is its code block rendered? Reports every
// <pre> (ancestor path + exact text, to check List<int> and the \" survived), plus the code-block chrome
// text (language label / copy button) that a naive textContent scrape would glue onto the code.
(() => {
  const path = el => { const p = []; for (let e = el; e && e !== document.body && p.length < 9; e = e.parentElement)
      p.push(e.tagName.toLowerCase() + (e.getAttribute('data-testid') ? `[data-testid=${e.getAttribute('data-testid')}]` : '')
        + (e.getAttribute('data-message-author-role') ? `[role=${e.getAttribute('data-message-author-role')}]` : '')
        + (typeof e.className === 'string' && e.className ? '.' + e.className.trim().split(/\s+/).slice(0, 2).join('.') : ''));
    return p.join(' < '); };
  const pres = [...document.querySelectorAll('pre')];
  return JSON.stringify({
    url: location.href,
    pres: pres.slice(-2).map(p => {
      // walk up to the smallest ancestor that has text outside the <pre> = the code block's chrome
      let blk = p.parentElement;
      for (let i = 0; i < 4 && blk && (blk.textContent || '').trim() === (p.textContent || '').trim(); i++) blk = blk.parentElement;
      const chrome = blk ? (blk.textContent || '').replace(p.textContent || '', '').trim().slice(0, 60) : '';
      return { path: path(p), code: p.textContent, chrome };
    }),
    tail: (document.querySelector('main') || document.body).innerText.slice(-500),
  }, null, 1);
})()
