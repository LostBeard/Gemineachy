using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// chatgpt.com, signed out. Measured 2026-10-12 with _tools/site-study.sh (stream-record.js):
    /// <list type="bullet">
    /// <item>Compose: <c>form textarea[name="prompt"]</c> (id mobile-composer-prompt), framework-controlled - set
    /// through the prototype value setter. Send and Stop are ONE button that morphs: <c>aria-label="Send message"</c>
    /// (type=submit, disabled while the box is empty or a reply streams) / <c>aria-label="Stop generating"</c>.</item>
    /// <item>Turns: <c>ol[aria-label="Conversation"]</c> holding <c>li[data-message-role="user"]</c> and
    /// <c>li[data-message-role="assistant"]</c> siblings, each with a per-message id.</item>
    /// <item>A reply streams with <c>data-message-streaming</c> and gains <c>data-message-complete</c> when done.</item>
    /// <item>Reply body: <c>[data-assistant-markdown]</c>. A code block is an attribute-less &lt;div&gt; holding a
    /// &lt;span&gt; language label and the &lt;pre&gt;, whose text is exact (blank lines, <c>&lt;int&gt;</c>, <c>\"</c>).</item>
    /// <item>A user message is one <c>p[data-user-message-copy]</c>, one text node, white-space: pre-wrap.</item>
    /// <item>New chat: <c>a[aria-label="New chat"]</c>.</item>
    /// </list>
    /// </summary>
    public class ChatGptSite : IChatSite
    {
        private readonly SpawnJSRuntime _js;
        public ChatGptSite(SpawnJSRuntime js) => _js = js;

        public string Name => "ChatGPT";
        public bool Matches(string host) =>
            string.Equals(host, "chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www.chatgpt.com", StringComparison.OrdinalIgnoreCase);

        public string ComposeSelector => "form textarea[name=\"prompt\"]";
        public string SendButtonSelector => "form button[aria-label=\"Send message\"]";
        public string UserMessageSelector => "li[data-message-role=\"user\"]";
        public string ReplySelector => "li[data-message-role=\"assistant\"]";
        // The pre's nearest <div> parent (label span + pre). No ancestor qualifier: the scrape self-test runs on a
        // detached replica that has no [data-assistant-markdown] around it.
        public string CodeBlockSelector => "div:has(> pre)";
        public string[] WatchedAttributes => new[] { "data-message-complete", "data-message-streaming", "aria-label", "disabled" };
        // Set from measurement: see the e2e comparison recorded in Web-Agents.md.
        public bool IntroduceWithWarmUp => true;
        public int MaxMessageLength => 0;   // no limit measured

        const string StopButtonSelector = "button[aria-label=\"Stop generating\"]";

        // Signed out, "New chat" opens a "Log in or sign up" modal instead of a new conversation (measured
        // 2026-10-12, script AND trusted click); only a full page load gives a fresh chat, which would restart the
        // extension mid-send. So no in-page retry here: a declined warm-up falls back to the tool note.
        public bool StartNewChat(Document document) => false;

        public string ReadCompose(HTMLElement compose) => SiteDom.TextAreaValue(compose);
        public void WriteCompose(HTMLElement compose, string text) => SiteDom.SetTextAreaValue(_js, compose, text);

        public bool CanSend(HTMLElement sendButton)
        {
            using var b = sendButton.JSRefCopy<HTMLButtonElement>();
            return !b.Disabled && sendButton.GetAttribute("aria-disabled") != "true";
        }

        public string ReadUserText(HTMLElement userMessage)
        {
            using var p = userMessage.QuerySelector<HTMLElement>("p[data-user-message-copy]");
            return ((p ?? userMessage).TextContent ?? "").Trim();
        }

        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            using var next = userMessage.NextElementSibling;
            if (next == null || next.GetAttribute("data-message-role") != "assistant") return null;
            return next.JSRefCopy<HTMLElement>();
        }

        public HTMLElement? ReplyContent(HTMLElement reply) =>
            reply.QuerySelector<HTMLElement>("[data-assistant-markdown]") ?? reply.JSRefCopy<HTMLElement>();

        public bool IsGenerating(Document document) =>
            SiteDom.Exists(document, StopButtonSelector)
            || SiteDom.Exists(document, "li[data-message-role=\"assistant\"][data-message-streaming]");

        public bool IsReplyComplete(Document document, HTMLElement reply) =>
            reply.HasAttribute("data-message-complete") && !SiteDom.Exists(document, StopButtonSelector);

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            using var p = userMessage.QuerySelector<HTMLElement>("p[data-user-message-copy]");
            if (p != null) SiteDom.BlankFromMarker(p, marker);
        }

        public string ReadCodeText(HTMLElement pre) => pre.TextContent ?? "";

        // div > span "csharp" + pre > code, exactly as measured.
        public HTMLElement BuildCodeBlockReplica(Document doc, string code) => SiteDom.CodeBlock(doc, "div", "span", "csharp", code);
    }
}
