using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EnterpriseAIApp.Services
{
    public class SileroVadDetector : IDisposable
    {
        private InferenceSession? _session;
        private DenseTensor<float>? _h;
        private DenseTensor<float>? _c;
        private readonly float[] _contextBuffer = new float[64];
        private readonly object _lock = new();
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;

        public SileroVadDetector(string? modelPath = null)
        {
            try
            {
                var candidates = new[]
                {
                    modelPath ?? "",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "silero_vad.onnx"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "silero_vad.onnx"),
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Assets\silero_vad.onnx",
                    @"C:\Users\Alphan.MonsterAlphan\AppData\Local\Programs\Python\Python312\Lib\site-packages\faster_whisper\assets\silero_vad_v6.onnx"
                };

                string? validPath = null;
                foreach (var c in candidates)
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    {
                        validPath = c;
                        break;
                    }
                }

                if (validPath != null)
                {
                    var options = new SessionOptions
                    {
                        IntraOpNumThreads = 1,
                        InterOpNumThreads = 1,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                    };

                    _session = new InferenceSession(validPath, options);
                    Reset();
                    _isInitialized = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SileroVadDetector] Init error: {ex.Message}");
                _isInitialized = false;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _h = new DenseTensor<float>(new[] { 1, 1, 128 });
                _c = new DenseTensor<float>(new[] { 1, 1, 128 });
                Array.Clear(_contextBuffer, 0, _contextBuffer.Length);
            }
        }

        public float DetectSpeechProbability(float[] samples512)
        {
            if (!_isInitialized || _session == null || samples512 == null || samples512.Length != 512)
            {
                return 0f;
            }

            lock (_lock)
            {
                try
                {
                    // Input tensor: [1, 576] (64 context + 512 samples)
                    var inputData = new float[576];
                    Array.Copy(_contextBuffer, 0, inputData, 0, 64);
                    Array.Copy(samples512, 0, inputData, 64, 512);

                    // Update context for next chunk (last 64 samples of current chunk)
                    Array.Copy(samples512, 512 - 64, _contextBuffer, 0, 64);

                    var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 576 });

                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input", inputTensor),
                        NamedOnnxValue.CreateFromTensor("h", _h!),
                        NamedOnnxValue.CreateFromTensor("c", _c!)
                    };

                    using var results = _session.Run(inputs);

                    float speechProb = 0f;
                    foreach (var r in results)
                    {
                        if (r.Name == "speech_probs")
                        {
                            var tensor = r.AsTensor<float>();
                            speechProb = tensor.GetValue(0);
                        }
                        else if (r.Name == "hn")
                        {
                            _h = (DenseTensor<float>)r.AsTensor<float>().Clone();
                        }
                        else if (r.Name == "cn")
                        {
                            _c = (DenseTensor<float>)r.AsTensor<float>().Clone();
                        }
                    }

                    return speechProb;
                }
                catch
                {
                    return 0f;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _session?.Dispose();
                _session = null;
                _isInitialized = false;
            }
        }
    }
}
