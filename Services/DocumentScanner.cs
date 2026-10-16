using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading.Tasks;
using System.Xml.Linq;
using EnterpriseAIApp.Models;
using Microsoft.Data.Sqlite;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace EnterpriseAIApp.Services
{
    public class LearnedConceptInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "process";
        public string RuleDescription { get; set; } = string.Empty;
    }

    public class LearnedRelationInfo
    {
        public string Source { get; set; } = string.Empty;
        public string Verb { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    public class ScanResult
    {
        public bool Success { get; set; } = true;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string AnalysisEngine { get; set; } = "Semantik NLP Analizcisi";
        public int ExtractedParagraphs { get; set; }
        public int CreatedBubblesCount { get; set; }
        public int CreatedLinksCount { get; set; }
        public List<LearnedConceptInfo> Concepts { get; set; } = new();
        public List<LearnedRelationInfo> Relations { get; set; } = new();
        public List<string> GeneratedQuestions { get; set; } = new();
        public List<string> GeneratedAnswers { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public string DocumentNodeId { get; set; } = string.Empty;
    }

    public class DocumentScanner
    {
        private readonly CompanyManager _companyManager;
        private readonly KnowledgeGraphManager _graphManager;
        private readonly TrainingManager _trainingManager;
        private readonly OllamaClient? _ollamaClient;
        private readonly GeminiClient? _geminiClient;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public DocumentScanner(
            CompanyManager companyManager,
            KnowledgeGraphManager graphManager,
            TrainingManager trainingManager,
            OllamaClient? ollamaClient = null,
            GeminiClient? geminiClient = null)
        {
            _companyManager = companyManager;
            _graphManager = graphManager;
            _trainingManager = trainingManager;
            _ollamaClient = ollamaClient;
            _geminiClient = geminiClient;
        }

        public async Task<ScanResult> ScanAndLearnDocumentAsync(string companyId, string sourceFilePath, string? selectedModel = null)
        {
            var fileName = Path.GetFileName(sourceFilePath);
            var fileInfo = new FileInfo(sourceFilePath);
            long fileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0;

            if (!fileInfo.Exists)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    ErrorMessage = "Belirtilen dosya bulunamadı."
                };
            }

            // 0. SQLite Veritabanı Dosyası mı? (Özel yapısal SQL tarayıcısı)
            if (IsSqliteDatabase(sourceFilePath))
            {
                return await ScanSqliteDatabaseAsync(companyId, sourceFilePath);
            }

            // 0.1 Ofis ve PDF Belgeleri için Özel Ayrıştırıcılar
            var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            string content = "";

            if (ext == ".docx")
            {
                content = ExtractTextFromDocx(sourceFilePath);
            }
            else if (ext == ".xlsx")
            {
                content = ExtractTextFromXlsx(sourceFilePath);
            }
            else if (ext == ".pdf")
            {
                content = ExtractTextFromPdf(sourceFilePath);
            }
            else
            {
                // 0.2 Standart İkili (Binary) Dosya Güvenlik Kontrolü (Null byte tespiti)
                try
                {
                    int checkLen = (int)Math.Min(1024, fileSizeBytes);
                    if (checkLen > 0)
                    {
                        byte[] headerBuf = new byte[checkLen];
                        using (var fs = File.OpenRead(sourceFilePath))
                        {
                            fs.ReadExactly(headerBuf, 0, checkLen);
                        }
                        if (headerBuf.Any(b => b == 0))
                        {
                            return new ScanResult
                            {
                                Success = false,
                                FileName = fileName,
                                FileSizeBytes = fileSizeBytes,
                                ErrorMessage = "İkili (binary) dosya doğrudan metin belgesi olarak taranamadı. Desteklenen veritabanı (.sqlite/.db), ofis (.docx/.xlsx), PDF (.pdf) veya metin/kod dosyaları yükleyiniz."
                            };
                        }
                    }
                }
                catch { }

                // Standart metni oku
                try
                {
                    content = File.ReadAllText(sourceFilePath);
                }
                catch (Exception ex)
                {
                    return new ScanResult
                    {
                        Success = false,
                        FileName = fileName,
                        ErrorMessage = $"Dosya okunamadı: {ex.Message}"
                    };
                }
            }

            var trimmedContent = content.Trim();

            // 2. Anlamsız / Boş / Rastgele Metin Kontrolü
            if (string.IsNullOrWhiteSpace(trimmedContent) || trimmedContent.Length < 35)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    ErrorMessage = "Belge çok kısa veya boş. Yapay zekanın kurumsal kural çıkarabilmesi için en az birkaç cümlelik açıklayıcı metin gereklidir."
                };
            }

            var words = trimmedContent.Split(new[] { ' ', '\r', '\n', '\t', '.', ',', ';', '!' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 6)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    ErrorMessage = "Belgede yeterli kelime veya cümle yapısı bulunamadı. Rastgele karakterler veya anlamsız metinler bilgi ağına eklenmez."
                };
            }

            int avgWordLen = words.Sum(w => w.Length) / Math.Max(1, words.Length);
            if (avgWordLen > 25)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    ErrorMessage = "Belge anlamsız veya şifrelenmiş karakter dizileri içeriyor. Doğal bir Türkçe/İngilizce metin veya kurumsal kod bulunamadı."
                };
            }

            var cleanDocTitle = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('-', ' ');
            var docNodeId = "doc_" + Guid.NewGuid().ToString("N")[..6];

            var newNodes = new List<KnowledgeNode>();
            var newLinks = new List<KnowledgeLink>();
            var learnedConcepts = new List<LearnedConceptInfo>();
            var learnedRelations = new List<LearnedRelationInfo>();
            var generatedQuestions = new List<string>();
            var generatedAnswers = new List<string>();
            string docSummary = "";

            // 3. Metni Derinlemesine Anlama (AI + NLP)
            bool usedAi = false;
            string engineUsed = "Semantik NLP Motoru";

            try
            {
                var aiResult = await ExtractWithAiAsync(trimmedContent, cleanDocTitle, docNodeId, fileName, selectedModel);
                if (aiResult != null && aiResult.Nodes.Count > 0)
                {
                    newNodes.AddRange(aiResult.Nodes);
                    newLinks.AddRange(aiResult.Links);
                    learnedConcepts.AddRange(aiResult.ConceptsInfo);
                    learnedRelations.AddRange(aiResult.RelationsInfo);
                    usedAi = true;
                    engineUsed = aiResult.EngineName;

                    if (aiResult.Questions.Count > 0 && aiResult.Questions.Count == aiResult.Answers.Count)
                    {
                        for (int i = 0; i < aiResult.Questions.Count; i++)
                        {
                            var q = aiResult.Questions[i];
                            var a = aiResult.Answers[i];
                            generatedQuestions.Add(q);
                            generatedAnswers.Add(a);
                            _trainingManager.AppendTrainingPair(companyId, q, a, $"Kaynak Doküman: {fileName} ({engineUsed})");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(aiResult.Summary))
                    {
                        docSummary = aiResult.Summary;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AI Document extraction error: {ex.Message}");
            }

            if (!usedAi || newNodes.Count == 0)
            {
                var nlpResult = ExtractWithSemanticNlp(trimmedContent, cleanDocTitle, docNodeId, fileName);
                if (nlpResult.Nodes.Count > 0)
                {
                    newNodes.AddRange(nlpResult.Nodes);
                    newLinks.AddRange(nlpResult.Links);
                    learnedConcepts.AddRange(nlpResult.ConceptsInfo);
                    learnedRelations.AddRange(nlpResult.RelationsInfo);
                    engineUsed = "Deterministik Semantik Analizci";
                }
            }

            // Eğer hiçbir anlamlı kural/kavram çıkarılamadıysa, sahte baloncuk oluşturma!
            if (newNodes.Count == 0)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    FileSizeBytes = fileSizeBytes,
                    ErrorMessage = "Belge tarandı ancak şirket mimarisine uygun belirgin bir kural, iş süreci veya kavram tespit edilemedi. Ağ kirliliğini önlemek için baloncuk oluşturulmadı."
                };
            }

            // Belgeyi şirketin izole documents klasörüne kopyala
            var destDir = _companyManager.GetCompanyDocumentsPath(companyId);
            var destPath = Path.Combine(destDir, fileName);
            File.Copy(sourceFilePath, destPath, true);

            // Ana Belge Baloncuğu
            var docNode = new KnowledgeNode
            {
                Id = docNodeId,
                Label = $"📄 {cleanDocTitle}",
                Group = "document",
                Size = 32,
                Description = $"Şirket kaynak belgesi: {fileName} ({trimmedContent.Length} karakter). Çıkarılan {newNodes.Count} kural bu belgeye bağlıdır.",
                ParentId = "core",
                DocSource = fileName,
                Tags = new() { "Doküman", cleanDocTitle }
            };
            newNodes.Insert(0, docNode);

            // Belgeyi AI Core'a bağla
            newLinks.Insert(0, new KnowledgeLink
            {
                Source = "core",
                Target = docNodeId,
                Label = "tanımlar",
                Type = "hierarchy",
                Strength = 1.0
            });

            // 4. Şirket LLM Hafızasına (Q&A Dataset) İş Kurallarını Kaydet (Eğer AI özel soru üretmediyse kural bazlı ekle)
            if (generatedQuestions.Count == 0)
            {
                foreach (var node in newNodes.Where(n => n.Id != docNodeId))
                {
                    var q = $"{cleanDocTitle} belgesine göre: '{node.Label}' kuralı ve şirket mimarisindeki yeri nedir?";
                    var a = node.Description;
                    generatedQuestions.Add(q);
                    generatedAnswers.Add(a);
                    _trainingManager.AppendTrainingPair(
                        companyId,
                        q,
                        a,
                        $"Kaynak Doküman: {fileName}"
                    );
                }
            }

            // 5. Baloncukları ve Doğru Anlamsal Bağlantıları Grafiğe Kaydet
            _graphManager.AddBulkKnowledge(companyId, newNodes, newLinks);

            var paragraphCount = trimmedContent.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Length;

            return new ScanResult
            {
                Success = true,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                AnalysisEngine = engineUsed,
                ExtractedParagraphs = paragraphCount > 0 ? paragraphCount : 1,
                CreatedBubblesCount = newNodes.Count,
                CreatedLinksCount = newLinks.Count,
                Concepts = learnedConcepts,
                Relations = learnedRelations,
                GeneratedQuestions = generatedQuestions,
                GeneratedAnswers = generatedAnswers,
                DocumentNodeId = docNodeId,
                Summary = !string.IsNullOrWhiteSpace(docSummary)
                    ? docSummary
                    : $"'{fileName}' belgesi başarıyla çözümlendi ({engineUsed}). {learnedConcepts.Count} adet iş kuralı/kavram ve {learnedRelations.Count} anlamsal sinaps kuruldu."
            };
        }

        public async Task<ScanResult> ScanAndLearnFolderAsync(string companyId, string folderPath, string? selectedModel = null)
        {
            var folderInfo = new DirectoryInfo(folderPath);
            if (!folderInfo.Exists)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = Path.GetFileName(folderPath),
                    ErrorMessage = "Belirtilen klasör bulunamadı."
                };
            }

            var folderName = folderInfo.Name;
            var cleanFolderTitle = folderName.Replace('_', ' ').Replace('-', ' ');

            // 1. Akıllı Dosya Taraması & Çöp/Önbellek Dizinlerini Atlama
            var ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "node_modules", ".next", ".git", ".vercel", "bin", "obj", "dist", "build",
                ".vscode", ".idea", "__pycache__", ".cache", "coverage", ".turbo", ".gradle",
                "packages", "vendor"
            };

            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".json", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".html", ".md", ".txt",
                ".env", ".local", ".yaml", ".yml", ".css", ".sql", ".py", ".cs"
            };

            var discoveredFiles = new List<FileInfo>();

            void TraverseDirectory(DirectoryInfo currentDir, int depth = 0)
            {
                if (depth > 6) return;
                try
                {
                    foreach (var dir in currentDir.EnumerateDirectories())
                    {
                        if (ignoredDirs.Contains(dir.Name) || dir.Name.StartsWith('.')) continue;
                        TraverseDirectory(dir, depth + 1);
                    }

                    foreach (var file in currentDir.EnumerateFiles())
                    {
                        if (file.Length > 2 * 1024 * 1024) continue; // 2MB üstü ikili/medya dosyalarını atla
                        var ext = file.Extension.ToLower();
                        if (textExtensions.Contains(ext) || file.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                        {
                            discoveredFiles.Add(file);
                        }
                    }
                }
                catch { }
            }

            TraverseDirectory(folderInfo);

            if (discoveredFiles.Count == 0)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = folderName,
                    ErrorMessage = "Klasörde taranabilecek metin, kod veya yapılandırma dosyası bulunamadı."
                };
            }

            // 2. Dosyaları Önem Derecesine Göre Grupla & Kanıtları Çıkar
            // A: Dil & İçerik Dosyaları (tr.json, messages, locales) -> En zengin ve doğrudan bilgi kaynağı!
            var contentFiles = discoveredFiles
                .Where(f => f.FullName.Contains("messages", StringComparison.OrdinalIgnoreCase) ||
                            f.FullName.Contains("locales", StringComparison.OrdinalIgnoreCase) ||
                            f.FullName.Contains("i18n", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("tr.json", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("en.json", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Contains("content", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name.Equals("tr.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            // B: Dokümantasyon (README.md, *.md)
            var docFiles = discoveredFiles
                .Where(f => f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // C: Proje Meta & Paket Yapısı (package.json, next.config)
            var metaFiles = discoveredFiles
                .Where(f => f.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Contains("config", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // D: Ana Sayfalar & Bileşenler (page.tsx, layout.tsx, index.html)
            var pageFiles = discoveredFiles
                .Where(f => f.Name.Equals("page.tsx", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("page.jsx", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("layout.tsx", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Kanıt Metnini Oluştur
            var evidenceBuilder = new StringBuilder();
            evidenceBuilder.AppendLine($"=== KLASÖR VE PROJE GENEL BİLGİLERİ ===");
            evidenceBuilder.AppendLine($"- Klasör Adı: {folderName}");
            evidenceBuilder.AppendLine($"- Toplam Taranan Dosya: {discoveredFiles.Count} adet");

            // Meta bilgisi (package.json var mı?)
            var packageJsonFile = metaFiles.FirstOrDefault(f => f.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase));
            if (packageJsonFile != null)
            {
                try
                {
                    var pkgRaw = File.ReadAllText(packageJsonFile.FullName);
                    using var doc = JsonDocument.Parse(pkgRaw);
                    var root = doc.RootElement;
                    var appName = root.TryGetProperty("name", out var n) ? n.GetString() : folderName;
                    evidenceBuilder.AppendLine($"- Proje/Uygulama Adı (package.json): {appName}");
                    if (root.TryGetProperty("dependencies", out var deps))
                    {
                        var depList = deps.EnumerateObject().Select(d => d.Name).Take(15);
                        evidenceBuilder.AppendLine($"- Web Teknolojileri / Kütüphaneler: {string.Join(", ", depList)}");
                    }
                }
                catch { }
            }

            // Dil & İçerik Kanıtları (Örn: tr.json, messages/tr.json)
            if (contentFiles.Count > 0)
            {
                evidenceBuilder.AppendLine($"\n=== WEB SİTESİ İÇERİKLERİ VE YERELLEŞTİRME METİNLERİ ===");
                foreach (var cf in contentFiles.Take(3))
                {
                    try
                    {
                        var relPath = Path.GetRelativePath(folderPath, cf.FullName);
                        var raw = File.ReadAllText(cf.FullName);
                        var snippet = raw.Length > 8000 ? raw[..8000] : raw;
                        evidenceBuilder.AppendLine($"--- Dosya: {relPath} ---");
                        evidenceBuilder.AppendLine(snippet);
                    }
                    catch { }
                }
            }

            // Dokümantasyon Kanıtları (README.md)
            if (docFiles.Count > 0)
            {
                evidenceBuilder.AppendLine($"\n=== PROJE DOKÜMANTASYONU (README) ===");
                foreach (var df in docFiles.Take(2))
                {
                    try
                    {
                        var relPath = Path.GetRelativePath(folderPath, df.FullName);
                        var raw = File.ReadAllText(df.FullName);
                        var snippet = raw.Length > 3500 ? raw[..3500] : raw;
                        evidenceBuilder.AppendLine($"--- Dosya: {relPath} ---");
                        evidenceBuilder.AppendLine(snippet);
                    }
                    catch { }
                }
            }

            // Ana Sayfa Bileşenleri (page.tsx)
            if (pageFiles.Count > 0)
            {
                evidenceBuilder.AppendLine($"\n=== ANA SAYFA VE KULLANICI ARAYÜZÜ BİLEŞENLERİ ===");
                foreach (var pf in pageFiles.Take(2))
                {
                    try
                    {
                        var relPath = Path.GetRelativePath(folderPath, pf.FullName);
                        var raw = File.ReadAllText(pf.FullName);
                        var snippet = raw.Length > 4000 ? raw[..4000] : raw;
                        evidenceBuilder.AppendLine($"--- Dosya: {relPath} ---");
                        evidenceBuilder.AppendLine(snippet);
                    }
                    catch { }
                }
            }

            // Diğer Bileşen Listesi
            var otherComponents = discoveredFiles
                .Where(f => f.FullName.Contains("components", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .Distinct()
                .Take(20)
                .ToList();
            if (otherComponents.Count > 0)
            {
                evidenceBuilder.AppendLine($"\n=== TESPİT EDİLEN DİĞER MODÜLLER / BİLEŞENLER ===");
                evidenceBuilder.AppendLine(string.Join(", ", otherComponents));
            }

            var evidenceText = evidenceBuilder.ToString();

            // 3. LLM ile Derinlemesine Analiz & Öğrenme
            var folderNodeId = "folder_" + Guid.NewGuid().ToString("N")[..6];
            var newNodes = new List<KnowledgeNode>();
            var newLinks = new List<KnowledgeLink>();
            var learnedConcepts = new List<LearnedConceptInfo>();
            var learnedRelations = new List<LearnedRelationInfo>();
            var generatedQuestions = new List<string>();
            var generatedAnswers = new List<string>();
            string projectSummary = "";
            string engineUsed = "Semantik Proje Analizcisi";

            var prompt = $@"Sen kurumsal bir yazılım mimarı ve web/mülk analistisin. Aşağıda bir proje/web sitesi klasöründen ('{folderName}') çıkarılan gerçek kod, yerelleştirme (i18n) ve içerik kanıtları yer almaktadır.
Bu proje bir web sitesi, yazılım projesi veya mülk/villa kiralama tanıtım sitesi olabilir.

Kanıtları derinlemesine incele ve tanıtılan mülkün/yazılımın GERÇEK bilgilerini öğren:
1. Kurumsal & Sektörel Özet (summary): Bu projenin/web sitesinin ne olduğunu, tanıtılan mekân/hizmet/ürün hakkında temel bilgileri (konum, mülk türü, kapasite, genel amaç vb.) anlatan 3-4 cümlelik net ve kapsamlı bir özet.
2. Temel Kavramlar & Mülk/Proje Özellikleri (concepts):
   - Tanıtılan mülkün/yazılımın her bir ana özelliğini belirle (örneğin: 'Kapasite & Yatak Odaları', 'Özel Kapalı Havuz', 'Akıllı Ev Teknolojisi', 'Konum & Mesafeler', 'Müstakil Bahçe & Mangal', 'Turizm İzin Belgesi', 'Rezervasyon & Fiyatlandırma', 'Klima & İklimlendirme').
   - Her kavram için kategori (code|process|data|security|rule) ve metindeki gerçek bilgilere dayalı detaylı açıklama.
3. Mantıksal Bağlantılar / Sinapslar (relations): Bu kavramlar arasındaki gerçek ilişkiler (source, target, label [içerir|yönetir|konumludur|sağlar|bağlıdır|korur|doğrular], type [dependency|process|dataflow|security]).
4. Şirket Eğitim Soru-Cevapları (qa_pairs): Kullanıcıların, müşterilerin veya çalışanların bu mülk/proje/web sitesi hakkında sorabileceği EN KRİTİK 6 ila 12 adet soru ve metinden çıkarılan kesin cevaplar (Örn: Kapasite nedir? Havuz kapalı mı açık mı? Nerede yer alıyor? İztuzu plajına ve havaalanına ne kadar mesafede? Turizm ruhsat numarası nedir? vb.).

KESİNLİKLE UYULMASI GEREKEN KURALLAR:
- ASLA başka projelerin şablon verilerini uydurma! SADECE yukarıda sunulan gerçek kod ve içerik kanıtlarına dayan.
- SADECE ve YALNIZCA geçerli JSON formatında şu şemayla yanıt ver:
{{
  ""summary"": ""..."",
  ""concepts"": [
    {{ ""name"": ""Kavram / Özellik Adı"", ""category"": ""code|process|data|security|rule"", ""description"": ""Gerçek açıklama ve kurallar"" }}
  ],
  ""relations"": [
    {{ ""source"": ""Kavram 1"", ""target"": ""Kavram 2"", ""label"": ""içerir|yönetir|konumludur|sağlar"", ""type"": ""dependency|process|dataflow"" }}
  ],
  ""qa_pairs"": [
    {{ ""question"": ""Soru metni?"", ""answer"": ""Detaylı kesin cevap."" }}
  ]
}}

PROJE KANITLARI:
{evidenceText}";

            string? responseText = null;
            bool isGeminiSelected = !string.IsNullOrWhiteSpace(selectedModel) && selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);
            bool isOllamaSelected = !string.IsNullOrWhiteSpace(selectedModel) && !selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);

            // 1. Kullanıcı Gemini seçtiyse
            if (isGeminiSelected && _geminiClient != null)
            {
                var modelId = selectedModel!.Substring("gemini:".Length);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    responseText = await _geminiClient.GenerateTextAsync(modelId, prompt, "Sen uzman bir web sitesi ve mülk analistisin. Sadece geçerli JSON üret.", cts.Token);
                    engineUsed = $"Google Gemini ({modelId})";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Gemini folder analysis error: {ex.Message}");
                }
            }
            // 2. Kullanıcı Ollama seçtiyse
            else if (isOllamaSelected && _ollamaClient != null && await _ollamaClient.CheckConnectionAsync())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    responseText = await _ollamaClient.GenerateTextAsync(selectedModel!, prompt, cts.Token);
                    engineUsed = $"Yerel Ollama ({selectedModel})";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Ollama folder analysis error: {ex.Message}");
                }
            }

            // 3. Fallback cascade
            if (string.IsNullOrWhiteSpace(responseText))
            {
                if (_geminiClient != null)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        responseText = await _geminiClient.GenerateTextAsync("gemini-3.7-flash", prompt, "Sen uzman bir web sitesi ve mülk analistisin. Sadece geçerli JSON üret.", cts.Token);
                        engineUsed = "Google Gemini 3.7 Flash (Otomatik Yedek)";
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(responseText) && _ollamaClient != null && await _ollamaClient.CheckConnectionAsync())
                {
                    try
                    {
                        var models = await _ollamaClient.GetAvailableModelsAsync();
                        var fallbackModel = models.FirstOrDefault(m => m.Contains("coder") || m.Contains("qwen") || m.Contains("gemma")) ?? models.FirstOrDefault() ?? "qwen2.5-coder:1.5b";
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        responseText = await _ollamaClient.GenerateTextAsync(fallbackModel, prompt, cts.Token);
                        engineUsed = $"Yerel Ollama ({fallbackModel}) (Otomatik Yedek)";
                    }
                    catch { }
                }
            }

            AiResponseFormat? aiData = null;
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    var jsonStart = responseText.IndexOf('{');
                    var jsonEnd = responseText.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonContent = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        aiData = JsonSerializer.Deserialize<AiResponseFormat>(jsonContent, _jsonOptions);
                    }
                }
                catch { }
            }

            // 4. Ana Klasör/Proje Düğümünü Oluştur
            var folderNode = new KnowledgeNode
            {
                Id = folderNodeId,
                Label = $"📁 {cleanFolderTitle}",
                Group = "document",
                Size = 34,
                Description = $"{folderName} proje klasörü ({discoveredFiles.Count} dosya). Web sitesi, içerikler ve mülk kuralları bu düğüme bağlıdır.",
                ParentId = "core",
                DocSource = folderName,
                Tags = new() { "Proje", "Web", cleanFolderTitle }
            };
            newNodes.Add(folderNode);

            newLinks.Add(new KnowledgeLink
            {
                Source = "core",
                Target = folderNodeId,
                Label = "barındırır",
                Type = "hierarchy",
                Strength = 1.0
            });

            var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (aiData?.Concepts != null && aiData.Concepts.Count > 0)
            {
                projectSummary = aiData.Summary ?? "";

                // Kavram düğümlerini bağla
                foreach (var c in aiData.Concepts.Take(15))
                {
                    if (string.IsNullOrWhiteSpace(c.Name)) continue;
                    var nodeId = "node_" + Guid.NewGuid().ToString("N")[..6];
                    var cat = NormalizeCategory(c.Category);
                    var desc = string.IsNullOrWhiteSpace(c.Description) ? $"{cleanFolderTitle} kapsamında tanımlanan {c.Name} özelliği." : c.Description;

                    var node = new KnowledgeNode
                    {
                        Id = nodeId,
                        Label = c.Name.Length > 30 ? c.Name[..30] : c.Name,
                        Group = cat,
                        Size = cat == "rule" ? 18 : 24,
                        Description = desc,
                        ParentId = folderNodeId,
                        DocSource = folderName,
                        Tags = new() { cat, cleanFolderTitle }
                    };

                    newNodes.Add(node);
                    learnedConcepts.Add(new LearnedConceptInfo
                    {
                        Name = node.Label,
                        Category = cat,
                        RuleDescription = desc
                    });

                    nameToId[c.Name.Trim()] = nodeId;

                    newLinks.Add(new KnowledgeLink
                    {
                        Source = folderNodeId,
                        Target = nodeId,
                        Label = "içerir",
                        Type = "hierarchy",
                        Strength = 0.9
                    });
                }

                // Sinapsları kur
                if (aiData.Relations != null)
                {
                    foreach (var rel in aiData.Relations)
                    {
                        if (string.IsNullOrWhiteSpace(rel.Source) || string.IsNullOrWhiteSpace(rel.Target)) continue;

                        var srcKey = nameToId.Keys.FirstOrDefault(k => k.Contains(rel.Source, StringComparison.OrdinalIgnoreCase) || rel.Source.Contains(k, StringComparison.OrdinalIgnoreCase));
                        var tgtKey = nameToId.Keys.FirstOrDefault(k => k.Contains(rel.Target, StringComparison.OrdinalIgnoreCase) || rel.Target.Contains(k, StringComparison.OrdinalIgnoreCase));

                        if (srcKey != null && tgtKey != null && srcKey != tgtKey)
                        {
                            var srcId = nameToId[srcKey];
                            var tgtId = nameToId[tgtKey];
                            var label = string.IsNullOrWhiteSpace(rel.Label) ? "ilişkilidir" : rel.Label;

                            newLinks.Add(new KnowledgeLink
                            {
                                Source = srcId,
                                Target = tgtId,
                                Label = label,
                                Type = string.IsNullOrWhiteSpace(rel.Type) ? "dependency" : rel.Type,
                                Strength = 0.8
                            });

                            learnedRelations.Add(new LearnedRelationInfo
                            {
                                Source = srcKey,
                                Verb = label,
                                Target = tgtKey
                            });
                        }
                    }
                }

                // Q&A Çiftlerini Şirket Hafızasına Ekle
                if (aiData.QaPairs != null && aiData.QaPairs.Count > 0)
                {
                    foreach (var pair in aiData.QaPairs)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Question) && !string.IsNullOrWhiteSpace(pair.Answer))
                        {
                            generatedQuestions.Add(pair.Question.Trim());
                            generatedAnswers.Add(pair.Answer.Trim());
                            _trainingManager.AppendTrainingPair(companyId, pair.Question.Trim(), pair.Answer.Trim(), $"Kaynak Proje: {folderName} ({engineUsed})");
                        }
                    }
                }
            }
            else
            {
                // Deterministik Çıkarım (Eğer LLM çevrimdışıysa)
                engineUsed = "Deterministik Web & Proje Ayrıştırıcısı";
                projectSummary = $"'{folderName}' projesi incelendi ({discoveredFiles.Count} dosya). Web bileşenleri ve yapılandırmalar başarıyla bilgi ağına entegre edildi.";

                // tr.json varsa doğrudan kavramları çıkar
                if (contentFiles.Count > 0)
                {
                    try
                    {
                        var primaryContent = File.ReadAllText(contentFiles[0].FullName);
                        using var doc = JsonDocument.Parse(primaryContent);
                        ExtractJsonConceptsRecursively(doc.RootElement, cleanFolderTitle, folderNodeId, folderName, newNodes, newLinks, learnedConcepts, nameToId, 0);
                    }
                    catch { }
                }

                // Sayfalar ve meta
                foreach (var page in pageFiles.Take(4))
                {
                    var pName = Path.GetFileNameWithoutExtension(page.Name);
                    var pId = "node_" + Guid.NewGuid().ToString("N")[..6];
                    newNodes.Add(new KnowledgeNode
                    {
                        Id = pId,
                        Label = $"Sayfa: {pName}",
                        Group = "code",
                        Size = 20,
                        Description = $"{folderName} projesinde tanımlanan {page.Name} bileşeni.",
                        ParentId = folderNodeId,
                        DocSource = folderName,
                        Tags = new() { "Web", cleanFolderTitle }
                    });
                    newLinks.Add(new KnowledgeLink
                    {
                        Source = folderNodeId,
                        Target = pId,
                        Label = "sayfasıdır",
                        Type = "hierarchy",
                        Strength = 0.85
                    });
                }
            }

            // Eğer Q&A üretilmediyse konseptlerden üret
            if (generatedQuestions.Count == 0)
            {
                foreach (var node in newNodes.Where(n => n.Id != folderNodeId).Take(10))
                {
                    var q = $"{cleanFolderTitle} projesine/villasına göre '{node.Label}' nedir ve ne işe yarar?";
                    var a = node.Description;
                    generatedQuestions.Add(q);
                    generatedAnswers.Add(a);
                    _trainingManager.AppendTrainingPair(companyId, q, a, $"Kaynak Proje: {folderName}");
                }
            }

            // 5. Bilgi Ağına Kaydet
            _graphManager.AddBulkKnowledge(companyId, newNodes, newLinks);

            long totalSizeBytes = discoveredFiles.Sum(f => f.Length);

            return new ScanResult
            {
                Success = true,
                FileName = folderName,
                FileSizeBytes = totalSizeBytes,
                AnalysisEngine = engineUsed,
                ExtractedParagraphs = discoveredFiles.Count,
                CreatedBubblesCount = newNodes.Count,
                CreatedLinksCount = newLinks.Count,
                Concepts = learnedConcepts,
                Relations = learnedRelations,
                GeneratedQuestions = generatedQuestions,
                GeneratedAnswers = generatedAnswers,
                DocumentNodeId = folderNodeId,
                Summary = !string.IsNullOrWhiteSpace(projectSummary)
                    ? projectSummary
                    : $"'{folderName}' projesi başarıyla çözümlendi ({engineUsed}). {discoveredFiles.Count} dosya taranarak {learnedConcepts.Count} adet iş kuralı/kavram ve {learnedRelations.Count} anlamsal sinaps kuruldu."
            };
        }

        private void ExtractJsonConceptsRecursively(
            JsonElement element,
            string docTitle,
            string parentNodeId,
            string fileName,
            List<KnowledgeNode> nodes,
            List<KnowledgeLink> links,
            List<LearnedConceptInfo> concepts,
            Dictionary<string, string> nameToId,
            int depth)
        {
            if (depth > 2 || nodes.Count > 15) return;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (nodes.Count > 15) break;

                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString() ?? "";
                        if (val.Length >= 10 && !val.StartsWith("http") && !val.StartsWith("/"))
                        {
                            var nodeId = "node_" + Guid.NewGuid().ToString("N")[..6];
                            var label = prop.Name;
                            if (label.Length > 25) label = label[..25];

                            var node = new KnowledgeNode
                            {
                                Id = nodeId,
                                Label = label,
                                Group = "process",
                                Size = 20,
                                Description = val.Length > 180 ? val[..180] + "..." : val,
                                ParentId = parentNodeId,
                                DocSource = fileName,
                                Tags = new() { "İçerik", docTitle }
                            };

                            nodes.Add(node);
                            nameToId[label] = nodeId;
                            concepts.Add(new LearnedConceptInfo
                            {
                                Name = label,
                                Category = "process",
                                RuleDescription = val
                            });

                            links.Add(new KnowledgeLink
                            {
                                Source = parentNodeId,
                                Target = nodeId,
                                Label = "tanımlar",
                                Type = "hierarchy",
                                Strength = 0.8
                            });
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        ExtractJsonConceptsRecursively(prop.Value, docTitle, parentNodeId, fileName, nodes, links, concepts, nameToId, depth + 1);
                    }
                }
            }
        }

        private class ExtractionContainer
        {
            public List<KnowledgeNode> Nodes { get; set; } = new();
            public List<KnowledgeLink> Links { get; set; } = new();
            public List<LearnedConceptInfo> ConceptsInfo { get; set; } = new();
            public List<LearnedRelationInfo> RelationsInfo { get; set; } = new();
            public List<string> Questions { get; set; } = new();
            public List<string> Answers { get; set; } = new();
            public string Summary { get; set; } = string.Empty;
            public string EngineName { get; set; } = string.Empty;
        }

        private class AiResponseFormat
        {
            public string? Summary { get; set; }
            public List<AiConceptItem>? Concepts { get; set; }
            public List<AiRelationItem>? Relations { get; set; }
            public List<AiQaItem>? QaPairs { get; set; }
        }

        private class AiQaItem
        {
            public string Question { get; set; } = string.Empty;
            public string Answer { get; set; } = string.Empty;
        }

        private class AiConceptItem
        {
            public string Name { get; set; } = string.Empty;
            public string Category { get; set; } = "process";
            public string Description { get; set; } = string.Empty;
        }

        private class AiRelationItem
        {
            public string Source { get; set; } = string.Empty;
            public string Target { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public string Type { get; set; } = "dependency";
        }

        private async Task<ExtractionContainer?> ExtractWithAiAsync(string content, string docTitle, string docNodeId, string fileName, string? selectedModel = null)
        {
            var snippet = content.Length > 12000 ? content[..12000] : content;

            var prompt = $@"Sen kurumsal bir yazılım ve mimari analiz uzmanısın. Aşağıdaki şirket belgesini derinlemesine analiz et ve öğren.
Bu belgeden:
1. Kurumsal Özet (summary): Belgenin ne amaçla hazırlandığını, şirketin hangi fonksiyonuna hizmet ettiğini anlatan 2-3 cümlelik net özet.
2. Temel Kavramlar & İş Kuralları (concepts): Her bir kavram/kural için ad, kategori (code, process, data, security, rule), ve detaylı açıklama.
3. Mantıksal Bağlantılar (relations): Bu kavramlar arasındaki GERÇEK sinapslar (source, target, label [depolar, korur, kullanır, tetikler, doğrular, izole eder, yönetir, üretir], type [dataflow, security, dependency, process]).
4. Şirket Eğitim Soru-Cevapları (qa_pairs): Bu belge hakkında kullanıcıların veya şirket çalışanlarının sorabileceği en kritik 3-6 adet soru ve belgeden çıkarılan kesin cevapları.

Rastgele bağlantı veya şablon kurma! SADECE metinde geçen gerçek bilgileri kullan.

Cevabını SADECE aşağıdaki JSON formatında saf JSON olarak ver:
{{
  ""summary"": ""Belgenin kurumsal ve sektörel özeti..."",
  ""concepts"": [
    {{ ""name"": ""Kavram Adı"", ""category"": ""code|process|data|security|rule"", ""description"": ""Açıklama ve kurallar"" }}
  ],
  ""relations"": [
    {{ ""source"": ""Kavram 1"", ""target"": ""Kavram 2"", ""label"": ""depolar|korur|kullanır|tetikler|doğrular|izole eder|yönetir"", ""type"": ""dataflow|security|dependency|process"" }}
  ],
  ""qa_pairs"": [
    {{ ""question"": ""Soru metni?"", ""answer"": ""Detaylı cevap metni."" }}
  ]
}}

İNCELENECEK METİN:
{snippet}";

            string? responseText = null;
            string engineName = "Bilinmeyen Yapay Zeka";

            bool isGeminiSelected = !string.IsNullOrWhiteSpace(selectedModel) && selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);
            bool isOllamaSelected = !string.IsNullOrWhiteSpace(selectedModel) && !selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);

            // 1. Kullanıcı Gemini seçtiyse
            if (isGeminiSelected && _geminiClient != null)
            {
                var modelId = selectedModel!.Substring("gemini:".Length);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    responseText = await _geminiClient.GenerateTextAsync(modelId, prompt, "Sen uzman bir kurumsal yazılım analisti ve doküman kavrama motorusun. Sadece geçerli JSON üret.", cts.Token);
                    engineName = $"Google Gemini ({modelId})";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Gemini model failed: {ex.Message}");
                }
            }
            // 2. Kullanıcı Ollama modeli seçtiyse (örn: qwen2.5-coder:1.5b)
            else if (isOllamaSelected && _ollamaClient != null && await _ollamaClient.CheckConnectionAsync())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    responseText = await _ollamaClient.GenerateTextAsync(selectedModel!, prompt, cts.Token);
                    engineName = $"Yerel Ollama ({selectedModel})";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Ollama model failed: {ex.Message}");
                }
            }

            // 3. Fallback cascade: Eğer seçilen model yanıt vermediyse veya model belirtilmediyse
            if (string.IsNullOrWhiteSpace(responseText))
            {
                if (_geminiClient != null)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        responseText = await _geminiClient.GenerateTextAsync("gemini-3.5-flash", prompt, "Sen uzman bir kurumsal yazılım analisti ve doküman kavrama motorusun. Sadece geçerli JSON üret.", cts.Token);
                        engineName = "Google Gemini (Otomatik Yedek)";
                    }
                    catch { }
                }

                if (string.IsNullOrWhiteSpace(responseText) && _ollamaClient != null && await _ollamaClient.CheckConnectionAsync())
                {
                    try
                    {
                        var models = await _ollamaClient.GetAvailableModelsAsync();
                        var fallbackModel = models.FirstOrDefault(m => m.Contains("coder") || m.Contains("qwen") || m.Contains("gemma")) ?? models.FirstOrDefault() ?? "qwen2.5-coder:1.5b";
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        responseText = await _ollamaClient.GenerateTextAsync(fallbackModel, prompt, cts.Token);
                        engineName = $"Yerel Ollama ({fallbackModel}) (Otomatik Yedek)";
                    }
                    catch { }
                }
            }

            if (string.IsNullOrWhiteSpace(responseText)) return null;

            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart) return null;

            var jsonContent = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var aiData = JsonSerializer.Deserialize<AiResponseFormat>(jsonContent, _jsonOptions);
            if (aiData == null || aiData.Concepts == null || aiData.Concepts.Count == 0) return null;

            var container = new ExtractionContainer
            {
                EngineName = engineName,
                Summary = aiData.Summary ?? string.Empty
            };

            // Q&A Çiftleri
            if (aiData.QaPairs != null)
            {
                foreach (var pair in aiData.QaPairs)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Question) && !string.IsNullOrWhiteSpace(pair.Answer))
                    {
                        container.Questions.Add(pair.Question.Trim());
                        container.Answers.Add(pair.Answer.Trim());
                    }
                }
            }

            var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in aiData.Concepts.Take(12))
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                var nodeId = "node_" + Guid.NewGuid().ToString("N")[..6];
                var cat = NormalizeCategory(c.Category);

                var desc = string.IsNullOrWhiteSpace(c.Description) ? $"{docTitle} kapsamında tanımlanan {c.Name} kuralı." : c.Description;

                var node = new KnowledgeNode
                {
                    Id = nodeId,
                    Label = c.Name.Length > 28 ? c.Name[..28] : c.Name,
                    Group = cat,
                    Size = cat == "rule" ? 18 : 24,
                    Description = desc,
                    ParentId = docNodeId,
                    DocSource = fileName,
                    Tags = new() { cat, docTitle }
                };

                container.Nodes.Add(node);
                container.ConceptsInfo.Add(new LearnedConceptInfo
                {
                    Name = node.Label,
                    Category = cat,
                    RuleDescription = desc
                });

                nameToId[c.Name.Trim()] = nodeId;

                // Belgeye bağla
                container.Links.Add(new KnowledgeLink
                {
                    Source = docNodeId,
                    Target = nodeId,
                    Label = "içerir",
                    Type = "hierarchy",
                    Strength = 0.9
                });
            }

            if (aiData.Relations != null)
            {
                foreach (var rel in aiData.Relations)
                {
                    if (string.IsNullOrWhiteSpace(rel.Source) || string.IsNullOrWhiteSpace(rel.Target)) continue;

                    var srcKey = nameToId.Keys.FirstOrDefault(k => k.Contains(rel.Source, StringComparison.OrdinalIgnoreCase) || rel.Source.Contains(k, StringComparison.OrdinalIgnoreCase));
                    var tgtKey = nameToId.Keys.FirstOrDefault(k => k.Contains(rel.Target, StringComparison.OrdinalIgnoreCase) || rel.Target.Contains(k, StringComparison.OrdinalIgnoreCase));

                    if (srcKey != null && tgtKey != null && srcKey != tgtKey)
                    {
                        var srcId = nameToId[srcKey];
                        var tgtId = nameToId[tgtKey];

                        var label = string.IsNullOrWhiteSpace(rel.Label) ? "ilişkilidir" : rel.Label;

                        container.Links.Add(new KnowledgeLink
                        {
                            Source = srcId,
                            Target = tgtId,
                            Label = label,
                            Type = string.IsNullOrWhiteSpace(rel.Type) ? "dependency" : rel.Type,
                            Strength = 0.8
                        });

                        container.RelationsInfo.Add(new LearnedRelationInfo
                        {
                            Source = srcKey,
                            Verb = label,
                            Target = tgtKey
                        });
                    }
                }
            }

            return container;
        }

        private ExtractionContainer ExtractWithSemanticNlp(string content, string docTitle, string docNodeId, string fileName)
        {
            var container = new ExtractionContainer();

            var rawSections = content.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (rawSections.Length <= 1)
            {
                rawSections = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            }

            var detectedConcepts = new List<(string Id, string Name, string Cat, string Desc, List<string> Keywords)>();

            int count = 0;
            foreach (var sec in rawSections)
            {
                var clean = sec.Trim();
                if (clean.Length < 25) continue;

                var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var firstLine = lines[0].Trim();

                firstLine = Regex.Replace(firstLine, @"^[\d\.\-\*\#\s\:\>]+", "").Trim();
                if (firstLine.Length > 28) firstLine = firstLine[..28];
                if (string.IsNullOrWhiteSpace(firstLine) || firstLine.Length < 3) continue;

                var cat = DetectCategory(firstLine + " " + clean);
                var nodeId = "node_" + Guid.NewGuid().ToString("N")[..6];
                var keywords = ExtractKeywords(clean);

                detectedConcepts.Add((nodeId, firstLine, cat, clean, keywords));
                count++;
                if (count >= 10) break;
            }

            // Anlamsız bir metinse (hiç kavram yakalanamadıysa) boş container dön
            if (detectedConcepts.Count == 0)
            {
                return container;
            }

            foreach (var item in detectedConcepts)
            {
                var desc = item.Desc.Length > 300 ? item.Desc[..300] + "..." : item.Desc;
                var node = new KnowledgeNode
                {
                    Id = item.Id,
                    Label = item.Name,
                    Group = item.Cat,
                    Size = item.Cat == "rule" ? 18 : 24,
                    Description = desc,
                    ParentId = docNodeId,
                    DocSource = fileName,
                    Tags = item.Keywords
                };
                container.Nodes.Add(node);
                container.ConceptsInfo.Add(new LearnedConceptInfo
                {
                    Name = item.Name,
                    Category = item.Cat,
                    RuleDescription = desc
                });

                container.Links.Add(new KnowledgeLink
                {
                    Source = docNodeId,
                    Target = item.Id,
                    Label = "içerir",
                    Type = "hierarchy",
                    Strength = 0.95
                });
            }

            // Kavramlar arası mantıksal ilişkileri çıkar
            for (int i = 0; i < detectedConcepts.Count; i++)
            {
                for (int j = i + 1; j < detectedConcepts.Count; j++)
                {
                    var a = detectedConcepts[i];
                    var b = detectedConcepts[j];

                    var rel = FindSemanticRelation(a.Name, a.Desc, b.Name, b.Desc);
                    if (rel != null)
                    {
                        container.Links.Add(new KnowledgeLink
                        {
                            Source = a.Id,
                            Target = b.Id,
                            Label = rel.Value.Label,
                            Type = rel.Value.Type,
                            Strength = 0.75
                        });

                        container.RelationsInfo.Add(new LearnedRelationInfo
                        {
                            Source = a.Name,
                            Verb = rel.Value.Label,
                            Target = b.Name
                        });
                    }
                }
            }

            return container;
        }

        private (string Label, string Type)? FindSemanticRelation(string nameA, string descA, string nameB, string descB)
        {
            string full = (descA + " " + descB).ToLowerInvariant();

            if (full.Contains("depolar") || full.Contains("kaydeder") || full.Contains("saklar") || full.Contains("veritaban"))
                return ("depolar", "dataflow");

            if (full.Contains("korur") || full.Contains("güvenlik") || full.Contains("şifre") || full.Contains("yetki"))
                return ("korur", "security");

            if (full.Contains("tetikler") || full.Contains("akış") || full.Contains("başlatır") || full.Contains("onay"))
                return ("tetikler", "process");

            if (full.Contains("kullanır") || full.Contains("çağırır") || full.Contains("tüketir") || full.Contains("servis"))
                return ("kullanır", "dependency");

            if (full.Contains("izole") || full.Contains("filtre") || full.Contains("tenant"))
                return ("izole eder", "security");

            return null;
        }

        private string DetectCategory(string text)
        {
            var lower = text.ToLowerInvariant();
            if (lower.Contains("kod") || lower.Contains("api") || lower.Contains("class") || lower.Contains("fonksiyon") || lower.Contains("c#") || lower.Contains("react"))
                return "code";
            if (lower.Contains("veritaban") || lower.Contains("sql") || lower.Contains("tablo") || lower.Contains("schema") || lower.Contains("veri"))
                return "data";
            if (lower.Contains("güvenlik") || lower.Contains("yetki") || lower.Contains("jwt") || lower.Contains("izolasyon") || lower.Contains("şifre"))
                return "security";
            if (lower.Contains("kural") || lower.Contains("yasak") || lower.Contains("zorunlu") || lower.Contains("standart"))
                return "rule";
            return "process";
        }

        private string NormalizeCategory(string? cat)
        {
            if (string.IsNullOrWhiteSpace(cat)) return "process";
            var c = cat.Trim().ToLowerInvariant();
            if (c.Contains("code") || c.Contains("kod")) return "code";
            if (c.Contains("data") || c.Contains("veri")) return "data";
            if (c.Contains("sec") || c.Contains("güven")) return "security";
            if (c.Contains("rule") || c.Contains("kural")) return "rule";
            return "process";
        }

        private List<string> ExtractKeywords(string text)
        {
            var words = Regex.Matches(text, @"\b[A-ZÇĞİÖŞÜa-zçğıöşü]{4,}\b")
                             .Cast<Match>()
                             .Select(m => m.Value)
                             .Where(w => !new[] { "için", "olan", "gibi", "kadar", "sonra", "ancak", "şekilde", "olarak" }.Contains(w.ToLowerInvariant()))
                             .Distinct()
                             .Take(4)
                             .ToList();
            return words;
        }

        public bool IsSqliteDatabase(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLower();
            if (ext is ".sqlite" or ".sqlite3" or ".db" or ".db3") return true;

            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Exists && fi.Length >= 16)
                {
                    using var fs = File.OpenRead(filePath);
                    byte[] header = new byte[16];
                    fs.ReadExactly(header, 0, 16);
                    var headerStr = Encoding.ASCII.GetString(header);
                    if (headerStr.StartsWith("SQLite format 3")) return true;
                }
            }
            catch { }
            return false;
        }

        public async Task<ScanResult> ScanSqliteDatabaseAsync(string companyId, string sqliteFilePath)
        {
            return await Task.Run(() =>
            {
                var fileName = Path.GetFileName(sqliteFilePath);
                var fi = new FileInfo(sqliteFilePath);
                var scanResult = new ScanResult
                {
                    Success = true,
                    FileName = fileName,
                    FileSizeBytes = fi.Exists ? fi.Length : 0,
                    AnalysisEngine = "SQLite İlişkisel Şema & Veri Motoru",
                    Concepts = new List<LearnedConceptInfo>(),
                    Relations = new List<LearnedRelationInfo>(),
                    GeneratedQuestions = new List<string>(),
                    GeneratedAnswers = new List<string>()
                };

                var company = _companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == companyId) ?? _companyManager.ActiveCompany;
                var companyName = company?.Name ?? "Şirket";

                // Veritabanı yolunu şirkete kaydet
                if (company != null)
                {
                    company.ApplicationDatabasePath = sqliteFilePath;
                    _companyManager.SaveCompanies();
                }

                var cleanDbName = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ');
                var dbNodeId = "db_" + Guid.NewGuid().ToString("N")[..6];
                scanResult.DocumentNodeId = dbNodeId;

                var newNodes = new List<KnowledgeNode>();
                var newLinks = new List<KnowledgeLink>();

                // 1. Veritabanı Hub Düğümü
                var hubNode = new KnowledgeNode
                {
                    Id = dbNodeId,
                    Label = $"📊 {cleanDbName} (SQLite)",
                    Group = "data",
                    Size = 34,
                    Description = $"{fileName} veritabanı ({fi.Length / 1024} KB). Şirketin finans, fatura, cari ve tahakkuk kayıtlarını depolar.",
                    ParentId = "core",
                    DocSource = fileName,
                    Tags = new List<string> { "Veritabanı", "SQLite", "Finans" }
                };
                newNodes.Add(hubNode);

                newLinks.Add(new KnowledgeLink
                {
                    Source = "core",
                    Target = dbNodeId,
                    Label = "yönetir",
                    Type = "dataflow",
                    Strength = 1.0
                });

                var tables = new List<(string TableName, int RowCount, List<string> Columns)>();

                try
                {
                    using var connection = new SqliteConnection($"Data Source={sqliteFilePath};Mode=ReadOnly");
                    connection.Open();

                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                    using var reader = cmd.ExecuteReader();
                    var tableNames = new List<string>();
                    while (reader.Read())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                    reader.Close();

                    foreach (var tbl in tableNames)
                    {
                        // Sütunları oku
                        var cols = new List<string>();
                        using var colCmd = connection.CreateCommand();
                        colCmd.CommandText = $"PRAGMA table_info(\"{tbl}\")";
                        using var colReader = colCmd.ExecuteReader();
                        while (colReader.Read())
                        {
                            cols.Add(colReader.GetString(1));
                        }
                        colReader.Close();

                        // Satır sayısını oku
                        int rowCount = 0;
                        using var cntCmd = connection.CreateCommand();
                        cntCmd.CommandText = $"SELECT COUNT(*) FROM \"{tbl}\"";
                        var cntObj = cntCmd.ExecuteScalar();
                        if (cntObj != null && int.TryParse(cntObj.ToString(), out var parsedCount))
                        {
                            rowCount = parsedCount;
                        }

                        tables.Add((tbl, rowCount, cols));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SQLite inspection error: {ex.Message}");
                }

                // Tabloları düğüm ve kural olarak ekle
                foreach (var (tableName, rowCount, cols) in tables)
                {
                    var friendlyName = tableName switch
                    {
                        "expenses" => "Faturalar ve Giderler Tablosu",
                        "incomes" => "Gelirler ve Hakedişler Tablosu",
                        "tax_payments" => "Vergi Ödemeleri Tablosu",
                        "tax_accruals" => "Vergi Tahakkukları Tablosu",
                        "tax_settings" => "Vergi Ayarları Tablosu",
                        _ => $"{tableName} Tablosu"
                    };

                    var icon = tableName switch
                    {
                        "expenses" => "📄",
                        "incomes" => "💰",
                        "tax_payments" or "tax_accruals" => "🏛️",
                        _ => "🗄️"
                    };

                    var colListStr = string.Join(", ", cols.Take(8));
                    var desc = $"{friendlyName} ({tableName}): Toplam {rowCount} kayıt. Sütunlar: {colListStr}.";

                    var tblNodeId = $"tbl_{tableName}";
                    var tblNode = new KnowledgeNode
                    {
                        Id = tblNodeId,
                        Label = $"{icon} {friendlyName}",
                        Group = "data",
                        Size = 24,
                        Description = desc,
                        ParentId = dbNodeId,
                        DocSource = fileName,
                        Tags = new List<string> { "Tablo", tableName, "SQL" }
                    };
                    newNodes.Add(tblNode);

                    newLinks.Add(new KnowledgeLink
                    {
                        Source = dbNodeId,
                        Target = tblNodeId,
                        Label = "içerir",
                        Type = "hierarchy",
                        Strength = 0.95
                    });

                    scanResult.Concepts.Add(new LearnedConceptInfo
                    {
                        Name = friendlyName,
                        Category = "data",
                        RuleDescription = desc
                    });

                    scanResult.Relations.Add(new LearnedRelationInfo
                    {
                        Source = cleanDbName,
                        Verb = "içerir",
                        Target = friendlyName
                    });
                }

                // Soru-Cevap eğitim çiftleri
                scanResult.GeneratedQuestions.Add($"{fileName} veritabanında hangi tablolar yer almaktadır?");
                scanResult.GeneratedAnswers.Add($"Veritabanında şu tablolar yer alır: {string.Join(", ", tables.Select(t => $"{t.TableName} ({t.RowCount} kayıt)"))}.");

                if (tables.Any(t => t.TableName == "expenses"))
                {
                    scanResult.GeneratedQuestions.Add("Faturalar ve gider kayıtları hangi tabloda tutulur?");
                    scanResult.GeneratedAnswers.Add("Faturalar, KDV oranları ve gider tutarları 'expenses' tablosunda tutulmaktadır.");
                }

                // Bilgi ağını kaydet
                var existingGraph = _graphManager.GetGraphData(company ?? new Company { Id = companyId, Name = companyName });
                var finalNodes = existingGraph.Nodes
                    .Where(n => !n.DocSource.Equals(fileName, StringComparison.OrdinalIgnoreCase) 
                             && !n.DocSource.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
                             && !n.Id.StartsWith("tbl_") 
                             && !n.Label.Contains("SQLite format 3")
                             && !n.Label.Contains('\0')
                             && !n.Description.Contains('\0'))
                    .ToList();
                finalNodes.AddRange(newNodes);

                var finalLinks = existingGraph.Links
                    .Where(l => !l.Source.Contains("SQLite format 3") 
                             && !l.Target.Contains("SQLite format 3")
                             && !l.Source.Contains('\0')
                             && !l.Target.Contains('\0'))
                    .ToList();
                finalLinks.AddRange(newLinks);

                _graphManager.SaveGraph(companyId, finalNodes, finalLinks);

                for (int i = 0; i < scanResult.GeneratedQuestions.Count; i++)
                {
                    _trainingManager.AppendTrainingPair(companyId, scanResult.GeneratedQuestions[i], scanResult.GeneratedAnswers[i]);
                }

                scanResult.CreatedBubblesCount = newNodes.Count;
                scanResult.CreatedLinksCount = newLinks.Count;
                scanResult.ExtractedParagraphs = tables.Count;
                scanResult.Summary = $"{fileName} veritabanı başarıyla incelendi. {tables.Count} tablo ({string.Join(", ", tables.Select(t => t.TableName))}) şirketin bilgi ağına ve hafızasına bağlandı.";

                return scanResult;
            });
        }

        #region Office (DOCX / XLSX) & PDF Text Extractors

        public static string ExtractTextFromDocx(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var archive = ZipFile.OpenRead(filePath);
                var entry = archive.GetEntry("word/document.xml");
                if (entry == null) return string.Empty;

                using var stream = entry.Open();
                var doc = XDocument.Load(stream);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

                foreach (var elem in doc.Descendants())
                {
                    if (elem.Name == w + "p")
                    {
                        var pText = string.Concat(elem.Descendants(w + "t").Select(t => t.Value));
                        if (!string.IsNullOrWhiteSpace(pText))
                        {
                            sb.AppendLine(pText.Trim());
                        }
                    }
                    else if (elem.Name == w + "tbl")
                    {
                        sb.AppendLine();
                        foreach (var row in elem.Descendants(w + "tr"))
                        {
                            var cells = row.Descendants(w + "tc")
                                           .Select(c => string.Concat(c.Descendants(w + "t").Select(t => t.Value)).Trim())
                                           .ToList();
                            if (cells.Count > 0)
                            {
                                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                            }
                        }
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[Word Dokümanı Okuma Hatası: {ex.Message}]");
            }
            return sb.ToString().Trim();
        }

        public static string ExtractTextFromXlsx(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var archive = ZipFile.OpenRead(filePath);

                // 1. Paylaşılan metinleri (shared strings) oku
                var sharedStrings = new List<string>();
                var ssEntry = archive.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                {
                    using var stream = ssEntry.Open();
                    var ssDoc = XDocument.Load(stream);
                    XNamespace ns = ssDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                    foreach (var si in ssDoc.Descendants(ns + "si"))
                    {
                        var text = string.Concat(si.Descendants(ns + "t").Select(t => t.Value));
                        sharedStrings.Add(text);
                    }
                }

                // 2. Çalışma sayfalarını tara
                var sheetEntries = archive.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name)
                    .ToList();

                foreach (var entry in sheetEntries)
                {
                    using var stream = entry.Open();
                    var sheetDoc = XDocument.Load(stream);
                    XNamespace ns = sheetDoc.Root?.Name.Namespace ?? "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                    var sheetName = Path.GetFileNameWithoutExtension(entry.Name);
                    sb.AppendLine($"\n### Excel Sayfası: {sheetName}\n");

                    var rows = sheetDoc.Descendants(ns + "row").ToList();
                    bool isHeader = true;

                    foreach (var row in rows)
                    {
                        var cellValues = new List<string>();
                        foreach (var c in row.Descendants(ns + "c"))
                        {
                            var t = c.Attribute("t")?.Value;
                            var v = c.Element(ns + "v")?.Value ?? "";
                            if (t == "s" && int.TryParse(v, out int sIdx) && sIdx >= 0 && sIdx < sharedStrings.Count)
                            {
                                cellValues.Add(sharedStrings[sIdx].Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim());
                            }
                            else
                            {
                                cellValues.Add(v.Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim());
                            }
                        }

                        if (cellValues.Count > 0 && cellValues.Any(val => !string.IsNullOrWhiteSpace(val)))
                        {
                            sb.AppendLine("| " + string.Join(" | ", cellValues) + " |");
                            if (isHeader)
                            {
                                sb.AppendLine("| " + string.Join(" | ", cellValues.Select(_ => "---")) + " |");
                                isHeader = false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[Excel Dosyası Okuma Hatası: {ex.Message}]");
            }
            return sb.ToString().Trim();
        }

        public static string ExtractTextFromPdf(string filePath)
        {
            var sb = new StringBuilder();
            try
            {
                using var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Import);
                sb.AppendLine($"[PDF Belgesi: {Path.GetFileName(filePath)} - Toplam {document.PageCount} Sayfa]");

                for (int i = 0; i < document.Pages.Count; i++)
                {
                    var page = document.Pages[i];
                    sb.AppendLine($"\n--- Sayfa {i + 1} ---");
                    var pageText = ExtractTextFromPdfPage(page);
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        sb.AppendLine(pageText);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[PDF Okuma Uyarısı: {ex.Message}]");
            }
            return sb.ToString().Trim();
        }

        private static string ExtractTextFromPdfPage(PdfPage page)
        {
            var sb = new StringBuilder();
            try
            {
                var content = PdfSharp.Pdf.Content.ContentReader.ReadContent(page);
                ExtractTextFromCObject(content, sb);
            }
            catch { }
            return sb.ToString().Trim();
        }

        private static void ExtractTextFromCObject(PdfSharp.Pdf.Content.Objects.CObject cObject, StringBuilder sb)
        {
            if (cObject is PdfSharp.Pdf.Content.Objects.COperator op)
            {
                if (op.OpCode.Name == "Tj" || op.OpCode.Name == "TJ")
                {
                    foreach (var operand in op.Operands)
                    {
                        ExtractTextFromCObject(operand, sb);
                    }
                    sb.Append(' ');
                }
            }
            else if (cObject is PdfSharp.Pdf.Content.Objects.CSequence seq)
            {
                foreach (var item in seq)
                {
                    ExtractTextFromCObject(item, sb);
                }
            }
            else if (cObject is PdfSharp.Pdf.Content.Objects.CString str)
            {
                sb.Append(str.Value);
            }
        }

        #endregion
    }
}