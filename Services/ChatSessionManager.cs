using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class ChatSessionManager
    {
        private readonly CompanyManager _companyManager;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true
        };

        public ChatSessionManager(CompanyManager companyManager)
        {
            _companyManager = companyManager;
        }

        public string GetSessionsDirectory(string companyId)
        {
            var companyDir = _companyManager.GetCompanyDirPath(companyId);
            var sessionsDir = Path.Combine(companyDir, "sessions");
            Directory.CreateDirectory(sessionsDir);
            return sessionsDir;
        }

        public List<ChatSession> GetSessions(string companyId)
        {
            var list = new List<ChatSession>();
            try
            {
                var dir = GetSessionsDirectory(companyId);
                var files = Directory.GetFiles(dir, "session_*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var session = JsonSerializer.Deserialize<ChatSession>(json, _jsonOptions);
                        if (session != null)
                        {
                            list.Add(session);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return list.OrderByDescending(s => s.UpdatedAt).ToList();
        }

        public ChatSession GetOrCreateActiveSession(string companyId)
        {
            var sessions = GetSessions(companyId);
            if (sessions.Count > 0)
            {
                return sessions[0];
            }

            return CreateNewSession(companyId, "Yeni Sohbet");
        }

        public ChatSession CreateNewSession(string companyId, string title = "Yeni Sohbet")
        {
            var session = new ChatSession
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                CompanyId = companyId,
                Title = title,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Messages = new List<ChatMessage>()
            };

            SaveSession(companyId, session);
            return session;
        }

        public void SaveSession(string companyId, ChatSession session)
        {
            try
            {
                var dir = GetSessionsDirectory(companyId);
                var filePath = Path.Combine(dir, $"session_{session.Id}.json");
                session.UpdatedAt = DateTime.Now;

                // Auto-generate a title if it's the default "Yeni Sohbet" and has a user message
                if ((string.IsNullOrWhiteSpace(session.Title) || session.Title == "Yeni Sohbet") && session.Messages.Count > 0)
                {
                    var firstUser = session.Messages.FirstOrDefault(m => m.IsUser);
                    if (firstUser != null && !string.IsNullOrWhiteSpace(firstUser.Content))
                    {
                        var clean = firstUser.Content.Trim();
                        var firstLine = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? clean;
                        session.Title = firstLine.Length > 28 ? firstLine[..28] + "..." : firstLine;
                    }
                }

                var json = JsonSerializer.Serialize(session, _jsonOptions);
                File.WriteAllText(filePath, json);
            }
            catch { }
        }

        public void DeleteSession(string companyId, string sessionId)
        {
            try
            {
                var dir = GetSessionsDirectory(companyId);
                var filePath = Path.Combine(dir, $"session_{sessionId}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch { }
        }

        public void RenameSession(string companyId, string sessionId, string newTitle)
        {
            try
            {
                var dir = GetSessionsDirectory(companyId);
                var filePath = Path.Combine(dir, $"session_{sessionId}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var session = JsonSerializer.Deserialize<ChatSession>(json, _jsonOptions);
                    if (session != null)
                    {
                        session.Title = newTitle.Trim();
                        session.UpdatedAt = DateTime.Now;
                        File.WriteAllText(filePath, JsonSerializer.Serialize(session, _jsonOptions));
                    }
                }
            }
            catch { }
        }
    }
}
