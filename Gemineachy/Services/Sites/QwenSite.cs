using System.Text.RegularExpressions;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// chat.qwen.ai, guest mode (signed out; an age "Continue" the USER accepts once). React + Ant Design.
    /// Measured 2026-10-12 with _tools/site-study.sh, turns-probe.js and virt-collect.js:
    /// <list type="bullet">
    /// <item>Compose: <c>textarea.message-input-textarea</c>, React-controlled. Send: <c>button[aria-label="Send"]</c>
    /// (disabled while the box is empty). While a reply streams, Send is replaced by <c>button[aria-label="Stop"]</c>.</item>
    /// <item>Turns: the user message and its reply have appeared both inside ONE turn wrapper and in separate ones,
    /// so a reply is paired by document order (see <see cref="FindReply"/>). User text: <c>p.user-message-content</c>, one text node,
    /// white-space: pre-wrap. Reply: <c>.qwen-chat-message-assistant</c>; its "Thinking completed" card sits
    /// OUTSIDE the answer body <c>.response-message-content</c>, and its footer gains a Regenerate button when done.</item>
    /// <item>Code: <c>pre.qwen-markdown-code</c> holds a header (language label + icon buttons) and a Monaco editor.
    /// Its text is the <c>.view-line</c> divs, absolutely positioned by <c>style.top</c> and NOT in line order in
    /// the DOM, with spaces as NBSP. A block too tall for Qwen's cap (about 37 lines, 748 px) gets the class
    /// <c>qwen-markdown-code-body-virtual</c> and keeps only the lines near the viewport in the DOM - and a hidden
    /// editor cannot be scrolled to render the rest. So such a block is reported incomplete, and the tool protocol
    /// asks the model for shorter parts (<see cref="ToolProtocol.IncompleteChunkLines"/>). That is only the fallback:
    /// the page-world bridge reads the block's markdown token (<c>{ type: 'code', text }</c>, a React prop of the
    /// component around the &lt;pre&gt;), which held a 150-line block exactly, <c>\"</c> and all.</item>
    /// </list>
    /// Monaco renders a TAB as spaces, so a tab in code arrives as spaces.
    /// </summary>
    public class QwenSite : IChatSite
    {
        private readonly SpawnJSRuntime _js;
        public QwenSite(SpawnJSRuntime js) => _js = js;

        public string Name => "Qwen";
        public bool Matches(string host) => string.Equals(host, "chat.qwen.ai", StringComparison.OrdinalIgnoreCase);

        public string ComposeSelector => "textarea.message-input-textarea";
        public string SendButtonSelector => "button[aria-label=\"Send\"]";
        public string UserMessageSelector => ".qwen-chat-message-user";
        public string ReplySelector => ".qwen-chat-message-assistant";
        // The <pre> is the whole block (header + editor), so it is both the block and the code element.
        public string CodeBlockSelector => "pre.qwen-markdown-code";
        // Exact text from the editor's own objects via wwwroot/page-bridge.js; the DOM reading below is the fallback.
        public bool ReadsCodeFromPage => true;
        public string[] WatchedAttributes => new[] { "disabled", "aria-label" };
        // Set from measurement: see the e2e comparison recorded in Web-Agents.md.
        public bool IntroduceWithWarmUp => false;
        public int MaxMessageLength => 0;   // no limit measured

        const string StopButtonSelector = "button[aria-label=\"Stop\"]";

        // Guest mode keeps one chat at /c/guest; a fresh chat is a page load (TODO measure an in-page "New Chat").
        public bool StartNewChat(Document document) => false;

        public string ReadCompose(HTMLElement compose) => SiteDom.TextAreaValue(compose);
        public void WriteCompose(HTMLElement compose, string text) => SiteDom.SetTextAreaValue(_js, compose, text);

        public bool CanSend(HTMLElement sendButton)
        {
            using var b = sendButton.JSRefCopy<HTMLButtonElement>();
            return !b.Disabled;
        }

        public string ReadUserText(HTMLElement userMessage)
        {
            using var p = userMessage.QuerySelector<HTMLElement>("p.user-message-content");
            return ((p ?? userMessage).TextContent ?? "").Trim();
        }

        /// <summary>The next message after <paramref name="userMessage"/> in document order, when it is a reply. The
        /// wrappers vary: a fresh guest chat put the user message and its reply in ONE
        /// <c>.qwen-chat-message-select-turn-message</c>, an earlier page in SEPARATE turn blocks - order is stable.</summary>
        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            using var doc = _js.GetDocument();
            if (doc == null) return null;
            var messages = doc.QuerySelectorAll<HTMLElement>($"{UserMessageSelector}, {ReplySelector}").Using(o => o.ToArray());
            try
            {
                int i = System.Array.FindIndex(messages, m => m.IsSameNode(userMessage));
                if (i < 0 || i + 1 >= messages.Length || !messages[i + 1].ClassList.Contains("qwen-chat-message-assistant")) return null;
                return messages[i + 1].JSRefCopy<HTMLElement>();
            }
            finally { foreach (var m in messages) m.Dispose(); }
        }

        public HTMLElement? ReplyContent(HTMLElement reply) =>
            reply.QuerySelector<HTMLElement>(".response-message-content") ?? reply.JSRefCopy<HTMLElement>();

        public bool IsGenerating(Document document) => SiteDom.Exists(document, StopButtonSelector);

        public bool IsReplyComplete(Document document, HTMLElement reply)
        {
            if (IsGenerating(document)) return false;
            using var regenerate = reply.QuerySelector("button[aria-label=\"Regenerate\"]");
            return regenerate != null;
        }

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            using var p = userMessage.QuerySelector<HTMLElement>("p.user-message-content");
            if (p != null) SiteDom.BlankFromMarker(p, marker);
        }

        static readonly Regex TopRegex = new Regex(@"top:\s*(-?[\d.]+)px", RegexOptions.Compiled);

        /// <summary>The Monaco lines in VISUAL order (by their <c>style.top</c>), NBSP back to spaces.</summary>
        public string ReadCodeText(HTMLElement pre)
        {
            var lines = pre.QuerySelectorAll<HTMLElement>(".view-lines .view-line").Using(o => o.ToArray());
            try
            {
                if (lines.Length == 0) return pre.TextContent ?? "";
                return string.Join("\n", lines
                    .Select(l =>
                    {
                        var m = TopRegex.Match(l.GetAttribute("style") ?? "");
                        return (Top: m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0,
                                Text: (l.TextContent ?? "").Replace(' ', ' '));
                    })
                    .OrderBy(x => x.Top)
                    .Select(x => x.Text));
            }
            finally { foreach (var l in lines) l.Dispose(); }
        }

        public bool IsCodeComplete(HTMLElement pre)
        {
            using var virt = pre.QuerySelector(".qwen-markdown-code-body-virtual");
            return virt == null;
        }

        public HTMLElement BuildCodeBlockReplica(Document doc, string code)
        {
            // pre.qwen-markdown-code > [header (label + action icons), body > ... > .view-lines > .view-line per line].
            // The lines go in REVERSED DOM order with their real tops, as Monaco recycles them out of order.
            var pre = doc.CreateElement<HTMLElement>("pre");
            pre.ClassName = "qwen-markdown-code";
            using var header = doc.CreateElement<HTMLElement>("div");
            header.ClassName = "qwen-markdown-code-header";
            using var label = doc.CreateElement<HTMLElement>("div");
            label.TextContent = "csharp";
            header.AppendChild(label);
            using var body = doc.CreateElement<HTMLElement>("div");
            body.ClassName = "qwen-markdown-code-body csharp";
            using var margin = doc.CreateElement<HTMLElement>("div");
            margin.ClassName = "margin-view-overlays";
            using var viewLines = doc.CreateElement<HTMLElement>("div");
            viewLines.ClassName = "view-lines";
            var lines = code.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                using var num = doc.CreateElement<HTMLElement>("div");
                num.TextContent = (i + 1).ToString();
                margin.AppendChild(num);
                using var l = doc.CreateElement<HTMLElement>("div");
                l.ClassName = "view-line";
                l.SetAttribute("style", $"top:{i * 20}px;height:20px;line-height:20px;");
                using var span = doc.CreateElement<HTMLElement>("span");
                span.TextContent = lines[i].Replace(' ', ' ');
                l.AppendChild(span);
                viewLines.AppendChild(l);
            }
            body.AppendChild(margin);
            body.AppendChild(viewLines);
            pre.AppendChild(header);
            pre.AppendChild(body);
            return pre;
        }
    }
}
