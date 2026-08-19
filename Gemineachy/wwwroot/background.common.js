// This script could be running in a Firefox extension background page or a Chrome extension ServiceWorker

// !! IMPORTANT !!: The service worker will not be woken up for events if the events are not attached to at the top level like below.
// https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Background_scripts#move_event_listeners
// Because .Net is WebAssembly, it starts asynchronously and therefore cannot attach to events during the initial synchronous load.
// Service workers and other contexts expect the code to be ready to handle events after the initial synchronous load.
// To try and adapt this old fashioned synchronous startup to work with .Net we have to hold events until .Net is ready to handle them.
// !! IMPORTANT !!: The way this method works simplifies some things but can complicate others.
// Important part: Event data is held in a queue and eventually re-dispatched once .Net signals it is ready.
// This can cause issues if you have other listeners to these events outside of .Net.
var holding = [];
var asyncStartupRunning = true;
// !! An ARRAY, not an object-keyed map. `attached[target]` used an EVENT OBJECT as a property key, and
// every one of them stringifies to the same "[object Object]" - so the first attachToEvent call claimed the
// slot and EVERY later call hit the `if (attached[target]) return;` guard and bailed. Only
// chrome.runtime.onInstalled was ever attached; chrome.runtime.onMessage never was. Chrome decides whether
// to wake a terminated service worker from the listeners registered during the worker's synchronous
// top-level evaluation, so with no onMessage listener there it simply refused to start the worker, and
// every message failed with "Could not establish connection. Receiving end does not exist." until the
// extension was reloaded by hand (which works only because .Net's own async listener exists while the
// worker happens to still be running).
var attached = [];
// attaches temporary event handlers
function attachToEvent(target, tempCb) {
    if (!target) return;
    if (attached.some(function (a) { return a.target === target; })) return;
    var att = {
        target: target,
        cb: function () {
            // !! IMPORTANT !!: once startup has finalized this handler goes INERT but STAYS ATTACHED.
            // Chrome decides whether to wake a terminated service worker from the listeners registered
            // during the worker's synchronous top-level evaluation. .Net's own handlers are attached
            // asynchronously (after the WASM runtime boots), so they do not count. If these temporary
            // handlers are detached, the worker ends up with NO synchronously-registered listener and
            // Chrome stops waking it altogether - every later sendMessage fails with
            // "Could not establish connection. Receiving end does not exist." until the extension is
            // reloaded by hand. Staying attached (and doing nothing) keeps the worker wakeable.
            if (!asyncStartupRunning) return void 0;
            var args = [...arguments];
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
    holding = [];
    // NOTE: the temporary handlers are deliberately NOT detached here - see attachToEvent above. They are
    // already inert (asyncStartupRunning is false by now); detaching them is what made the service worker
    // permanently unwakeable after its first startup.
    // re-dispatch events
    for (var e of ret) {
        try {
            e.target.dispatch(...e.args);
        } catch (e) {
            console.error(e);
        }
    }
}

// attach temporary event handlers to whatever events are needed
// manifest permissions may be needed for some events
attachToEvent(chrome.runtime.onInstalled);
attachToEvent(chrome.runtime.onStartup);
attachToEvent(chrome.runtime.onSuspend);
attachToEvent(chrome.runtime.onMessageExternal, (data, sender, response) => response != null);
attachToEvent(chrome.runtime.onMessage, (data, sender, response) => response != null);
