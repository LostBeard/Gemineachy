using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// Google Search AI Mode (www.google.com/search?udm=50), signed out. Obfuscated Wiz markup, but these hooks are
    /// semantic. Measured 2026-10-12 (_tools/site-study.sh, aimode-stream.js):
    /// <list type="bullet">
    /// <item>Compose: the one <c>textarea[maxlength="8192"]</c> (hidden feedback boxes are maxlength 1500).
    /// Send: <c>button[data-xid="input-plate-send-button"]</c>. 8192 characters per message.</item>
    /// <item>A user query renders as <c>span[role=heading][aria-level=2]</c> (one pre-wrap text node in an inner
    /// span); each reply is a <c>[data-scope-id="turn"]</c>. Queries are NOT inside their turns: query i pairs
    /// with turn i in document order.</item>
    /// <item>No Stop control. A turn is complete when its feedback buttons (Good/Bad response) appear - they come
    /// last. (<c>data-complete="true"</c> is set on follow-up turns only, never on a chat's first turn.)</item>
    /// <item>Reply body = the element right after <c>h3[data-ignore-copy]</c>. That heading reads "AI Mode reply
    /// for &lt;the query&gt;" - for the warm-up or note it would carry an EXAMPLE «TOOL_CALL», so it must never be
    /// scraped as part of the reply.</item>
    /// <item>Code block: <c>div.aiModeUiCodeBlock__Container</c> around a &lt;pre&gt; whose text is exact.</item>
    /// </list>
    /// </summary>
    public class GoogleAiModeSite : IChatSite
    {
        private readonly SpawnJSRuntime _js;
        public GoogleAiModeSite(SpawnJSRuntime js) => _js = js;

        public string Name => "Google AI Mode";
        // The content script only injects on AI Mode URLs (manifest include_globs *udm=50*), so the host is enough.
        public bool Matches(string host) => string.Equals(host, "www.google.com", StringComparison.OrdinalIgnoreCase);

        public string ComposeSelector => "textarea[maxlength=\"8192\"]";
        public string SendButtonSelector => "button[data-xid=\"input-plate-send-button\"]";
        public string UserMessageSelector => "span[role=\"heading\"][aria-level=\"2\"]";
        public string ReplySelector => "[data-scope-id=\"turn\"]";
        public string CodeBlockSelector => "div.aiModeUiCodeBlock__Container";
        public string[] WatchedAttributes => new[] { "data-complete", "disabled" };
        // Measured 2026-10-12: with the tool note appended to the first message, AI Mode answered with a plain
        // web-results fallback ("10 sites - here are top web results") 3/3; the short warm-up got the ListTools call 3/3.
        public bool IntroduceWithWarmUp => true;
        public int MaxMessageLength => 8192;
        public bool StartNewChat(Document document) => false;

        const string FeedbackSelector = "button[aria-label=\"Good response\"]";

        public string ReadCompose(HTMLElement compose) => SiteDom.TextAreaValue(compose);
        public void WriteCompose(HTMLElement compose, string text) => SiteDom.SetTextAreaValue(_js, compose, text);

        public bool CanSend(HTMLElement sendButton)
        {
            using var b = sendButton.JSRefCopy<HTMLButtonElement>();
            return !b.Disabled;
        }

        public string ReadUserText(HTMLElement userMessage) => (userMessage.TextContent ?? "").Trim();

        /// <summary>Query i answers with turn i (document order) - the queries are not inside their turns.</summary>
        public HTMLElement? FindReply(HTMLElement userMessage)
        {
            using var document = _js.GetDocument();
            if (document == null) return null;
            var queries = document.QuerySelectorAll<HTMLElement>(UserMessageSelector).Using(o => o.ToArray());
            var turns = document.QuerySelectorAll<HTMLElement>(ReplySelector).Using(o => o.ToArray());
            try
            {
                int idx = -1;
                for (int i = 0; i < queries.Length; i++) if (queries[i].IsSameNode(userMessage)) { idx = i; break; }
                return idx >= 0 && idx < turns.Length ? turns[idx].JSRefCopy<HTMLElement>() : null;
            }
            finally
            {
                foreach (var q in queries) q.Dispose();
                foreach (var t in turns) t.Dispose();
            }
        }

        public HTMLElement? ReplyContent(HTMLElement reply) => reply.QuerySelector<HTMLElement>("h3[data-ignore-copy] + div");

        public bool IsGenerating(Document document)
        {
            var queries = document.QuerySelectorAll<HTMLElement>(UserMessageSelector).Using(o => o.Length);
            var turns = document.QuerySelectorAll<HTMLElement>(ReplySelector).Using(o => o.ToArray());
            try
            {
                if (queries > turns.Length) return true;           // sent, reply not rendered yet
                return turns.Length > 0 && !Complete(turns[^1]);
            }
            finally { foreach (var t in turns) t.Dispose(); }
        }

        public bool IsReplyComplete(Document document, HTMLElement reply) => Complete(reply);

        // The feedback buttons are the one completion signal present on EVERY turn: a follow-up turn gets
        // data-complete="true" ~0.6 s before its feedback buttons, but the FIRST turn of a chat never gets
        // data-complete at all (measured 2026-10-12) - waiting on it left that turn pending forever. (English labels:
        // the only stable hook on these buttons.)
        private static bool Complete(HTMLElement turn)
        {
            using var fb = turn.QuerySelector(FeedbackSelector);
            return fb != null;
        }

        public void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr)
        {
            using var inner = userMessage.QuerySelector<HTMLElement>("span");
            SiteDom.BlankFromMarker(inner ?? userMessage, marker);
        }

        public string ReadCodeText(HTMLElement pre) => pre.TextContent ?? "";

        public HTMLElement BuildCodeBlockReplica(Document doc, string code)
        {
            var block = SiteDom.CodeBlock(doc, "div", "span", "csharp", code);
            block.ClassName = "aiModeUiCodeBlock__Container";
            return block;
        }
    }
}
