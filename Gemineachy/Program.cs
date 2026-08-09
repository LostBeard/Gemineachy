using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using SpawnDev.SpawnJS.RazorUI;
using SpawnDev.SpawnJS.WebWorkers;
using Gemineachy.Services;
using Gemineachy;

// SpawnJSApp is a minimal DI container with SpawnJSRuntime and BackgroundServiceManager.
var builder = SpawnJSAppBuilder.CreateDefault(args, out var JS);

// easy way to detect if we are running in a browser extension content script
var appBaseUri = new Uri(JS.AppBaseUri);
var isBrowserExtensionContentScript = JS.GlobalScope == GlobalScope.Window && appBaseUri.Scheme.Contains("-extension");

// We'll add components based what the environment is detected
if (isBrowserExtensionContentScript)
{
    // When running as browser extension content script we'll render Apptray.razor instead of an App.razor
    // And we'll add styling to the host itself that will be created to handle the ShadowRoot so it renders out-of-line from the website's own elements
    builder.RootComponents.Add<AppTray>(new AttachShadowRootOptions { Mode = "closed" }).SetHostStyle("all: revert; position: fixed; top: 0; left: 50%; z-index: 65536; font-size: 16px; font-weight: normal; font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;");
}
else
{
    // Add root components
    builder.RootComponents.Add<App>(new AttachShadowRootOptions { Mode = "open" });
}

// register WebWorkerService
builder.Services.AddWebWorkerService();

// register RazorUI (themeable component library on top of the renderer)
builder.Services.AddRazorUI();

// Additional services
builder.Services.AddSingleton<GeminiChatService>();

// SpawnJSRunAsync autostarts IBackgroundService and IAsyncBackgroundService services
await builder.Build().RunAsync();
