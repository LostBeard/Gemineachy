namespace Gemineachy.Services
{
    /// <summary>
    /// Similar to ManualResetEvent but async
    /// </summary>
    public class AsyncManualResetEvent
    {
        /// <summary>
        /// Returns true if the signal has been set
        /// </summary>
        public bool Signaled => _tcs.Task.IsCompleted;
        /// <summary>
        /// Wait until signaled
        /// </summary>
        /// <returns></returns>
        public Task WaitAsync() => _tcs.Task;
        TaskCompletionSource _tcs = new TaskCompletionSource();
        /// <summary>
        /// New instance
        /// </summary>
        /// <param name="initialState"></param>
        public AsyncManualResetEvent(bool initialState)
        {
            if (initialState) Set();
        }
        /// <summary>
        /// Set signaled to release all awaiters and optionally reset
        /// </summary>
        public void Set(bool reset = false)
        {
            _tcs.TrySetResult();
            if (reset) _tcs = new TaskCompletionSource();
        }
        /// <summary>
        /// Reset to unsignaled if signaled
        /// </summary>
        public void Reset()
        {
            if (!Signaled) return;
            _tcs = new TaskCompletionSource();
        }
    }
}
