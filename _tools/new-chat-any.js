// Open a new chat on the current site. Needs site-table.js prepended: cdp.cs <site> file:site-table.js+new-chat-any.js
(() => { const S = window.__gemSite; if (!S) return 'no site-table entry'; S.newChat(); return 'new chat'; })()
