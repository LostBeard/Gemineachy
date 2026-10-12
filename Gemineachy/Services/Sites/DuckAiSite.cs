using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// duck.ai (DuckDuckGo's private AI chat). React. No account; a one-time terms "Continue" the USER accepts.
    /// Measured 2026-10-12, signed out:
    /// <list type="bullet">
    /// <item>Compose: <c>form[data-chat-footer] textarea[name="user-prompt"]</c>, React-controlled; Send:
    /// <c>button[type="submit"][aria-label="Send"]</c>, DISABLED while the box is empty or a reply streams.</item>
    /// <item>User turn: <c>div[data-testid="user-message"]</c>; its reply is the <c>[data-activeresponse]</c>
    /// element beside it (id <c>&lt;chat&gt;-assistant-message-&lt;turn&gt;-&lt;version&gt;</c>).</item>
    /// <item>A reply streams with <c>data-activeresponse="false"</c> and flips to <c>"true"</c> when done.</item>
    /// <item>"Stop generating" is ALWAYS in the DOM - hidden (visibility:hidden, 0x0) when idle - so it must be
    /// judged by its size, not its presence.</item>
    /// <item>A multi-line user message is ONE &lt;p&gt; (white-space: pre-wrap), so the appended tool note
    /// cannot be hidden line by line - it is trimmed from that &lt;p&gt;'s text node instead.</item>
    /// <item>Code block: <c>div[data-streamdown="code-block"]</c> (language header + copy button) around a
    /// &lt;pre&gt; whose text is exact.</item>
    /// </list>
    /// </summary>
    public class DuckAiSite : IChatSite
    {
        public string Name => "Duck.ai";
        public bool Matches(string host) => string.Equals(host, "duck.ai", StringComparison.OrdinalIgnoreCase);

        public string ComposeSelector => "textarea[name=\"user-prompt\"]";
        public string SendButtonSelector => "form[data-chat-footer] button[type=\"submit\"]";
        public string UserMessageSelector => "[data-testid=\"user-message\"]";
        public string ReplySelector => "[data-activeresponse]";
        public string CodeBlockSelector => "[data-streamdown=\"code-block\"]";
        public string[] WatchedAttributes => new[] { "data-activeresponse", "disabled" };
        // Duck.ai's GPT model reasons from its BUILT-IN tool list: with the tool note appended to the first
        // message it declined 4/4 new chats ("I can't write files from this chat"), yet a message whose ONLY ask
        // was to write the call was followed. So a new chat starts with a hidden warm-up round (2026-10-12).
        public bool IntroduceWithWarmUp => true;

        /// <summary>
        /// Click the sidebar's "New Chat" button. It has no stable id, data-* or aria attribute (only obfuscated
        /// classes, 2026-10-12), so it is matched by its visible text - "New Chat", never "New Voice Chat".
        /// </summary>
        public bool StartNewChat(Document document)
        {
            var buttons = document.QuerySelectorAll<HTMLElement>("button").Using(o => o.ToArray());
            try
            {
                foreach (var b in buttons)
                {
                    var t = (b.TextContent ?? "").Trim();
                    if (t.StartsWith("New Chat", StringComparison.OrdinalIgnoreCase)) { b.Click(); return true; }
                }
                return false;
            }
            finally { foreach (var b in buttons) b.Dispose(); }
        }

        const string StopButtonSelector = "button[aria-label=\"Stop generating\"]";

        private readonly SpawnJSRuntime _js;
        public DuckAiSite(SpawnJSRuntime js) => _js = js;

        public string ReadCompose(HTMLElement compose) => SiteDom.TextAreaValue(compose);

        // React-controlled textarea: see SiteDom.SetTextAreaValue.
        public void WriteCompose(HTMLElement compose, string text) => SiteDom.SetTextAreaValue(_js, compose, text);

        public bool CanSend(HTMLElement sendButton)
        {
            using var button = sendButton.JSRefCopy<HTMLButtonElement>();
            return !button.Disabled;
        }

        public string ReadUserText(HTMLElement userMessage)
        {
            using var p = userMessage.QuerySelector<HTMLElement>("p");
            return ((p ?? userMessage).TextContent ?? "").Trim();
        }

        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            using var turn = userMessage.ParentElementAs<HTMLElement>();
            return turn?.QuerySelector<HTMLElement>(ReplySelector);
        }

        // [data-dark-theme] is the markdown root - it excludes the "Duck.ai said" label and the model name.
        public HTMLElement? ReplyContent(HTMLElement reply) =>
            reply.QuerySelector<HTMLElement>("[data-dark-theme]") ?? reply.JSRefCopy<HTMLElement>();

        public bool IsGenerating(Document document)
        {
            if (SiteDom.Visible(document, StopButtonSelector)) return true;
            using var streaming = document.QuerySelector("[data-activeresponse=\"false\"]");
            return streaming != null;
        }

        public bool IsReplyComplete(Document document, HTMLElement reply) =>
            reply.GetAttribute("data-activeresponse") == "true" && !SiteDom.Visible(document, StopButtonSelector);

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            // One <p> holds every line, but React renders it as MANY text nodes - one per line with "\n" nodes
            // between (measured: 26 nodes, the marker in the 4th). SiteDom.BlankFromMarker handles that shape.
            using var p = userMessage.QuerySelector<HTMLElement>("p");
            if (p != null) SiteDom.BlankFromMarker(p, marker);
        }

        /// <summary>
        /// Duck.ai renders code with Shiki as <c>&lt;pre&gt;&lt;code&gt;</c> holding one block-level &lt;span&gt; PER LINE
        /// and NO newline text between them (a blank line is a span holding just "\n"). So textContent glues
        /// the lines together - measured 2026-10-12: a WriteFile wrote <c>using System;using System.Linq;</c>
        /// on one line. Join the line spans instead.
        /// </summary>
        public string ReadCodeText(HTMLElement pre)
        {
            var lines = pre.QuerySelectorAll<HTMLElement>("code > span").Using(o => o.ToArray());
            try
            {
                if (lines.Length == 0) return pre.TextContent ?? "";
                return string.Join("\n", lines.Select(l =>
                {
                    var t = l.TextContent ?? "";
                    return t.EndsWith('\n') ? t.Substring(0, t.Length - 1) : t;
                }));
            }
            finally { foreach (var l in lines) l.Dispose(); }
        }

        public HTMLElement BuildCodeBlockReplica(Document doc, string code)
        {
            // div[data-streamdown=code-block] > header (language + copy button) + div[...code-block-body] > pre > code
            var block = doc.CreateElement<HTMLElement>("div");
            block.SetAttribute("data-streamdown", "code-block");
            using var header = doc.CreateElement<HTMLElement>("div");
            header.TextContent = "csharp";
            using var copy = doc.CreateElement<HTMLElement>("button");
            copy.TextContent = "Copy";
            header.AppendChild(copy);
            using var body = doc.CreateElement<HTMLElement>("div");
            body.SetAttribute("data-streamdown", "code-block-body");
            using var pre = doc.CreateElement<HTMLElement>("pre");
            using var codeEl = doc.CreateElement<HTMLElement>("code");
            // One block span per line, no newline text between them; a blank line is a span holding "\n".
            foreach (var line in code.Split('\n'))
            {
                using var span = doc.CreateElement<HTMLElement>("span");
                span.ClassName = "block";
                span.TextContent = line.Length == 0 ? "\n" : line;
                codeEl.AppendChild(span);
            }
            pre.AppendChild(codeEl);
            body.AppendChild(pre);
            block.AppendChild(header);
            block.AppendChild(body);
            return block;
        }

    }
}
