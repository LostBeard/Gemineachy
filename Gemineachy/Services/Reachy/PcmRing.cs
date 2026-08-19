namespace Gemineachy.Services.Reachy
{
    /// <summary>
    /// Fixed-capacity ring of PCM samples: the most recent N seconds of the robot's microphone, always
    /// available without having had to know in advance that they mattered.
    /// </summary>
    /// <remarks>
    /// A capture window that starts when you ask for it can only ever record the future, so using it means
    /// coordinating a human with a countdown. Keeping a rolling window instead means the audio is already
    /// there when someone decides to transcribe it - which is also what voice-activity detection will need,
    /// since a VAD marks an utterance that has ALREADY been spoken.
    /// <para>
    /// A ring rather than a trimmed list because the mic delivers ~50 chunks a second: shifting a
    /// half-million-sample list on every one of those is a lot of memory traffic to avoid one modulo.
    /// </para>
    /// </remarks>
    public sealed class PcmRing
    {
        private readonly short[] _buffer;
        private int _write;
        private long _total;
        private readonly object _lock = new();

        public PcmRing(int capacity) => _buffer = new short[Math.Max(1, capacity)];

        /// <summary>Total samples ever written - keeps counting past capacity.</summary>
        public long TotalWritten { get { lock (_lock) return _total; } }
        /// <summary>Samples currently retained (capped at capacity).</summary>
        public int Count { get { lock (_lock) return (int)Math.Min(_total, _buffer.Length); } }
        public int Capacity => _buffer.Length;

        public void Write(short[] samples)
        {
            if (samples is null || samples.Length == 0) return;
            lock (_lock)
            {
                // A chunk longer than the whole ring can only leave its tail behind.
                int start = Math.Max(0, samples.Length - _buffer.Length);
                for (int i = start; i < samples.Length; i++)
                {
                    _buffer[_write] = samples[i];
                    _write = (_write + 1) % _buffer.Length;
                }
                _total += samples.Length;
            }
        }

        /// <summary>
        /// Copy out the most recent <paramref name="samples"/> samples in chronological order, or
        /// everything retained if fewer have been written.
        /// </summary>
        public short[] Snapshot(int samples)
        {
            lock (_lock)
            {
                int have = (int)Math.Min(_total, _buffer.Length);
                int take = Math.Min(samples, have);
                var outp = new short[take];
                // Walk back from the write cursor so the result ends at "now".
                int read = ((_write - take) % _buffer.Length + _buffer.Length) % _buffer.Length;
                for (int i = 0; i < take; i++)
                {
                    outp[i] = _buffer[read];
                    read = (read + 1) % _buffer.Length;
                }
                return outp;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_buffer);
                _write = 0;
                _total = 0;
            }
        }
    }
}
