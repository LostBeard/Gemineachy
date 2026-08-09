namespace Gemineachy
{
    /// <summary>
    /// Used to mark a method as am agent tool
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class AgentToolAttribute : Attribute
    {
        /// <summary>
        /// Tool description
        /// </summary>
        public string Description { get; private set; }
        public AgentToolAttribute(string description)
        {
            Description = description;
        }
    }
}
