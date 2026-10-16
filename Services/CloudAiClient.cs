using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class CloudApiConfig
    {
        public string OpenAiKey { get; set; } = string.Empty;
        public string ClaudeKey { get; set; } = string.Empty;
        public string GoogleKey { get; set; } = string.Empty;
    }

    public class CloudModelInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Provider { get; set; }
        public string Description { get; set; }

        public CloudModelInfo(string id, string displayName, string provider, string description)
        {
            Id = id;
            DisplayName = displayName;
            Provider = provider;
            Description = description;
        }
    }

    public class CloudAiClient
    {
        private readonly HttpClient _httpClient;
        private readonly GeminiClient _geminiClient;
        private readonly string _configFilePath;
        private CloudApiConfig _config;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public CloudApiConfig Config => _config;

        public CloudAiClient(GeminiClient geminiClient)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _geminiClient = geminiClient;
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "cloud_api_keys.json");
            _config = LoadConfig();
        }

        private CloudApiConfig LoadConfig()
        {
            var cfg = new CloudApiConfig();
            try
            {
                // 1. Check primary file: Data/cloud_api_keys.json
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    cfg = JsonSerializer.Deserialize<CloudApiConfig>(json, _jsonOptions) ?? cfg;
                }

                // 2. Migration check: Data/gemini_config.json
                if (string.IsNullOrWhiteSpace(cfg.GoogleKey))
                {
                    var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "gemini_config.json");
                    if (File.Exists(legacyPath))
                    {
                        var legJson = File.ReadAllText(legacyPath);
                        using var doc = JsonDocument.Parse(legJson);
                        if (doc.RootElement.TryGetProperty("ApiKey", out var el))
                        {
                            cfg.GoogleKey = el.GetString() ?? "";
                        }
                    }
                }

                // 3. Environment Variables fallback
                if (string.IsNullOrWhiteSpace(cfg.GoogleKey))
                {
                    cfg.GoogleKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
                }
                if (string.IsNullOrWhiteSpace(cfg.OpenAiKey))
                {
                    cfg.OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
                }
                if (string.IsNullOrWhiteSpace(cfg.ClaudeKey))
                {
                    cfg.ClaudeKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
                }
            }
            catch { }
            return cfg;
        }

        public void SaveConfig(string openAiKey, string claudeKey, string googleKey)
        {
            _config.OpenAiKey = openAiKey.Trim();
            _config.ClaudeKey = claudeKey.Trim();
            _config.GoogleKey = googleKey.Trim();

            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_configFilePath, JsonSerializer.Serialize(_config, _jsonOptions));

                // Also sync Google key to GeminiClient
                _geminiClient.ApiKey = _config.GoogleKey;
            }
            catch { }
        }

        public bool HasAnyCloudKey()
        {
            return !string.IsNullOrWhiteSpace(_config.OpenAiKey) ||
                   !string.IsNullOrWhiteSpace(_config.ClaudeKey) ||
                   !string.IsNullOrWhiteSpace(_config.GoogleKey);
        }

        public bool IsCloudModel(string? modelTag)
        {
            if (string.IsNullOrWhiteSpace(modelTag)) return false;
            return modelTag.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase) ||
                   modelTag.StartsWith("openai:", StringComparison.OrdinalIgnoreCase) ||
                   modelTag.StartsWith("claude:", StringComparison.OrdinalIgnoreCase);
        }

        public List<CloudModelInfo> GetAvailableCloudModels()
        {
            var list = new List<CloudModelInfo>();

            if (!string.IsNullOrWhiteSpace(_config.OpenAiKey))
            {
                list.Add(new("openai:gpt-4o", "☁️ [OpenAI] GPT-4o (Amiral Gemisi)", "OpenAI", "Görsel ve metin analizinde yüksek doğruluk ve hız."));
                list.Add(new("openai:gpt-4o-mini", "☁️ [OpenAI] GPT-4o Mini (Işık Hızında)", "OpenAI", "Düşük gecikmeli, ekonomik ve çok hızlı model."));
            }

            if (!string.IsNullOrWhiteSpace(_config.ClaudeKey))
            {
                list.Add(new("claude:claude-3-5-sonnet-20241022", "☁️ [Claude] Claude 3.5 Sonnet (Akıl Yürütme)", "Anthropic", "Karmaşık raporlama, mantık ve mimari analizde en güçlü Claude modeli."));
                list.Add(new("claude:claude-3-5-haiku-20241022", "☁️ [Claude] Claude 3.5 Haiku (Hızlı)", "Anthropic", "Hızlı kurumsal yanıtlar ve operasyonel görevler."));
            }

            if (!string.IsNullOrWhiteSpace(_config.GoogleKey))
            {
                list.Add(new("gemini:gemini-3.5-flash", "⚡ [Google] Gemini 3.5 Flash (En Hızlı & Akıllı)", "Google", "Google DeepMind'ın yüksek hızlı ve akıllı modeli (~1-2sn)."));
                list.Add(new("gemini:gemini-3.5-flash-lite", "⚡ [Google] Gemini 3.5 Flash Lite (Işık Hızında, <1sn)", "Google", "Ultra düşük gecikmeli, anlık yanıt süresi sunan hafif model."));
                list.Add(new("gemini:gemini-flash-latest", "🌟 [Google] Gemini Flash (Kararlı)", "Google", "Mali analiz ve ERP otomasyonu için optimize edilmiş kararlı model."));
                list.Add(new("gemini:gemini-3-flash-preview", "✨ [Google] Gemini 3 Flash Preview", "Google", "Yeni nesil deneysel model."));
            }

            return list;
        }

        public async Task<bool> TestConnectionAsync(string provider, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                switch (provider.ToLowerInvariant())
                {
                    case "google":
                        return await _geminiClient.CheckConnectionWithKeyAsync(key);

                    case "openai":
                        using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models"))
                        {
                            req.Headers.Add("Authorization", $"Bearer {key.Trim()}");
                            var res = await _httpClient.SendAsync(req);
                            return res.IsSuccessStatusCode;
                        }

                    case "claude":
                        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                        {
                            req.Headers.Add("x-api-key", key.Trim());
                            req.Headers.Add("anthropic-version", "2023-06-01");
                            req.Content = new StringContent(
                                JsonSerializer.Serialize(new
                                {
                                    model = "claude-3-5-haiku-20241022",
                                    max_tokens = 5,
                                    messages = new[] { new { role = "user", content = "ping" } }
                                }),
                                Encoding.UTF8,
                                "application/json");
                            var res = await _httpClient.SendAsync(req);
                            return res.IsSuccessStatusCode;
                        }

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task StreamChatAsync(
            string modelKey,
            List<OllamaChatMessage> messages,
            Action<string> onChunkReceived,
            CancellationToken cancellationToken = default,
            List<ChatAttachment>? attachments = null,
            List<GeminiFunctionDeclaration>? tools = null)
        {
            if (modelKey.StartsWith("gemini:"))
            {
                var geminiModel = modelKey.Substring("gemini:".Length);
                await _geminiClient.StreamChatAsync(geminiModel, messages, onChunkReceived, cancellationToken, attachments, tools);
            }
            else if (modelKey.StartsWith("openai:"))
            {
                var openAiModel = modelKey.Substring("openai:".Length);
                await StreamOpenAiChatAsync(openAiModel, messages, onChunkReceived, cancellationToken, attachments);
            }
            else if (modelKey.StartsWith("claude:"))
            {
                var claudeModel = modelKey.Substring("claude:".Length);
                await StreamClaudeChatAsync(claudeModel, messages, onChunkReceived, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Desteklenmeyen bulut model türü: {modelKey}");
            }
        }

        private async Task StreamOpenAiChatAsync(
            string model,
            List<OllamaChatMessage> messages,
            Action<string> onChunkReceived,
            CancellationToken cancellationToken,
            List<ChatAttachment>? attachments = null)
        {
            if (string.IsNullOrWhiteSpace(_config.OpenAiKey))
            {
                throw new InvalidOperationException("OpenAI API Anahtarı girilmemiş. Lütfen 🔑 simgesinden OpenAI anahtarınızı tanımlayın.");
            }

            var openAiMsgs = new List<object>();
            foreach (var m in messages)
            {
                if (m.Role == "user" && attachments != null && attachments.Count > 0 && m == messages.Last(x => x.Role == "user"))
                {
                    var contentParts = new List<object>
                    {
                        new { type = "text", text = m.Content }
                    };
                    foreach (var att in attachments)
                    {
                        if (!string.IsNullOrEmpty(att.Base64Data))
                        {
                            var mime = string.IsNullOrEmpty(att.MimeType) ? "image/png" : att.MimeType;
                            contentParts.Add(new
                            {
                                type = "image_url",
                                image_url = new { url = $"data:{mime};base64,{att.Base64Data}" }
                            });
                        }
                    }
                    openAiMsgs.Add(new { role = m.Role, content = contentParts });
                }
                else
                {
                    openAiMsgs.Add(new { role = m.Role, content = m.Content });
                }
            }

            var payload = new
            {
                model = model,
                messages = openAiMsgs,
                stream = true,
                temperature = 0.7
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("Authorization", $"Bearer {_config.OpenAiKey.Trim()}");

            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: ")) line = line.Substring(6).Trim();
                if (line == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentEl))
                        {
                            var text = contentEl.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                onChunkReceived(text);
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private async Task StreamClaudeChatAsync(
            string model,
            List<OllamaChatMessage> messages,
            Action<string> onChunkReceived,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_config.ClaudeKey))
            {
                throw new InvalidOperationException("Anthropic Claude API Anahtarı girilmemiş. Lütfen 🔑 simgesinden Claude anahtarınızı tanımlayın.");
            }

            string systemPrompt = "";
            var claudeMsgs = new List<object>();

            foreach (var m in messages)
            {
                if (m.Role == "system")
                {
                    systemPrompt = m.Content;
                }
                else
                {
                    claudeMsgs.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
                }
            }

            var payload = new
            {
                model = model,
                system = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                messages = claudeMsgs,
                max_tokens = 4096,
                stream = true
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", _config.ClaudeKey.Trim());
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: ")) line = line.Substring(6).Trim();

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (type == "content_block_delta" && doc.RootElement.TryGetProperty("delta", out var deltaEl))
                        {
                            if (deltaEl.TryGetProperty("text", out var textEl))
                            {
                                var text = textEl.GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    onChunkReceived(text);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
