using System;
using System.Collections.Generic;

namespace EnterpriseAIApp.Models
{
    public class ChatMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Role { get; set; } = "user"; // "user", "assistant", "system", "tool"
        public string Content { get; set; } = string.Empty;
        public string CompanyId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public List<ToolExecution> ToolCalls { get; set; } = new();
        public List<ChatAttachment> Attachments { get; set; } = new();
        public bool IsStreaming { get; set; } = false;

        public bool IsUser => Role == "user";
        public bool IsAssistant => Role == "assistant";
    }

    public class ChatAttachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string FileType { get; set; } = "generic"; // "image", "pdf", "word", "excel", "text"
        public string MimeType { get; set; } = "application/octet-stream";
        public string? Base64Data { get; set; }
        public long SizeBytes { get; set; }
        public string? ExtractedText { get; set; }
    }

    public class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string CompanyId { get; set; } = string.Empty;
        public string Title { get; set; } = "Yeni Sohbet";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public class ToolExecution
    {
        public string ToolName { get; set; } = string.Empty;
        public string Parameters { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public bool IsSuccess { get; set; } = true;
        public DateTime ExecutedAt { get; set; } = DateTime.Now;
    }
}
