// Script loader works in window and worker scopes
(async function () {
    async function loadScript(src) {
        src = chrome.runtime.getURL(src);
        if (typeof globalThis.importScripts === 'function') {
            globalThis.importScripts(src);
        } else if (globalThis.document) {
            const script = document.createElement('script');
            const loadTask = new Promise((onload, onerror) => Object.assign(script, { onload, onerror, src }));
            (document.head || docuemnt.documentElement).append(script);
            await loadTask;
        }
    }
    // Load anything that needs to load before .Net Wasm
    //
    // Synchronously fired events need to be captured by Javascript and
    // held for .Net Wasm to pick up and handle once it loads.
    // 
    // Load .Net Wasm app
    await loadScript('app/main.classic.js');
})();
