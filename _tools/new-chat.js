(() => {
  const b = [...document.querySelectorAll('button, a')].find(e => /new chat/i.test((e.getAttribute('aria-label')||'') + ' ' + (e.textContent||'')));
  if (b) { b.click(); return 'clicked new chat'; }
  location.href = 'https://gemini.google.com/app';
  return 'navigating';
})()
