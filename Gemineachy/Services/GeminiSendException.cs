namespace Gemineachy.Services
{
    /// <summary>Thrown when Gemini does not accept an outgoing message after all send attempts.</summary>
    public class GeminiSendException : Exception
    {
        public GeminiSendException(string message) : base(message) { }
    }
}
