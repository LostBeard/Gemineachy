# Gemineachy - Project Rules

Global rules: `D:\users\tj\Projects\CLAUDE.md`. Read those first.

**What it is:** a .NET WASM Manifest V3 browser extension that adds tools and games to the Gemini web chat, with Reachy Mini integration planned. Built on SpawnJS + `SpawnDev.SpawnJS.BrowserExtension`.

## Layout

- Repo root: `Gemineachy\Gemineachy\` (holds `Gemineachy.slnx`, `README.md`, `_tools\`)
- App project: `Gemineachy\Gemineachy\Gemineachy\` - `Services\`, `Games\`, `Apps\`, `Desktop\`, `wwwroot\`
- **`_tools\` is the test suite and lives IN the repo** (moved in 2026-09-07 - it had been sitting outside version control):

| harness | covers | last run |
|---|---|---|
| `_tools/tool-tests` | `ToolProtocol` (incl. fencing / `[Verbatim]`), `ToolCall`, `DelegateFormatter`, `VirtualFilePath`, `AgentToolAttribute`, `VerbatimAttribute` | 104/104 |
| `_tools/checkers-tests` | `Games/CheckersGame.cs` | 45/45 |
| `_tools/chess-tests` | `Games/ChessGame.cs` | 32/32 |

Run: `cd _tools/<harness> && dotnet run -c Release`. They `<Compile Include>` the real production source directly (`..\..\Gemineachy\...`), so they test what ships - keep it that way.

🔴 **Keep tooling IN the repo.** These harnesses were lost once already to a scratchpad wipe, and the tool pipeline's pure logic had zero tests until they were rewritten. A suite that does not exist silently stops being a regression gate.

## Multi-site: `ChatAgentService` + one `IChatSite` adapter per chat site (2026-10-12)

The tool loop (`Services/ChatAgentService.cs`, was GeminiChatService) is site-neutral. Everything about a
page lives in `Services/Sites/`: `GeminiSite` (gemini.google.com), `DuckAiSite` (duck.ai). The page's
host picks the adapter at startup; the manifest's `content_scripts.matches` decides where the extension
runs. Ranked candidates for the next adapters, with measured signed-out behaviour, are in
`..\Web-Agents.md` (outside the repo).

Adding a site: probe it signed out with `_tools/site-probe.js`, `turns-probe.js` and a stream sampler
(`stream-sample.js` / `duck-stream.js`). Then write the adapter, add the host to the manifest (match
patterns only - never name sites in the description), and give it a `BuildCodeBlockReplica` that copies
the site's REAL code-block DOM, so the always-on scrape self-test guards it. Every adapter member must be
measured, not assumed:
- **Completion**: an attribute flip (Gemini `aria-busy`, Duck `data-activeresponse`) - list it in
  `WatchedAttributes`, or the MutationObserver never sees it.
- **Code text**: Duck renders one span per line with NO newline between them, so `textContent` glues
  lines together. Qwen's `<pre>` adds a label, line numbers and NBSPs. Override `ReadCodeText`.
- **Compose**: React textareas need the PROTOTYPE value setter (`Reflect.Set(proto, "value", v, el)`).
- **Note hiding**: Duck renders a message as many text nodes in one `<p>`, so blank them in place.
- **Turns re-render**: Gemini re-renders every turn and drops our seen-marker. `ChatAgentService` keeps ONE
  live turn and follows a same-text re-render, so a second turn must never start for it.
- **Model adherence**: Gemini follows the tool note appended to the first message. Duck.ai's GPT model
  declines it ("I can't write files from here") 4/4, so `IntroduceWithWarmUp` sends a hidden warm-up
  first (wording A/B-measured; see `ToolProtocol.BuildWarmUpMessage`). A declined warm-up is retried
  once in a fresh chat (`StartNewChat`), because the user's message is still held at that point. The
  first real message then carries a one-line hidden hint (`BuildPostWarmUpHint`). Measured end to end on
  fresh chats: 1/7 with the note alone, 5/5 with warm-up + hint. Never ask a model to hide its own reply -
  that reads as deception and it refuses; the extension hides the reply itself.
- **New chat**: "New chat" is in-page navigation, so the extension survives it. `ToolsNeedIntroducing()`
  treats a conversation with no user messages as new, so the note or warm-up rides again.

Test harnesses for any site: `repro-csharp.sh <out> <path> <msg> <site>`, `warmup-ab.sh` (wording A/B,
fresh chats), Files app "🧪 Test tools" (FsSelfTest, per origin - each site has its own OPFS).

## 🔴 Only code blocks are exact - `[Verbatim]` + code-region marks (2026-10-12)

Outside a code block, the chat page's markdown rendering silently DROPS text, and the scraped text cannot
show what is missing. Live repro: a C# `WriteFile` turned `List<int>` into `List` (tag-stripped) and `\"`
into `"`. The JSON stayed valid, so the corruption was silent.
- `ReadMessageText` wraps every code block's text in `ToolProtocol.CodeStart`/`CodeEnd` (private-use chars).
  The protocol uses them to know what was fenced. Everyone else gets `StripCodeMarks(...)`.
- A tool parameter carrying code/exact text is marked `[Verbatim]`. It binds ONLY from a code block (a fenced
  call or a fenced PAYLOAD); otherwise the call fails with an error telling the model how to resend.
- The first-message note, `ListTools` and the schema lookups all carry `BuildCallRules()`, because a model that
  reaches a tool via the note + `ListTools` never sees the full manifest. The index lists argument names
  (`WriteFile(path, content*, append?)`) so the model stops guessing them.

## 🔴 Driving Gemini's page - three measured traps (2026-10-12)

- **Set text, then cross a task boundary before clicking Send** (`await Task.Delay(1)`). Quill syncs its
  model after the current task, and Send sends the model. A same-task click was never accepted (0/2).
- **A turn is done only when nothing in it is `aria-busy="true"`**, not merely when Stop disappears. Stop went
  away about 2 s before streaming ended, the turn was read at 68 chars, and a correct `WriteFile` call was
  silently never run.
- **Probe the chat tab while it is the ACTIVE tab.** A hidden tab is throttled (timers about 1/s, no animation
  frames). Opening `chrome://extensions` hides it, and `_tools/reload-all.sh` re-activates it afterwards.
  Windows also hides a COVERED window (occlusion tracking), so launch the debug Chrome with
  `_tools/launch-debug-chrome.ps1` - it disables that and keeps probes deterministic while TJ works in front
  of it. Switching TABS still hides a tab, so real hidden-tab behaviour stays testable on purpose.
  Send acceptance = compose cleared OR Stop showing OR a new turn, and it is re-checked before any retry, so a
  slow accept is never answered with a second send.

## 🔴 Scraping the LLM reply out of the rendered DOM

This is where the painful bugs live. Two distinct, independently proven failures:

**1. Markdown strips `\` before ASCII punctuation.** Scraping a reply from the RENDERED DOM corrupts JSON escapes: `\"` arrives as a bare `"` and the tool-call JSON breaks (byte-276 on a WriteFile call). `\r\n` survived; `\"` did not.

**Fix:** FENCE the `«TOOL_CALL»{json}»` block, and route code and free-form args through a VERBATIM code-block PAYLOAD channel - `«PAYLOAD:argName»`…`«/PAYLOAD:argName»`, name-form, with the arg omitted from the JSON.

⚠️ Do NOT put the marker INSIDE the JSON - it ends in `»` which is CallClose and truncates the block.

**2. `textContent` glues the code block's CHROME onto the code.** Gemini's live DOM reads `«PAYLOAD:content»C#using System;` (and `JSON«TOOL_CALL …`), so a written C# file began with a literal `C#`.

**Fix:** deep-CLONE the node, replace each code block WHOLESALE with its `<pre>` text, then read `textContent`. You cannot use `innerText` - it returns "" for the `display:none` hidden tool traffic.

⚠️ Anchor an end-of-string strip with `\z`, NEVER `$` - `$` also matches before a final newline, so `Replace` eats both. That one was caught by the startup self-test on its first run; keep that self-test.

## ⚠️ WebExtension APIs - do not assert compat from memory

Both `browser` and `chrome` exist in content scripts in both Chrome and Firefox. Do not add a `browser ?? chrome` shim on a remembered incompatibility. When TJ reports observing something work, that outranks recollection - reconcile, don't argue.

The fix that actually worked for DNR `modifyHeaders` was `host_permissions`.

⚠️ Archive with forward slashes: a backslash in a compressed archive path breaks Firefox extension subfolders.

## Browser / WASM

Standard SpawnJS extension rules apply (see `SpawnDev.SpawnJS\...\CLAUDE.md`). In particular: an unhandled exception on a callback EXITS the WASM runtime, and slot lifetime is manual.
