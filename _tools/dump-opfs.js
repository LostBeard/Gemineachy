(async () => {
  const root = await navigator.storage.getDirectory();
  const fh = await root.getFileHandle('CodeBlockFix.cs');
  return JSON.stringify(await (await fh.getFile()).text());
})()
