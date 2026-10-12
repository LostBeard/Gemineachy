// Generic turn-structure probe for a chat site: finds the element holding the given user text
// (window.__userNeedle) and the reply's first <pre>, walks both up to their common ancestor, and reports
// each level's tag/classes/data-* attributes plus sibling counts - enough to choose stable turn selectors.
(() => {
  const needle = window.__userNeedle || 'Reply with exactly one fenced code block';
  const desc = e => e.tagName.toLowerCase()
    + [...e.attributes].filter(a => a.name.startsWith('data-') || a.name === 'role' || a.name === 'aria-label' || a.name === 'id')
        .map(a => `[${a.name}=${a.value.slice(0, 40)}]`).join('')
    + (typeof e.className === 'string' && e.className ? '.' + e.className.trim().split(/\s+/).slice(0, 4).join('.') : '');
  const all = [...document.querySelectorAll('body *')];
  const user = all.filter(e => e.childElementCount === 0 && (e.textContent || '').includes(needle)).pop()
            || all.filter(e => (e.textContent || '').includes(needle)).pop();
  const pre = [...document.querySelectorAll('pre')].pop();
  if (!user || !pre) return JSON.stringify({ user: !!user, pre: !!pre });
  const chain = el => { const c = []; for (let e = el; e && e !== document.body; e = e.parentElement) c.push(e); return c; };
  const uc = chain(user), pc = chain(pre);
  const common = uc.find(e => pc.includes(e));
  const below = (c) => c.slice(0, c.indexOf(common) + 1).map(e => `${desc(e)} {sibs:${e.parentElement ? e.parentElement.childElementCount : 0}}`);
  const buttons = [...document.querySelectorAll('button')].filter(b => b.getBoundingClientRect().width > 0)
    .map(b => (b.getAttribute('aria-label') || '') + '|' + (b.getAttribute('data-testid') || '') + '|' + (b.type || '') + '|' + (b.textContent || '').trim().slice(0, 20));
  return JSON.stringify({ userChain: below(uc), preChain: below(pc), commonKids: common ? [...common.children].map(desc).slice(0, 12) : [], buttons: buttons.slice(-14) }, null, 1);
})()
