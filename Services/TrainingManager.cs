using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class TrainingManager
    {
        private readonly CompanyManager _companyManager;

        public TrainingManager(CompanyManager companyManager)
        {
            _companyManager = companyManager;
        }

        public void AppendTrainingPair(string companyId, string instruction, string output, string input = "")
        {
            if (string.IsNullOrWhiteSpace(companyId) || string.IsNullOrWhiteSpace(instruction) || string.IsNullOrWhiteSpace(output))
                return;

            var trimmedOutput = output.Trim();
            var trimmedInst = instruction.Trim();

            // 1. Asla hata mesajlarını hafızaya kaydetme
            if (trimmedOutput.StartsWith("[Hata]", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.StartsWith("Hata:", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("Exception:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 2. Asla prompt sızıntılarını, meta açıklamaları veya sistem kuralı tekrarlarını kaydetme
            if (trimmedOutput.StartsWith("Açıklama:\n\n1. **Soru Verilir**", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("Soru Verilir**:", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("Belge ve Tablo Verileri**:", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("araştırmayı başlattım", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("terminal kodlarını", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("NİHAİ Cevabını Ver", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("[KRİTİK", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.Contains("[SİSTEM", StringComparison.OrdinalIgnoreCase) ||
                trimmedOutput.StartsWith("```tool_call", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var datasetFile = _companyManager.GetCompanyDatasetPath(companyId);
            var entry = new
            {
                instruction = trimmedInst,
                input = input,
                output = trimmedOutput,
                company_id = companyId,
                timestamp = DateTime.UtcNow.ToString("o")
            };

            var jsonLine = JsonSerializer.Serialize(entry) + Environment.NewLine;
            File.AppendAllText(datasetFile, jsonLine);
        }

        public int GetDatasetSampleCount(string companyId)
        {
            var datasetFile = _companyManager.GetCompanyDatasetPath(companyId);
            if (!File.Exists(datasetFile)) return 0;

            try
            {
                var lines = File.ReadAllLines(datasetFile);
                return lines.Length;
            }
            catch
            {
                return 0;
            }
        }

        public string GenerateOllamaModelfile(Company company, string baseModel = "qwen2.5-coder:7b")
        {
            var modelfile = $@"FROM {baseModel}

# Sirkete Ozel Sistem Promptu & Guvenliksizlestirilmis Kodlama Modu
SYSTEM """"""
{company.GetFullSystemPrompt()}
""""""

# Performans Parametreleri
PARAMETER temperature 0.2
PARAMETER top_p 0.95
PARAMETER num_ctx 32768
";
            var companyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Companies", company.Id);
            var modelfilePath = Path.Combine(companyDir, "Modelfile");
            File.WriteAllText(modelfilePath, modelfile);
            return modelfilePath;
        }

        public async Task<string> BuildCompanyModelAsync(Company company, string customModelName, string baseModel = "qwen2.5-coder:7b")
        {
            var modelfilePath = GenerateOllamaModelfile(company, baseModel);

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ollama",
                        Arguments = $"create {customModelName} -f \"{modelfilePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var stdout = proc.StandardOutput.ReadToEnd();
                        var stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit();

                        if (proc.ExitCode == 0)
                        {
                            return $"Basarili! Sirket Modeli olusturuldu: {customModelName}\n{stdout}";
                        }
                        return $"Ollama hata verdi:\n{stderr}";
                    }
                    return "Ollama process baslatilamadi.";
                }
                catch (Exception ex)
                {
                    return $"Hata: {ex.Message}";
                }
            });
        }

        public string GetRelevantMemorySnippets(string companyId, string userQuery, int maxSnippets = 4)
        {
            if (string.IsNullOrWhiteSpace(userQuery)) return string.Empty;

            try
            {
                var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ve", "veya", "ile", "bir", "bu", "şu", "o", "da", "de", "için", "mi", "mı", "mu", "mü",
                    "var", "yok", "ne", "nasıl", "nedir", "kim", "nerede", "hangi", "kaç", "bakar", "mısın",
                    "eder", "misin", "hakkında", "bilgi", "ver", "yapar", "mısın", "lütfen", "neler",
                    "bak", "baksana", "bakmak", "listele", "listelemek", "göster", "getir", "bul", "bulmak",
                    "yap", "et", "al", "yaz", "oku", "söyle", "bunu", "şunu", "buna", "şuna", "sonra",
                    "önce", "şimdi", "bugün", "gün", "şey", "şeyler", "ses", "en", "uygun", "uygunlarını",
                    "misiniz", "hadi", "tamam", "oldu", "peki", "evet", "hayır", "bana", "bize"
                };

                var keywords = Regex.Matches(userQuery, @"[\p{L}\p{Nd}\-_]{3,}")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !stopWords.Contains(w))
                    .Distinct()
                    .ToList();

                if (keywords.Count == 0) return string.Empty;

                var candidateSnippets = new List<(string Snippet, double Score)>();

                // 1. Check training_pairs.jsonl
                var datasetPath = _companyManager.GetCompanyDatasetPath(companyId);
                if (File.Exists(datasetPath))
                {
                    var lines = File.ReadAllLines(datasetPath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            var instruction = root.TryGetProperty("instruction", out var iEl) ? iEl.GetString() ?? "" : "";
                            var output = root.TryGetProperty("output", out var oEl) ? oEl.GetString() ?? "" : "";

                            if (string.IsNullOrWhiteSpace(instruction) && string.IsNullOrWhiteSpace(output)) continue;

                            // Hatalı, ham tool_call veya sızdırılmış meta prompt içeren kayıtları RAG hafızasına ASLA katma
                            if (output.StartsWith("```tool_call", StringComparison.OrdinalIgnoreCase) ||
                                output.StartsWith("[Hata]", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Soru Verilir**:", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("Belge ve Tablo Verileri**:", StringComparison.OrdinalIgnoreCase) ||
                                output.Contains("terminal kodlarını", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            double score = 0;
                            var combinedText = (instruction + " " + output).ToLowerInvariant();

                            foreach (var kw in keywords)
                            {
                                if (instruction.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 4.0;
                                else if (combinedText.Contains(kw)) score += 1.0;
                            }

                            // Sadece güçlü çoklu anahtar kelime eşleşmesinde (skor >= 6.0) dahil et
                            if (score >= 6.0)
                            {
                                var cleanOutput = output.Length > 280 ? output[..280] + "..." : output;
                                candidateSnippets.Add(($"📌 [Örnek Soru]: {instruction} ➔ [Cevap Referansı]: {cleanOutput}", score));
                            }
                        }
                        catch { }
                    }
                }

                // 2. Check knowledge_nodes.json
                var companyDir = _companyManager.GetCompanyDirPath(companyId);
                var nodesPath = Path.Combine(companyDir, "dataset", "knowledge_nodes.json");
                if (File.Exists(nodesPath))
                {
                    try
                    {
                        var json = File.ReadAllText(nodesPath);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var nodeEl in doc.RootElement.EnumerateArray())
                            {
                                var label = nodeEl.TryGetProperty("label", out var lEl) ? lEl.GetString() ?? "" : "";
                                var desc = nodeEl.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "";
                                var docSrc = nodeEl.TryGetProperty("docSource", out var sEl) ? sEl.GetString() ?? "" : "";

                                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(desc)) continue;

                                double score = 0;
                                var combined = (label + " " + desc + " " + docSrc).ToLowerInvariant();
                                foreach (var kw in keywords)
                                {
                                    if (label.Contains(kw, StringComparison.OrdinalIgnoreCase)) score += 4.0;
                                    else if (combined.Contains(kw)) score += 1.2;
                                }

                                if (score > 0)
                                {
                                    var cleanDesc = desc.Length > 280 ? desc[..280] + "..." : desc;
                                    candidateSnippets.Add(($"🌐 [Bilgi Düğümü]: {label} ({docSrc}) ➔ {cleanDesc}", score));
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (candidateSnippets.Count == 0) return string.Empty;

                var topSnippets = candidateSnippets
                    .OrderByDescending(s => s.Score)
                    .Take(maxSnippets)
                    .Select(s => s.Snippet)
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine("=== 🧠 KURUMSAL HAFIZA & DOKÜMAN BULGULARI (Kullanıcının Sorusuyla İlgili Bilgiler) ===");
                sb.AppendLine("Aşağıdaki bilgiler şirketin taranmış resmi dokümanlarından, bilgi ağından ve hafızasından dinamik olarak getirilmiştir. İlgiliyse yanıtında doğrudan referans al:");
                foreach (var snip in topSnippets)
                {
                    sb.AppendLine($"- {snip}");
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
