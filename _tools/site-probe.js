// Survey an AI chat site for a Gemineachy adapter: is it usable signed out, what is the compose box, what is
// the send control, and (after a message) how are turns and code blocks rendered. Read-only.
(() => {
  const vis = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const path = el => { const p = []; for (let e = el; e && e !== document.body && p.length < 8; e = e.parentElement)
      p.push(e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (e.getAttribute('data-testid') ? `[data-testid=${e.getAttribute('data-testid')}]` : '')
        + (e.getAttribute('data-message-author-role') ? `[role=${e.getAttribute('data-message-author-role')}]` : '')
        + (typeof e.className === 'string' && e.className ? '.' + e.className.trim().split(/\s+/).slice(0, 3).join('.') : ''));
    return p.join(' < '); };
  const composes = [...document.querySelectorAll('textarea, [contenteditable="true"], input[type="text"]')].filter(vis);
  const buttons = [...document.querySelectorAll('button, [role="button"]')].filter(vis);
  const label = b => ((b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '').trim().slice(0, 30)).trim();
  return JSON.stringify({
    url: location.href, title: document.title,
    signInPrompts: buttons.map(label).filter(t => /log ?in|sign ?in|sign ?up/i.test(t)).slice(0, 5),
    composes: composes.map(e => ({ tag: e.tagName, placeholder: e.getAttribute('placeholder') || e.getAttribute('aria-placeholder') || e.getAttribute('data-placeholder'), cls: (e.className || '').toString().slice(0, 80), path: path(e).slice(0, 300) })),
    sendLike: buttons.map(label).filter(t => /send|submit|ask|arrow/i.test(t)).slice(0, 8),
    pres: [...document.querySelectorAll('pre')].slice(0, 3).map(p => ({ path: path(p), text: (p.textContent || '').slice(0, 120) })),
    roleNodes: [...document.querySelectorAll('[data-message-author-role], [data-testid*="message"], [data-role], [class*="message" i]')].slice(0, 12).map(e => path(e).slice(0, 200)),
  }, null, 1);
})()
