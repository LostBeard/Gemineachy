using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// gemini.google.com. Angular + a Quill editor. Turns are <c>div.conversation-container</c>, each holding one
    /// <c>user-query</c> and one <c>model-response</c>. Measured 2026-10-12, signed out.
    /// </summary>
    public class GeminiSite : IChatSite
    {
        public string Name => "Gemini";
        public bool Matches(string host) => string.Equals(host, "gemini.google.com", StringComparison.OrdinalIgnoreCase);

        // ql-editor is the first contenteditable in chat-window (ql-clipboard, also contenteditable, follows it).
        public string ComposeSelector => "chat-window div[contenteditable=\"true\"]";
        public string SendButtonSelector => "chat-window button[aria-label*=\"Send\"]";
        public string UserMessageSelector => "chat-window user-query";
        public string ReplySelector => "chat-window model-response";
        public string CodeBlockSelector => "code-block";
        public string[] WatchedAttributes => new[] { "aria-busy" };
        // The tool note appended to the first message is enough: Gemini used the tools first try 3/3 (2026-10-12).
        public bool IntroduceWithWarmUp => false;
        public int MaxMessageLength => 0;   // no limit measured
        public bool StartNewChat(Document document) => false;   // not needed: no warm-up on Gemini

        const string StopButtonSelector = "chat-window button[aria-label*=\"Stop\"]";
        const string DictateButtonSelector = "chat-window button[aria-label*=\"Dictate\"]";
        // A reply is still streaming while anything inside it is aria-busy="true". The Stop button alone is NOT
        // enough - sampled 2026-10-12: Stop disappeared at 5.5 s while the reply was still aria-busy and
        // streaming until 7.7 s, so the turn was read early (68 chars of a WriteFile call), parsed as zero
        // calls, and the model's correct tool call was silently never executed.
        const string BusySelector = "[aria-busy=\"true\"]";
        const string UserLineSelector = "p.query-text-line";

        // Dormant file-attachment path (Gemini-only), kept for real PDF/image uploads.
        public const string FileAttachmentsSelector = "uploader-file-preview gem-attachment";
        public const string FilesInputSelector = "input[type='file']";
        public const string UploadButtonSelector = "chat-window button[aria-label*=\"Upload\"]";

        public string ReadCompose(HTMLElement compose) => compose.TextContent ?? "";

        public void WriteCompose(HTMLElement compose, string text)
        {
            compose.Focus();
            compose.TextContent = text;
            using var inputEvent = new InputEvent("input", new InputEventOptions
            {
                Bubbles = true,
                Cancelable = true,
                InputType = "insertText",
                Data = text
            });
            compose.DispatchEvent(inputEvent);
        }

        // Gemini only renders the Send button while it can be clicked.
        public bool CanSend(HTMLElement sendButton) => true;

        public string ReadUserText(HTMLElement userMessage)
        {
            var lines = userMessage.QuerySelectorAll<HTMLElement>(UserLineSelector).Using(o => o.ToArray());
            try
            {
                // Query only the text-line paragraphs, skipping hidden accessibility labels ("You said").
                if (lines.Length > 0) return string.Join("\n", lines.Select(l => l.TextContent ?? "")).Trim();
                return (userMessage.TextContent ?? "").Trim();
            }
            finally { foreach (var l in lines) l.Dispose(); }
        }

        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            using var turn = userMessage.Closest<HTMLElement>("div.conversation-container");
            return turn?.QuerySelector<HTMLElement>("model-response");
        }

        public HTMLElement? ReplyContent(HTMLElement reply) =>
            reply.QuerySelector<HTMLElement>("[id*='model-response-message-content']");

        public bool IsGenerating(Document document)
        {
            if (Exists(document, StopButtonSelector)) return true;
            if (Exists(document, $"{ReplySelector} {BusySelector}")) return true;
            // Neither Send nor Dictate shown = mid-transition (a turn starting or finishing).
            return !Exists(document, SendButtonSelector) && !Exists(document, DictateButtonSelector);
        }

        public bool IsReplyComplete(Document document, HTMLElement reply)
        {
            if (Exists(document, StopButtonSelector)) return false;
            using var busy = reply.QuerySelector(BusySelector);
            return busy == null;
        }

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            // The note lives in the SAME user-query as the user's own text, one <p> per line: hide from the
            // marker line to the end, leaving the user's own lines visible.
            var lines = userMessage.QuerySelectorAll<HTMLElement>(UserLineSelector).Using(o => o.ToArray());
            var hiding = false;
            foreach (var p in lines)
            {
                using (p)
                {
                    if (!hiding && (p.TextContent ?? "").IndexOf(marker, StringComparison.Ordinal) >= 0) hiding = true;
                    if (hiding) p.SetAttribute(hiddenAttr, "");
                }
            }
        }

        public string ReadCodeText(HTMLElement pre) => pre.TextContent ?? "";

        public HTMLElement BuildCodeBlockReplica(Document doc, string code)
        {
            // Gemini's code block as observed 2026-08-18: decoration header (language label + button text), then
            // <pre><code>. TextContent of the whole block reads "C#using System;…" - the bug the scraper fixes.
            var block = doc.CreateElement<HTMLElement>("code-block");
            using var deco = doc.CreateElement<HTMLElement>("div");
            deco.ClassName = "code-block-decoration header-formatted";
            using var label = doc.CreateElement<HTMLElement>("span");
            label.TextContent = "C#";
            using var buttons = doc.CreateElement<HTMLElement>("div");
            buttons.ClassName = "buttons";
            buttons.TextContent = "content_copy";
            deco.AppendChild(label);
            deco.AppendChild(buttons);
            using var pre = doc.CreateElement<HTMLElement>("pre");
            using var codeEl = doc.CreateElement<HTMLElement>("code");
            codeEl.TextContent = code;
            pre.AppendChild(codeEl);
            block.AppendChild(deco);
            block.AppendChild(pre);
            return block;
        }

        private static bool Exists(Document document, string selector)
        {
            using var el = document.QuerySelector(selector);
            return el != null;
        }
    }
}
