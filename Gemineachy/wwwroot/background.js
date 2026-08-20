console.log(`background.js running`, new Date().toISOString());

// This script could be running in a Firefox extension background page or a Chrome extension ServiceWorker
var holding = [];
var asyncStartupRunning = true;
var attached = [];
// attaches temporary event handlers
function attachToEvent(target, tempCb) {
    if (!target) return;
    if (attached.some(function (a) { return a.target === target; })) return;
    console.log(`creating hold for event`, target);
    var att = {
        target: target,
        cb: function (...args) {
            console.log(`held event`, target, args);
            if (!asyncStartupRunning) return;
            var held = {
                target: target,
                args: args,
            };
            holding.push(held);
            return !tempCb ? void 0 : tempCb(...args);
        }
    };
    attached.push(att);
    target.addListener(att.cb);
}
// .Net will (SHOULD) call this method after it has finished starting and initializing all service that implement IBackgroundService and IAsyncBackgroundService
function finalizeAsyncStartup() {
    if (!asyncStartupRunning) return;
    asyncStartupRunning = false;
    var ret = holding;
    console.log(`finalizeAsyncStartup called`, holding.length, holding);
    for (const att of attached) {
        att.target.removeListener(att.cb);
    }
    holding = [];
    for (var e of ret) {
        try {
            e.target.dispatch(...e.args);
        } catch (e) {
            console.error(e);
        }
    }
}

attachToEvent(chrome.runtime.onInstalled);
attachToEvent(chrome.runtime.onStartup);
attachToEvent(chrome.runtime.onMessage, (data, sender, response) => response != null);
//attachToEvent(chrome.runtime.onMessageExternal, (data, sender, response) => response != null);