using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class GeminiClient
    {
        private readonly HttpClient _httpClient;
        private string _apiKey;
        private readonly string _configFilePath;

        public static readonly string DefaultApiKey = "";

        public GeminiClient()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "gemini_config.json");
            if (!File.Exists(configPath))
            {
                var curr = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 4 && curr != null; i++)
                {
                    var check = Path.Combine(curr.FullName, "Data", "gemini_config.json");
                    if (File.Exists(check))
                    {
                        configPath = check;
                        break;
                    }
                    curr = curr.Parent;
                }
            }
            _configFilePath = configPath;
            _apiKey = LoadApiKey();

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };
        }

        public string ApiKey
        {
            get => _apiKey;
            set
            {
                _apiKey = value?.Trim() ?? string.Empty;
                SaveApiKey(_apiKey);
            }
        }

        public string ConfigFilePath => _configFilePath;

        private string LoadApiKey()
        {
            try
            {
                // 1. Ortam Değişkeni Kontrolü (GEMINI_API_KEY)
                var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

                // 2. Yapılandırma Dosyası Kontrolü (Data/gemini_config.json)
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ApiKey", out var keyEl))
                    {
                        var key = keyEl.GetString();
                        if (!string.IsNullOrWhiteSpace(key)) return key.Trim();
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        private void SaveApiKey(string key)
        {
            try
            {
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var payload = JsonSerializer.Serialize(new { ApiKey = key?.Trim() ?? string.Empty }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, payload);
            }
            catch { }
        }

        public async Task<bool> CheckConnectionAsync()
        {
            return await CheckConnectionWithKeyAsync(_apiKey);
        }

        public async Task<bool> CheckConnectionWithKeyAsync(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={key.Trim()}";
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public List<GeminiModelInfo> GetAvailableModels()
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return new List<GeminiModelInfo>();

            return new List<GeminiModelInfo>
            {
                new("gemini-3.5-flash", "⚡ Gemini 3.5 Flash (En Hızlı & Akıllı)", "Google DeepMind'ın yüksek hızlı ve akıllı modeli (~1-2sn)."),
                new("gemini-3.5-flash-lite", "⚡ Gemini 3.5 Flash Lite (Işık Hızında, <1sn)", "Ultra düşük gecikmeli, anlık yanıt süresi sunan hafif model."),
                new("gemini-flash-latest", "🌟 Gemini Flash (Kararlı)", "Mali analiz ve ERP otomasyonu için optimize edilmiş kararlı multimodal model."),
                new("gemini-3-flash-preview", "✨ Gemini 3 Flash Preview", "Yeni nesil deneysel yüksek performanslı model.")
            };
        }

        public async Task StreamChatAsync(
            string modelName,
            List<OllamaChatMessage> messages,
            Action<string> onChunkReceived,
            CancellationToken cancellationToken = default,
            List<ChatAttachment>? attachments = null,
            List<GeminiFunctionDeclaration>? tools = null)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Google Gemini API anahtarı tanımlanmamış. Lütfen model menüsündeki 🔑 simgesinden bir API anahtarı girin veya yerel Ollama modelini seçin.");
            }

            // Format Gemini API payload
            var systemMessage = messages.FirstOrDefault(m => m.Role.Equals("system", StringComparison.OrdinalIgnoreCase))?.Content;
            var conversationMessages = messages.Where(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase)).ToList();

            var contents = new List<object>();
            for (int i = 0; i < conversationMessages.Count; i++)
            {
                var msg = conversationMessages[i];
                var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) || msg.Role.Equals("model", StringComparison.OrdinalIgnoreCase)
                    ? "model"
                    : "user";

                var partsList = new List<object>
                {
                    new { text = msg.Content }
                };

                // Son kullanıcı mesajına görsel ekleri dahil et
                if (i == conversationMessages.Count - 1 && role == "user" && attachments != null && attachments.Count > 0)
                {
                    foreach (var att in attachments)
                    {
                        if (!string.IsNullOrEmpty(att.Base64Data))
                        {
                            partsList.Add(new
                            {
                                inline_data = new
                                {
                                    mime_type = string.IsNullOrEmpty(att.MimeType) ? "image/png" : att.MimeType,
                                    data = att.Base64Data
                                }
                            });
                        }
                    }
                }

                contents.Add(new
                {
                    role = role,
                    parts = partsList.ToArray()
                });
            }

            // Optional Native Tools / Function Declarations
            object[]? toolPayload = null;
            object? toolConfigPayload = null;
            if (tools != null && tools.Count > 0)
            {
                var decls = tools
                    .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = t.Parameters ?? new { type = "OBJECT", properties = new { } }
                    }).ToArray();

                toolPayload = new object[]
                {
                    new
                    {
                        functionDeclarations = decls
                    }
                };

                toolConfigPayload = new
                {
                    functionCallingConfig = new
                    {
                        mode = "AUTO"
                    }
                };
            }

            object requestPayload;
            if (!string.IsNullOrWhiteSpace(systemMessage))
            {
                requestPayload = new
                {
                    system_instruction = new
                    {
                        parts = new object[]
                        {
                            new { text = systemMessage }
                        }
                    },
                    contents = contents,
                    tools = toolPayload,
                    toolConfig = toolConfigPayload,
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 8192
                    }
                };
            }
            else
            {
                requestPayload = new
                {
                    contents = contents,
                    tools = toolPayload,
                    toolConfig = toolConfigPayload,
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 8192
                    }
                };
            }

            var jsonBody = JsonSerializer.Serialize(requestPayload);
            var cleanTarget = (modelName ?? "").Replace("gemini:", "").Replace("models/", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanTarget) || cleanTarget.Contains("2.5") || cleanTarget.Contains("2.0"))
            {
                cleanTarget = "gemini-3.5-flash";
            }

            var modelsToTry = new List<string>
            {
                cleanTarget,
                "gemini-3.5-flash",
                "gemini-3.5-flash-lite",
                "gemini-flash-latest",
                "gemini-3-flash-preview"
            }.Distinct().ToList();

            bool streamSucceeded = false;
            Exception? lastEx = null;

            foreach (var targetModel in modelsToTry)
            {
                try
                {
                    var cleanModelName = targetModel.Replace("gemini:", "").Replace("models/", "").Trim();
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cleanModelName}:streamGenerateContent?alt=sse&key={_apiKey}";
                    
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                    };
                    
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        var errStr = await response.Content.ReadAsStringAsync(cancellationToken);
                        lastEx = new HttpRequestException($"Gemini API Hatası ({response.StatusCode}): {errStr}");
                        
                        // Model bulunamadıysa (404) veya aşırı yüklüyse (503/429), sıradaki stabil modeli dene
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                            response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            continue;
                        }
                        throw lastEx;
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    bool receivedAnyChunk = false;
                    while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (line == null) break;
                        var trimmedLine = line.Trim();
                        if (!trimmedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                        var jsonStr = trimmedLine.Substring(5).Trim();
                        if (string.IsNullOrWhiteSpace(jsonStr)) continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(jsonStr);
                            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                            {
                                var firstCandidate = candidates[0];
                                if (firstCandidate.TryGetProperty("content", out var candContent) &&
                                    candContent.TryGetProperty("parts", out var parts))
                                {
                                    foreach (var part in parts.EnumerateArray())
                                    {
                                        if (part.TryGetProperty("text", out var textEl))
                                        {
                                            var text = textEl.GetString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                onChunkReceived(text);
                                                receivedAnyChunk = true;
                                            }
                                        }
                                        else if (part.TryGetProperty("functionCall", out var funcCallEl))
                                        {
                                            var fName = funcCallEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : "";
                                            var fArgs = funcCallEl.TryGetProperty("args", out var aEl) ? aEl.GetRawText() : "{}";
                                            var callBlock = $"\n```tool_call\n{{\n  \"tool\": \"{fName}\",\n  \"parameters\": {fArgs}\n}}\n```\n";
                                            onChunkReceived(callBlock);
                                            receivedAnyChunk = true;
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // SSE ayrıştırma toleransı
                        }
                    }

                    if (receivedAnyChunk)
                    {
                        streamSucceeded = true;
                        break; // Başarılı canlı akış tamamlandı
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    // Eğer bağlantı koptuysa veya 503/404 ise sıradaki modeli dene
                }
            }

            if (!streamSucceeded)
            {
                throw lastEx ?? new Exception("Gemini API'den canlı akış yanıtı alınamadı.");
            }
        }

        public async Task<string> GenerateTextAsync(string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default)
        {
            return await GenerateTextAsync("gemini-3.5-flash", prompt, systemPrompt, cancellationToken);
        }

        public async Task<string> GenerateTextAsync(string targetModelName, string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Google Gemini API anahtarı tanımlanmamış. Lütfen model menüsündeki 🔑 simgesinden bir API anahtarı girin veya yerel Ollama modelini seçin.");
            }

            var contents = new List<object>
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            };

            object requestPayload = !string.IsNullOrWhiteSpace(systemPrompt)
                ? new
                {
                    system_instruction = new
                    {
                        parts = new object[]
                        {
                            new { text = systemPrompt }
                        }
                    },
                    contents = contents,
                    generationConfig = new
                    {
                        temperature = 0.3,
                        maxOutputTokens = 8192
                    }
                }
                : new
                {
                    contents = contents,
                    generationConfig = new
                    {
                        temperature = 0.3,
                        maxOutputTokens = 8192
                    }
                };

            var jsonBody = JsonSerializer.Serialize(requestPayload);
            var cleanTarget = (targetModelName ?? "").Replace("gemini:", "").Replace("models/", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanTarget) || cleanTarget.Contains("3.7") || cleanTarget.Contains("2.5") || cleanTarget.Contains("2.0"))
            {
                cleanTarget = "gemini-3.5-flash";
            }

            var modelsToTry = new List<string> { cleanTarget, "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-flash-latest", "gemini-3-flash-preview" }
                .Distinct()
                .ToList();

            Exception? lastEx = null;
            foreach (var targetModel in modelsToTry)
            {
                try
                {
                    var cleanModelName = targetModel.Replace("models/", "").Trim();
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cleanModelName}:generateContent?key={_apiKey}";
                    
                    using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using var response = await _httpClient.PostAsync(url, content, cancellationToken);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(responseJson);

                    if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var candContent) &&
                            candContent.TryGetProperty("parts", out var parts) &&
                            parts.GetArrayLength() > 0)
                        {
                            var sb = new StringBuilder();
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("text", out var textEl))
                                {
                                    sb.Append(textEl.GetString());
                                }
                            }
                            var text = sb.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw lastEx ?? new Exception("Gemini API'den geçerli yanıt alınamadı.");
        }

        public async Task<string> GenerateVisionAsync(
            string targetModelName,
            string prompt,
            byte[] imageBytes,
            string mimeType = "image/png",
            string? systemPrompt = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("Google Gemini API anahtarı tanımlanmamış.");
            }

            var base64Data = Convert.ToBase64String(imageBytes);
            var contents = new List<object>
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = base64Data
                            }
                        }
                    }
                }
            };

            object requestPayload = !string.IsNullOrWhiteSpace(systemPrompt)
                ? new
                {
                    system_instruction = new
                    {
                        parts = new object[]
                        {
                            new { text = systemPrompt }
                        }
                    },
                    contents = contents,
                    generationConfig = new
                    {
                        temperature = 0.4,
                        maxOutputTokens = 8192
                    }
                }
                : new
                {
                    contents = contents,
                    generationConfig = new
                    {
                        temperature = 0.4,
                        maxOutputTokens = 8192
                    }
                };

            var jsonBody = JsonSerializer.Serialize(requestPayload);
            var cleanTarget = (targetModelName ?? "").Replace("gemini:", "").Replace("models/", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanTarget) || cleanTarget.Contains("3.7") || cleanTarget.Contains("2.5") || cleanTarget.Contains("2.0"))
            {
                cleanTarget = "gemini-3.5-flash";
            }

            var modelsToTry = new List<string> { cleanTarget, "gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-flash-latest" }
                .Distinct()
                .ToList();

            Exception? lastEx = null;
            foreach (var targetModel in modelsToTry)
            {
                try
                {
                    var cleanModelName = targetModel.Replace("models/", "").Trim();
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cleanModelName}:generateContent?key={_apiKey}";

                    using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using var response = await _httpClient.PostAsync(url, content, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(responseJson);

                    if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var firstCandidate = candidates[0];
                        if (firstCandidate.TryGetProperty("content", out var candContent) &&
                            candContent.TryGetProperty("parts", out var parts) &&
                            parts.GetArrayLength() > 0)
                        {
                            var sb = new StringBuilder();
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("text", out var textEl))
                                {
                                    sb.Append(textEl.GetString());
                                }
                            }
                            var text = sb.ToString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                return text;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }
            }

            throw lastEx ?? new Exception("Gemini Vision API'den geçerli görsel yanıt alınamadı.");
        }
    }

    public record GeminiModelInfo(string Id, string DisplayName, string Description);

    public class GeminiFunctionDeclaration
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public object? Parameters { get; set; }
    }
}
