using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EnterpriseAIApp.Services
{
    public class CustomLLMRunner
    {
        private readonly string _customLLMDir;
        private Process? _trainingProcess;

        public event Action<int, int, double, double, double>? TrainingProgressUpdated; // step, maxSteps, loss, lr, speed
        public event Action<string>? LogReceived;
        public event Action<bool, string>? TrainingCompleted;

        public CustomLLMRunner()
        {
            _customLLMDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CustomLLM_Scratch");
            if (!Directory.Exists(_customLLMDir))
            {
                // Fallback to project root directory if running from bin/Debug
                var parent = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.Parent?.Parent?.Parent?.FullName;
                if (parent != null)
                {
                    var checkPath = Path.Combine(parent, "CustomLLM_Scratch");
                    if (Directory.Exists(checkPath))
                    {
                        _customLLMDir = checkPath;
                    }
                }
            }
        }

        public string GetCustomLLMDir() => _customLLMDir;

        public string GetSampleDataPath() => Path.Combine(_customLLMDir, "sample_data.txt");

        public bool IsTrainingRunning => _trainingProcess != null && !_trainingProcess.HasExited;

        public async Task StartTrainingAsync(string modelSize, int steps, double lr, string dataPath)
        {
            if (IsTrainingRunning) return;

            await Task.Run(() =>
            {
                try
                {
                    var trainScript = Path.Combine(_customLLMDir, "train.py");
                    var lrStr = lr.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "py",
                        Arguments = $"-3.12 \"{trainScript}\" --size {modelSize} --steps {steps} --lr {lrStr} --data \"{dataPath}\"",
                        WorkingDirectory = _customLLMDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    _trainingProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

                    _trainingProcess.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            LogReceived?.Invoke(e.Data);
                            ParseTrainingLog(e.Data, steps);
                        }
                    };

                    _trainingProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            LogReceived?.Invoke("[Hata]: " + e.Data);
                        }
                    };

                    _trainingProcess.Start();
                    _trainingProcess.BeginOutputReadLine();
                    _trainingProcess.BeginErrorReadLine();

                    _trainingProcess.WaitForExit();
                    var success = _trainingProcess.ExitCode == 0;
                    TrainingCompleted?.Invoke(success, success ? "Eğitim başarıyla tamamlandı!" : "Eğitim sırasında bir hata oluştu.");
                }
                catch (Exception ex)
                {
                    TrainingCompleted?.Invoke(false, ex.Message);
                }
                finally
                {
                    _trainingProcess = null;
                }
            });
        }

        public void StopTraining()
        {
            try
            {
                if (_trainingProcess != null && !_trainingProcess.HasExited)
                {
                    _trainingProcess.Kill(true);
                }
            }
            catch { }
        }

        public async Task<string> GenerateAsync(string prompt, double temperature = 0.7, int topK = 30, int maxTokens = 100)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var genScript = Path.Combine(_customLLMDir, "generate.py");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "py",
                        Arguments = $"-3.12 \"{genScript}\" \"{prompt.Replace("\"", "\\\"")}\"",
                        WorkingDirectory = _customLLMDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) return "Model çalıştırılamadı.";

                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        return stdout.Trim();
                    }
                    return $"Hata:\n{stderr}";
                }
                catch (Exception ex)
                {
                    return $"Hata: {ex.Message}";
                }
            });
        }

        private void ParseTrainingLog(string line, int maxSteps)
        {
            // Format: Adım [050/100] | Loss: 1.7500 | LR: 0.000500 | Hız: 7869 tok/s
            var match = Regex.Match(line, @"Adim\s+\[(\d+)/(\d+)\]\s+\|\s+Loss:\s+([\d\.]+)\s+\|\s+LR:\s+([\d\.]+)\s+\|\s+Hiz:\s+([\d\.]+)");
            if (match.Success)
            {
                int step = int.Parse(match.Groups[1].Value);
                int total = int.Parse(match.Groups[2].Value);
                double loss = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                double lr = double.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                double speed = double.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture);

                TrainingProgressUpdated?.Invoke(step, total, loss, lr, speed);
            }
        }
    }
}