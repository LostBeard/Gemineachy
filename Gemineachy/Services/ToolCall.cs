using System.Reflection;
using System.Text.Json.Serialization;

namespace Gemineachy.Services
{
    /// <summary>
    /// A single registered tool: the delegate to invoke plus the metadata presented to the agent.
    /// </summary>
    public class ToolCall
    {
        [JsonIgnore]
        public Type ToolType { get; }
        [JsonIgnore]
        public MethodInfo MethodInfo { get; }
        [JsonIgnore]
        public object? Instance { get; }
        public string ToolName { get; } = "";
        public string Signature { get; } = "";
        public string Description { get; } = "";
        public ToolCall(string name, Type type, MethodInfo methodInfo, object? instance, string description)
        {
            ToolName = name;
            ToolType = type;
            MethodInfo = methodInfo;
            Instance = instance;
            Description = description;
            Signature = DelegateFormatter.GetCsharpSignature(methodInfo);
        }
    }
}
