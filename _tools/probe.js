// Inspect the booted Gemineachy extension in the Gemini page context.
(async () => {
  const out = {};
  out.url = location.href;
  out.globals = Object.keys(globalThis).filter(k => /gemin|spawn|__gem|tool/i.test(k));
  // Desktop host = div with very high z-index (memory: z-index 65536)
  const zHosts = [...document.querySelectorAll('div')].filter(d => {
    const z = parseInt(d.style.zIndex || '0', 10); return z >= 60000;
  }).map(d => ({ z: d.style.zIndex, shadow: !!d.shadowRoot }));
  out.zHosts = zHosts;
  // Any style tag mentioning gemineachy tool visibility
  out.gemStyle = [...document.querySelectorAll('style')].some(s => (s.textContent||'').includes('gemineachy'));
  // Count all shadow hosts
  out.shadowHosts = [...document.querySelectorAll('*')].filter(e => e.shadowRoot).length;
  // dotnet / blazor markers
  out.hasDotnet = typeof globalThis.getDotnetRuntime !== 'undefined' || !!document.querySelector('script[src*="_framework"], script[src*="dotnet"]');
  return JSON.stringify(out);
})()
