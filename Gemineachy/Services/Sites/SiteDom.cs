using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Gemineachy.Services.Sites
{
    /// <summary>DOM helpers shared by the chat-site adapters.</summary>
    internal static class SiteDom
    {
        /// <summary>True when <paramref name="selector"/> matches anything.</summary>
        public static bool Exists(Document document, string selector)
        {
            using var el = document.QuerySelector(selector);
            return el != null;
        }

        /// <summary>True when <paramref name="selector"/> matches an element with a non-zero layout box. For
        /// controls a site keeps in the DOM while idle and merely hides (Duck.ai's "Stop generating").</summary>
        public static bool Visible(Document document, string selector)
        {
            using var el = document.QuerySelector<HTMLElement>(selector);
            if (el == null) return false;
            using var rect = el.GetBoundingClientRect();
            return rect.Width > 0 && rect.Height > 0;
        }

        /// <summary>Set a React-style controlled &lt;textarea&gt;'s value so the framework sees it, then fire input.</summary>
        /// <remarks>
        /// React-like frameworks track a controlled field's value through a property they install on the ELEMENT,
        /// so a plain <c>el.value = x</c> updates that tracker too and the framework then sees "no change" and drops
        /// the input event. Calling the PROTOTYPE's value setter with the element as receiver bypasses the tracker -
        /// the same thing a real keystroke does.
        /// </remarks>
        public static void SetTextAreaValue(SpawnJSRuntime js, HTMLElement textarea, string text)
        {
            textarea.Focus();
            using var proto = js.Get<SpawnJSObject>("HTMLTextAreaElement.prototype");
            Reflect.Set(proto, "value", text, textarea);
            using var inputEvent = new Event("input", new EventOptions { Bubbles = true, Cancelable = true });
            textarea.DispatchEvent(inputEvent);
        }

        /// <summary>The value of a &lt;textarea&gt;.</summary>
        public static string TextAreaValue(HTMLElement textarea)
        {
            using var ta = textarea.JSRefCopy<HTMLTextAreaElement>();
            return ta.Value ?? "";
        }

        /// <summary>
        /// Blank out everything from <paramref name="marker"/> on inside <paramref name="container"/>'s direct TEXT
        /// nodes, in place (nodeValue only), plus the blank lines just before it. For a user message rendered as
        /// one element with white-space: pre-wrap - one text node (ChatGPT) or one node per line (Duck.ai). The
        /// framework keeps references to these nodes and only ever rewrites their values, so no structure changes
        /// under it. Idempotent: once blanked, the marker is gone.
        /// </summary>
        public static void BlankFromMarker(HTMLElement container, string marker)
        {
            using var nodes = container.ChildNodes;
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

        /// <summary>Click the first element matching <paramref name="selector"/>; false when there is none.</summary>
        public static bool Click(Document document, string selector)
        {
            using var el = document.QuerySelector<HTMLElement>(selector);
            if (el == null) return false;
            el.Click();
            return true;
        }

        /// <summary>A replica code block: a wrapper element holding a label element and &lt;pre&gt;&lt;code&gt;.</summary>
        public static HTMLElement CodeBlock(Document doc, string wrapperTag, string labelTag, string label, string code)
        {
            var block = doc.CreateElement<HTMLElement>(wrapperTag);
            using var lab = doc.CreateElement<HTMLElement>(labelTag);
            lab.TextContent = label;
            using var pre = doc.CreateElement<HTMLElement>("pre");
            using var codeEl = doc.CreateElement<HTMLElement>("code");
            codeEl.TextContent = code;
            pre.AppendChild(codeEl);
            block.AppendChild(lab);
            block.AppendChild(pre);
            return block;
        }
    }
}
