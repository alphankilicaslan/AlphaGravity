using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EnterpriseAIApp.Models
{
    public class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "qwen2.5-coder:7b";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("system")]
        public string? System { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = true;

        [JsonPropertyName("options")]
        public Dictionary<string, object>? Options { get; set; }
    }

    public class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "qwen2.5-coder:7b";

        [JsonPropertyName("messages")]
        public List<OllamaChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = true;
    }

    public class OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Images { get; set; }
    }

    public class OllamaChatChunk
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    public class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelDetails>? Models { get; set; }
    }

    public class OllamaModelDetails
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
