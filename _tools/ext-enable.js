// Run on chrome://extensions. Enable/disable the Gemineachy extension: set window.__extEnabled = true|false
// first. Used to test the chat page's own behaviour with no content script attached (A/B for send bugs).
(async () => {
  const l = await chrome.developerPrivate.getExtensionsInfo();
  const e = l.find(x => x.name === 'Gemineachy');
  if (!e) return 'Gemineachy not installed';
  await new Promise((res, rej) => chrome.management.setEnabled(e.id, !!window.__extEnabled, () => chrome.runtime.lastError ? rej(chrome.runtime.lastError.message) : res()));
  const after = (await chrome.developerPrivate.getExtensionsInfo()).find(x => x.id === e.id);
  return `${e.name} ${after.state}`;
})()
