// Wait for a FRESH node dump. set-filter.js clears the attribute first, so its reappearance - unlike the
// status line, which persists from the previous run - proves this run finished.
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  for (let i = 0; i < 100; i++) {
    const d = document.documentElement.getAttribute('data-gem-sttdump');
    if (d) return d;
    await sleep(2000);
  }
  return '(timed out waiting for dump)';
})()
