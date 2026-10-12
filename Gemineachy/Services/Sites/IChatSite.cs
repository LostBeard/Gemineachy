using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>
    /// Everything <see cref="ChatAgentService"/> needs to know about ONE chat site's page: where the compose
    /// box and Send control are, how to put text in it, where user messages and replies render, when a
    /// reply has finished, and what a rendered code block looks like. The tool loop itself is site-neutral.
    /// </summary>
    /// <remarks>
    /// Every member here was MEASURED on the live site (signed out) - see each implementation's notes. Chat
    /// sites restructure their DOM without warning; when an adapter breaks, re-probe with the _tools scripts
    /// (site-probe.js, turns-probe.js, stream-sample.js / duck-stream.js) rather than guessing.
    /// </remarks>
    public interface IChatSite
    {
        /// <summary>Display name ("Gemini", "Duck.ai"), also used in user-facing text.</summary>
        string Name { get; }

        /// <summary>True when this adapter drives the page at <paramref name="host"/> (e.g. "duck.ai").</summary>
        bool Matches(string host);

        /// <summary>CSS selector of the compose box. Also styles it while a hidden tool send is in flight.</summary>
        string ComposeSelector { get; }

        /// <summary>CSS selector of the Send control (it need not be enabled yet).</summary>
        string SendButtonSelector { get; }

        /// <summary>CSS selector matching every user message element, in document order.</summary>
        string UserMessageSelector { get; }

        /// <summary>CSS selector matching every reply element, in document order.</summary>
        string ReplySelector { get; }

        /// <summary>The outer element of a rendered code block. The scraper replaces it WHOLESALE with its
        /// &lt;pre&gt; text, so labels and copy buttons never reach the tool parser.</summary>
        string CodeBlockSelector { get; }

        /// <summary>
        /// True when this site's model needs a hidden WARM-UP round before the first message of a chat: it is
        /// asked to write nothing but a ListTools call, gets the real tool list back, and only then sees the
        /// user's message. False = the tool note appended to the user's first message is enough.
        /// </summary>
        bool IntroduceWithWarmUp { get; }

        /// <summary>True when this site's model follows the first-message tool note written as an automated note FROM
        /// THE EXTENSION ("NOT from the user"); false for the user's own voice. It differs per model (2026-10-12):
        /// Duck.ai's GPT treated the extension voice as third-party injection, while Gemini declined the user voice.</summary>
        bool ToolNoteInExtensionVoice => false;

        /// <summary>The most characters one message may carry on this site, or 0 when no limit is known. Tool
        /// results longer than this are trimmed WITH a note telling the model to ask for less - never cut silently.</summary>
        int MaxMessageLength { get; }

        /// <summary>Open a fresh, empty chat (used to retry a declined warm-up in a clean context). Returns
        /// false when the site has no way to do it.</summary>
        bool StartNewChat(Document document);

        /// <summary>The attributes whose changes signal progress (a reply finishing, Send enabling). The page's
        /// MutationObserver watches exactly these, so a completion that is only an attribute flip is seen.</summary>
        string[] WatchedAttributes { get; }

        /// <summary>The text of a compose box.</summary>
        string ReadCompose(HTMLElement compose);

        /// <summary>Put <paramref name="text"/> in the compose box the way the page's own editor will accept
        /// it. The caller crosses a task boundary afterwards before clicking Send.</summary>
        void WriteCompose(HTMLElement compose, string text);

        /// <summary>True when the Send control can be clicked right now.</summary>
        bool CanSend(HTMLElement sendButton);

        /// <summary>The text of a user message as rendered (used for marker detection and OnQuery).</summary>
        string ReadUserText(HTMLElement userMessage);

        /// <summary>The reply that answers <paramref name="userMessage"/>, or null if it has not rendered yet.</summary>
        HTMLElement? FindReply(HTMLElement userMessage);

        /// <summary>The element holding a reply's rendered markdown body - without headers such as a model name.</summary>
        HTMLElement? ReplyContent(HTMLElement reply);

        /// <summary>True when the page is producing a reply (or has not finished settling one).</summary>
        bool IsGenerating(Document document);

        /// <summary>True when <paramref name="reply"/> has finished streaming.</summary>
        bool IsReplyComplete(Document document, HTMLElement reply);

        /// <summary>Cosmetically hide the tool note appended to a user's first message: everything from
        /// <paramref name="marker"/> on. Must be idempotent - it runs on every DOM mutation.</summary>
        void HideFirstNote(HTMLElement userMessage, string marker, string hiddenAttr);

        /// <summary>The element holding a rendered code block's text: &lt;pre&gt; on most sites, an editor's content
        /// element where the site renders code in an editor (Z.ai: CodeMirror's <c>.cm-content</c>).</summary>
        string CodeSelector => "pre";

        /// <summary>Elements inside a reply that are NOT the answer (Z.ai's "Thought Process" panel), removed from
        /// the scraped copy before it is read, or null when there are none.</summary>
        string? ExcludeSelector => null;

        /// <summary>The exact code text of a rendered code block's <see cref="CodeSelector"/> element.</summary>
        string ReadCodeText(HTMLElement pre);

        /// <summary>True when the page-world bridge (wwwroot/page-bridge.js, listed for this site in the manifest) can
        /// read this site's code blocks exactly from the page's own editor objects. The scraper then asks it first
        /// and uses the DOM reading (<see cref="ReadCodeText"/>) only when the bridge has no answer.</summary>
        bool ReadsCodeFromPage => false;

        /// <summary>False when the page rendered only PART of this code block (a virtualizing editor shows the lines
        /// near the viewport), so <see cref="ReadCodeText"/> cannot return all of it. The tool protocol then refuses
        /// any payload from it rather than write a silently truncated file.</summary>
        bool IsCodeComplete(HTMLElement code) => true;

        /// <summary>A DETACHED replica of one rendered code block holding <paramref name="code"/>, built the way
        /// this site renders it (chrome included), for the always-on scrape self-test.</summary>
        HTMLElement BuildCodeBlockReplica(Document document, string code);
    }
}
