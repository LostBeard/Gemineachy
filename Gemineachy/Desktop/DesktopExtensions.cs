using Microsoft.Extensions.DependencyInjection;

namespace Gemineachy.Desktop
{
    /// <summary>
    /// DI registration for the windowing framework. Mirrors how services are registered in Program.cs.
    /// </summary>
    public static class DesktopExtensions
    {
        /// <summary>Register the window manager (call once).</summary>
        public static IServiceCollection AddAppManager(this IServiceCollection services)
        {
            services.AddSingleton<AppManager>();
            return services;
        }

        /// <summary>Register an app so it appears in the Start menu / taskbar and can be launched.</summary>
        public static IServiceCollection AddApp<TApp>(this IServiceCollection services) where TApp : AppBase
            => services.AddApp(typeof(TApp));

        /// <summary>Register an app by type.</summary>
        public static IServiceCollection AddApp(this IServiceCollection services, Type appType)
        {
            // Validated here (throws early if the type is not a valid app).
            services.AddSingleton(new RegisteredApp(appType));
            return services;
        }
    }
}
