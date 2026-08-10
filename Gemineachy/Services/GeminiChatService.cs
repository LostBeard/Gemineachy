using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using File = SpawnDev.SpawnJS.JSObjects.File;

namespace Gemineachy.Services
{
    public class GeminiChatService(SpawnJSRuntime JS, SpawnDomRenderer Renderer) : IAsyncBackgroundService
    {
        const string FileAttachmentsSelector = "uploader-file-preview gem-attachment";
        const string TextInputSelector = "chat-window div[contenteditable=\"true\"]";
        const string FilesInputSelector = "input[type='file']";
        const string UploadButtonSelector = "chat-window button[aria-label*=\"Upload\"]";
        const string DictateButtonSelector = "chat-window button[aria-label*=\"Dictate\"]";
        const string SendButtonSelector = "chat-window button[aria-label*=\"Send\"]";
        const string StopButtonSelector = "chat-window button[aria-label*=\"Stop\"]";
        // Marker attribute placed on tool-traffic chat elements (tool-call model responses and our
        // injected tool-response/manifest user messages). Hidden by CSS unless ShowToolTraffic is on.
        const string ToolTrafficAttr = "data-gemineachy-tool";
        const string ShowToolsClass = "gemineachy-show-tools";
        // While a tool send is in flight this class is on <html>; CSS collapses the compose box's inner
        // text so our programmatic type+send doesn't flash/grow/shrink the box.
        const string ToolSendingClass = "gemineachy-tool-sending";
        private Task? _ready = null;
        public Task Ready => _ready ??= InitAsync();
        /// <summary>
        /// Fired when the query is sent<br/>
        /// string query, Task&lt;string> response
        /// </summary>
        public event Action<string, Task<string>>? OnQuery;
        /// <summary>
        /// Fired when the full response is ready<br/>
        /// string query, string response
        /// </summary>
        public event Action<string, string>? OnQueryResponse;
        public event Action? OnDOMMutation;
        public event Action? OnStateChanged;
        public Task WhileBusy => _busyTask.Task;
        public bool Busy => _busyTask.Task.IsCompleted == false;
        private Document? _document;
        private MutationObserver? _responseObserver;
        private List<string> _processedConversationQuery = new List<string>();
        private TaskCompletionSource _busyTask = new TaskCompletionSource();
        private ActionCallback? _mutationCallback;
        private SemaphoreSlim _queryLock = new SemaphoreSlim(1);

        public Dictionary<string, ToolCall> Tools = new Dictionary<string, ToolCall>();
        private async Task InitAsync()
        {
            Console.WriteLine($"GeminiChatService.InitAsync() {JS.GlobalScopeName} {JS.InstanceId}");
            if (JS.IsWindow)
            {
                _document = JS.GetDocument();
                if (_document != null)
                {
                    // inject the CSS that hides tool-traffic chat elements (revealed by ShowToolTraffic)
                    InjectToolTrafficStyle();
                    // ignore all existing messages
                    UpdateFromChat(true);
                    _mutationCallback = Callback.Create(Mutation_Observed);
                    _responseObserver = new MutationObserver(_mutationCallback);
                    using var chatContainer = _document.DocumentElement;
                    if (chatContainer != null)
                    {
                        _responseObserver.Observe(chatContainer, new MutationObserverOptions
                        {
                            ChildList = true,
                            Subtree = true,
                            CharacterData = true
                        });
                    }
                    // register tools this service provides
                    //RegisterTool(SendToolInfo, "Re-sends the full tool manifest to you. Call if you have lost track of the available tools.");
                    //RegisterTool(GetTypeInfo, "Returns the C#-like structure (public properties) of a .NET type used by a tool. Pass the type name shown in a tool's schema.");
                    //RegisterTool(GetTime, "Returns the user's current local date and time as a string.");
                    //RegisterTool(Echo, "Echoes the message back. Minimal connectivity test. Do not call this in a loop.");

                    try
                    {
                        Register(this);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Could not register {ex.ToString()}");
                    }

                    // Send the manifest AFTER the UI has rendered, not as part of Ready. The renderer
                    // starts only once every IAsyncBackgroundService.Ready has completed, so blocking
                    // Ready on a Gemini round-trip here would stall the whole UI. Instead we hook the
                    // renderer's first after-render and send it then (non-blocking).
                    //Renderer.OnAfterRenderAsync += OnRendererAfterRenderAsync;
                }
            }
        }
        private bool _toolInfoSent;
        private async Task OnRendererAfterRenderAsync(bool firstRender)
        {
            if (!firstRender || _toolInfoSent) return;
            _toolInfoSent = true;
            try
            {
                await SendToolInfo();
            }
            catch (Exception ex)
            {
                // The very first send on a FRESH chat is the message that makes Gemini create the
                // conversation and change the URL (/app -> /app/<id>). That SPA navigation swaps the
                // chat DOM mid-flight, so our completion detection can miss the turn finishing and the
                // Query times out. The manifest usually still reached Gemini; retry once now that the
                // chat exists so the base tools are reliably registered. Never let this bubble into the
                // renderer's exception handler (it is not a render fault).
                Console.WriteLine($"Initial tool manifest send did not complete cleanly ({ex.GetType().Name}); retrying once after the chat settles.");
                try
                {
                    await Task.Delay(1500);
                    await SendToolInfo();
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"Tool manifest retry did not complete cleanly ({ex2.GetType().Name}). Use the \"Introduce Tools\" button to re-send if needed.");
                }
            }
        }
        private bool _showToolTraffic = false;
        /// <summary>
        /// When false (default), tool calls (Gemini's <c>«TOOL_CALL»</c> responses) and tool responses
        /// (our injected [TOOL_RESULTS] / [TOOL_MANIFEST] messages) are hidden in the chat window.
        /// Set true to reveal them ("Show tool calls" toggle).
        /// </summary>
        public bool ShowToolTraffic
        {
            get => _showToolTraffic;
            set
            {
                if (_showToolTraffic == value) return;
                _showToolTraffic = value;
                ApplyShowToolTraffic();
                OnStateChanged?.Invoke();
            }
        }
        private void InjectToolTrafficStyle()
        {
            if (_document == null) return;
            using var head = (Node?)_document.Head ?? _document.DocumentElement;
            if (head == null) return;
            using var style = new HTMLStyleElement();
            // IMPORTANT: hide WITHOUT display:none. We mark tool-call responses while Gemini is still
            // streaming into them; display:none removes the element from layout, and Gemini's frontend
            // won't finalize the turn (Stop->Send) for an unlaid-out element - which stalls our
            // completion detection until the user reveals it. Instead use the standard "visually hidden"
            // (clip/off-screen) technique: the element stays rendered and measurable so Gemini finalizes
            // normally, but it's visually gone with no gap. Scoped to :not(show) so the toggle simply
            // stops matching and the element renders normally.
            style.TextContent =
                $"html:not(.{ShowToolsClass}) [{ToolTrafficAttr}]{{" +
                "position:absolute !important;width:1px !important;height:1px !important;" +
                "padding:0 !important;margin:-1px !important;overflow:hidden !important;" +
                "clip:rect(0,0,0,0) !important;clip-path:inset(50%) !important;" +
                "white-space:nowrap !important;border:0 !important;opacity:0 !important;" +
                "pointer-events:none !important;}" +
                // While a tool send is in flight, collapse the compose box's text to a single line and
                // hide it, so filling it with our (hidden) message and clearing it doesn't flash/grow.
                $"html.{ToolSendingClass} {TextInputSelector}{{" +
                "max-height:1lh !important;overflow:hidden !important;opacity:0 !important;}}";
            head.AppendChild(style);
        }
        private void ApplyShowToolTraffic()
        {
            if (_document == null) return;
            using var html = _document.DocumentElement;
            if (html == null) return;
            using var classList = html.ClassList;
            classList.Toggle(ShowToolsClass, _showToolTraffic);
        }
        /// <summary>Toggle the "tool send in flight" class so CSS collapses/hides the compose box text.</summary>
        private void SetToolSending(bool sending)
        {
            if (_document == null) return;
            using var html = _document.DocumentElement;
            if (html == null) return;
            using var classList = html.ClassList;
            classList.Toggle(ToolSendingClass, sending);
        }
        /// <summary>
        /// Hide tool-traffic chat elements the instant they appear/stream in - runs on EVERY mutation,
        /// decoupled from the per-turn completion logic (so a tool call is hidden while it streams, not
        /// only once Gemini finishes). Idempotent: the <c>:not([attr])</c> selectors skip already-marked
        /// elements, and it uses TextContent (no layout reflow) to stay cheap under rapid streaming.
        /// </summary>
        private void EarlyHideToolTraffic()
        {
            if (_document == null) return;
            // Our injected hidden user messages: tool results / manifest / game prompts.
            var userQueries = _document
                .QuerySelectorAll<HTMLElement>($"chat-window user-query:not([{ToolTrafficAttr}])")
                .Using(o => o.ToArray());
            foreach (var uq in userQueries)
            {
                using (uq)
                {
                    using var line = uq.QuerySelector<HTMLElement>("p.query-text-line");
                    var text = line?.TextContent?.TrimStart() ?? "";
                    if (text.StartsWith(ToolProtocol.ResultsMarker, StringComparison.Ordinal)
                        || text.StartsWith(ToolProtocol.ManifestMarker, StringComparison.Ordinal)
                        || text.StartsWith(ToolProtocol.GameMarker, StringComparison.Ordinal))
                    {
                        uq.SetAttribute(ToolTrafficAttr, "");
                    }
                }
            }
            // Gemini responses that are tool-call blocks, or that the agent chose to hide with the
            // «HIDDEN» marker - hidden the moment the marker streams in.
            var modelResponses = _document
                .QuerySelectorAll<HTMLElement>($"chat-window model-response:not([{ToolTrafficAttr}])")
                .Using(o => o.ToArray());
            foreach (var mr in modelResponses)
            {
                using (mr)
                {
                    var text = mr.TextContent ?? "";
                    if (text.Contains(ToolProtocol.CallOpen, StringComparison.Ordinal)
                        || text.Contains(ToolProtocol.HiddenResponseMarker, StringComparison.Ordinal))
                        mr.SetAttribute(ToolTrafficAttr, "");
                }
            }
        }
        /// <summary>
        /// This is presented as a tool to the agent so the agent can query the type structure to allow informed tool calling<br/>
        /// Returns the type structure which will likely look like the C# code of the object type (with only public properties (non-JsonIOngore tagged))
        /// </summary>
        /// <param name="typeName"></param>
        /// <returns></returns>
        [AgentTool("Returns the C#-like structure (public properties) of a .NET type used by a tool. Pass the type name shown in a tool's schema.")]
        public string GetTypeInfo(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "// error: typeName was empty";
            var type = ResolveToolType(typeName);
            if (type == null)
                return $"// unknown type '{typeName}'. It must be a type used by a registered tool's parameters.";
            return DescribeType(type);
        }
        /// <summary>Find a type by name among the parameter types (and their transitive property types)
        /// of all registered tools. Keeps GetTypeInfo scoped to types the agent can actually reference.</summary>
        private Type? ResolveToolType(string typeName)
        {
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>();
            foreach (var t in Tools.Values)
                foreach (var p in t.MethodInfo.GetParameters())
                    queue.Enqueue(p.ParameterType);
            while (queue.Count > 0)
            {
                var raw = queue.Dequeue();
                var t = Nullable.GetUnderlyingType(raw) ?? raw;
                if (!seen.Add(t)) continue;
                if (t.IsArray) { queue.Enqueue(t.GetElementType()!); continue; }
                if (t.IsGenericType) { foreach (var g in t.GetGenericArguments()) queue.Enqueue(g); }
                if (IsComplexType(t))
                {
                    if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) return t;
                    foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        queue.Enqueue(prop.PropertyType);
                }
            }
            return null;
        }
        private static bool IsComplexType(Type t) =>
            t.IsClass && t != typeof(string) || (t.IsValueType && !t.IsPrimitive && !t.IsEnum && Nullable.GetUnderlyingType(t) == null && t != typeof(decimal) && t != typeof(DateTime) && t != typeof(Guid));
        private static string DescribeType(Type type)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"public class {type.Name} {{");
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                var desc = prop.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine($"    // {desc}");
                sb.AppendLine($"    public {DelegateFormatter.GetFriendlyTypeName(prop.PropertyType)} {prop.Name} {{ get; set; }}");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }
        [AgentTool("Returns the user's current local date and time as a string.")]
        string GetTime() => DateTime.Now.ToString();

        // --- Tool discovery (keeps the standing manifest lean: it lists names + one-liners; the full
        //     argument schemas are fetched on demand through these) --------------------------------------
        [AgentTool("Find tools by keywords (space or comma separated). Returns the matching tools WITH their full argument schemas, ready to call. Use this to discover which tool to use and how to call it.")]
        string SearchTools(string query)
        {
            var matches = ToolProtocol.MatchTools(Tools.Values, query);
            if (matches.Count == 0)
                return $"No tools match '{query}'. Call {ToolProtocol.ListToolsName} to see everything available.";
            return ToolProtocol.SerializeSchemas(matches);
        }
        [AgentTool("Returns the full argument schema for a tool by exact name, or several names comma-separated (in either 'name' or 'names'). Call this before using a tool whose arguments you are unsure of.")]
        string GetToolSchema(string name = "", string names = "")
        {
            var combined = string.Join(",", new[] { name, names }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var wanted = combined.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = Tools.Values.Where(t => wanted.Contains(t.ToolName)).ToList();
            if (matches.Count == 0)
                return $"No tool named '{name}'. Call {ToolProtocol.ListToolsName} or {ToolProtocol.SearchToolsName} (keywords) to find it.";
            return ToolProtocol.SerializeSchemas(matches);
        }
        [AgentTool("Returns the current tool index: every available tool's name and a one-line summary (no argument schemas). Use SearchTools or GetToolSchema for a tool's arguments.")]
        string ListTools() => ToolProtocol.BuildToolIndex(Tools.Values);

        public bool UnregisterTool(string toolName)
        {
            return Tools.Remove(toolName);
        }
        public void Register<TType>(TType implementation) where TType : class
        {
            var typeName = typeof(TType).Name;
            var ret = new List<ToolCall>();
            var methods = implementation!.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                var agentToolAttribute = method.GetCustomAttribute<AgentToolAttribute>();
                if (agentToolAttribute == null) continue;
                // tool name
                var toolName = $"{typeName}.{method.Name}";
                // check if exists
                if (Tools.ContainsKey(toolName))
                {
                    // Tool already registered
                    continue;
                }
                try
                {
                    // register
                    RegisterTool(new ToolCall(toolName, typeof(TType), method, implementation, agentToolAttribute.Description));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not register: {toolName}" + ex.ToString());
                }
            }
        }
        public void Unregister<TType>(TType implementation)
        {
            var typeName = typeof(TType).Name;
            var methods = implementation!.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                var agentToolAttribute = method.GetCustomAttribute<AgentToolAttribute>();
                if (agentToolAttribute == null) continue;
                var toolName = $"{typeName}.{method.Name}";
                UnregisterTool(toolName);
            }
        }
        public void RegisterTool(ToolCall tool)
        {
            // Idempotent: a component that remounts re-registers the same tool name without throwing.
            Tools[tool.ToolName] = tool;
        }
        public void RegisterTool(Delegate fn, string description = "")
        {
            var type = fn.Target?.GetType() ?? fn.Method.ReflectedType ?? fn.Method.DeclaringType!;
            var typeName = type.Name;
            var toolName = $"{typeName}.{fn.Method.Name}";
            RegisterTool(new ToolCall(toolName, type, fn.Method, fn.Target, description));
        }
        public void RegisterTool<TType>(Delegate fn, string description = "")
        {
            var type = typeof(TType);
            var typeName = type.Name;
            var toolName = $"{typeName}.{fn.Method.Name}";
            RegisterTool(new ToolCall(toolName, type, fn.Method, fn.Target, description));
        }
        [AgentTool("Echoes the message back. Minimal connectivity test. Do not call this in a loop.")]
        async Task<string> Echo(string message)
        {
            Console.WriteLine($"Echo was called: {message}");
            return message;
        }
        // Names of the tools Gemini has already been told about, so a register/unregister can send only
        // the DELTA (added/removed) instead of re-dumping the whole manifest. Empty until the first
        // introduction (SendToolInfo).
        private readonly HashSet<string> _announcedTools = new();
        private bool _protocolIntroduced;

        [AgentTool("Re-sends the tool manifest (protocol + the current tool index) to you. Call if you have lost track of the available tools or how to call them.")]
        public async Task SendToolInfo() => await SendToolInfo(null);
        /// <summary>
        /// (Re)introduce the full standing manifest: protocol + discovery instructions + the compact tool
        /// index (names + one-line summaries; NOT the full argument schemas - those are fetched on demand
        /// via SearchTools/GetToolSchema). Resets the announced-set baseline so subsequent
        /// <see cref="AnnounceToolChanges"/> calls send only deltas.
        /// </summary>
        /// <param name="addendum">Optional context for why the manifest is being (re)sent.</param>
        public async Task SendToolInfo(string? addendum)
        {
            var manifest = ToolProtocol.BuildManifest(Tools.Values);
            var header = $"{ToolProtocol.ManifestMarker} {nameof(Gemineachy)} tool manifest"
                       + (string.IsNullOrWhiteSpace(addendum) ? "" : $" (Addendum: {addendum})");
            // Set the baseline BEFORE the await so a tool change racing the send still diffs correctly.
            _announcedTools.Clear();
            foreach (var n in Tools.Keys) _announcedTools.Add(n);
            _protocolIntroduced = true;
            await Query(header, ToolProtocol.ManifestFileName, manifest);
        }
        /// <summary>
        /// Announce a tool change to Gemini as a DELTA. The first call (before any introduction) sends the
        /// full standing manifest; after that it sends only what was added (name + one-line summary) and
        /// removed (name) since the last announcement, plus a brief call/discovery reminder. Robust no
        /// matter how Tools was mutated - it diffs the live set against what Gemini was last told.
        /// </summary>
        /// <param name="addendum">Optional context to append (e.g. game setup instructions).</param>
        public async Task AnnounceToolChanges(string? addendum = null)
        {
            if (!_protocolIntroduced)
            {
                // Nothing introduced yet: the first message must carry the protocol + index.
                await SendToolInfo(addendum);
                return;
            }
            var current = Tools.Keys.ToHashSet();
            var added = Tools.Values.Where(t => !_announcedTools.Contains(t.ToolName)).ToList();
            var removed = _announcedTools.Where(n => !current.Contains(n)).ToList();
            if (added.Count == 0 && removed.Count == 0 && string.IsNullOrWhiteSpace(addendum))
                return; // nothing to say
            // Update the baseline before the await (see SendToolInfo note).
            _announcedTools.Clear();
            foreach (var n in current) _announcedTools.Add(n);
            var message = ToolProtocol.BuildToolChangeMessage(added, removed, addendum);
            await Query($"{ToolProtocol.ManifestMarker} tool change", ToolProtocol.ManifestFileName, message);
        }

        /// <summary>
        /// Send Gemini a hidden informational note about tool CONTEXT that isn't a tool add/remove (e.g. a
        /// filesystem mount appearing/disappearing). If the manifest hasn't been introduced yet, this sends
        /// the full lean manifest with the note as its addendum so the tools get introduced too.
        /// </summary>
        public async Task NotifyToolContext(string note)
        {
            if (string.IsNullOrWhiteSpace(note)) return;
            if (!_protocolIntroduced)
            {
                await SendToolInfo(note);
                return;
            }
            await Query($"{ToolProtocol.ManifestMarker} {note}");
        }
        private void Mutation_Observed()
        {
            // Hide tool traffic first, on every mutation, so it never flashes visible while streaming.
            EarlyHideToolTraffic();
            UpdateFromChat();
            OnDOMMutation?.Invoke();
        }
        private bool IsProcessing() => StopButtonVisible() || (!SendButtonVisible() && !DictateButtonVisible());
        private bool StopButtonVisible()
        {
            if (_document == null) return false;
            using var button = _document.QuerySelector(StopButtonSelector);
            return button != null;
        }
        private bool SendButtonVisible()
        {
            if (_document == null) return false;
            using var button = _document.QuerySelector(SendButtonSelector);
            return button != null;
        }
        private bool DictateButtonVisible()
        {
            if (_document == null) return false;
            using var button = _document.QuerySelector(DictateButtonSelector);
            return button != null;
        }
        private bool UploadButtonVisible()
        {
            if (_document == null) return false;
            using var button = _document.QuerySelector(UploadButtonSelector);
            return button != null;
        }
        private void UpdateFromChat(bool ignoreExisting = false)
        {
            if (_document == null) return;
            var isProcessing = IsProcessing();
            if (isProcessing && _busyTask.Task.IsCompleted)
            {
                _busyTask = new TaskCompletionSource();
                OnStateChanged?.Invoke();
            }
            // chat-window > chat-window-content > div#chat-history > infinite-scroller > div.conversation-container#[a-f0-9]{16} > user-query, model-response, model-response-disclaimers
            var node = _document.QuerySelector<HTMLDivElement>("chat-window infinite-scroller > div.conversation-container:last-child");
            var keepNode = false;
            if (node != null)
            {
                var id = node.Id;
                if (!string.IsNullOrEmpty(id) && !_processedConversationQuery.Contains(id))
                {
                    _processedConversationQuery.Add(id);
                    if (ignoreExisting) return;
                    // Query only the text-line paragraphs, skipping hidden accessibility labels
                    var lineElements = node.QuerySelectorAll<HTMLElement>("user-query p.query-text-line").Using(o => o.ToArray());
                    var queryLines = lineElements.Select(l => l.TextContent?.Trim()).Where(t => !string.IsNullOrEmpty(t));
                    var query = string.Join(" ", queryLines).Trim();
                    if (!string.IsNullOrEmpty(query))
                    {
                        keepNode = true;
                        // Loop-guard reset only (hiding is handled by EarlyHideToolTraffic on every
                        // mutation). Plumbing (tool results/manifest) is part of an in-flight round and
                        // must NOT reset the guard; game prompts and real user turns DO (fresh round).
                        bool isPlumbing = query.StartsWith(ToolProtocol.ResultsMarker, StringComparison.Ordinal)
                                          || query.StartsWith(ToolProtocol.ManifestMarker, StringComparison.Ordinal);
                        if (!isPlumbing) ResetToolLoop();
                        var queryTCS = new TaskCompletionSource<string>();
                        OnQuery?.Invoke(query, queryTCS.Task);
                        void completionCheck()
                        {
                            var isProcessing = IsProcessing();
                            if (isProcessing) return;
                            OnDOMMutation -= completionCheck;
                            // Read the whole message body via TextContent, NOT innerText and NOT a join
                            // of <p> elements. TextContent includes code blocks and, crucially, still
                            // returns the text when the element is display:none - which it IS once
                            // EarlyHideToolTraffic hides a tool-call response. innerText is layout-
                            // dependent and returns "" for hidden elements, which silently blanked the
                            // tool-call parse and stalled the game while tool calls were hidden. Our
                            // delimiter-based parser («TOOL_CALL … ») does not need innerText's newlines.
                            using var contentNode = node.QuerySelector<HTMLElement>("model-response [id*='model-response-message-content']");
                            var modelResponse = contentNode?.TextContent?.Trim() ?? "";
                            // (Hiding of tool-call responses happens in EarlyHideToolTraffic on every
                            // mutation, so the block is hidden while streaming - not only once complete.)
                            // handle tool calling
                            _ = HandleAgentMessage(modelResponse);
                            //
                            queryTCS.TrySetResult(modelResponse);
                            OnQueryResponse?.Invoke(query, modelResponse);
                            node.Dispose();
                        }
                        OnDOMMutation += completionCheck;
                        completionCheck();
                    }
                }
                if (!keepNode) node?.Dispose();
            }
            if (!isProcessing && _busyTask.Task.IsCompleted == false)
            {
                _busyTask.TrySetResult();
                OnStateChanged?.Invoke();
            }
        }
        /// <summary>Max consecutive automatic tool rounds before we stop, to guard against a
        /// tool-call loop between the extension and Gemini. Reset by <see cref="ResetToolLoop"/>.</summary>
        public int MaxToolRounds { get; set; } = 8;
        private int _toolRounds = 0;
        // Signature of the previous round's calls + whether that round produced any failure. Used to
        // detect a model stuck re-emitting a call identical to one that JUST failed (observed: Gemini
        // repeating the same malformed CheckersBoard.Move 4x). Re-executing it only reproduces the same
        // error, so we break the loop with a sharper corrective nudge instead of round-tripping again.
        private string? _lastRoundSig = null;
        private bool _lastRoundFailed = false;
        /// <summary>Call when a genuine (non-tool) user turn occurs to reset the loop guard.</summary>
        public void ResetToolLoop()
        {
            _toolRounds = 0;
            _lastRoundSig = null;
            _lastRoundFailed = false;
        }

        /// <summary>Order-sensitive signature of a round's calls (tool name + argument JSON), so an
        /// identical repeat is recognizable regardless of surrounding prose or whitespace.</summary>
        private static string RoundSignature(List<ToolProtocol.ParsedCall> calls) =>
            string.Join("␞", calls.Select(c => c.ParseError != null
                ? $"!{c.Raw}"
                : $"{c.Tool}({(c.HasArgs ? c.Args.GetRawText() : "")})"));

        async Task HandleAgentMessage(string modelResponse)
        {
            // Fire-and-forget from the mutation handler, so it must never throw (an unobserved fault can
            // surface as a runtime ThrowAsync). Catch send failures and log them.
            try
            {
                var calls = ToolProtocol.ParseCalls(modelResponse);
                if (calls.Count == 0) return;

                if (_toolRounds >= MaxToolRounds)
                {
                    _toolRounds = 0;
                    await Query($"{ToolProtocol.ResultsMarker} Tool-call loop guard tripped after {MaxToolRounds} consecutive rounds; not executing further tool calls. Please continue without calling tools, or ask the user how to proceed.");
                    return;
                }

                // If the model just repeated a call identical to one that already failed, don't re-run
                // it (same input -> same failure). Nudge it to change the call instead.
                var sig = RoundSignature(calls);
                if (_lastRoundFailed && sig == _lastRoundSig)
                {
                    _toolRounds++;
                    await Query($"{ToolProtocol.ResultsMarker} That tool call is identical to the previous one, which failed with the same error - re-sending it will not change the result. Read the error above and change the call before retrying (check the tool name, the required argument names, and their values). Arguments may be passed either nested as {{\"tool\":\"Name\",\"args\":{{...}}}} or flat as {{\"tool\":\"Name\",\"argName\":value}}.");
                    return;
                }
                _toolRounds++;

                var results = new List<ToolResult>();
                bool anyFailed = false;
                foreach (var call in calls)
                {
                    var result = await InvokeToolCallAsync(call);
                    results.Add(result);
                    if (!result.Ok) anyFailed = true;
                }
                _lastRoundSig = sig;
                _lastRoundFailed = anyFailed;
                var json = ToolProtocol.SerializeResults(results);
                await Query(ToolProtocol.ResultsMarker, ToolProtocol.ResultsFileName, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemineachy: failed to deliver tool results to Gemini: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>One tool invocation's outcome, serialized back to Gemini as
        /// {tool, ok, result?} on success or {tool, ok:false, error} on failure. A concrete type (not
        /// an anonymous object) so the ok flag can be read directly and it survives trimming.</summary>
        public sealed class ToolResult
        {
            [JsonPropertyName("tool")] public string Tool { get; set; } = "";
            [JsonPropertyName("ok")] public bool Ok { get; set; }
            [JsonPropertyName("result")] public object? Result { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }

        /// <summary>Invoke one parsed tool call, mapping named JSON args to the delegate's parameters.
        /// Never throws - failures are returned as {tool, ok:false, error} so Gemini can recover.</summary>
        async Task<ToolResult> InvokeToolCallAsync(ToolProtocol.ParsedCall call)
        {
            if (call.ParseError != null)
                return new ToolResult { Tool = call.Tool, Ok = false, Error = call.ParseError };
            if (!Tools.TryGetValue(call.Tool, out var tool))
                return new ToolResult { Tool = call.Tool, Ok = false, Error = $"Unknown tool '{call.Tool}'. Call the tool by its exact registered name." };

            try
            {
                var method = tool.MethodInfo;
                var parameters = method.GetParameters();
                if (!ToolProtocol.TryBindArguments(parameters, call.Args, call.HasArgs, out var argValues, out var bindError))
                    return new ToolResult { Tool = call.Tool, Ok = false, Error = bindError };
                var result = await InvokeMaybeAsync(tool, argValues, method.ReturnType);
                Console.WriteLine($"Tool '{call.Tool}' invoked.");
                return new ToolResult { Tool = call.Tool, Ok = true, Result = result };
            }
            catch (Exception ex)
            {
                var msg = (ex as System.Reflection.TargetInvocationException)?.InnerException?.Message ?? ex.Message;
                JS.LogError($"Tool '{call.Tool}' threw: {msg}");
                return new ToolResult { Tool = call.Tool, Ok = false, Error = msg };
            }
        }

        ///// <summary>Invoke a delegate that may be synchronous, Task, or Task&lt;T&gt;, returning its value.</summary>
        //static async Task<object?> InvokeMaybeAsync(Delegate handler, object?[] args, Type returnType)
        //{
        //    var result = handler.DynamicInvoke(args);
        //    if (result is Task task)
        //    {
        //        await task.ConfigureAwait(false);
        //        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        //            return returnType.GetProperty("Result")!.GetValue(task);
        //        return null; // non-generic Task
        //    }
        //    return result;
        //}
        /// <summary>Invoke a delegate that may be synchronous, Task, or Task&lt;T&gt;, returning its value.</summary>
        static async Task<object?> InvokeMaybeAsync(ToolCall handler, object?[] args, Type returnType)
        {
            var result = handler.MethodInfo.Invoke(handler.Instance, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                    return returnType.GetProperty("Result")!.GetValue(task);
                return null; // non-generic Task
            }
            return result;
        }
        private async Task AttachFiles(IEnumerable<File> files)
        {
            if (_document == null || files == null || files.Count() == 0) return;
            // click the Upload button so the input[type="file"] will get attached to the dom
            using var button = await QuerySelectorAsync<HTMLButtonElement>(UploadButtonSelector, 5000);
            button.Click();
            // get input[type="file"]
            using var fileInput = await QuerySelectorAsync<HTMLInputElement>(FilesInputSelector, 5000);
            // get the current attached count to compare against during loading
            var attachmentCountBefore = _document.QuerySelectorAll<HTMLDivElement>(FileAttachmentsSelector).Using(o => o.Length);
            // add files to input[type="file"]
            using var dataTransfer = new DataTransfer();
            foreach (var f in files)
            {
                dataTransfer.Items.Add(f);
            }
            fileInput.Files = dataTransfer.Files;
            var eventInit = new EventOptions
            {
                Bubbles = true,
                Cancelable = true,
            };
            // fire events to notify the page
            using var inputEvent = new Event("input", eventInit);
            using var changeEvent = new Event("change", eventInit);
            // dispatch the input events
            fileInput.DispatchEvent(inputEvent);
            fileInput.DispatchEvent(changeEvent);
            // wait for the file(s) to be processed
            await QuerySelectorAsync(async (d) =>
            {
                // do not return true until the files having been fully processed
                // "svg[class*='progress']"
                // document.querySelectorAll("uploader-file-preview gem-attachment svg[class*='progress']")
                var isProcessing = IsProcessing();
                if (isProcessing)
                {
                    Console.WriteLine($"~ AttachFiles: processing");
                    return false;
                }
                using var progressSVGs = d.QuerySelectorAll<HTMLDivElement>("uploader-file-preview gem-attachment svg[class*='progress']");
                // if any loading progress bars are still there we need to keep waiting
                if (progressSVGs.Length > 0)
                {
                    Console.WriteLine($"~ AttachFiles: still loading");
                    return false;
                }
                // get the attachments
                var attachments = d.QuerySelectorAll<HTMLDivElement>(FileAttachmentsSelector).Using(o => o.ToArray());
                // Wait for the count to increase. We do NOT wait for a specific total: Gemini caps
                // attachments (currently 10), so requesting more never reaches an "expected" count and
                // would hang. Any increase past the previous count means our upload registered.
                if (attachments.Length <= attachmentCountBefore)
                {
                    Console.WriteLine($"~ AttachFiles: attachments not added yet.");
                    foreach (var a in attachments) a.Dispose();
                    return false;
                }
                Console.WriteLine($"Attachments: {attachments.Length}");
                foreach (var attachment in attachments)
                {
                    var labelType = attachment.QuerySelector(".gem-attachment-extension-label")?.Using(o => o.TextContent);
                    var labelName = attachment.QuerySelector(".gem-attachment-text")?.Using(o => o.TextContent);
                    Console.WriteLine($"Attachment: {labelType} {labelName}");
                    attachment.Dispose();
                }
                return true;
            });
        }
        /// <summary>
        /// Send an agent query with a block of data, and await the response.<br/>
        /// The data is INLINED into the (hidden) message rather than attached as a file: tool/game traffic
        /// is already hidden from the user by CSS, so an attachment is no longer needed to keep it private -
        /// and inlining avoids the file-upload flow entirely (which required Chrome's normal profile and was
        /// a source of send errors). <paramref name="fileName"/> is kept as a readable label for the block.
        /// </summary>
        public async Task<string> Query(string text, string fileName, string fileText, double timeoutMS = 60000)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMS));
            return await Query(text, fileName, fileText, cts.Token);
        }
        /// <summary>
        /// Send an agent query with an inlined block of data (see the timeout overload), and await the response.
        /// </summary>
        public async Task<string> Query(string text, string fileName, string fileText, CancellationToken cancellationToken)
        {
            var body = string.IsNullOrEmpty(fileText)
                ? text
                : (string.IsNullOrEmpty(fileName)
                    ? $"{text}\n\n{fileText}"
                    : $"{text}\n\n--- {fileName} ---\n{fileText}");
            return await Query(body, (IEnumerable<File>?)null, cancellationToken);
        }
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public async Task<string> Query(string text, double timeoutMS = 60000)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMS));
            return await Query(text, null, cts.Token);
        }
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public async Task<string> Query(string text, IEnumerable<File>? files, double timeoutMS = 60000)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMS));
            return await Query(text, files, cts.Token);
        }
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public async Task<string> Query(string text, IEnumerable<File>? files, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(_document);
            if (string.IsNullOrEmpty(text) && (files == null || files.Count() == 0))
            {
                // nothing to send.
                return "";
            }
            var haveLock = false;
            try
            {
                await _queryLock.WaitAsync(cancellationToken);
                haveLock = true;
                await WhileBusy.WaitAsync(cancellationToken);
                return await SendWithRetryAsync(text, files, cancellationToken);
            }
            finally
            {
                if (haveLock) _queryLock.Release();
            }
        }

        /// <summary>Max times to (re)click Send when Gemini doesn't accept a message (e.g. transient errors).</summary>
        public int MaxSendAttempts { get; set; } = 3;
        /// <summary>How long to wait for the compose box to clear (= send accepted) before treating a send as
        /// failed and retrying. Kept short so a silently-dropped click retries quickly instead of stalling.</summary>
        public double SendAcceptTimeoutMs { get; set; } = 4000;

        /// <summary>
        /// Send the message and await Gemini's response, verifying the send was ACCEPTED (Gemini clears
        /// the compose box only on success) and retrying if not. A failed send leaves the compose box
        /// intact, so a retry is just another Send click - no re-typing, no duplicate attachments.
        /// </summary>
        private async Task<string> SendWithRetryAsync(string text, IEnumerable<File>? files, CancellationToken ct)
        {
            // Hide/collapse the compose box's inner text for the whole send so our programmatic type+send
            // doesn't flash or grow/shrink the box. (Empty box during the response wait looks identical to
            // a normal empty box, so keeping it on across the response is fine.)
            SetToolSending(true);
            try
            {
                for (int attempt = 1; attempt <= MaxSendAttempts; attempt++)
                {
                    var tcs = new TaskCompletionSource<Task<string>>();
                    void onQuery(string query, Task<string> response) => tcs.TrySetResult(response);
                    OnQuery += onQuery;
                    try
                    {
                        await PrepareAndSendAsync(text, files, ct);
                        if (await WaitForSendAcceptedAsync(ct))
                        {
                            // Accepted (compose box cleared). Await the model response.
                            var response = await tcs.Task.WaitAsync(ct);
                            return await response.WaitAsync(ct);
                        }
                        // Failure diagnostics (only on a failed send, so no spam): what did the compose box look
                        // like, and does Gemini show an error toast?
                        Console.WriteLine($"Gemineachy: send attempt {attempt}/{MaxSendAttempts} NOT accepted. composeText=\"{Trunc(CurrentComposeText())}\" errorToast=\"{DetectErrorText()}\"");
                    }
                    finally
                    {
                        OnQuery -= onQuery;
                    }
                    if (attempt < MaxSendAttempts)
                        await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt), ct); // simple backoff
                }
                // Permanent failure: clear the stale text so it does not linger in the user's compose box.
                Console.WriteLine($"Gemineachy: message not accepted after {MaxSendAttempts} attempts; clearing compose. errorToast=\"{DetectErrorText()}\"");
                await ClearComposeAsync();
                throw new GeminiSendException($"Gemini did not accept the message after {MaxSendAttempts} attempts.");
            }
            finally
            {
                SetToolSending(false);
            }
        }

        /// <summary>
        /// Ensure the compose box holds exactly this message (text + the expected attachments), then Send.
        /// Idempotent across retries: we (re)type only if the text differs, and (re)attach only if the
        /// attachments are missing - a failed send commonly keeps the text but DROPS the file, so a naive
        /// "text already there, skip" would resend with no attachment.
        /// </summary>
        private async Task PrepareAndSendAsync(string text, IEnumerable<File>? files, CancellationToken ct)
        {
            var fileList = files?.ToList();
            var hasFiles = fileList is { Count: > 0 };

            // Ensure Gemini is idle before THIS attempt. The most common "send not accepted (no error)"
            // cause is clicking Send while Gemini is still finishing the previous turn, so the click is a
            // no-op. Waiting per-attempt (not just once in Query) makes retries wait for readiness too.
            await WhileBusy.WaitAsync(ct);

            using var inputElement = QuerySelector<HTMLDivElement>(TextInputSelector);
            ArgumentNullException.ThrowIfNull(inputElement);

            if ((inputElement.TextContent ?? "") != text)
            {
                inputElement.Focus();
                inputElement.TextContent = text;
                using var inputEvent = new InputEvent("input", new InputEventOptions
                {
                    Bubbles = true,
                    Cancelable = true,
                    InputType = "insertText",
                    Data = text
                });
                inputElement.DispatchEvent(inputEvent);
            }

            // (Re)attach only when NOTHING is attached: initial send, or a retry after a failed send
            // dropped the file. We compare against zero (not the file count) because Gemini caps
            // attachments, so a "did every file attach?" check could never be satisfied for large sets.
            if (hasFiles && CurrentAttachmentCount() == 0)
            {
                await AttachFiles(fileList!);
            }

            // Deterministic ready signal (verified via CDP against live Gemini): the Send button only
            // exists once the compose box has content, and it appears already enabled - so "present" ==
            // "clickable" (no separate disabled state, no timer needed). Wait for it to appear, then
            // re-grab it fresh immediately before clicking: Angular re-renders the compose after a turn,
            // and a button reference resolved a moment earlier can be detached, making Click() a silent
            // no-op. A missed click is still caught by the accept-timeout + retry.
            await QuerySelectorAsync<HTMLButtonElement>(SendButtonSelector, ct);
            using var sendButton = QuerySelector<HTMLButtonElement>(SendButtonSelector);
            sendButton?.Click();
        }

        /// <summary>Send is accepted when Gemini clears the compose box; times out (=> failed) otherwise.</summary>
        private async Task<bool> WaitForSendAcceptedAsync(CancellationToken ct)
        {
            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptCts.CancelAfter(TimeSpan.FromMilliseconds(SendAcceptTimeoutMs));
            try
            {
                await QuerySelectorAsync(d =>
                {
                    using var input = d.QuerySelector<HTMLDivElement>(TextInputSelector);
                    return string.IsNullOrEmpty(input?.TextContent);
                }, acceptCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false; // accept timeout -> failed send, caller retries
            }
        }

        private int CurrentAttachmentCount() =>
            _document?.QuerySelectorAll<HTMLDivElement>(FileAttachmentsSelector).Using(o => o.Length) ?? 0;

        private string CurrentComposeText()
        {
            using var input = QuerySelector<HTMLDivElement>(TextInputSelector);
            return input?.TextContent ?? "";
        }

        /// <summary>DEBUG: best-effort scan for a Gemini error toast/snackbar so failure logs can show it.</summary>
        private string DetectErrorText()
        {
            if (_document == null) return "";
            foreach (var sel in new[] { "mat-snack-bar-container", "[role='alert']", "[class*='snackbar']", "[class*='error-']", "[class*='-error']" })
            {
                try
                {
                    using var el = _document.QuerySelector(sel);
                    var t = el?.TextContent?.Trim();
                    if (!string.IsNullOrEmpty(t)) return $"{sel} => {t}";
                }
                catch { }
            }
            return "(none found)";
        }

        private static string Trunc(string s, int n = 48) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…").Replace("\n", "\\n");

        /// <summary>Clear the compose box (used after a permanent send failure so stale text doesn't linger).</summary>
        private async Task ClearComposeAsync()
        {
            try
            {
                using var input = QuerySelector<HTMLDivElement>(TextInputSelector);
                if (input == null) return;
                input.Focus();
                input.TextContent = "";
                using var ev = new InputEvent("input", new InputEventOptions
                {
                    Bubbles = true,
                    Cancelable = true,
                    InputType = "deleteContentBackward"
                });
                input.DispatchEvent(ev);
                await Task.Delay(1);
            }
            catch { /* best effort */ }
        }
        #region DOM stuff
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public async Task<TNode> QuerySelectorAsync<TNode>(string selector, double waitMS = 60000) where TNode : Element
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMS));
            return await QuerySelectorAsync<TNode>(selector, null, cts.Token);
        }
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public Task<TNode> QuerySelectorAsync<TNode>(string selector, CancellationToken cancellationToken) where TNode : Element => QuerySelectorAsync(selector, (Func<TNode, bool>?)null, cancellationToken);
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public async Task<TNode> QuerySelectorAsync<TNode>(string selector, Func<TNode, bool>? where, double waitMS = 60000) where TNode : Element
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMS));
            return await QuerySelectorAsync<TNode>(selector, where, cts.Token);
        }
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public async Task<TNode> QuerySelectorAsync<TNode>(string selector, Func<TNode, bool>? where, CancellationToken cancellationToken) where TNode : Element
        {
            ArgumentNullException.ThrowIfNull(_document);
            var ret = _document.QuerySelector<TNode>(selector);
            if (ret == null)
            {
                var tcs = new TaskCompletionSource();
                void mutationCallback()
                {
                    ret = _document.QuerySelector<TNode>(selector);
                    if (ret != null && (where?.Invoke(ret) ?? true)) tcs.TrySetResult();
                }
                OnDOMMutation += mutationCallback;
                try
                {
                    await tcs.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    OnDOMMutation -= mutationCallback;
                }
            }
            return ret!;
        }
        public Task QuerySelectorAsync(Func<Document, Task<bool>> where) => QuerySelectorAsync(where, CancellationToken.None);
        /// <summary>
        /// Wait until the specfiied callback returns true
        /// </summary>
        /// <param name="where"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task QuerySelectorAsync(Func<Document, Task<bool>> where, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(_document);
            var tcs = new TaskCompletionSource();
            async void mutationCallback()
            {
                try
                {
                    if (await where(_document)) tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            OnDOMMutation += mutationCallback;
            mutationCallback();
            try
            {
                await tcs.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                OnDOMMutation -= mutationCallback;
            }
        }
        /// <summary>
        /// Wait until the specfiied callback returns true
        /// </summary>
        /// <param name="where"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task QuerySelectorAsync(Func<Document, bool> where, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(_document);
            var tcs = new TaskCompletionSource();
            void mutationCallback()
            {
                try
                {
                    if (where(_document)) tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }
            OnDOMMutation += mutationCallback;
            mutationCallback();
            try
            {
                await tcs.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                OnDOMMutation -= mutationCallback;
            }
        }
        /// <summary>
        /// Use the selector to return the the specified Element
        /// </summary>
        public TNode? QuerySelector<TNode>(string selector) where TNode : Element => _document?.QuerySelector<TNode>(selector);
        #endregion
    }
}
