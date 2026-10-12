namespace Gemineachy
{
    /// <summary>
    /// Marks an agent-tool parameter whose value is code or other text that must arrive EXACTLY - file
    /// contents, a literal to find, a regex, a replacement.
    /// </summary>
    /// <remarks>
    /// A chat page renders the model's reply as markdown, and outside a code block that rendering is lossy
    /// in ways the scraped text cannot reveal: <c>List&lt;int&gt;</c> loses <c>&lt;int&gt;</c> (taken for an HTML
    /// tag), <c>\"</c> loses its backslash, <c>*x*</c> loses its asterisks. So a verbatim parameter is only
    /// bound from a value that came out of a code block - a fenced tool call or a fenced PAYLOAD block - and
    /// otherwise the call fails with an error telling the model how to resend it. The index marks these
    /// parameters with <c>*</c> so the model knows up front.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
    public class VerbatimAttribute : Attribute
    {
    }
}
