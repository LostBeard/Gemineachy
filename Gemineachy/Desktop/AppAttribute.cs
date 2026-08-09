namespace Gemineachy.Desktop
{
    /// <summary>
    /// Marks a component as a Gemineachy "app" - a windowed component launchable from the taskbar/start
    /// menu. The component must inherit <see cref="AppBase"/>. Register it in Program.cs with AddApp&lt;T&gt;().
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class AppAttribute : Attribute
    {
        /// <summary>Display name. Defaults to the component type name if empty.</summary>
        public string Name { get; set; } = "";
        /// <summary>
        /// Icon: an image path/URL/data-URI (rendered as &lt;img&gt;; relative paths resolve against the
        /// extension origin) OR an emoji/short text (rendered as text). Images are the norm; emoji is a
        /// zero-asset convenience.
        /// </summary>
        public string Icon { get; set; } = "";
        /// <summary>If true (default), launching again focuses the existing window instead of opening another.</summary>
        public bool SingleInstance { get; set; } = true;
        /// <summary>If true, the app is pinned to the main taskbar (not only in the Start menu).</summary>
        public bool Pinned { get; set; } = false;
        /// <summary>If true, the app is hidden from the Start menu / taskbar (e.g. a helper launched by another app).</summary>
        public bool Unlisted { get; set; } = false;
        /// <summary>Optional one-line description shown in the Start menu.</summary>
        public string Description { get; set; } = "";
    }
}
