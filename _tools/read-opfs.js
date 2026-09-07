(async () => {
  const root = await navigator.storage.getDirectory();
  const names = [];
  for await (const [n, h] of root.entries()) names.push(n + (h.kind === 'directory' ? '/' : ''));
  let content = null, err = null;
  try {
    const fh = await root.getFileHandle('CodeBlockFix.cs');
    content = await (await fh.getFile()).text();
  } catch (e) { err = String(e); }
  return JSON.stringify({ names, err, first80: content === null ? null : content.slice(0, 80), len: content === null ? 0 : content.length });
})()
