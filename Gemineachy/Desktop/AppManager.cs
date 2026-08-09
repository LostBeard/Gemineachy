namespace Gemineachy.Desktop
{
    /// <summary>
    /// The windowing "OS": holds the registry of installed apps and the set of open windows, and handles
    /// launch / close / minimize / restore / focus (with z-ordering). UI (Desktop/Taskbar) subscribes to
    /// <see cref="OnChanged"/> and re-renders. Registered as a singleton; apps are supplied via DI.
    /// </summary>
    public class AppManager
    {
        private readonly List<RegisteredApp> _apps;
        private readonly List<AppWindow> _windows = new();
        private int _zTop = 10;

        public IReadOnlyList<RegisteredApp> Apps => _apps;
        public IReadOnlyList<AppWindow> Windows => _windows;
        /// <summary>Apps to show in the Start menu (listed, alphabetical).</summary>
        public IEnumerable<RegisteredApp> ListedApps => _apps.Where(a => !a.Unlisted).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        /// <summary>Apps pinned to the main taskbar.</summary>
        public IEnumerable<RegisteredApp> PinnedApps => _apps.Where(a => a.Pinned && !a.Unlisted).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        /// <summary>Id of the currently focused (front) window, if any.</summary>
        public int? FocusedId { get; private set; }

        /// <summary>Raised whenever the window/app state changes. Handlers should marshal to the UI dispatcher.</summary>
        public event Action? OnChanged;

        public AppManager(IEnumerable<RegisteredApp> apps) => _apps = apps.ToList();

        public RegisteredApp? FindApp(Type appType) => _apps.FirstOrDefault(a => a.AppType == appType);

        public AppWindow Launch<T>() where T : AppBase => Launch(typeof(T));
        public AppWindow Launch(Type appType) =>
            Launch(FindApp(appType) ?? throw new ArgumentException($"App {appType.FullName} is not registered."));

        public AppWindow Launch(RegisteredApp app)
        {
            if (app.SingleInstance)
            {
                var existing = _windows.FirstOrDefault(w => w.App == app);
                if (existing != null) { Restore(existing.Id); return existing; }
            }
            // Stagger spawns so stacked windows are visible.
            var n = _windows.Count % 6;
            var w = new AppWindow(app) { X = 80 + n * 28, Y = 90 + n * 28 };
            _windows.Add(w);
            Focus(w.Id);
            return w;
        }

        public bool Close(int id)
        {
            var w = Get(id);
            if (w == null) return false;
            _windows.Remove(w);
            if (FocusedId == id) FocusedId = _windows.OrderByDescending(x => x.ZIndex).FirstOrDefault(x => !x.Minimized)?.Id;
            Changed();
            return true;
        }

        public bool Minimize(int id)
        {
            var w = Get(id);
            if (w == null) return false;
            w.Minimized = true;
            if (FocusedId == id) FocusedId = _windows.OrderByDescending(x => x.ZIndex).FirstOrDefault(x => !x.Minimized)?.Id;
            Changed();
            return true;
        }

        public bool Restore(int id)
        {
            var w = Get(id);
            if (w == null) return false;
            w.Minimized = false;
            return Focus(id);
        }

        public bool Focus(int id)
        {
            var w = Get(id);
            if (w == null) return false;
            w.Minimized = false;
            w.ZIndex = ++_zTop;
            FocusedId = id;
            Changed();
            return true;
        }

        /// <summary>Taskbar behavior: if the window is focused and visible, minimize it; otherwise bring it up.</summary>
        public bool ToggleMinimize(int id)
        {
            var w = Get(id);
            if (w == null) return false;
            if (!w.Minimized && FocusedId == id) return Minimize(id);
            return Restore(id);
        }

        private AppWindow? Get(int id) => _windows.FirstOrDefault(w => w.Id == id);

        /// <summary>Notify subscribers (Desktop/Taskbar) to re-render.</summary>
        public void Changed() => OnChanged?.Invoke();
    }
}
