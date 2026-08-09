using Microsoft.AspNetCore.Components;

namespace Gemineachy.Desktop
{
    /// <summary>
    /// Base class for all Gemineachy apps. An app is a normal Razor component that runs inside a window
    /// (<see cref="AppFrame"/>). It gets its window via a cascading <see cref="AppWindow"/> and can drive
    /// its own window (title, close, minimize, focus) through the injected <see cref="AppManager"/>.
    /// </summary>
    public abstract class AppBase : ComponentBase, IDisposable
    {
        [CascadingParameter] public AppWindow Window { get; set; } = default!;
        [Inject] protected AppManager AppManager { get; set; } = default!;

        private string? _title;
        /// <summary>The window title. Setting it updates the taskbar/titlebar.</summary>
        public string? Title
        {
            get => _title ?? Window?.App.Name;
            set
            {
                _title = value;
                if (Window != null) Window.TitleOverride = value;
                AppManager?.Changed();
            }
        }

        protected override void OnInitialized()
        {
            if (Window != null) Window.Instance = this;
        }

        /// <summary>Optional launch hook - called by AppManager after the window is created (args optional).</summary>
        public virtual Task OnOpenAsync(IReadOnlyDictionary<string, string>? args) => Task.CompletedTask;

        public bool Close() => Window != null && AppManager.Close(Window.Id);
        public bool Minimize() => Window != null && AppManager.Minimize(Window.Id);
        public bool Focus() => Window != null && AppManager.Focus(Window.Id);

        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            OnDispose();
        }
        protected virtual void OnDispose() { }
    }
}
