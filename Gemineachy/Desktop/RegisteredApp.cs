using System.Reflection;

namespace Gemineachy.Desktop
{
    /// <summary>
    /// A registered app: the component type plus its <see cref="AppAttribute"/> metadata. Created at
    /// registration time (AddApp&lt;T&gt;()) and validated then.
    /// </summary>
    public class RegisteredApp
    {
        public Type AppType { get; }
        public AppAttribute Meta { get; }
        public string Name => Meta.Name;
        public string Icon => Meta.Icon;
        public bool SingleInstance => Meta.SingleInstance;
        public bool Pinned => Meta.Pinned;
        public bool Unlisted => Meta.Unlisted;
        public string Description => Meta.Description;

        public RegisteredApp(Type appType)
        {
            if (!typeof(AppBase).IsAssignableFrom(appType))
                throw new ArgumentException($"{appType.FullName} must inherit from {nameof(AppBase)} to be registered as an app.");
            Meta = appType.GetCustomAttribute<AppAttribute>(inherit: false)
                ?? throw new ArgumentException($"{appType.FullName} is missing the required [{nameof(AppAttribute)}].");
            if (string.IsNullOrWhiteSpace(Meta.Name)) Meta.Name = appType.Name;
            AppType = appType;
        }
    }
}
