// Read a file straight out of the page origin's OPFS (independent of the extension) and return its exact
// text as a JSON string. Set window.__opfsPath to a path relative to the OPFS root, e.g. "sub/Hello.cs".
// With no path set, lists the root. This is the ground truth for what a FileSystem tool actually wrote.
(async () => {
  const root = await navigator.storage.getDirectory();
  const path = (window.__opfsPath || '').replace(/^\/+/, '');
  if (!path) {
    const names = [];
    for await (const [n, h] of root.entries()) names.push(n + (h.kind === 'directory' ? '/' : ''));
    return JSON.stringify({ root: names.sort() });
  }
  const parts = path.split('/');
  let dir = root;
  for (const p of parts.slice(0, -1)) dir = await dir.getDirectoryHandle(p);
  const file = await (await dir.getFileHandle(parts[parts.length - 1])).getFile();
  return JSON.stringify({ path, size: file.size, text: await file.text() });
})()
