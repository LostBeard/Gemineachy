// Script loader works in window
(async function () {
    async function loadScript(src) {
        src = chrome.runtime.getURL(src);
        const script = document.createElement('script');
        const loadTask = new Promise((onload, onerror) => Object.assign(script, { onload, onerror, src }));
        (document.head || document.documentElement).append(script);
        await loadTask;
    }
    // Load anything that needs to load before .Net Wasm
    //
    // Synchronously fired events need to be captured by Javascript and
    // held for .Net Wasm to pick up and handle once it loads.
    //
    // background.common.js attaches temporary listeners (runtime.onMessage, etc.) that QUEUE events fired
    // before .NET (WASM) has booted, and re-dispatches them once .NET calls finalizeAsyncStartup(). Without
    // it, a relay message that arrives while the service worker is cold-starting would be lost. (Same
    // approach Anaglyphohol uses.)
    await loadScript('app/background.js');
    //
    // Load .Net Wasm app
    await loadScript('app/main.classic.js');
})();
