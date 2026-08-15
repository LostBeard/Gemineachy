using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using SpawnDev.SpawnJS.RazorUI;
using SpawnDev.SpawnJS.WebWorkers;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using Gemineachy.Services;
using Gemineachy.Services.Reachy;
using Gemineachy.Desktop;
using Gemineachy.Apps;
using Gemineachy;

// SpawnJSApp is a minimal DI container with SpawnJSRuntime and BackgroundServiceManager.
var builder = SpawnJSAppBuilder.CreateDefault(args, out var JS);

// This same WASM app boots in TWO contexts: the content script on the Gemini page, and the extension's
// background service worker. Detect which so each context registers only what it needs.
var extensionMode = BrowserExtensionService.GetExtensionMode();

// BrowserExtensionService exposes ExtensionMode + Browser.Runtime (messaging). It is an IBackgroundService.
builder.Services.AddSingleton<BrowserExtensionService>();

if (extensionMode == ExtensionMode.Background)
{
    // Background service worker: NO UI. Its only job is the HTTP relay so the content page (which is
    // CORS-blocked from the LAN daemon) can reach the Reachy Mini through the worker's host permissions.
    builder.Services.AddSingleton<HttpRelayBackgroundService>();
}
else
{
    // Content script (or a normal dev page): render the windowed desktop (AppDesktop) into a shadow root,
    // styled so it renders out-of-line from the host site. Mode "open" so the DOM is reachable for CDP.
    builder.RootComponents.Add<AppDesktop>(new AttachShadowRootOptions { Mode = "open" }).SetHostStyle("all: revert; position: fixed; top: 0; left: 0; width: 0; height: 0; z-index: 65536; font-size: 16px; font-weight: normal; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;");
    builder.RootComponents.AddSharedStyleSheet("Gemineachy.styles.css");

    // register WebWorkerService
    builder.Services.AddWebWorkerService();
    // register RazorUI (themeable component library on top of the renderer)
    builder.Services.AddRazorUI();

    // Additional services
    builder.Services.AddSingleton<GeminiChatService>();
    // Filesystem service: registers its always-available tools at startup and restores persisted mounts.
    builder.Services.AddSingleton<FileSystemService>();
    // Phase-1 Reachy relay proof (writes data-reachy-probe to the DOM); flip its RunProbe const off later.
    builder.Services.AddSingleton<ReachyRelayProbe>();
    // Reachy robots: registers its always-available tools at startup and restores persisted robots.
    builder.Services.AddSingleton<ReachyService>();

    // Windowed desktop environment + registered apps
    builder.Services.AddAppManager();
    builder.Services.AddApp<CheckersApp>();
    builder.Services.AddApp<ChessApp>();
    builder.Services.AddApp<FilesApp>();
    builder.Services.AddApp<ReachyApp>();
    builder.Services.AddApp<SettingsApp>();
}

// SpawnJSRunAsync autostarts IBackgroundService and IAsyncBackgroundService services
await builder.Build().RunAsync();
