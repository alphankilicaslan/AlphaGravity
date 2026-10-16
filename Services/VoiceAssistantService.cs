using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace EnterpriseAIApp.Services
{
    public enum VoiceChatState
    {
        Idle,
        Listening,
        Transcribing,
        Thinking,
        Speaking
    }

    public class VoiceAssistantService : IDisposable
    {
        private const string VoiceBridgeUrl = "http://127.0.0.1:58222";
        private readonly CloudAiClient _cloudAiClient;
        private readonly HttpClient _httpClient;
        private readonly SpeechSynthesizer _synthesizer;

        private WaveIn? _waveIn;
        private MemoryStream? _recordingStream;
        private WaveFileWriter? _waveWriter;

        // NAudio Edge-TTS Playback
        private WaveOut? _waveOut;
        private Mp3FileReader? _mp3Reader;
        private MemoryStream? _mp3Stream;
        private readonly object _playbackLock = new();

        private bool _isRecording = false;
        private bool _isLiveMode = false;
        private bool _isVoiceFeedbackEnabled = true;
        private string _selectedVoice = "tr-TR-AhmetNeural";

        // Silero VAD & Adaptive Audio parameters
        private readonly SileroVadDetector _sileroVad = new();
        private readonly List<float> _vadSampleBuffer = new(2048);
        private readonly object _vadLock = new();
        private int _consecutiveSilenceMs = 0;
        private int _totalSpeechDurationMs = 0;
        private bool _isUtteranceCompletedTriggered = false;

        private DateTime _lastSpeechDetectedAt = DateTime.MinValue;
        private DateTime _speechStartedAt = DateTime.MinValue;
        private DateTime _recordingStartedAt = DateTime.MinValue;
        private bool _hasSpokenInTurn = false;
        private float _ambientNoiseRms = 15f;
        private float _calibratedThreshold = 35f;
        private readonly System.Timers.Timer _vadTimer;
        private Process? _voiceBridgeProcess;

        public event Action<string>? SpeechRecognized;
        public event Action<bool>? RecordingStateChanged;
        public event Action<bool>? SpeakingStateChanged;
        public event Action<VoiceChatState, string>? LiveStateChanged;
        public event Action<string>? StatusMessage;
        public event Action? LiveUtteranceCompleted;
        public event Action<float>? AudioLevelChanged;

        public bool IsRecording => _isRecording;
        public bool IsLiveMode => _isLiveMode;
        public bool IsVoiceFeedbackEnabled
        {
            get => _isVoiceFeedbackEnabled;
            set => _isVoiceFeedbackEnabled = value;
        }

        public string SelectedVoice
        {
            get => _selectedVoice;
            set => _selectedVoice = value;
        }

        public VoiceAssistantService(CloudAiClient cloudAiClient)
        {
            _cloudAiClient = cloudAiClient;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            _synthesizer = new SpeechSynthesizer();

            ConfigureSynthesizer();

            _vadTimer = new System.Timers.Timer(120); // Check VAD every 120ms
            _vadTimer.Elapsed += VadTimer_Elapsed;

            // Background warm up / bridge check
            _ = Task.Run(EnsureVoiceBridgeRunningAsync);
        }

        private void ConfigureSynthesizer()
        {
            try
            {
                var voices = _synthesizer.GetInstalledVoices();
                var trVoice = voices.FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("tr", StringComparison.OrdinalIgnoreCase));
                if (trVoice != null)
                {
                    _synthesizer.SelectVoice(trVoice.VoiceInfo.Name);
                }
                _synthesizer.Rate = 1;
                _synthesizer.Volume = 100;
            }
            catch { }
        }

        public async Task<bool> EnsureVoiceBridgeRunningAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(1500);
                var healthRes = await _httpClient.GetAsync($"{VoiceBridgeUrl}/health", cts.Token);
                if (healthRes.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch { }

            // Bridge not running, launch it
            return await Task.Run(() =>
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var candidates = new[]
                    {
                        Path.Combine(baseDir, "VoiceBridge", "voice_service.py"),
                        Path.Combine(Directory.GetCurrentDirectory(), "VoiceBridge", "voice_service.py"),
                        @"c:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\VoiceBridge\voice_service.py"
                    };

                    string? bridgeScript = candidates.FirstOrDefault(File.Exists);
                    if (string.IsNullOrEmpty(bridgeScript)) return false;

                    var pythonPath = FindPythonExecutable();
                    if (string.IsNullOrEmpty(pythonPath)) return false;

                    var psi = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"\"{bridgeScript}\"",
                        WorkingDirectory = Path.GetDirectoryName(bridgeScript) ?? baseDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    };

                    _voiceBridgeProcess = Process.Start(psi);

                    // Wait up to 15 seconds for health (CUDA float16 large-v3-turbo takes ~4-6 seconds)
                    for (int i = 0; i < 30; i++)
                    {
                        Thread.Sleep(500);
                        try
                        {
                            using var cts = new CancellationTokenSource(800);
                            var check = _httpClient.GetAsync($"{VoiceBridgeUrl}/health", cts.Token).GetAwaiter().GetResult();
                            if (check.IsSuccessStatusCode) return true;
                        }
                        catch { }
                    }
                }
                catch { }
                return false;
            });
        }

        private static string FindPythonExecutable()
        {
            var candidates = new[]
            {
                @"C:\Users\Alphan.MonsterAlphan\AppData\Local\Programs\Python\Python312\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python.exe"),
                "python.exe"
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return "python";
        }

        public void StartRecording()
        {
            try
            {
                StopSpeaking();
                StopRecordingInternal();

                _recordingStream = new MemoryStream();
                _waveIn = new WaveIn
                {
                    WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz 16-bit Mono (Whisper standard)
                    BufferMilliseconds = 50,
                    NumberOfBuffers = 3
                };

                _waveWriter = new WaveFileWriter(_recordingStream, _waveIn.WaveFormat);
                _waveIn.DataAvailable += OnWaveInDataAvailable;
                _waveIn.StartRecording();

                _isRecording = true;
                _hasSpokenInTurn = false;
                _isUtteranceCompletedTriggered = false;
                _lastSpeechDetectedAt = DateTime.UtcNow;
                _speechStartedAt = DateTime.UtcNow;
                _recordingStartedAt = DateTime.UtcNow;
                _ambientNoiseRms = 15f;
                _calibratedThreshold = 35f;
                _consecutiveSilenceMs = 0;
                _totalSpeechDurationMs = 0;

                lock (_vadLock)
                {
                    _vadSampleBuffer.Clear();
                    _sileroVad.Reset();
                }

                RecordingStateChanged?.Invoke(true);

                if (_isLiveMode)
                {
                    _vadTimer.Start();
                    LiveStateChanged?.Invoke(VoiceChatState.Listening, "🎙️ Sizi dinliyor... Konuşun.");
                }
                else
                {
                    StatusMessage?.Invoke("🎙️ Dinleniyor... Konuşun.");
                }
            }
            catch (Exception ex)
            {
                _isRecording = false;
                RecordingStateChanged?.Invoke(false);
                StatusMessage?.Invoke($"Mikrofon başlatılamadı: {ex.Message}");
            }
        }

        private void OnWaveInDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_waveWriter == null || e.BytesRecorded == 0 || !_isRecording) return;

            try
            {
                _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);

                if (_isLiveMode && !_isUtteranceCompletedTriggered)
                {
                    int numSamples = e.BytesRecorded / 2;
                    float[] chunkFloats = new float[numSamples];
                    float sumSq = 0f;

                    for (int i = 0, sampleIdx = 0; i < e.BytesRecorded - 1; i += 2, sampleIdx++)
                    {
                        short s = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                        float f = s / 32768.0f;
                        chunkFloats[sampleIdx] = f;
                        sumSq += s * s;
                    }

                    // RMS volume calculation for UI visualizer
                    float rms = numSamples > 0 ? MathF.Sqrt(sumSq / numSamples) : 0f;
                    float normalizedLevel = Math.Clamp(rms / 400f, 0f, 1f);
                    AudioLevelChanged?.Invoke(normalizedLevel);

                    // Calibrate noise floor in initial 400ms
                    if ((DateTime.UtcNow - _recordingStartedAt).TotalMilliseconds < 400)
                    {
                        _ambientNoiseRms = (_ambientNoiseRms * 0.7f) + (rms * 0.3f);
                        _calibratedThreshold = Math.Clamp(_ambientNoiseRms * 1.8f, 25f, 120f);
                    }

                    // Feed 512-sample chunks (32ms at 16kHz) directly to Silero VAD
                    lock (_vadLock)
                    {
                        _vadSampleBuffer.AddRange(chunkFloats);

                        while (_vadSampleBuffer.Count >= 512 && !_isUtteranceCompletedTriggered)
                        {
                            var samples512 = new float[512];
                            _vadSampleBuffer.CopyTo(0, samples512, 0, 512);
                            _vadSampleBuffer.RemoveRange(0, 512);

                            float speechProb;
                            if (_sileroVad.IsInitialized)
                            {
                                speechProb = _sileroVad.DetectSpeechProbability(samples512);
                            }
                            else
                            {
                                // Resilient energy fallback if ONNX is not available
                                speechProb = (rms > _calibratedThreshold) ? 0.85f : 0.05f;
                            }

                            // Speech detection threshold: 0.70 (0.65 - 0.75 range)
                            // Deep neural filter ignores breath, keyboard clicks, fan noise, and echo
                            if (speechProb >= 0.70f)
                            {
                                if (!_hasSpokenInTurn)
                                {
                                    _hasSpokenInTurn = true;
                                    _speechStartedAt = DateTime.UtcNow;
                                    LiveStateChanged?.Invoke(VoiceChatState.Listening, "🎙️ Konuşma algılandı...");
                                }
                                _lastSpeechDetectedAt = DateTime.UtcNow;
                                _consecutiveSilenceMs = 0;
                                _totalSpeechDurationMs += 32;
                            }
                            else if (speechProb < 0.35f && _hasSpokenInTurn)
                            {
                                _consecutiveSilenceMs += 32;

                                // User requirement: 600 ms - 800 ms silence (we set 650 ms)
                                // Triggers Whisper in under 1 second after speech completes!
                                if (_consecutiveSilenceMs >= 650 && _totalSpeechDurationMs >= 300)
                                {
                                    _isUtteranceCompletedTriggered = true;
                                    _vadTimer.Stop();
                                    Task.Run(() => LiveUtteranceCompleted?.Invoke());
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void VadTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isLiveMode || !_isRecording || _isUtteranceCompletedTriggered) return;

            var totalDurationSec = (DateTime.UtcNow - _recordingStartedAt).TotalSeconds;

            if (_hasSpokenInTurn)
            {
                var silenceMs = (DateTime.UtcNow - _lastSpeechDetectedAt).TotalMilliseconds;
                var speechDurationMs = (DateTime.UtcNow - _speechStartedAt).TotalMilliseconds;

                // Watchdog fallback: if VAD missed an edge or max sentence reached
                if ((silenceMs >= 750 && speechDurationMs >= 300) || totalDurationSec >= 45.0)
                {
                    _isUtteranceCompletedTriggered = true;
                    _vadTimer.Stop();
                    LiveUtteranceCompleted?.Invoke();
                }
            }
            else
            {
                // User hasn't spoken yet:
                // NEVER shut down prematurely! Patiently wait for up to 300 seconds (5 minutes)
                if (totalDurationSec >= 300.0)
                {
                    _vadTimer.Stop();
                    StopLiveMode();
                    StatusMessage?.Invoke("Mikrofon uzun süreli boşta kalma nedeniyle durduruldu.");
                }
            }
        }

        public byte[] StopRecording()
        {
            _vadTimer.Stop();
            return StopRecordingInternal();
        }

        private byte[] StopRecordingInternal()
        {
            if (!_isRecording) return Array.Empty<byte>();

            try
            {
                _isRecording = false;

                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnWaveInDataAvailable;
                    try { _waveIn.StopRecording(); } catch { }
                    try { _waveIn.Dispose(); } catch { }
                    _waveIn = null;
                }

                if (_waveWriter != null)
                {
                    try { _waveWriter.Flush(); } catch { }
                    try { _waveWriter.Dispose(); } catch { }
                    _waveWriter = null;
                }

                byte[] result = _recordingStream?.ToArray() ?? Array.Empty<byte>();
                _recordingStream?.Dispose();
                _recordingStream = null;

                RecordingStateChanged?.Invoke(false);
                return result;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public async Task<string> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
        {
            if (wavBytes == null || wavBytes.Length < 1600)
            {
                return string.Empty;
            }

            // 1. FAST PATH: Local Faster-Whisper Turbo Microservice (GPU/CPU <400ms)
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(15000);

                using var content = new ByteArrayContent(wavBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

                var res = await _httpClient.PostAsync($"{VoiceBridgeUrl}/transcribe", content, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("text", out var tEl))
                    {
                        var text = tEl.GetString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }
                }
            }
            catch { }

            // 2. CLI FALLBACK: Local Faster-Whisper Turbo CLI script
            try
            {
                var cliResult = await Task.Run(() =>
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var cliScript = Path.Combine(baseDir, "VoiceBridge", "transcribe_cli.py");
                    if (!File.Exists(cliScript))
                    {
                        var alt = Path.Combine(Directory.GetCurrentDirectory(), "VoiceBridge", "transcribe_cli.py");
                        if (File.Exists(alt)) cliScript = alt;
                    }

                    if (File.Exists(cliScript))
                    {
                        var tempWav = Path.GetTempFileName() + ".wav";
                        File.WriteAllBytes(tempWav, wavBytes);

                        var python = FindPythonExecutable();
                        var psi = new ProcessStartInfo
                        {
                            FileName = python,
                            Arguments = $"\"{cliScript}\" \"{tempWav}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };

                        using var p = Process.Start(psi);
                        if (p != null)
                        {
                            var outText = p.StandardOutput.ReadToEnd().Trim();
                            p.WaitForExit(8000);
                            try { File.Delete(tempWav); } catch { }
                            if (!string.IsNullOrEmpty(outText)) return outText;
                        }
                    }
                    return string.Empty;
                });

                if (!string.IsNullOrEmpty(cliResult)) return cliResult;
            }
            catch { }

            // 3. CLOUD FALLBACK: OpenAI Dedicated Whisper-1 Audio Transcription API
            var openAiKey = _cloudAiClient?.Config?.OpenAiKey;
            if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                try
                {
                    using var form = new MultipartFormDataContent();
                    var audioContent = new ByteArrayContent(wavBytes);
                    audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    form.Add(audioContent, "file", "recording.wav");
                    form.Add(new StringContent("whisper-1"), "model");
                    form.Add(new StringContent("tr"), "language");

                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
                    req.Headers.Add("Authorization", $"Bearer {openAiKey.Trim()}");
                    req.Content = form;

                    var res = await _httpClient.SendAsync(req, cancellationToken);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync(cancellationToken);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("text", out var txtEl))
                        {
                            var text = txtEl.GetString()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(text)) return text;
                        }
                    }
                }
                catch { }
            }

            return string.Empty;
        }

        public async Task<string?> ToggleDictationAsync(CancellationToken cancellationToken = default)
        {
            if (_isRecording)
            {
                StatusMessage?.Invoke("⏳ Faster-Whisper ile çözümleniyor...");
                var audio = StopRecording();
                var text = await TranscribeAudioAsync(audio, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SpeechRecognized?.Invoke(text);
                    StatusMessage?.Invoke("✅ Ses metne döküldü.");
                    return text;
                }
                else
                {
                    StatusMessage?.Invoke("⚠️ Ses algılanamadı.");
                    return null;
                }
            }
            else
            {
                StartRecording();
                return null;
            }
        }

        public void StartLiveMode()
        {
            _isLiveMode = true;
            LiveStateChanged?.Invoke(VoiceChatState.Listening, "🎙️ Dinliyor...");
            StartRecording();
        }

        public void StopLiveMode()
        {
            _isLiveMode = false;
            _vadTimer.Stop();
            StopRecording();
            StopSpeaking();
            LiveStateChanged?.Invoke(VoiceChatState.Idle, "Kapalı");
        }

        public void SpeakText(string text, Action? onCompleted = null)
        {
            if (!_isVoiceFeedbackEnabled || string.IsNullOrWhiteSpace(text))
            {
                onCompleted?.Invoke();
                return;
            }

            // Clean markdown syntax for natural speech
            var clean = Regex.Replace(text, @"```[\s\S]*?```", "");
            clean = Regex.Replace(clean, @"\|[\s\S]*?\|", "");
            clean = Regex.Replace(clean, @"[#*`_>\[\]\(\)\{\}]", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(clean))
            {
                onCompleted?.Invoke();
                return;
            }

            if (clean.Length > 380)
            {
                clean = clean.Substring(0, 380) + "... Devamını ekrandan okuyabilirsiniz.";
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    StopSpeaking();

                    byte[]? mp3Bytes = null;

                    // 1. OpenAI Text-to-Speech: If user has an OpenAI API Key configured
                    var openAiKey = _cloudAiClient?.Config?.OpenAiKey;
                    if (!string.IsNullOrWhiteSpace(openAiKey))
                    {
                        mp3Bytes = await GenerateOpenAiTtsAudioAsync(clean, openAiKey);
                    }

                    // 2. High-Fidelity 100% Free Edge-TTS (tr-TR-AhmetNeural): If OpenAI key absent or unpaid/failed
                    if (mp3Bytes == null || mp3Bytes.Length < 500)
                    {
                        mp3Bytes = await GenerateEdgeTtsAudioAsync(clean);
                    }

                    if (mp3Bytes != null && mp3Bytes.Length > 500)
                    {
                        PlayMp3Audio(mp3Bytes, onCompleted);
                        return;
                    }
                }
                catch { }

                // Fallback: Windows SAPI5
                try
                {
                    _synthesizer.SpeakAsyncCancelAll();
                    if (onCompleted != null)
                    {
                        EventHandler<SpeakCompletedEventArgs>? handler = null;
                        handler = (s, e) =>
                        {
                            _synthesizer.SpeakCompleted -= handler;
                            SpeakingStateChanged?.Invoke(false);
                            onCompleted.Invoke();
                        };
                        _synthesizer.SpeakCompleted += handler;
                    }
                    SpeakingStateChanged?.Invoke(true);
                    _synthesizer.SpeakAsync(clean);
                }
                catch
                {
                    SpeakingStateChanged?.Invoke(false);
                    onCompleted?.Invoke();
                }
            });
        }

        private async Task<byte[]?> GenerateEdgeTtsAudioAsync(string text)
        {
            // 1. Try VoiceBridge HTTP
            try
            {
                using var cts = new CancellationTokenSource(5000);
                var body = new { text = text, voice = _selectedVoice, rate = "+0%" };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                var res = await _httpClient.PostAsync($"{VoiceBridgeUrl}/tts", content, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
                    if (bytes != null && bytes.Length > 200) return bytes;
                }
            }
            catch { }

            // 2. Try CLI script
            try
            {
                return await Task.Run(() =>
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var cliScript = Path.Combine(baseDir, "VoiceBridge", "tts_cli.py");
                    if (!File.Exists(cliScript))
                    {
                        var alt = Path.Combine(Directory.GetCurrentDirectory(), "VoiceBridge", "tts_cli.py");
                        if (File.Exists(alt)) cliScript = alt;
                    }

                    if (File.Exists(cliScript))
                    {
                        var tempTxt = Path.GetTempFileName() + ".txt";
                        var tempMp3 = Path.GetTempFileName() + ".mp3";
                        File.WriteAllText(tempTxt, text, Encoding.UTF8);

                        var python = FindPythonExecutable();
                        var psi = new ProcessStartInfo
                        {
                            FileName = python,
                            Arguments = $"\"{cliScript}\" \"{tempTxt}\" \"{tempMp3}\" \"{_selectedVoice}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };

                        using var p = Process.Start(psi);
                        if (p != null)
                        {
                            p.WaitForExit(5000);
                            try { File.Delete(tempTxt); } catch { }
                            if (File.Exists(tempMp3))
                            {
                                var data = File.ReadAllBytes(tempMp3);
                                try { File.Delete(tempMp3); } catch { }
                                if (data.Length > 200) return data;
                            }
                        }
                    }
                    return null;
                });
            }
            catch { }

            return null;
        }

        private async Task<byte[]?> GenerateOpenAiTtsAudioAsync(string text, string openAiKey)
        {
            try
            {
                using var cts = new CancellationTokenSource(8000);
                var payload = new
                {
                    model = "tts-1",
                    input = text,
                    voice = "alloy",
                    response_format = "mp3"
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                req.Headers.Add("Authorization", $"Bearer {openAiKey.Trim()}");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var res = await _httpClient.SendAsync(req, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
                    if (bytes != null && bytes.Length > 500)
                    {
                        return bytes;
                    }
                }
            }
            catch { }
            return null;
        }

        private void PlayMp3Audio(byte[] mp3Bytes, Action? onCompleted)
        {
            lock (_playbackLock)
            {
                try
                {
                    CleanUpPlayback();

                    _mp3Stream = new MemoryStream(mp3Bytes);
                    _mp3Reader = new Mp3FileReader(_mp3Stream);
                    _waveOut = new WaveOut();
                    _waveOut.Init(_mp3Reader);

                    _waveOut.PlaybackStopped += (s, e) =>
                    {
                        lock (_playbackLock)
                        {
                            CleanUpPlayback();
                        }
                        SpeakingStateChanged?.Invoke(false);
                        onCompleted?.Invoke();
                    };

                    SpeakingStateChanged?.Invoke(true);
                    _waveOut.Play();
                }
                catch
                {
                    CleanUpPlayback();
                    SpeakingStateChanged?.Invoke(false);
                    onCompleted?.Invoke();
                }
            }
        }

        private void CleanUpPlayback()
        {
            try
            {
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }
                if (_mp3Reader != null)
                {
                    _mp3Reader.Dispose();
                    _mp3Reader = null;
                }
                if (_mp3Stream != null)
                {
                    _mp3Stream.Dispose();
                    _mp3Stream = null;
                }
            }
            catch { }
        }

        public void StopSpeaking()
        {
            lock (_playbackLock)
            {
                CleanUpPlayback();
            }

            try
            {
                _synthesizer.SpeakAsyncCancelAll();
            }
            catch { }

            SpeakingStateChanged?.Invoke(false);
        }

        public void Dispose()
        {
            try
            {
                StopLiveMode();
                StopSpeaking();
                _sileroVad.Dispose();
                _synthesizer.Dispose();
                _httpClient.Dispose();

                if (_voiceBridgeProcess != null && !_voiceBridgeProcess.HasExited)
                {
                    try { _voiceBridgeProcess.Kill(); } catch { }
                    _voiceBridgeProcess.Dispose();
                    _voiceBridgeProcess = null;
                }
            }
            catch { }
        }
    }
}
