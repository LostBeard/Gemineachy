using System.Diagnostics;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.ILGPU;
using SpawnDev.ILGPU.ML;
using SpawnDev.ILGPU.ML.Hub;
using SpawnDev.ILGPU.ML.Pipelines;
using SpawnDev.ILGPU.ML.Graph;

namespace Gemineachy.Services.Reachy
{
    /// <summary>
    /// Speech-to-text for the robot's microphone, run in-house on the GPU.
    /// </summary>
    /// <remarks>
    /// Whisper via SpawnDev.ILGPU.ML rather than a cloud speech API: the whole point of this extension is
    /// that the local half stays local, and shipping a child's living room to a speech endpoint to reach an
    /// LLM would give that away for nothing. The audio arrives already in Whisper's format -
    /// <see cref="SpawnDev.Reachy.RoseAudioLink.OnMicAudio"/> emits 16 kHz mono PCM16 because the robot's
    /// link downmixes and decimates for exactly this - so there is no resampling on this path.
    /// <para>
    /// The model is fetched once through <see cref="ModelHub"/> and cached in OPFS, so only the first run
    /// pays for the download.
    /// </para>
    /// </remarks>
    public class SpeechService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= Task.CompletedTask;
        private Task? _ready;

        private readonly SpawnJSRuntime _js;
        private global::ILGPU.Context.Builder? _contextBuilder;
        private global::ILGPU.Context? _context;
        private global::ILGPU.Runtime.Accelerator? _accelerator;
        private SpeechRecognitionPipeline? _pipeline;
        private Task<bool>? _loading;

        /// <summary>What Whisper is running on, for the UI - an empty accelerator name is itself a finding.</summary>
        public string Describe() => string.IsNullOrWhiteSpace(AcceleratorName) ? $"(unnamed {_acceleratorKind})" : AcceleratorName;
        private string _acceleratorKind = "none";

        /// <summary>Sample rate this service expects, and the rate the robot link already delivers.</summary>
        public const int SampleRate = 16000;

        private readonly SpawnDev.SpawnJS.BrowserExtension.Services.BrowserExtensionService? _bes;

        public SpeechService(SpawnJSRuntime js, SpawnDev.SpawnJS.BrowserExtension.Services.BrowserExtensionService? bes = null)
        {
            _js = js;
            _bes = bes;
        }

        /// <summary>Name of the accelerator Whisper is running on, once loaded.</summary>
        public string AcceleratorName { get; private set; } = "";
        /// <summary>True once the model is resident and transcription can be requested.</summary>
        public bool IsLoaded => _pipeline != null;
        /// <summary>Last load/transcribe error, for the UI.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>The encoder/decoder ONNX input names, so the pipeline's positional feeding is checkable.</summary>
        public string ModelInputs { get; private set; } = "";
        /// <summary>Causal-mask stats captured during the last self-test - the backend-difference evidence.</summary>
        public string MaskTrace { get; private set; } = "";

        /// <summary>Milliseconds the model load took (download excluded on a warm OPFS cache).</summary>
        public long LoadMs { get; private set; }

        /// <summary>
        /// Load the accelerator and the Whisper model. Safe to call repeatedly - concurrent callers share
        /// the one load, because two callers each pulling ~40 MB and building a second pipeline is a way to
        /// run out of GPU memory rather than a way to be fast.
        /// </summary>
        public Task<bool> LoadAsync() => _loading ??= LoadCoreAsync();

        private async Task<bool> LoadCoreAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                _contextBuilder = MLContext.Create();
                await _contextBuilder.AllAcceleratorsAsync();
                _context = _contextBuilder.ToContext();
                _accelerator = await _context.CreatePreferredAcceleratorAsync();
                if (_accelerator == null)
                {
                    LastError = "No accelerator available in this scope.";
                    return false;
                }
                AcceleratorName = _accelerator.Name;
                _acceleratorKind = _accelerator.AcceleratorType.ToString();
                Console.WriteLine($"[STT] accelerator: {AcceleratorName}");

                using var hub = new ModelHub(_js);
                var encoderBytes = await hub.LoadAsync(ModelHub.KnownModels.WhisperTiny, "onnx/encoder_model.onnx");
                var encoder = InferenceSession.CreateFromFile(_accelerator, encoderBytes);
                var decoderBytes = await hub.LoadAsync(ModelHub.KnownModels.WhisperTiny, "onnx/decoder_model.onnx");
                var decoder = InferenceSession.CreateFromFile(_accelerator, decoderBytes);
                var tokenizerJson = System.Text.Encoding.UTF8.GetString(
                    await hub.LoadAsync(ModelHub.KnownModels.WhisperTiny, "tokenizer.json"));

                var pipeline = new SpeechRecognitionPipeline(encoder, decoder, _accelerator)
                {
                    // The decode loop re-feeds the WHOLE token sequence every step (no KV cache), so cost
                    // grows quadratically and the 224 default can run for many minutes. A short utterance is
                    // well under this; the cap bounds the worst case rather than trimming normal speech.
                    MaxTokens = 64,
                };
                pipeline.LoadTokenizer(tokenizerJson);
                _pipeline = pipeline;
                // The pipeline feeds the decoder positionally - InputNames[0] as input_ids and [1] as the
                // encoder hidden states. If the ONNX graph declares them the other way round, the decoder
                // is handed its inputs swapped, which produces confident nonsense rather than an error.
                // Record the real names so that assumption is checkable instead of assumed.
                ModelInputs = $"enc[{string.Join(",", encoder.InputNames)}] dec[{string.Join(",", decoder.InputNames)}]";
                Console.WriteLine($"[STT] {ModelInputs}");
                LoadMs = sw.ElapsedMilliseconds;
                Console.WriteLine($"[STT] whisper-tiny ready in {LoadMs}ms on {AcceleratorName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"[STT] load failed: {ex}");
                _loading = null;   // a failed load must not be cached as the answer forever
                return false;
            }
        }

        /// <summary>
        /// Transcribe one utterance of 16 kHz mono PCM16 - the format the robot's link already delivers.
        /// </summary>
        public async Task<TranscriptionOutcome> TranscribeAsync(short[] pcm16k, int sampleRate = SampleRate)
        {
            if (!await LoadAsync()) return new TranscriptionOutcome("", 0, LastError);
            var sw = Stopwatch.StartNew();
            try
            {
                // PCM16 -> normalised float, the only conversion this path needs.
                var samples = new float[pcm16k.Length];
                for (int i = 0; i < pcm16k.Length; i++) samples[i] = pcm16k[i] / 32768f;

                var result = await _pipeline!.TranscribeAsync(samples, sampleRate);
                var text = (result.Text ?? "").Trim();
                return new TranscriptionOutcome(text, sw.ElapsedMilliseconds, null);
            }
            catch (Exception ex)
            {
                // Carry the throwing frames out with the message: this runs in the content script, whose
                // console is not where the failure is being read from, and "IndexOutOfRangeException" on
                // its own says nothing about which stage of the pipeline gave up.
                var frames = string.Join(" <- ", (ex.StackTrace ?? "")
                    .Split('\n').Select(f => f.Trim()).Where(f => f.Length > 0).Take(4));
                LastError = $"{ex.GetType().Name}: {ex.Message} [{frames}]";
                Console.WriteLine($"[STT] transcribe failed: {ex}");
                return new TranscriptionOutcome("", sw.ElapsedMilliseconds, LastError);
            }
        }

        /// <summary>
        /// Transcribe a known speech recording shipped with the extension and check the result.
        /// </summary>
        /// <remarks>
        /// The room is not a test fixture. Every browser-side attempt so far was ambiguous because the audio
        /// level varied between runs - a bad transcript could not be told apart from a quiet house, and one
        /// "degenerate output" conclusion turned out to be Whisper behaving correctly on silence. This runs
        /// the SAME Harvard-sentence recording the desktop harness uses, so the browser result is directly
        /// comparable to a known-good one and needs nobody to speak.
        /// </remarks>
        public async Task<string> RunSelfTestAsync()
        {
            const string expect = "sockets";     // from "Paint the sockets in the wall, dull green."
            try
            {
                var url = _bes?.Runtime?.GetURL("app/testdata/stt-harvard-8k.wav");
                if (string.IsNullOrEmpty(url)) return "STT self-test: cannot resolve the test file URL (not an extension?).";
                using var response = await _js.CallAsync<string, Response>("fetch", url);
                if (!response.Ok) return $"STT self-test: fetch failed ({response.Status}) for {url}";
                using var buffer = await response.ArrayBuffer();
                using var bytes = new Uint8Array(buffer);
                var wav = bytes.ReadBytes();

                var (pcm, rate) = DecodeWav16(wav);

                // Optional per-node tensor capture, off unless a filter attribute is present. It found the
                // whisper attention-fusion double-scale by bisecting the encoder graph against the desktop
                // reference: set data-gem-sttfilter to a node-name or op-type substring, data-gem-sttmax to a
                // line budget, and read the result back off data-gem-sttdump. The budget matters because the
                // decoder re-runs per token, so an op-type filter without one costs a readback per matching
                // node per token and the self-test never finishes. The sink exists because this runs in the
                // content script, whose console the page-world test driver cannot read.
                var dump = new List<string>();
                var filter = DumpFilter();
                if (filter != null)
                {
                    GraphExecutor.DumpSink = l => dump.Add(l);
                    GraphExecutor.DumpTensorsMatching = filter;
                    GraphExecutor.DumpMaxLines = DumpMax();
                    GraphExecutor.ResetDumpBudget();
                }

                var sw = Stopwatch.StartNew();
                var outcome = await TranscribeAsync(pcm, rate);
                sw.Stop();
                GraphExecutor.DumpTensorsMatching = null;
                GraphExecutor.DumpSink = null;


                // The whole dump goes on documentElement, not just the four lines that fit the status text:
                // localizing a divergence means comparing a RUN of nodes against the desktop reference, and
                // this service lives in the content script, whose console the page-world driver cannot read.
                try
                {
                    using var document = _js.Get<Document>("document");
                    using var docEl = document.DocumentElement!;
                    docEl.SetAttribute("data-gem-sttdump", string.Join("\n", dump));
                }
                catch (Exception ex) { Console.WriteLine($"[STT] dump marker failed: {ex.Message}"); }

                MaskTrace = string.Join(" | ", dump.Take(4).Select(l =>
                {
                    var i = l.IndexOf("shape=", StringComparison.Ordinal);
                    return i >= 0 ? l.Substring(i) : l;
                }));
                if (outcome.Error != null) return $"STT self-test FAILED: {outcome.Error}";
                var ok = outcome.Text.Contains(expect, StringComparison.OrdinalIgnoreCase);
                return $"STT self-test {(ok ? "PASS" : "FAIL")} [{pcm.Length / (double)rate:F1}s @ {rate}Hz, "
                     + $"{outcome.ElapsedMs}ms on {Describe()}]: \"{Trunc(outcome.Text, 120)}\" MASK: {Trunc(MaskTrace, 260)}";
            }
            catch (Exception ex) { return $"STT self-test THREW {ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>
        /// Which tensors the self-test dumps, read from <c>data-gem-sttfilter</c> on documentElement.
        /// </summary>
        /// <remarks>
        /// Bisecting a graph divergence means changing this filter repeatedly. Reading it from the DOM keeps
        /// each step a page-world attribute write instead of a rebuild/publish/reload of the extension.
        /// </remarks>
        private string? DumpFilter()
        {
            try
            {
                using var document = _js.Get<Document>("document");
                using var docEl = document.DocumentElement!;
                var f = docEl.GetAttribute("data-gem-sttfilter");
                if (!string.IsNullOrWhiteSpace(f)) return f;
            }
            catch { }
            return null;
        }

        /// <summary>Dump line budget, read from <c>data-gem-sttmax</c>; see <see cref="DumpFilter"/>.</summary>
        private int DumpMax()
        {
            try
            {
                using var document = _js.Get<Document>("document");
                using var docEl = document.DocumentElement!;
                if (int.TryParse(docEl.GetAttribute("data-gem-sttmax"), out var n) && n > 0) return n;
            }
            catch { }
            return 12;
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        /// <summary>Minimal 16-bit PCM RIFF/WAVE reader, downmixed to mono. Returns the file's REAL rate so
        /// the caller can see it rather than assume it.</summary>
        private static (short[] Pcm, int SampleRate) DecodeWav16(byte[] data)
        {
            if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
                throw new InvalidDataException("not a RIFF/WAVE file");
            int pos = 12, channels = 1, bits = 16, rate = SampleRate;
            while (pos + 8 <= data.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(data, body + 2);
                    rate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToInt16(data, body + 14);
                }
                else if (id == "data")
                {
                    if (bits != 16) throw new NotSupportedException($"{bits}-bit WAV not supported");
                    int count = Math.Min(size, data.Length - body) / 2;
                    int frames = count / Math.Max(1, channels);
                    var outp = new short[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        int sum = 0;
                        for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(data, body + (f * channels + c) * 2);
                        outp[f] = (short)(sum / channels);
                    }
                    return (outp, rate);
                }
                pos = body + size + (size & 1);
            }
            throw new InvalidDataException("no data chunk");
        }

        /// <param name="Text">Transcribed text, empty if nothing was recognised.</param>
        /// <param name="ElapsedMs">Wall-clock transcription time, so the cost is measured and not assumed.</param>
        /// <param name="Error">Non-null when the attempt failed.</param>
        public record TranscriptionOutcome(string Text, long ElapsedMs, string? Error);
    }
}
