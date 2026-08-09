namespace Gemineachy
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class AgentToolAttribute : Attribute
    {
        public string Description { get; private set; }
        public AgentToolAttribute(string description)
        {
            Description = description;
        }
    }
}
