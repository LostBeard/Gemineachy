(() => {
  const ed = document.querySelector('div.ql-editor[contenteditable="true"]');
  const convos = document.querySelectorAll('chat-window infinite-scroller > div.conversation-container').length;
  const btns = [...document.querySelectorAll('button')].filter(b => /send/i.test((b.getAttribute('aria-label')||'')));
  return JSON.stringify({
    url: location.href, convos,
    editorText: ed ? ed.textContent.slice(0,120) : null,
    sendButtons: btns.map(b => ({ label: b.getAttribute('aria-label'), disabled: b.disabled, visible: !!b.offsetParent })),
    scrapeTest: document.documentElement.getAttribute('data-gem-scrapetest'),
    hasDesktop: !!document.querySelector('div[style*="65536"]')
  }, null, 1);
})()
