using System.Text.Json.Serialization;

namespace Gemineachy.Services
{
    /// <summary>
    /// A single registered tool: the delegate to invoke plus the metadata presented to the agent.
    /// </summary>
    public class ToolCall
    {
        [JsonIgnore]
        public Delegate ToolHandler { get; set; } = null!;
        public string ToolName { get; set; } = "";
        public string Signature { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
