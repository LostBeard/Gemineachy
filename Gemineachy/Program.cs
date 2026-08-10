using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using SpawnDev.SpawnJS.RazorUI;
using SpawnDev.SpawnJS.WebWorkers;
using Gemineachy.Services;
using Gemineachy.Desktop;
using Gemineachy.Apps;
using Gemineachy;

// SpawnJSApp is a minimal DI container with SpawnJSRuntime and BackgroundServiceManager.
var builder = SpawnJSAppBuilder.CreateDefault(args, out var JS);

// easy way to detect if we are running in a browser extension content script
var appBaseUri = new Uri(JS.AppBaseUri);
var isBrowserExtensionContentScript = JS.GlobalScope == GlobalScope.Window && appBaseUri.Scheme.Contains("-extension");

// We'll add components based what the environment is detected
if (isBrowserExtensionContentScript || true)
{
    // When running as browser extension content script we render the windowed desktop (AppDesktop) into a
    // shadow root, styled so it renders out-of-line from the website's own elements. Mode "open" (not
    // "closed") so the extension's DOM is reachable for CDP-driven testing.
    builder.RootComponents.Add<AppDesktop>(new AttachShadowRootOptions { Mode = "open" }).SetHostStyle("all: revert; position: fixed; top: 0; left: 0; width: 0; height: 0; z-index: 65536; font-size: 16px; font-weight: normal; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;");
}
else
{
    // Add root components
    builder.RootComponents.Add<App>(new AttachShadowRootOptions { Mode = "open" });
}

// App's generated styles
builder.RootComponents.AddSharedStyleSheet("Gemineachy.styles.css");

// register WebWorkerService
builder.Services.AddWebWorkerService();

// register RazorUI (themeable component library on top of the renderer)
builder.Services.AddRazorUI();

// Additional services
builder.Services.AddSingleton<GeminiChatService>();
// Filesystem service: registers its always-available tools at startup and restores persisted mounts.
builder.Services.AddSingleton<FileSystemService>();

// Windowed desktop environment + registered apps
builder.Services.AddAppManager();
builder.Services.AddApp<CheckersApp>();
builder.Services.AddApp<FilesApp>();
builder.Services.AddApp<SettingsApp>();

// SpawnJSRunAsync autostarts IBackgroundService and IAsyncBackgroundService services
await builder.Build().RunAsync();
