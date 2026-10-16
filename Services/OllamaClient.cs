using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class OllamaClient
    {
        private readonly HttpClient _httpClient;
        private string _baseUrl = "http://localhost:11434";

        public OllamaClient(string? baseUrl = null)
        {
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                _baseUrl = baseUrl.TrimEnd('/');
            }

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(25)
            };
        }

        public static async Task<bool> TryEnsureOllamaRunningAsync()
        {
            try
            {
                using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                var res = await testClient.GetAsync("http://localhost:11434/api/tags");
                if (res.IsSuccessStatusCode) return true;
            }
            catch { }

            string? ollamaExe = null;
            var localPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            if (File.Exists(localPath))
            {
                ollamaExe = localPath;
            }
            else
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var cand = Path.Combine(dir.Trim(), "ollama.exe");
                    if (File.Exists(cand))
                    {
                        ollamaExe = cand;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(ollamaExe))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ollamaExe,
                        Arguments = "serve",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);

                    for (int i = 0; i < 6; i++)
                    {
                        await Task.Delay(400);
                        try
                        {
                            using var testClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
                            var res = await testClient.GetAsync("http://localhost:11434/api/tags");
                            if (res.IsSuccessStatusCode) return true;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return false;
        }

        public async Task<bool> CheckConnectionAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync("/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync()
        {
            var list = new List<string>();
            try
            {
                var response = await _httpClient.GetAsync("/api/tags");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(json);
                    if (tags?.Models != null)
                    {
                        foreach (var m in tags.Models)
                        {
                            if (!string.IsNullOrWhiteSpace(m.Name) && !list.Contains(m.Name))
                            {
                                list.Add(m.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing models via API: {ex.Message}");
            }

            // Disk üzerindeki yerel model manifestlerini de tara (Ollama yeni ayağa kalkıyorsa veya ek modeller varsa)
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var manifestsDir = Path.Combine(userProfile, ".ollama", "models", "manifests");
                if (Directory.Exists(manifestsDir))
                {
                    var manifestFiles = Directory.GetFiles(manifestsDir, "*", SearchOption.AllDirectories);
                    foreach (var mf in manifestFiles)
                    {
                        var rel = Path.GetRelativePath(manifestsDir, mf).Replace('\\', '/');
                        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            string tag = parts[^1];
                            string model = parts[^2];
                            string modelTag = tag.Equals("latest", StringComparison.OrdinalIgnoreCase)
                                ? $"{model}:latest"
                                : $"{model}:{tag}";
                            if (!list.Contains(modelTag) && !list.Contains(model))
                            {
                                list.Add(modelTag);
                            }
                        }
                    }
                }
            }
            catch { }

            if (list.Count == 0)
            {
                list.Add("qwen2.5-coder:1.5b");
                list.Add("llama3.2:latest");
            }

            return list;
        }

        public async Task StreamChatAsync(
            string model,
            List<OllamaChatMessage> messages,
            Action<string> onChunkReceived,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new OllamaChatRequest
            {
                Model = model,
                Messages = messages,
                Stream = true
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = content
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var chunk = JsonSerializer.Deserialize<OllamaChatChunk>(line);
                    if (chunk?.Message?.Content != null)
                    {
                        onChunkReceived(chunk.Message.Content);
                    }

                    if (chunk?.Done == true)
                    {
                        break;
                    }
                }
                catch
                {
                    // Skip malformed chunks
                }
            }
        }

        public async Task<string> GenerateTextAsync(string model, string prompt, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            var messages = new List<OllamaChatMessage>
            {
                new() { Role = "user", Content = prompt }
            };
            try
            {
                await StreamChatAsync(model, messages, chunk => sb.Append(chunk), cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GenerateTextAsync error: {ex.Message}");
            }
            return sb.ToString();
        }
    }
}
