namespace Gemineachy.Desktop
{
    /// <summary>
    /// A running app instance = one window. Holds window state (title, position, z-order, minimized) and
    /// a reference to the live app component instance once mounted.
    /// </summary>
    public class AppWindow
    {
        private static int _nextId = 0;
        public int Id { get; } = ++_nextId;
        public RegisteredApp App { get; }

        /// <summary>Per-window title override; falls back to the app name.</summary>
        public string? TitleOverride { get; set; }
        public string Title => string.IsNullOrEmpty(TitleOverride) ? App.Name : TitleOverride!;
        public string Icon => App.Icon;

        public bool Minimized { get; set; }
        public int ZIndex { get; set; }
        /// <summary>Window top-left in viewport pixels (committed after a drag; also the initial spawn spot).</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>The live app component instance (set by the app itself on init).</summary>
        public AppBase? Instance { get; set; }

        public AppWindow(RegisteredApp app) => App = app;
    }
}
