(() => {
  const mgr = document.querySelector('extensions-manager');
  const list = mgr.shadowRoot.querySelector('extensions-item-list');
  const items = [...list.shadowRoot.querySelectorAll('extensions-item')];
  const it = items.find(i => (i.shadowRoot.querySelector('#name')?.textContent || '').toLowerCase().includes('gemineachy'));
  if (!it) return 'not found';
  const tog = it.shadowRoot.querySelector('#enableToggle');
  if (!tog) return 'no toggle';
  const was = tog.checked;
  tog.click();
  return JSON.stringify({ wasEnabled: was, nowEnabled: !was });
})()
