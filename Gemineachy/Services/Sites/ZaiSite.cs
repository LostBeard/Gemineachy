using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// chat.z.ai (Zhipu's GLM chat), Svelte. Works signed out; a "Skip for now" login prompt may appear and is the
    /// user's to dismiss. Measured 2026-10-12 with _tools/stream-record.js, zai-busy.js and virt-collect.js:
    /// <list type="bullet">
    /// <item>Compose: <c>textarea#chat-input</c>; Send: <c>button#send-message-button</c> (type=submit, disabled
    /// while the box is empty). While a reply streams the Send button is REMOVED from the DOM (a Stop control
    /// takes its place) and it returns when the reply is done - the page's busy signal. The model selector's
    /// <c>data-disabled</c> stays set after the reply, so it is NOT one.</item>
    /// <item>Messages: <c>div[id^="message-"]</c> siblings. A user message is <c>div.user-message</c> whose text
    /// is ONE text node (white-space: pre-wrap) inside <c>.chat-user</c>. A reply holds <c>.chat-assistant</c>, and
    /// its action bar (<c>div.buttons</c>, beside it) gains <c>button.regenerate-response-button</c> when done.</item>
    /// <item>Reply body: <c>#response-content-container</c> (the id repeats in every reply). It starts with a
    /// collapsible "Thought Process" panel, <c>.thinking-chain-container</c> - the model's reasoning, never its
    /// answer, so it is excluded from the scrape.</item>
    /// <item>Code: no &lt;pre&gt;. A block is a wrapper div holding a language label div, a sticky copy-button div and
    /// <c>div.language-*</c> around a CodeMirror 6 editor. Each line is a <c>.cm-line</c> (an empty line holds just a
    /// &lt;br&gt;), with no newline text between lines. CodeMirror VIRTUALIZES: a visible 600-line block kept 28 lines
    /// in the DOM and put one <c>.cm-gap</c> spacer in place of the rest, but a HIDDEN reply (the extension hides
    /// tool traffic) rendered all 600 of 600. A gap means the text is partial. The page-world bridge reads the exact
    /// document first (<c>.cm-content</c>'s <c>cmView.view.state.doc</c>); the DOM lines are the fallback.</item>
    /// </list>
    /// </summary>
    public class ZaiSite : IChatSite
    {
        private readonly SpawnJSRuntime _js;
        public ZaiSite(SpawnJSRuntime js) => _js = js;

        public string Name => "Z.ai";
        public bool Matches(string host) => string.Equals(host, "chat.z.ai", StringComparison.OrdinalIgnoreCase);

        public string ComposeSelector => "textarea#chat-input";
        public string SendButtonSelector => "button#send-message-button";
        public string UserMessageSelector => "div.user-message";
        public string ReplySelector => ".chat-assistant";
        // The wrapper div around label + copy button + editor. No ancestor qualifier: the scrape self-test runs
        // on a detached replica.
        public string CodeBlockSelector => "div:has(> div[class*=\"language-\"])";
        public string CodeSelector => ".cm-content";
        public string? ExcludeSelector => ".thinking-chain-container";
        // Exact text from the editor's own objects via wwwroot/page-bridge.js; the DOM reading below is the fallback.
        public bool ReadsCodeFromPage => true;
        public string[] WatchedAttributes => new[] { "disabled" };
        // Set from measurement: see the e2e comparison recorded in Web-Agents.md.
        public bool IntroduceWithWarmUp => false;
        public int MaxMessageLength => 0;   // no limit measured

        // The sidebar's "New Chat" button is in the DOM even while the sidebar is collapsed.
        public bool StartNewChat(Document document) => SiteDom.Click(document, "button#new-chat-button");

        public string ReadCompose(HTMLElement compose) => SiteDom.TextAreaValue(compose);
        public void WriteCompose(HTMLElement compose, string text) => SiteDom.SetTextAreaValue(_js, compose, text);

        public bool CanSend(HTMLElement sendButton)
        {
            using var b = sendButton.JSRefCopy<HTMLButtonElement>();
            return !b.Disabled;
        }

        const string UserTextSelector = ".chat-user div[class*=\"max-h-\"] > div";

        public string ReadUserText(HTMLElement userMessage)
        {
            using var t = userMessage.QuerySelector<HTMLElement>(UserTextSelector) ?? userMessage.QuerySelector<HTMLElement>(".chat-user");
            return ((t ?? userMessage).TextContent ?? "").Trim();
        }

        // Every message div; "-start" ids are scroll anchors INSIDE a message.
        const string MessageSelector = "div[id^=\"message-\"]:not([id$=\"-start\"])";

        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            // Each message sits in its OWN wrapper div, so a reply is not the user message's sibling: walk all
            // messages in document order, from the user message to the next reply (or the next user message).
            using var doc = _js.GetDocument();
            if (doc == null) return null;
            var messages = doc.QuerySelectorAll<HTMLElement>(MessageSelector).Using(o => o.ToArray());
            try
            {
                int i = System.Array.FindIndex(messages, m => m.IsSameNode(userMessage));
                if (i < 0) return null;
                for (int j = i + 1; j < messages.Length; j++)
                {
                    if (messages[j].ClassList.Contains("user-message")) return null;
                    var reply = messages[j].QuerySelector<HTMLElement>(ReplySelector);
                    if (reply != null) return reply;
                }
                return null;
            }
            finally { foreach (var m in messages) m.Dispose(); }
        }

        public HTMLElement? ReplyContent(HTMLElement reply) =>
            reply.QuerySelector<HTMLElement>("#response-content-container") ?? reply.JSRefCopy<HTMLElement>();

        public bool IsGenerating(Document document) => !SiteDom.Exists(document, SendButtonSelector);

        public bool IsReplyComplete(Document document, HTMLElement reply)
        {
            if (IsGenerating(document)) return false;
            using var message = reply.Closest<HTMLElement>("div[id^=\"message-\"]");
            using var regenerate = message?.QuerySelector("button.regenerate-response-button");
            return regenerate != null;
        }

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            using var t = userMessage.QuerySelector<HTMLElement>(UserTextSelector);
            if (t != null) SiteDom.BlankFromMarker(t, marker);
        }

        /// <summary>CodeMirror lines joined with newlines: textContent alone would glue them together.</summary>
        public string ReadCodeText(HTMLElement content)
        {
            var lines = content.QuerySelectorAll<HTMLElement>(".cm-line").Using(o => o.ToArray());
            try { return string.Join("\n", lines.Select(l => l.TextContent ?? "")); }
            finally { foreach (var l in lines) l.Dispose(); }
        }

        /// <summary>A <c>.cm-gap</c> spacer stands in for lines CodeMirror did not render.</summary>
        public bool IsCodeComplete(HTMLElement content)
        {
            using var gap = content.QuerySelector(".cm-gap");
            return gap == null;
        }

        public HTMLElement BuildCodeBlockReplica(Document doc, string code)
        {
            // wrapper > [label "csharp", sticky div > button (copy), div.language-csharp > div > div.cm-editor >
            // div.cm-scroller > div.cm-content > div.cm-line per line (<br> in an empty one)]
            var block = doc.CreateElement<HTMLElement>("div");
            using var label = doc.CreateElement<HTMLElement>("div");
            label.TextContent = "csharp";
            using var sticky = doc.CreateElement<HTMLElement>("div");
            using var copy = doc.CreateElement<HTMLElement>("button");
            copy.ClassName = "copy-code-button";
            copy.TextContent = "Copy";
            sticky.AppendChild(copy);
            using var lang = doc.CreateElement<HTMLElement>("div");
            lang.ClassName = "language-csharp";
            using var inner = doc.CreateElement<HTMLElement>("div");
            using var editor = doc.CreateElement<HTMLElement>("div");
            editor.ClassName = "cm-editor";
            using var scroller = doc.CreateElement<HTMLElement>("div");
            scroller.ClassName = "cm-scroller";
            using var content = doc.CreateElement<HTMLElement>("div");
            content.ClassName = "cm-content";
            content.SetAttribute("role", "textbox");
            // A final "\n" is a last, empty line - the same as CodeMirror's document model.
            foreach (var line in code.Split('\n'))
            {
                using var l = doc.CreateElement<HTMLElement>("div");
                l.ClassName = "cm-line";
                if (line.Length == 0) { using var br = doc.CreateElement<HTMLElement>("br"); l.AppendChild(br); }
                else l.TextContent = line;
                content.AppendChild(l);
            }
            scroller.AppendChild(content);
            editor.AppendChild(scroller);
            inner.AppendChild(editor);
            lang.AppendChild(inner);
            block.AppendChild(label);
            block.AppendChild(sticky);
            block.AppendChild(lang);
            return block;
        }
    }
}
