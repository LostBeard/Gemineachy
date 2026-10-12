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

        public string ReadCompose(HTMLElement compose)
        {
            using var ta = compose.JSRefCopy<HTMLTextAreaElement>();
            return ta.Value ?? "";
        }

        public void WriteCompose(HTMLElement compose, string text)
        {
            compose.Focus();
            // React tracks a controlled textarea's value through a property it installs on the ELEMENT, so a plain
            // `el.value = x` updates that tracker too and React then sees "no change" and drops the input event.
            // Calling the PROTOTYPE's value setter with the element as receiver bypasses the tracker - the same
            // thing a real keystroke does - so the input event below reaches React's onChange.
            using var proto = _js.Get<SpawnJSObject>("HTMLTextAreaElement.prototype");
            Reflect.Set(proto, "value", text, compose);
            using var inputEvent = new Event("input", new EventOptions { Bubbles = true, Cancelable = true });
            compose.DispatchEvent(inputEvent);
        }

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
            if (StopVisible(document)) return true;
            using var streaming = document.QuerySelector("[data-activeresponse=\"false\"]");
            return streaming != null;
        }

        public bool IsReplyComplete(Document document, HTMLElement reply) =>
            reply.GetAttribute("data-activeresponse") == "true" && !StopVisible(document);

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            // One <p> holds every line, but React renders it as MANY text nodes - one per line with "\n" nodes
            // between (measured: 26 nodes, the marker in the 4th). Blank out the note IN PLACE, nodeValue only:
            // cut the marker's node at the marker, empty every node after it, and empty the blank lines before
            // it. React keeps references to these nodes and only ever rewrites their values, so no structure
            // changes under it. Idempotent - once trimmed the marker is gone.
            using var p = userMessage.QuerySelector<HTMLElement>("p");
            if (p == null) return;
            using var nodes = p.ChildNodes;
            var texts = new List<Text>();
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    var n = nodes.Item<Text>(i);
                    if (n.NodeType == 3) texts.Add(n); else n.Dispose();
                }
                int idx = texts.FindIndex(t => (t.Data ?? "").Contains(marker, StringComparison.Ordinal));
                if (idx < 0) return;
                var d0 = texts[idx].Data ?? "";
                texts[idx].Data = d0.Substring(0, d0.IndexOf(marker, StringComparison.Ordinal));
                for (int j = idx + 1; j < texts.Count; j++) texts[j].Data = "";
                for (int j = idx; j >= 0; j--)
                {
                    var d = texts[j].Data ?? "";
                    if (d.Trim().Length == 0) texts[j].Data = "";
                    else { texts[j].Data = d.TrimEnd(); break; }
                }
            }
            finally { foreach (var t in texts) t.Dispose(); }
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

        private static bool StopVisible(Document document)
        {
            using var stop = document.QuerySelector<HTMLElement>(StopButtonSelector);
            if (stop == null) return false;
            using var rect = stop.GetBoundingClientRect();
            return rect.Width > 0 && rect.Height > 0;
        }
    }
}
