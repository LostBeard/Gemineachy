using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using File = SpawnDev.SpawnJS.JSObjects.File;

namespace Gemineachy.Services
{
    public class GeminiChatService(SpawnJSRuntime JS) : IAsyncBackgroundService
    {
        const string TextInputSelector = "chat-window div[contenteditable=\"true\"]";
        const string FilesInputSelector = "chat-window input[type='file']";
        const string UploadButtonSelector = "chat-window button[aria-label*=\"Upload\"]";
        const string DictateButtonSelector = "chat-window button[aria-label*=\"Dictate\"]";
        const string SendButtonSelector = "chat-window button[aria-label*=\"Send\"]";
        const string StopButtonSelector = "chat-window button[aria-label*=\"Stop\"]";
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
        private async Task InitAsync()
        {
            Console.WriteLine($"GeminiChatService.InitAsync() {JS.GlobalScopeName} {JS.InstanceId}");
            if (JS.IsWindow)
            {
                _document = JS.GetDocument();
                if (_document != null)
                {
                    UpdateFromChat();
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
                }
            }
        }
        private void Mutation_Observed()
        {
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
        private void UpdateFromChat()
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
                    // Query only the text-line paragraphs, skipping hidden accessibility labels
                    var lineElements = node.QuerySelectorAll<HTMLElement>("user-query p.query-text-line").Using(o => o.ToArray());
                    var queryLines = lineElements.Select(l => l.TextContent?.Trim()).Where(t => !string.IsNullOrEmpty(t));
                    var query = string.Join(" ", queryLines).Trim();
                    if (!string.IsNullOrEmpty(query))
                    {
                        keepNode = true;
                        var queryTCS = new TaskCompletionSource<string>();
                        OnQuery?.Invoke(query, queryTCS.Task);
                        void completionCheck()
                        {
                            var isProcessing = IsProcessing();
                            if (isProcessing) return;
                            OnDOMMutation -= completionCheck;
                            var lineElements = node.QuerySelectorAll<HTMLElement>("model-response [id*='model-response-message-content'] p").Using(o => o.ToArray());
                            var modelResponseLines = lineElements.Select(l => l.TextContent?.Trim()).Where(t => !string.IsNullOrEmpty(t));
                            var modelResponse = string.Join(" ", modelResponseLines);
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
        private async Task AttachFiles(IEnumerable<File> files)
        {
            if (_document == null || files == null || files.Count() == 0) return;
            // click the Upload button so the input[type="file"] will get attached to the dom
            using var button = await QuerySelectorAsync<HTMLButtonElement>(UploadButtonSelector, 5000);
            button.Click();
            // get input[type="file"]
            using var fileInput = await QuerySelectorAsync<HTMLInputElement>(FilesInputSelector, 5000);
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
            fileInput.DispatchEvent(inputEvent);
            fileInput.DispatchEvent(changeEvent);
            await Task.Delay(1);
        }
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public Task<string> Query(string text) => Query(text, null, CancellationToken.None);
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public async Task<string> Query(string text, double timeoutMS)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMS));
            return await Query(text, null, cts.Token);
        }
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public Task<string> Query(string text, IEnumerable<File>? files) => Query(text, files, CancellationToken.None);
        /// <summary>
        /// Send an agent query and await the response
        /// </summary>
        public async Task<string> Query(string text, IEnumerable<File>? files, double timeoutMS)
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
            var haveLock = false;
            var removeOnQuery = false;
            var tcs = new TaskCompletionSource<Task<string>>();
            void onQuery(string query, Task<string> response)
            {
                tcs.TrySetResult(response);
            }
            try
            {
                await _queryLock.WaitAsync(cancellationToken);
                haveLock = true;
                await WhileBusy.WaitAsync(cancellationToken);
                using var inputElement = QuerySelector<HTMLDivElement>(TextInputSelector);
                ArgumentNullException.ThrowIfNull(inputElement);
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
                // give the dom a chance to react
                await Task.Delay(1);
                if (files != null && files.Count() > 0)
                {
                    await AttachFiles(files);
                }
                // the send button should now be visible
                using var sendButton = QuerySelector<HTMLButtonElement>(SendButtonSelector);
                // get ready to handle the result
                OnQuery += onQuery;
                removeOnQuery = true;
                // Try clicking the send button first as it's the most reliable method
                if (sendButton != null && !sendButton.Disabled)
                {
                    sendButton.Click();
                }
                else
                {
                    // Fallback to Enter keydown/keyup simulation
                    using var keyboardEvent = new KeyboardEvent("keydown", new KeyboardEventOptions
                    {
                        Bubbles = true,
                        Key = "Enter",
                        Code = "Enter",
                        KeyCode = 13,
                        Which = 13,
                    });
                    inputElement.DispatchEvent(keyboardEvent);
                }
                var response = await tcs.Task.WaitAsync(cancellationToken);
                var resp = await response.WaitAsync(cancellationToken);
                return resp;
            }
            finally
            {
                if (haveLock) _queryLock.Release();
                if (removeOnQuery)
                {
                    OnQuery -= onQuery;
                }
            }
        }
        #region DOM stuff
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public Task<TNode> QuerySelectorAsync<TNode>(string selector) where TNode : Element => QuerySelectorAsync<TNode>(selector, CancellationToken.None);
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public async Task<TNode> QuerySelectorAsync<TNode>(string selector, double waitMS) where TNode : Element
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMS));
            return await QuerySelectorAsync<TNode>(selector, cts.Token);
        }
        /// <summary>
        /// Asynchronously wait for the selector to return the the specified Element
        /// </summary>
        public async Task<TNode> QuerySelectorAsync<TNode>(string selector, CancellationToken cancellationToken) where TNode : Element
        {
            ArgumentNullException.ThrowIfNull(_document);
            var ret = _document.QuerySelector<TNode>(selector);
            if (ret == null)
            {
                var tcs = new TaskCompletionSource();
                void mutationCallback()
                {
                    ret = _document.QuerySelector<TNode>(selector);
                    if (ret != null) tcs.TrySetResult();
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
        /// <summary>
        /// Use the selector to return the the specified Element
        /// </summary>
        public TNode? QuerySelector<TNode>(string selector) where TNode : Element => _document?.QuerySelector<TNode>(selector);
        #endregion
    }
}
