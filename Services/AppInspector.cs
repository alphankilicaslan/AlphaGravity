using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnterpriseAIApp.Models;
using Microsoft.Data.Sqlite;

namespace EnterpriseAIApp.Services
{
    public class AppControlInfo
    {
        public string Name { get; set; } = string.Empty;
        public string ControlType { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string ActionDescription { get; set; } = string.Empty;
        public string ParentScreen { get; set; } = string.Empty;
    }

    public class AppScreenInfo
    {
        public string ScreenName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public List<AppControlInfo> Controls { get; set; } = new();
        public List<string> Operations { get; set; } = new();
    }

    public class AppToolItem
    {
        public string Name { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TargetScreen { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public List<string> RequiredParameters { get; set; } = new();
    }

    public class AppBinarySymbols
    {
        public List<string> Operations { get; set; } = new();
        public List<string> Controls { get; set; } = new();
        public List<string> Libraries { get; set; } = new();
        public List<string> DomainKeywords { get; set; } = new();
        public HashSet<string> RawStrings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AppRawEvidence
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string Architecture { get; set; } = "x64";
        public string Subsystem { get; set; } = "GUI";
        public bool IsDotNet { get; set; }

        // FileVersionInfo
        public string ProductName { get; set; } = string.Empty;
        public string FileDescription { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public string Comments { get; set; } = string.Empty;
        public string LegalCopyright { get; set; } = string.Empty;
        public string OriginalFilename { get; set; } = string.Empty;
        public string FileVersion { get; set; } = string.Empty;
        public string ProductVersion { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;

        // Native / .NET Imports & Exports
        public List<string> ImportedDlls { get; set; } = new();
        public List<string> ImportedFunctions { get; set; } = new();
        public List<string> ExportedSymbols { get; set; } = new();
        public List<string> DotNetDependencies { get; set; } = new();
        public List<AppScreenInfo> DotNetScreens { get; set; } = new();

        // Electron / Node / Web Assets
        public bool IsElectronApp { get; set; }
        public string? PackageJsonRaw { get; set; }
        public string? PackageName { get; set; }
        public string? PackageProductName { get; set; }
        public string? PackageDescription { get; set; }
        public string? PackageVersion { get; set; }
        public string? PackageAuthor { get; set; }
        public string? PackageHomepage { get; set; }
        public string? PackageMain { get; set; }
        public List<string> PackageDependencies { get; set; } = new();
        public Dictionary<string, string> PackageScripts { get; set; } = new();
        public List<(string Command, string Title)> ContributesCommands { get; set; } = new();
        public List<string> AsarFilesList { get; set; } = new();
        public string? UpdaterUrl { get; set; }

        // Python / PyInstaller & Script Assets
        public bool IsPythonPyInstallerApp { get; set; }
        public string? PythonMainScriptPath { get; set; }
        public string? DiscoveredDatabasePath { get; set; }
        public List<AppScreenInfo> DiscoveredScriptScreens { get; set; } = new();
        public List<string> DiscoveredDatabaseTables { get; set; } = new();

        // Extracted Distinct Identifiers
        public List<string> DiscoveredOperations { get; set; } = new();
        public List<string> DiscoveredControls { get; set; } = new();
        public List<string> DiscoveredKeywords { get; set; } = new();

        // Live UI Automation & Dynamic Execution Evidence
        public bool WasLiveInspected { get; set; }
        public string LiveMainWindowTitle { get; set; } = string.Empty;
        public List<string> LiveWindowTitles { get; set; } = new();
        public List<AppControlInfo> LiveDiscoveredControls { get; set; } = new();
        public List<string> LiveDiscoveredTexts { get; set; } = new();
        public List<string> LiveDiscoveredMenus { get; set; } = new();
    }

    public class AppInspector
    {
        private readonly CompanyManager _companyManager;
        private readonly KnowledgeGraphManager _graphManager;
        private readonly TrainingManager _trainingManager;
        private readonly OllamaClient? _ollamaClient;
        private readonly GeminiClient? _geminiClient;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true
        };

        #region Win32 P/Invoke for UI Automation & Window Inspection
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }
        #endregion

        public AppInspector(
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

        public async Task<ScanResult> InspectAndLearnApplicationAsync(string companyId, string exeFilePath, bool allowLiveLaunch = true, string? selectedModel = null)
        {
            var fileName = Path.GetFileName(exeFilePath);
            var appName = Path.GetFileNameWithoutExtension(exeFilePath);
            var fileInfo = new FileInfo(exeFilePath);

            if (!fileInfo.Exists)
            {
                return new ScanResult
                {
                    Success = false,
                    FileName = fileName,
                    ErrorMessage = "Belirtilen uygulama (.exe) dosyası bulunamadı."
                };
            }

            // 1. Gerçek Dosya ve PE Başlık Kanıtlarını Topla
            var evidence = ExtractRawFileEvidence(exeFilePath);

            // 2. Electron / Chromium / Node / Asar Dizin Varlıklarını İncele
            InspectDirectoryAssets(exeFilePath, evidence);

            // 3. Canlı Uygulama & Windows UI Automation İncelemesi (Dinamik Çalıştırma & Canlı Pencere Taraması)
            await InspectLiveRunningOrLaunchedAppAsync(exeFilePath, evidence, allowLiveLaunch);

            // 4. Güvenli & Akıllı İkili Sembol ve Dize Taraması (Bellek sızıntısız streaming)
            var symbols = ExtractBinarySymbolsSafely(exeFilePath);
            foreach (var op in symbols.Operations)
            {
                if (!evidence.DiscoveredOperations.Contains(op))
                    evidence.DiscoveredOperations.Add(op);
            }
            foreach (var ctrl in symbols.Controls)
            {
                if (!evidence.DiscoveredControls.Contains(ctrl))
                    evidence.DiscoveredControls.Add(ctrl);
            }
            foreach (var kw in symbols.DomainKeywords)
            {
                if (!evidence.DiscoveredKeywords.Contains(kw))
                    evidence.DiscoveredKeywords.Add(kw);
            }
            foreach (var lib in symbols.Libraries)
            {
                if (!evidence.ImportedDlls.Contains(lib))
                    evidence.ImportedDlls.Add(lib);
            }

            // Canlı keşfedilen kontrolleri ve menüleri de kanıtlara ekle
            foreach (var ctrl in evidence.LiveDiscoveredControls)
            {
                if (!evidence.DiscoveredControls.Contains(ctrl.Name))
                    evidence.DiscoveredControls.Add(ctrl.Name);
            }
            foreach (var menu in evidence.LiveDiscoveredMenus)
            {
                if (!evidence.DiscoveredOperations.Contains(menu))
                    evidence.DiscoveredOperations.Add(menu);
            }
            foreach (var txt in evidence.LiveDiscoveredTexts)
            {
                if (!evidence.DiscoveredKeywords.Contains(txt.ToLower()))
                    evidence.DiscoveredKeywords.Add(txt.ToLower());
            }

            // 5. Uygulamanın Gerçek Başlığı ve Açıklamasını Kanıtlardan Çıkar
            string productTitle = !string.IsNullOrWhiteSpace(evidence.PackageProductName)
                ? evidence.PackageProductName
                : !string.IsNullOrWhiteSpace(evidence.ProductName)
                    ? evidence.ProductName
                    : appName;

            if (productTitle.Contains("Windows®", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(evidence.OriginalFilename))
            {
                productTitle = Path.GetFileNameWithoutExtension(evidence.OriginalFilename);
            }

            // Canlı pencere başlığından daha net ürün adı yakalanabildiyse kullan
            if (!string.IsNullOrWhiteSpace(evidence.LiveMainWindowTitle) &&
                (productTitle.Equals(appName, StringComparison.OrdinalIgnoreCase) || productTitle.Contains("Windows®", StringComparison.OrdinalIgnoreCase) || productTitle.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                string cleanLiveTitle = evidence.LiveMainWindowTitle;
                if (cleanLiveTitle.Contains(" - "))
                {
                    cleanLiveTitle = cleanLiveTitle.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries).Last().Trim();
                }
                if (cleanLiveTitle.Length >= 2 && !cleanLiveTitle.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    productTitle = cleanLiveTitle;
                }
            }

            string description = !string.IsNullOrWhiteSpace(evidence.PackageDescription)
                ? evidence.PackageDescription
                : !string.IsNullOrWhiteSpace(evidence.FileDescription)
                    ? evidence.FileDescription
                    : $"{productTitle} masaüstü uygulaması.";

            string version = !string.IsNullOrWhiteSpace(evidence.PackageVersion)
                ? evidence.PackageVersion
                : !string.IsNullOrWhiteSpace(evidence.ProductVersion)
                    ? evidence.ProductVersion
                    : !string.IsNullOrWhiteSpace(evidence.FileVersion)
                        ? evidence.FileVersion
                        : "1.0.0";

            // 5. Ekranlar ve Araçların Sentezi:
            // Öncelik 1: PE Reflection ile .NET formları bulunduysa doğrudan kullan
            // Öncelik 2: Gemini AI veya Yerel Ollama ile kanıtlardan öğren
            // Öncelik 3: Kanıta dayalı deterministik sentez (KESİNLİKLE ŞABLONSUZ)
            List<AppScreenInfo> screens = new();
            List<AppToolItem> tools = new();
            string domain = DetectDomainFromEvidence(evidence);
            string analysisEngineName = "Kanıta Dayalı İkili & Semantik Ayrıştırma Motoru";

            if (evidence.IsDotNet && evidence.DotNetScreens.Count > 0)
            {
                screens = evidence.DotNetScreens;
                tools = GenerateToolsFromScreens(appName, productTitle, screens);
                analysisEngineName = ".NET Reflection & Yönetilen UI Ayrıştırıcısı";
            }
            else if (evidence.IsPythonPyInstallerApp && evidence.DiscoveredScriptScreens.Count > 0)
            {
                var (detScreens, detTools, detDomain) = SynthesizeFromEvidenceDeterministically(evidence, productTitle, description);
                screens = detScreens;
                tools = detTools;
                domain = detDomain;
                analysisEngineName = "Python / PyInstaller ve İlişkili Betik Ayrıştırıcısı";
            }
            else
            {
                // AI ile öğrenmeyi dene (Kanıtları besle, şablon veriyi yasakla)
                var (aiScreens, aiTools, aiDomain, aiDesc, engineUsed) = await SynthesizeWithAIAsync(evidence, productTitle, description, domain, selectedModel);
                if (aiScreens.Count > 0 && aiTools.Count > 0)
                {
                    screens = aiScreens;
                    tools = aiTools;
                    if (!string.IsNullOrWhiteSpace(aiDomain)) domain = aiDomain;
                    if (!string.IsNullOrWhiteSpace(aiDesc)) description = aiDesc;
                    analysisEngineName = engineUsed;
                }
                else
                {
                    // AI çevrimdışı ise tamamen çıkarılan kanıtlardan deterministik sentez yap (ASLA ŞABLON YOK)
                    var (detScreens, detTools, detDomain) = SynthesizeFromEvidenceDeterministically(evidence, productTitle, description);
                    screens = detScreens;
                    tools = detTools;
                    domain = detDomain;
                    analysisEngineName = "Kanıta Dayalı Deterministik Sembol & Modül Motoru";
                }
            }

            var allControls = screens.SelectMany(s => s.Controls).ToList();

            // 6. Bilgi Ağında Baloncukları (Nodes) ve Sinapsları (Links) Oluştur
            var company = _companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == companyId) ?? _companyManager.ActiveCompany;
            var companyName = company?.Name ?? "Şirket";

            var scanResult = new ScanResult
            {
                Success = true,
                FileName = fileName,
                FileSizeBytes = fileInfo.Length,
                AnalysisEngine = analysisEngineName,
                ExtractedParagraphs = screens.Count,
                Concepts = new List<LearnedConceptInfo>(),
                Relations = new List<LearnedRelationInfo>(),
                GeneratedQuestions = new List<string>(),
                GeneratedAnswers = new List<string>()
            };

            var companyDir = _companyManager.GetCompanyDirPath(companyId);
            var appsDir = Path.Combine(companyDir, "applications");
            Directory.CreateDirectory(appsDir);

            var appSlug = Regex.Replace(appName.ToLower(), "[^a-z0-9_]", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(appSlug)) appSlug = "app";
            var appHubId = $"app_{appSlug}";
            scanResult.DocumentNodeId = appHubId;

            var newNodes = new List<KnowledgeNode>();
            var newLinks = new List<KnowledgeLink>();

            // Hub Düğümü
            var hubNode = new KnowledgeNode
            {
                Id = appHubId,
                Label = $"🖥️ {productTitle}",
                Group = "app",
                Size = 36,
                Description = $"{productTitle} ({version}) - {domain}. {allControls.Count} UI bileşeni ve {screens.Count} modül içerir. {description}",
                ParentId = "core",
                DocSource = fileName,
                Tags = new List<string> { "Uygulama", "Masaüstü", domain, evidence.Architecture }
            };
            newNodes.Add(hubNode);

            // AI Core -> Hub Linki
            newLinks.Add(new KnowledgeLink
            {
                Source = "core",
                Target = appHubId,
                Label = "yönetir",
                Type = "rule",
                Strength = 1.0
            });
            scanResult.Relations.Add(new LearnedRelationInfo { Source = $"{companyName} AI Core", Verb = "yönetir", Target = productTitle });

            // Ekran ve Tool Düğümleri
            var toolDefinitionsJson = new List<object>();
            int screenIdx = 0;

            foreach (var screen in screens.Take(12))
            {
                screenIdx++;
                var screenId = $"{appHubId}_s_{screenIdx}";

                var controlSummary = screen.Controls.Count > 0
                    ? $"Bileşenler: {string.Join(", ", screen.Controls.Take(6).Select(c => $"{c.Name} ({c.Label})"))}"
                    : "Modül arayüzü.";

                var opSummary = screen.Operations.Count > 0
                    ? $" | İşlemler: {string.Join(", ", screen.Operations.Take(5))}"
                    : "";

                var screenNode = new KnowledgeNode
                {
                    Id = screenId,
                    Label = screen.Title,
                    Group = "app",
                    Size = 24,
                    Description = $"{screen.Title}: {screen.Purpose} {controlSummary}{opSummary}",
                    ParentId = appHubId,
                    DocSource = fileName,
                    Tags = new List<string> { "Ekran", domain, "Modül" }
                };
                newNodes.Add(screenNode);

                newLinks.Add(new KnowledgeLink
                {
                    Source = appHubId,
                    Target = screenId,
                    Label = "içerir",
                    Type = "dependency",
                    Strength = 0.95
                });

                // Bu ekrana ait veya ilişkili Tool Düğümü
                var toolItem = tools.FirstOrDefault(t => t.TargetScreen == screen.Title || t.TargetScreen == screen.ScreenName)
                               ?? tools.ElementAtOrDefault(screenIdx - 1);

                if (toolItem != null)
                {
                    var toolId = $"tool_{toolItem.Name}";
                    if (!newNodes.Any(n => n.Id == toolId))
                    {
                        var toolNode = new KnowledgeNode
                        {
                            Id = toolId,
                            Label = $"🛠️ {toolItem.Title}",
                            Group = "tool",
                            Size = 22,
                            Description = toolItem.Description,
                            ParentId = screenId,
                            DocSource = fileName,
                            Tags = new List<string> { "AI Tool", "MCP", toolItem.Name }
                        };
                        newNodes.Add(toolNode);

                        newLinks.Add(new KnowledgeLink
                        {
                            Source = screenId,
                            Target = toolId,
                            Label = "otomatize eder",
                            Type = "tool",
                            Strength = 0.95
                        });

                        toolDefinitionsJson.Add(new
                        {
                            type = "function",
                            function = new
                            {
                                name = toolItem.Name,
                                description = toolItem.Description,
                                parameters = new
                                {
                                    type = "object",
                                    properties = toolItem.Parameters,
                                    required = toolItem.RequiredParameters
                                }
                            }
                        });

                        scanResult.Relations.Add(new LearnedRelationInfo
                        {
                            Source = screen.Title,
                            Verb = "otomatize eder",
                            Target = toolItem.Title
                        });
                    }
                }

                scanResult.Concepts.Add(new LearnedConceptInfo
                {
                    Name = screen.Title,
                    Category = "app",
                    RuleDescription = $"{screen.Purpose} ({controlSummary})"
                });

                scanResult.Relations.Add(new LearnedRelationInfo
                {
                    Source = productTitle,
                    Verb = "içerir",
                    Target = screen.Title
                });
            }

            // 7. Araçları Şirket Klasörüne Kaydet (Data/Companies/{companyId}/tools/registered_tools.json)
            var toolEngine = new ToolEngine(_companyManager);
            toolEngine.EnsureCompanyToolsRegistered(companyId, toolDefinitionsJson);

            // 8. Bilgi Ağını Kaydet (Eski geçersiz düğümleri ve şablonları temizle)
            var existingGraph = _graphManager.GetGraphData(company ?? new Company { Id = companyId, Name = companyName });
            var finalNodes = existingGraph.Nodes
                .Where(n =>
                    !n.Id.StartsWith("learned_") &&
                    n.Id != appHubId &&
                    (string.IsNullOrEmpty(n.ParentId) || !n.ParentId.StartsWith(appHubId)) &&
                    !n.DocSource.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                    !n.Id.StartsWith($"tool_{appSlug}") &&
                    !n.Id.StartsWith("app_alpnet") &&
                    !n.Id.StartsWith("tool_alpnet") &&
                    !n.Id.StartsWith("tool_alpfinans"))
                .ToList();
            finalNodes.AddRange(newNodes);

            var finalLinks = existingGraph.Links
                .Where(l =>
                    !string.IsNullOrEmpty(l.Source) && !string.IsNullOrEmpty(l.Target) &&
                    !l.Source.StartsWith("learned_") && !l.Target.StartsWith("learned_") &&
                    l.Source != appHubId && l.Target != appHubId &&
                    !l.Source.StartsWith(appHubId) && !l.Target.StartsWith(appHubId) &&
                    !l.Source.StartsWith($"tool_{appSlug}") && !l.Target.StartsWith($"tool_{appSlug}") &&
                    !l.Source.StartsWith("tool_alpnet") && !l.Target.StartsWith("tool_alpnet") &&
                    !l.Source.StartsWith("app_alpnet") && !l.Target.StartsWith("app_alpnet"))
                .ToList();
            finalLinks.AddRange(newLinks);

            _graphManager.SaveGraph(companyId, finalNodes, finalLinks);

            // 8.1 Tam Kullanım Şemasını JSON Olarak Kaydet (Data/Companies/{companyId}/applications/{appSlug}_schema.json)
            var schemaObj = new
            {
                applicationName = productTitle,
                executable = Path.GetFileName(exeFilePath),
                executablePath = exeFilePath,
                databasePath = evidence.DiscoveredDatabasePath ?? "",
                databaseTables = evidence.DiscoveredDatabaseTables,
                domain = domain,
                screens = screens,
                tools = tools
            };
            var schemaJson = JsonSerializer.Serialize(schemaObj, _jsonOptions);
            var schemaFilePath = Path.Combine(appsDir, $"{appSlug}_schema.json");
            File.WriteAllText(schemaFilePath, schemaJson, Encoding.UTF8);

            // 9. Şirket Ayarlarını, Uygulama Yollarını & Sistem Promptunu Bu Uygulamanın Gerçek Bilgisiyle Güncelle
            if (company != null)
            {
                company.ApplicationPath = exeFilePath;
                company.ApplicationExecutable = Path.GetFileName(exeFilePath);
                if (!string.IsNullOrEmpty(evidence.DiscoveredDatabasePath))
                {
                    company.ApplicationDatabasePath = evidence.DiscoveredDatabasePath;
                }
                company.ApplicationSchemaJson = schemaJson;

                var toolNamesList = string.Join(", ", tools.Select(t => t.Title));
                company.SystemPrompt = $"Bu çalışma alanında '{productTitle}' ({domain}) masaüstü uygulaması entegredir.\n" +
                                       $"- Uygulama Yolu: \"{exeFilePath}\"\n" +
                                       (!string.IsNullOrEmpty(evidence.DiscoveredDatabasePath) ? $"- Veritabanı Yolu: \"{evidence.DiscoveredDatabasePath}\"\n" : "") +
                                       $"Yapay zeka modelleri kullanıcı adına bu uygulamanın kontrollerini tetikleyebilir ve araçlarını kullanabilir:\n" +
                                       $"- Tanımlı Araçlar: {toolNamesList}.\n" +
                                       $"Kullanıcıdan gelen isteklerde ilgili araçları Function Calling / Tool kullanarak çalıştırın.";

                company.Industry = domain;

                // Workspace dizininde AlpFinans launcher (.cmd, .bat, .ps1 ve kopya) oluştur
                _companyManager.EnsureWorkspaceAppLaunchers(companyId, _companyManager.GetCompanyWorkspace(companyId));
                _companyManager.SaveCompanies();
            }

            // 10. Gerçek Soru-Cevap Eğitim Çiftleri Üret
            var generatedQAs = GenerateDomainTailoredQAs(productTitle, domain, screens, allControls, tools, evidence);
            foreach (var (q, a) in generatedQAs)
            {
                _trainingManager.AppendTrainingPair(companyId, q, a);
                scanResult.GeneratedQuestions.Add(q);
                scanResult.GeneratedAnswers.Add(a);
            }

            scanResult.CreatedBubblesCount = newNodes.Count;
            scanResult.CreatedLinksCount = newLinks.Count;

            string liveNote = evidence.WasLiveInspected
                ? $" Canlı Windows UI Automation ile pencere, menü ve {evidence.LiveDiscoveredControls.Count} kontrolü dinamik olarak doğrulandı."
                : "";
            scanResult.Summary = $"{productTitle} ({domain}) başarıyla incelendi. Gerçek kanıtlardan {screens.Count} modül, {allControls.Count} UI bileşeni ve {tools.Count} yapay zeka aracı şirketin AI hafızasına entegre edildi.{liveNote}";

            return scanResult;
        }

        // ==========================================
        // CANLI ÇALIŞMA & WINDOWS UI AUTOMATION İNCELEMESİ
        // ==========================================
        private async Task InspectLiveRunningOrLaunchedAppAsync(string exeFilePath, AppRawEvidence evidence, bool allowLiveLaunch = true)
        {
            var appName = Path.GetFileNameWithoutExtension(exeFilePath);
            Process? targetProcess = null;
            bool launchedByUs = false;
            var launchedPids = new List<int>();

            try
            {
                // 1. Hedef uygulama zaten çalışıyor mu? (Sadece canlı süreçleri dikkate al)
                var runningProcs = Process.GetProcessesByName(appName).Where(p =>
                {
                    try { return !p.HasExited; } catch { return false; }
                }).ToList();

                foreach (var p in runningProcs)
                {
                    try
                    {
                        if (p.MainModule?.FileName.Equals(exeFilePath, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            targetProcess = p;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetProcess == null && runningProcs.Count > 0)
                {
                    targetProcess = runningProcs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? runningProcs.FirstOrDefault();
                }

                // 2. Çalışmıyorsa ve izin verildiyse test amacıyla güvenle başlat
                if (targetProcess == null && allowLiveLaunch)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exeFilePath,
                            WorkingDirectory = Path.GetDirectoryName(exeFilePath) ?? "",
                            UseShellExecute = true
                        };

                        var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            targetProcess = proc;
                            launchedByUs = true;
                            launchedPids.Add(proc.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"App launch error: {ex.Message}");
                    }
                }

                if (targetProcess != null)
                {
                    // UI penceresinin ayağa kalkmasını bekle (en fazla 8000ms)
                    var sw = Stopwatch.StartNew();
                    var targetHwnds = new List<IntPtr>();

                    while (sw.ElapsedMilliseconds < 8000)
                    {
                        try
                        {
                            if (targetProcess == null || targetProcess.HasExited)
                            {
                                var fresh = Process.GetProcessesByName(appName).FirstOrDefault(p => !p.HasExited);
                                if (fresh != null)
                                {
                                    targetProcess = fresh;
                                    if (launchedByUs && !launchedPids.Contains(fresh.Id))
                                    {
                                        launchedPids.Add(fresh.Id);
                                    }
                                }
                            }
                        }
                        catch { }

                        targetHwnds = FindVisibleWindowsForProcess(targetProcess, appName);
                        if (targetHwnds.Count > 0)
                        {
                            await Task.Delay(350); // pencerenin elemanlarının render olması için mikro bekleme
                            break;
                        }
                        await Task.Delay(150);
                    }

                    if (targetHwnds.Count == 0)
                    {
                        targetHwnds = FindVisibleWindowsForProcess(targetProcess, appName);
                    }

                    if (targetHwnds.Count > 0)
                    {
                        evidence.WasLiveInspected = true;

                        foreach (var hWnd in targetHwnds.Take(6))
                        {
                            var sbTitle = new StringBuilder(256);
                            GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                            var winTitle = sbTitle.ToString().Trim();

                            if (!string.IsNullOrWhiteSpace(winTitle))
                            {
                                if (!evidence.LiveWindowTitles.Contains(winTitle))
                                    evidence.LiveWindowTitles.Add(winTitle);

                                if (string.IsNullOrEmpty(evidence.LiveMainWindowTitle))
                                    evidence.LiveMainWindowTitle = winTitle;
                            }

                            // UI Automation ile arayüzü derinlemesine incele
                            InspectWindowViaUIAutomation(hWnd, winTitle, evidence);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Live inspection error: {ex.Message}");
            }
            finally
            {
                // 3. Eğer süreç bu inceleme için başlatıldıysa, KESİNLİKLE ve GÜVENLE sonlandır (Lingering test process önleme)
                if (launchedByUs && targetProcess != null)
                {
                    try
                    {
                        if (!targetProcess.HasExited)
                        {
                            targetProcess.CloseMainWindow();
                            var exited = targetProcess.WaitForExit(800);
                            if (!exited && !targetProcess.HasExited)
                            {
                                targetProcess.Kill(entireProcessTree: true);
                            }
                        }
                    }
                    catch { }

                    // Başlattığımız tüm PID'leri ve onların alt süreçlerini güvenle sonlandır
                    foreach (var pid in launchedPids)
                    {
                        try
                        {
                            var p = Process.GetProcessById(pid);
                            if (!p.HasExited) p.Kill(entireProcessTree: true);
                        }
                        catch { }
                    }
                }
            }
        }

        private List<IntPtr> FindVisibleWindowsForProcess(Process? proc, string appName)
        {
            var hwnds = new List<IntPtr>();
            int targetPid = 0;
            try { if (proc != null && !proc.HasExited) targetPid = proc.Id; } catch { }

            var candidatePids = new HashSet<int>();
            if (targetPid != 0) candidatePids.Add(targetPid);

            try
            {
                foreach (var p in Process.GetProcessesByName(appName).Where(p => { try { return !p.HasExited; } catch { return false; } }))
                {
                    candidatePids.Add(p.Id);
                }
            }
            catch { }

            EnumWindows((hWnd, lParam) =>
            {
                if (IsWindowVisible(hWnd))
                {
                    GetWindowThreadProcessId(hWnd, out uint windowPid);
                    bool pidMatch = candidatePids.Contains((int)windowPid);

                    var sb = new StringBuilder(256);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    var title = sb.ToString().Trim();

                    var sbClass = new StringBuilder(256);
                    GetClassName(hWnd, sbClass, sbClass.Capacity);
                    var className = sbClass.ToString().Trim();

                    bool isNotepadCandidate = appName.Equals("notepad", StringComparison.OrdinalIgnoreCase) &&
                        (className.Equals("Notepad", StringComparison.OrdinalIgnoreCase) ||
                         className.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Not Defteri", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Notepad", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Adsız", StringComparison.OrdinalIgnoreCase));

                    if (pidMatch || isNotepadCandidate)
                    {
                        if (!string.IsNullOrEmpty(title) &&
                            !title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) &&
                            !title.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!hwnds.Contains(hWnd)) hwnds.Add(hWnd);
                        }
                        else
                        {
                            GetWindowRect(hWnd, out RECT r);
                            if (r.Width >= 100 && r.Height >= 100)
                            {
                                if (!hwnds.Contains(hWnd)) hwnds.Add(hWnd);
                            }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            try
            {
                if (hwnds.Count == 0 && proc != null && !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                {
                    if (!hwnds.Contains(proc.MainWindowHandle)) hwnds.Add(proc.MainWindowHandle);
                }
            }
            catch { }

            return hwnds;
        }

        private void InspectWindowViaUIAutomation(IntPtr hWnd, string windowTitle, AppRawEvidence evidence)
        {
            try
            {
                var rootElement = AutomationElement.FromHandle(hWnd);
                if (rootElement == null) return;

                if (string.IsNullOrWhiteSpace(windowTitle))
                {
                    try
                    {
                        var autoName = rootElement.Current.Name?.Trim();
                        if (!string.IsNullOrWhiteSpace(autoName))
                        {
                            windowTitle = autoName;
                            if (string.IsNullOrEmpty(evidence.LiveMainWindowTitle))
                                evidence.LiveMainWindowTitle = autoName;
                            if (!evidence.LiveWindowTitles.Contains(autoName))
                                evidence.LiveWindowTitles.Add(autoName);
                        }
                    }
                    catch { }
                }

                // Büyük veya takılan UI ağaçlarına karşı 4 saniyelik güvenlik zaman aşımı
                var uiTask = Task.Run(() =>
                {
                    try
                    {
                        var allElements = rootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        int count = 0;

                        foreach (AutomationElement el in allElements)
                        {
                            count++;
                            if (count > 250) break; // Performans ve sınır

                            try
                            {
                                var cur = el.Current;
                                var name = cur.Name?.Trim() ?? "";
                                var autoId = cur.AutomationId?.Trim() ?? "";
                                var ctrlType = cur.ControlType;

                                // Menüler
                                if (ctrlType == ControlType.MenuItem || ctrlType == ControlType.Menu || ctrlType == ControlType.MenuBar)
                                {
                                    if (!string.IsNullOrWhiteSpace(name) && name.Length >= 2 && name.Length <= 40)
                                    {
                                        if (!evidence.LiveDiscoveredMenus.Contains(name))
                                            evidence.LiveDiscoveredMenus.Add(name);
                                    }
                                }
                                // Butonlar
                                else if (ctrlType == ControlType.Button || ctrlType == ControlType.SplitButton)
                                {
                                    var label = !string.IsNullOrWhiteSpace(name) ? name : autoId;
                                    if (!string.IsNullOrWhiteSpace(label) && label.Length >= 2 && label.Length <= 50)
                                    {
                                        var ctrl = new AppControlInfo
                                        {
                                            Name = !string.IsNullOrWhiteSpace(autoId) ? autoId : $"btn_{Regex.Replace(label.ToLower(), "[^a-z0-9]", "_")}",
                                            ControlType = "Button (Buton)",
                                            Label = label,
                                            ActionDescription = $"{label} buton işlemi.",
                                            ParentScreen = windowTitle
                                        };

                                        if (!evidence.LiveDiscoveredControls.Any(c => c.Name == ctrl.Name || c.Label == ctrl.Label))
                                            evidence.LiveDiscoveredControls.Add(ctrl);
                                    }
                                }
                                // Metin Giriş Alanları
                                else if (ctrlType == ControlType.Edit || ctrlType == ControlType.Document)
                                {
                                    var label = !string.IsNullOrWhiteSpace(name) ? name : autoId;
                                    if (!string.IsNullOrWhiteSpace(label) && label.Length >= 2 && label.Length <= 50)
                                    {
                                        var ctrl = new AppControlInfo
                                        {
                                            Name = !string.IsNullOrWhiteSpace(autoId) ? autoId : $"txt_{Regex.Replace(label.ToLower(), "[^a-z0-9]", "_")}",
                                            ControlType = "TextBox (Metin Girişi)",
                                            Label = label,
                                            ActionDescription = $"{label} veri düzenleme alanı.",
                                            ParentScreen = windowTitle
                                        };

                                        if (!evidence.LiveDiscoveredControls.Any(c => c.Name == ctrl.Name || c.Label == ctrl.Label))
                                            evidence.LiveDiscoveredControls.Add(ctrl);
                                    }
                                }
                                // Sekmeler
                                else if (ctrlType == ControlType.TabItem || ctrlType == ControlType.Tab)
                                {
                                    if (!string.IsNullOrWhiteSpace(name) && name.Length >= 2 && name.Length <= 40)
                                    {
                                        if (!evidence.LiveDiscoveredMenus.Contains(name))
                                            evidence.LiveDiscoveredMenus.Add(name);
                                    }
                                }
                                // Açılır Kutular
                                else if (ctrlType == ControlType.ComboBox || ctrlType == ControlType.List || ctrlType == ControlType.DataGrid)
                                {
                                    var label = !string.IsNullOrWhiteSpace(name) ? name : autoId;
                                    if (!string.IsNullOrWhiteSpace(label) && label.Length >= 2 && label.Length <= 50)
                                    {
                                        var ctrl = new AppControlInfo
                                        {
                                            Name = !string.IsNullOrWhiteSpace(autoId) ? autoId : $"cmb_{Regex.Replace(label.ToLower(), "[^a-z0-9]", "_")}",
                                            ControlType = "ComboBox (Açılır Liste)",
                                            Label = label,
                                            ActionDescription = $"{label} seçim alanı.",
                                            ParentScreen = windowTitle
                                        };

                                        if (!evidence.LiveDiscoveredControls.Any(c => c.Name == ctrl.Name || c.Label == ctrl.Label))
                                            evidence.LiveDiscoveredControls.Add(ctrl);
                                    }
                                }
                                // UI Metinleri / Açıklamaları
                                else if (ctrlType == ControlType.Text)
                                {
                                    if (!string.IsNullOrWhiteSpace(name) && name.Length >= 3 && name.Length <= 80 && !name.All(char.IsDigit))
                                    {
                                        if (!evidence.LiveDiscoveredTexts.Contains(name) && evidence.LiveDiscoveredTexts.Count < 25)
                                            evidence.LiveDiscoveredTexts.Add(name);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                });

                uiTask.Wait(4000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InspectWindowViaUIAutomation error: {ex.Message}");
            }
        }

        // ==========================================
        // 1. PE & FILEVERSIONINFO KANIT TOPLAYICI
        // ==========================================
        private AppRawEvidence ExtractRawFileEvidence(string exeFilePath)
        {
            var evidence = new AppRawEvidence
            {
                FilePath = exeFilePath,
                FileName = Path.GetFileName(exeFilePath)
            };

            var fi = new FileInfo(exeFilePath);
            evidence.FileSizeBytes = fi.Exists ? fi.Length : 0;

            // FileVersionInfo
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exeFilePath);
                evidence.ProductName = vi.ProductName?.Trim() ?? "";
                evidence.FileDescription = vi.FileDescription?.Trim() ?? "";
                evidence.CompanyName = vi.CompanyName?.Trim() ?? "";
                evidence.Comments = vi.Comments?.Trim() ?? "";
                evidence.LegalCopyright = vi.LegalCopyright?.Trim() ?? "";
                evidence.OriginalFilename = vi.OriginalFilename?.Trim() ?? "";
                evidence.FileVersion = vi.FileVersion?.Trim() ?? "";
                evidence.ProductVersion = vi.ProductVersion?.Trim() ?? "";
                evidence.InternalName = vi.InternalName?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FileVersionInfo error: {ex.Message}");
            }

            // PE Headers
            try
            {
                using var stream = File.OpenRead(exeFilePath);
                using var peReader = new PEReader(stream);

                if (peReader.PEHeaders != null)
                {
                    evidence.Architecture = peReader.PEHeaders.CoffHeader.Machine.ToString();
                    if (peReader.PEHeaders.PEHeader != null)
                    {
                        var sub = peReader.PEHeaders.PEHeader.Subsystem;
                        evidence.Subsystem = sub == Subsystem.WindowsGui
                            ? "Windows GUI Masaüstü Uygulaması"
                            : "Windows Konsol / Arka Plan Servisi";
                    }

                    // PE İçe Aktarılan DLL'ler ve Win32 API Fonksiyonları (ILT / IAT)
                    var (importedDlls, importedFns) = ExtractPeImports(peReader, stream);
                    evidence.ImportedDlls = importedDlls;
                    evidence.ImportedFunctions = importedFns;
                    foreach (var fn in importedFns)
                    {
                        if (!evidence.DiscoveredOperations.Contains(fn))
                            evidence.DiscoveredOperations.Add(fn);
                    }

                    // PE Exports
                    evidence.ExportedSymbols = ExtractPeExports(peReader, stream);

                    // .NET Metadata
                    if (peReader.HasMetadata)
                    {
                        evidence.IsDotNet = true;
                        var reader = peReader.GetMetadataReader();

                        foreach (var asmRefHandle in reader.AssemblyReferences)
                        {
                            var asmRef = reader.GetAssemblyReference(asmRefHandle);
                            var refName = reader.GetString(asmRef.Name);
                            evidence.DotNetDependencies.Add(refName);
                        }

                        foreach (var typeHandle in reader.TypeDefinitions)
                        {
                            var typeDef = reader.GetTypeDefinition(typeHandle);
                            var typeName = reader.GetString(typeDef.Name);

                            if (typeName.StartsWith("<") || typeName.Contains("AnonymousType") || string.IsNullOrWhiteSpace(typeName))
                                continue;

                            bool isScreen = typeName.StartsWith("Frm", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.StartsWith("Form", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.EndsWith("Form", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.EndsWith("Window", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.EndsWith("View", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.EndsWith("Page", StringComparison.OrdinalIgnoreCase) ||
                                            typeName.EndsWith("Dialog", StringComparison.OrdinalIgnoreCase);

                            var screen = new AppScreenInfo
                            {
                                ScreenName = typeName,
                                Title = BeautifyIdentifier(typeName),
                                Purpose = $"{BeautifyIdentifier(typeName)} operasyonlarını yöneten ekran modülü."
                            };

                            foreach (var fieldHandle in typeDef.GetFields())
                            {
                                var fieldDef = reader.GetFieldDefinition(fieldHandle);
                                var fieldName = reader.GetString(fieldDef.Name);
                                if (fieldName.StartsWith("<") || string.IsNullOrWhiteSpace(fieldName)) continue;

                                var cType = CategorizeControl(fieldName);
                                if (cType != null && !screen.Controls.Any(c => c.Name == fieldName))
                                {
                                    var ctrl = new AppControlInfo
                                    {
                                        Name = fieldName,
                                        ControlType = cType,
                                        Label = BeautifyIdentifier(fieldName),
                                        ActionDescription = $"{BeautifyIdentifier(fieldName)} kontrolü.",
                                        ParentScreen = typeName
                                    };
                                    screen.Controls.Add(ctrl);
                                }
                            }

                            foreach (var methodHandle in typeDef.GetMethods())
                            {
                                var methodDef = reader.GetMethodDefinition(methodHandle);
                                var methodName = reader.GetString(methodDef.Name);
                                if (methodName.StartsWith("<") || methodName.StartsWith("get_") || methodName.StartsWith("set_") || methodName == ".ctor")
                                    continue;

                                if (methodName.EndsWith("_Click", StringComparison.OrdinalIgnoreCase) ||
                                    methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase) ||
                                    methodName.StartsWith("Run") || methodName.StartsWith("Scan") || methodName.StartsWith("Toggle") ||
                                    methodName.StartsWith("Start") || methodName.StartsWith("Stop") || methodName.StartsWith("Fetch"))
                                {
                                    screen.Operations.Add(methodName);
                                }
                            }

                            if (isScreen && (screen.Controls.Count > 0 || screen.Operations.Count > 0))
                            {
                                evidence.DotNetScreens.Add(screen);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PE parsing error: {ex.Message}");
            }

            return evidence;
        }

        private (List<string> Dlls, List<string> Functions) ExtractPeImports(PEReader peReader, Stream stream)
        {
            var dlls = new List<string>();
            var functions = new List<string>();
            try
            {
                var importDir = peReader.PEHeaders?.PEHeader?.ImportTableDirectory;
                if (peReader.PEHeaders == null || importDir == null || importDir.Value.RelativeVirtualAddress == 0 || importDir.Value.Size == 0)
                    return (dlls, functions);

                var rva = importDir.Value.RelativeVirtualAddress;
                var section = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                    rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize);

                if (section.VirtualAddress == 0) return (dlls, functions);

                int offsetInSection = rva - section.VirtualAddress;
                int rawOffset = section.PointerToRawData + offsetInSection;

                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                stream.Seek(rawOffset, SeekOrigin.Begin);

                bool is64Bit = peReader.PEHeaders.PEHeader?.Magic == PEMagic.PE32Plus;

                while (stream.Position + 20 <= stream.Length)
                {
                    uint originalFirstThunk = reader.ReadUInt32();
                    uint timeDateStamp = reader.ReadUInt32();
                    uint forwarderChain = reader.ReadUInt32();
                    uint nameRva = reader.ReadUInt32();
                    uint firstThunk = reader.ReadUInt32();

                    if (nameRva == 0) break;

                    long currentDescPos = stream.Position;

                    // 1. DLL Adını Oku
                    var nameSection = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                        nameRva >= s.VirtualAddress && nameRva < s.VirtualAddress + s.VirtualSize);

                    if (nameSection.VirtualAddress != 0)
                    {
                        long nameRawOffset = nameSection.PointerToRawData + (nameRva - nameSection.VirtualAddress);
                        stream.Seek(nameRawOffset, SeekOrigin.Begin);

                        var sb = new StringBuilder();
                        int b;
                        while ((b = stream.ReadByte()) > 0 && sb.Length < 100)
                        {
                            sb.Append((char)b);
                        }

                        var dllName = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(dllName) && !dlls.Contains(dllName, StringComparer.OrdinalIgnoreCase))
                        {
                            dlls.Add(dllName);
                        }
                    }

                    // 2. İçe Aktarılan API Fonksiyonlarını ILT / IAT Üzerinden Oku
                    uint thunkRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
                    if (thunkRva != 0 && functions.Count < 60)
                    {
                        var thunkSection = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                            thunkRva >= s.VirtualAddress && thunkRva < s.VirtualAddress + s.VirtualSize);

                        if (thunkSection.VirtualAddress != 0)
                        {
                            long thunkRawOffset = thunkSection.PointerToRawData + (thunkRva - thunkSection.VirtualAddress);
                            stream.Seek(thunkRawOffset, SeekOrigin.Begin);

                            int fnCountForDll = 0;
                            while (stream.Position < stream.Length && fnCountForDll < 8 && functions.Count < 60)
                            {
                                ulong thunkVal = is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
                                if (thunkVal == 0) break;

                                bool isOrdinal = is64Bit ? (thunkVal & 0x8000000000000000) != 0 : (thunkVal & 0x80000000) != 0;
                                if (!isOrdinal)
                                {
                                    uint fnNameRva = (uint)(thunkVal & 0x7FFFFFFF);
                                    var fnSection = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                                        fnNameRva >= s.VirtualAddress && fnNameRva < s.VirtualAddress + s.VirtualSize);

                                    if (fnSection.VirtualAddress != 0)
                                    {
                                        long fnRawOffset = fnSection.PointerToRawData + (fnNameRva - fnSection.VirtualAddress) + 2; // +2 bayt Hint atla
                                        long savedPos = stream.Position;
                                        stream.Seek(fnRawOffset, SeekOrigin.Begin);

                                        var fnSb = new StringBuilder();
                                        int ch;
                                        while ((ch = stream.ReadByte()) > 0 && fnSb.Length < 80)
                                        {
                                            if (ch >= 0x20 && ch <= 0x7E) fnSb.Append((char)ch);
                                            else break;
                                        }

                                        var fnName = fnSb.ToString().Trim();
                                        if (fnName.Length >= 3 && !functions.Contains(fnName, StringComparer.OrdinalIgnoreCase))
                                        {
                                            functions.Add(fnName);
                                            fnCountForDll++;
                                        }

                                        stream.Seek(savedPos, SeekOrigin.Begin);
                                    }
                                }
                            }
                        }
                    }

                    stream.Seek(currentDescPos, SeekOrigin.Begin);
                    if (dlls.Count >= 30) break;
                }
            }
            catch { }
            return (dlls, functions);
        }

        private List<string> ExtractPeExports(PEReader peReader, Stream stream)
        {
            var exports = new List<string>();
            try
            {
                var exportDir = peReader.PEHeaders?.PEHeader?.ExportTableDirectory;
                if (peReader.PEHeaders == null || exportDir == null || exportDir.Value.RelativeVirtualAddress == 0 || exportDir.Value.Size == 0)
                    return exports;

                var rva = exportDir.Value.RelativeVirtualAddress;
                var section = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                    rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize);

                if (section.VirtualAddress == 0) return exports;

                int rawOffset = section.PointerToRawData + (rva - section.VirtualAddress);
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                stream.Seek(rawOffset + 24, SeekOrigin.Begin); // NumberOfNames
                uint numberOfNames = reader.ReadUInt32();
                uint addressOfFunctions = reader.ReadUInt32();
                uint addressOfNames = reader.ReadUInt32();

                if (addressOfNames != 0 && numberOfNames > 0)
                {
                    var namesSection = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                        addressOfNames >= s.VirtualAddress && addressOfNames < s.VirtualAddress + s.VirtualSize);

                    if (namesSection.VirtualAddress != 0)
                    {
                        long namesRawOffset = namesSection.PointerToRawData + (addressOfNames - namesSection.VirtualAddress);
                        int countToRead = (int)Math.Min(numberOfNames, 25);

                        for (int i = 0; i < countToRead; i++)
                        {
                            stream.Seek(namesRawOffset + (i * 4), SeekOrigin.Begin);
                            uint nameRva = reader.ReadUInt32();
                            if (nameRva == 0) continue;

                            var strSection = peReader.PEHeaders.SectionHeaders.FirstOrDefault(s =>
                                nameRva >= s.VirtualAddress && nameRva < s.VirtualAddress + s.VirtualSize);

                            if (strSection.VirtualAddress != 0)
                            {
                                stream.Seek(strSection.PointerToRawData + (nameRva - strSection.VirtualAddress), SeekOrigin.Begin);
                                var sb = new StringBuilder();
                                int b;
                                while ((b = stream.ReadByte()) > 0 && sb.Length < 100)
                                {
                                    sb.Append((char)b);
                                }
                                var expName = sb.ToString().Trim();
                                if (!string.IsNullOrWhiteSpace(expName) && !exports.Contains(expName))
                                    exports.Add(expName);
                            }
                        }
                    }
                }
            }
            catch { }
            return exports;
        }

        // ==========================================
        // 2. ELECTRON / CHROMIUM / ASAR İNCELEME
        // ==========================================
        private void InspectDirectoryAssets(string exeFilePath, AppRawEvidence evidence)
        {
            var exeDir = Path.GetDirectoryName(exeFilePath) ?? "";
            var appName = Path.GetFileNameWithoutExtension(exeFilePath);

            // Aday asar ve paket yolları
            var candidateAsarPaths = new List<string>
            {
                Path.Combine(exeDir, "resources", "app.asar"),
                Path.Combine(exeDir, "app.asar"),
                Path.Combine(exeDir, "..", "resources", "app.asar"),
                Path.Combine(exeDir, "resources", "app.asar.unpacked")
            };

            // İsmi temizle (Setup, Installer, (1), -x64 vb. ekleri kaldır)
            var cleanAppName = Regex.Replace(appName, @"\s*(\([0-9]+\)|[-_]x64|[-_]setup|[-_]installer|setup|installer)", "", RegexOptions.IgnoreCase).Trim();

            // Kurulu dizinleri kontrol et (Örn: Downloads veya Masaüstünden installer seçildiyse)
            var searchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { appName, cleanAppName };
            if (!string.IsNullOrWhiteSpace(evidence.ProductName))
            {
                searchNames.Add(evidence.ProductName);
                var cleanProd = Regex.Replace(evidence.ProductName, @"\s*(setup|installer)", "", RegexOptions.IgnoreCase).Trim();
                searchNames.Add(cleanProd);
            }

            var baseDirs = new List<string>();
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                baseDirs.Add(Path.Combine(localAppData, "Programs"));
                baseDirs.Add(localAppData);
            }
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) baseDirs.Add(programFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86)) baseDirs.Add(programFilesX86);

            foreach (var bDir in baseDirs)
            {
                foreach (var name in searchNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    candidateAsarPaths.Add(Path.Combine(bDir, name, "resources", "app.asar"));
                    candidateAsarPaths.Add(Path.Combine(bDir, name, "app.asar"));
                }
            }

            // Electron dizin belirteçleri
            bool hasElectronMarkers = File.Exists(Path.Combine(exeDir, "chrome_100_percent.pak")) ||
                                     File.Exists(Path.Combine(exeDir, "resources.pak")) ||
                                     File.Exists(Path.Combine(exeDir, "v8_context_snapshot.bin")) ||
                                     File.Exists(Path.Combine(exeDir, "LICENSE.electron.txt")) ||
                                     File.Exists(Path.Combine(exeDir, "ffmpeg.dll"));

            if (hasElectronMarkers)
            {
                evidence.IsElectronApp = true;
            }

            // Doğrudan package.json var mı? (Dizin veya unpacked asar içinde)
            var directPkgPaths = new List<string>
            {
                Path.Combine(exeDir, "resources", "app", "package.json"),
                Path.Combine(exeDir, "resources", "app.asar.unpacked", "package.json"),
                Path.Combine(exeDir, "package.json")
            };
            foreach (var dp in directPkgPaths)
            {
                if (File.Exists(dp))
                {
                    try
                    {
                        var content = File.ReadAllText(dp);
                        ParsePackageJson(content, evidence);
                        evidence.IsElectronApp = true;
                        break;
                    }
                    catch { }
                }
            }

            // app-update.yml var mı? (Güncelleme sunucusu ve yayıncı bilgisi)
            var candidateUpdateYmls = new[]
            {
                Path.Combine(exeDir, "resources", "app-update.yml"),
                Path.Combine(exeDir, "app-update.yml")
            };
            foreach (var uy in candidateUpdateYmls)
            {
                if (File.Exists(uy))
                {
                    try
                    {
                        var ymlContent = File.ReadAllText(uy);
                        var urlMatch = Regex.Match(ymlContent, @"url:\s*([^\r\n]+)");
                        if (urlMatch.Success)
                        {
                            evidence.UpdaterUrl = urlMatch.Groups[1].Value.Trim();
                        }
                    }
                    catch { }
                }
            }

            // app.asar'ı bul ve ayrıştır
            foreach (var asarPath in candidateAsarPaths)
            {
                if (File.Exists(asarPath))
                {
                    evidence.IsElectronApp = true;
                    ParseAsarArchive(asarPath, evidence);
                    break;
                }
            }

            // 2. Python / PyInstaller ve İlişkili Veritabanı / Betik Varlıklarını İncele
            InspectPythonAndScriptAssets(exeFilePath, evidence);
        }

        private void InspectPythonAndScriptAssets(string exeFilePath, AppRawEvidence evidence)
        {
            try
            {
                var exeDir = Path.GetDirectoryName(exeFilePath) ?? "";
                if (string.IsNullOrEmpty(exeDir) || !Directory.Exists(exeDir)) return;

                var searchDirs = new List<string> { exeDir };
                try
                {
                    var parent = Directory.GetParent(exeDir)?.FullName;
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    {
                        searchDirs.Add(parent);
                    }
                    foreach (var sub in Directory.GetDirectories(exeDir))
                    {
                        searchDirs.Add(sub);
                        try
                        {
                            foreach (var sub2 in Directory.GetDirectories(sub))
                            {
                                searchDirs.Add(sub2);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // 1. SQLite veritabanı dosyaları ara
                foreach (var d in searchDirs)
                {
                    try
                    {
                        var sqliteFiles = Directory.GetFiles(d, "*.sqlite").Concat(Directory.GetFiles(d, "*.db"));
                        foreach (var sf in sqliteFiles)
                        {
                            var fn = Path.GetFileName(sf).ToLower();
                            if (fn.Contains("finans") || fn.Contains("muhasebe") || fn.Contains("data") || fn.Contains("veritabani") || string.IsNullOrEmpty(evidence.DiscoveredDatabasePath))
                            {
                                evidence.DiscoveredDatabasePath = sf;
                                try
                                {
                                    using var conn = new SqliteConnection($"Data Source={sf};Mode=ReadOnly");
                                    conn.Open();
                                    using var cmd = conn.CreateCommand();
                                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                                    using var reader = cmd.ExecuteReader();
                                    while (reader.Read())
                                    {
                                        var tbl = reader.GetString(0);
                                        if (!evidence.DiscoveredDatabaseTables.Contains(tbl))
                                            evidence.DiscoveredDatabaseTables.Add(tbl);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Python Masaüstü Betikleri (.py) ve PyInstaller (.spec) Dosyaları Ara
                string? mainPyScript = null;
                foreach (var d in searchDirs)
                {
                    try
                    {
                        var pyFiles = Directory.GetFiles(d, "*.py");
                        foreach (var py in pyFiles)
                        {
                            var fn = Path.GetFileName(py).ToLower();
                            if (fn.Contains("app_desktop") || fn.Contains("main") || fn.Contains("gui") || fn.Contains("app"))
                            {
                                mainPyScript = py;
                                break;
                            }
                        }
                        if (mainPyScript != null) break;
                    }
                    catch { }
                }

                if (mainPyScript != null && File.Exists(mainPyScript))
                {
                    evidence.IsPythonPyInstallerApp = true;
                    evidence.PythonMainScriptPath = mainPyScript;
                    var pyContent = File.ReadAllText(mainPyScript);

                    // Başlık / Title
                    var titleMatch = Regex.Match(pyContent, @"(?:self\.)?title\s*\(\s*[""']([^""']+)[""']\s*\)");
                    if (titleMatch.Success && string.IsNullOrEmpty(evidence.ProductName))
                    {
                        evidence.ProductName = titleMatch.Groups[1].Value.Trim();
                    }

                    // Sekmeler / Navigasyon Öğeleri (CustomTkinter / Tkinter / PyQt)
                    var navMatches = Regex.Matches(pyContent, @"\(\s*[""']([a-zA-Z0-9_-]+)[""']\s*,\s*[""']([^""']+)[""']");
                    var detectedScreens = new List<AppScreenInfo>();

                    foreach (Match m in navMatches)
                    {
                        var navKey = m.Groups[1].Value.Trim().ToLowerInvariant();
                        var navLabel = m.Groups[2].Value.Trim();
                        navLabel = Regex.Replace(navLabel, @"^[^\w\s]+", "").Trim();

                        if (navKey is "dashboard" or "vision" or "expenses" or "incomes" or "settings" or "reports" or "fatura" or "gelir")
                        {
                            var sName = $"{char.ToUpper(navKey[0])}{navKey[1..]}View";
                            if (detectedScreens.Any(s => s.ScreenName == sName)) continue;

                            var screenInfo = new AppScreenInfo
                            {
                                ScreenName = sName,
                                Title = navLabel,
                                Purpose = $"{navLabel} ekranı. Finansal işlemleri ve modül kayıtlarını yönetir.",
                                Operations = new List<string>(),
                                Controls = new List<AppControlInfo>()
                            };

                            if (navKey == "expenses" || navKey == "fatura")
                            {
                                screenInfo.Purpose = "Faturalar, gider kayıtları, KDV oranları ve vergi kalkanı yönetimi.";
                                screenInfo.Operations.Add("Yeni Fatura Oluştur");
                                screenInfo.Operations.Add("Gider Kaydı Ekle");
                                screenInfo.Operations.Add("Faturaları Listele");
                                screenInfo.Controls.Add(new AppControlInfo { Name = "txtFaturaNo", Label = "Fatura No", ControlType = "TextBox", ActionDescription = "Fatura numarasını girer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "txtTutar", Label = "Fatura Tutarı", ControlType = "TextBox", ActionDescription = "Fatura tutarını girer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "txtMusteri", Label = "Müşteri / Cari", ControlType = "TextBox", ActionDescription = "Cari ünvanını girer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "cmbKdvOrani", Label = "KDV Oranı (%1, %10, %20)", ControlType = "ComboBox", ActionDescription = "KDV oranını seçer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "btnKaydet", Label = "Faturayı Kaydet", ControlType = "Button", ActionDescription = "Faturayı deftere işler.", ParentScreen = navLabel });
                            }
                            else if (navKey == "incomes" || navKey == "gelir")
                            {
                                screenInfo.Purpose = "Gelirler, müşteri hakedişleri ve şirket fatura tahsilatları yönetimi.";
                                screenInfo.Operations.Add("Yeni Gelir Kaydı Ekle");
                                screenInfo.Operations.Add("Hakedişleri Listele");
                                screenInfo.Controls.Add(new AppControlInfo { Name = "txtGelirBaslik", Label = "Gelir Başlığı", ControlType = "TextBox", ActionDescription = "Gelir açıklamasını girer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "txtGelirTutar", Label = "Brüt Tutar", ControlType = "TextBox", ActionDescription = "Gelir tutarını girer.", ParentScreen = navLabel });
                                screenInfo.Controls.Add(new AppControlInfo { Name = "btnGelirKaydet", Label = "Geliri Kaydet", ControlType = "Button", ActionDescription = "Geliri sisteme işler.", ParentScreen = navLabel });
                            }
                            else if (navKey == "dashboard")
                            {
                                screenInfo.Purpose = "Genel bakış, ciro, KDV dengesi ve tahakkuk eden vergi durumunu görüntüler.";
                                screenInfo.Operations.Add("Finansal Durumu Göster");
                                screenInfo.Operations.Add("KDV Dengesini Hesapla");
                                screenInfo.Controls.Add(new AppControlInfo { Name = "btnVergiOzeti", Label = "Vergi & Ciro Özeti", ControlType = "Button", ActionDescription = "Özet finansal raporu açar.", ParentScreen = navLabel });
                            }
                            else if (navKey == "vision")
                            {
                                screenInfo.Purpose = "OCR ile fatura ve makbuz görsellerini, PDF belgelerini otomatik okuma ve ayrıştırma.";
                                screenInfo.Operations.Add("Belge Tara ve OCR Yap");
                                screenInfo.Controls.Add(new AppControlInfo { Name = "btnBelgeSec", Label = "Belge Yükle / Tara", ControlType = "Button", ActionDescription = "Fatura görseli yükler.", ParentScreen = navLabel });
                            }
                            else if (navKey == "settings")
                            {
                                screenInfo.Purpose = "Şirket türü, gelir vergisi dilimleri ve sabit vergi ayarları.";
                                screenInfo.Operations.Add("Vergi Ayarlarını Güncelle");
                            }

                            detectedScreens.Add(screenInfo);
                        }
                    }

                    if (detectedScreens.Count > 0)
                    {
                        evidence.DiscoveredScriptScreens = detectedScreens;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InspectPythonAndScriptAssets error: {ex.Message}");
            }
        }

        private void ParseAsarArchive(string asarPath, AppRawEvidence evidence)
        {
            try
            {
                using var stream = File.OpenRead(asarPath);
                using var reader = new BinaryReader(stream);

                if (stream.Length < 16) return;

                // Asar başlığı (Chromium Pickle)
                uint magic = reader.ReadUInt32();
                if (magic != 4) return;

                uint headerSize = reader.ReadUInt32();
                uint headerStringSize = reader.ReadUInt32();
                uint headerJsonLen = reader.ReadUInt32();

                if (headerJsonLen == 0 || headerJsonLen > 10 * 1024 * 1024) return;

                byte[] jsonBytes = reader.ReadBytes((int)headerJsonLen);
                string jsonHeader = Encoding.UTF8.GetString(jsonBytes);

                long dataStart = 16 + headerJsonLen;

                using var doc = JsonDocument.Parse(jsonHeader);
                if (doc.RootElement.TryGetProperty("files", out var filesEl))
                {
                    // Asar içindeki dosyaları tara ve package.json'ı bul
                    TraverseAsarFiles(filesEl, "", stream, dataStart, evidence);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Asar parse error: {ex.Message}");
            }
        }

        private void TraverseAsarFiles(JsonElement element, string currentPath, Stream stream, long dataStart, AppRawEvidence evidence)
        {
            if (element.ValueKind != JsonValueKind.Object) return;

            foreach (var prop in element.EnumerateObject())
            {
                var name = prop.Name;
                var subPath = string.IsNullOrEmpty(currentPath) ? name : $"{currentPath}/{name}";

                if (prop.Value.TryGetProperty("files", out var subFiles))
                {
                    TraverseAsarFiles(subFiles, subPath, stream, dataStart, evidence);
                }
                else
                {
                    // Kod / modül dosyası bulundu
                    if (!subPath.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase))
                    {
                        var ext = Path.GetExtension(name).ToLower();
                        if (ext is ".js" or ".ts" or ".tsx" or ".jsx" or ".json" or ".html" or ".proto")
                        {
                            if (evidence.AsarFilesList.Count < 150)
                            {
                                evidence.AsarFilesList.Add(subPath);
                            }
                        }
                    }

                    // package.json bulundu mu?
                    bool isCandidatePackageJson = name.Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(evidence.PackageJsonRaw) || subPath.Equals("package.json", StringComparison.OrdinalIgnoreCase) || subPath.Equals("app/package.json", StringComparison.OrdinalIgnoreCase));

                    if (isCandidatePackageJson)
                    {
                        try
                        {
                            long offset = 0;
                            if (prop.Value.TryGetProperty("offset", out var offEl))
                            {
                                if (offEl.ValueKind == JsonValueKind.String && long.TryParse(offEl.GetString(), out var oVal))
                                    offset = oVal;
                                else if (offEl.ValueKind == JsonValueKind.Number)
                                    offset = offEl.GetInt64();
                            }

                            long size = 0;
                            if (prop.Value.TryGetProperty("size", out var szEl))
                            {
                                if (szEl.ValueKind == JsonValueKind.Number)
                                    size = szEl.GetInt64();
                                else if (szEl.ValueKind == JsonValueKind.String && long.TryParse(szEl.GetString(), out var sVal))
                                    size = sVal;
                            }

                            if (size > 0 && size < 2 * 1024 * 1024)
                            {
                                long filePos = dataStart + offset;
                                if (filePos + size <= stream.Length)
                                {
                                    stream.Seek(filePos, SeekOrigin.Begin);
                                    byte[] buf = new byte[size];
                                    stream.ReadExactly(buf, 0, (int)size);
                                    string pkgContent = Encoding.UTF8.GetString(buf);
                                    ParsePackageJson(pkgContent, evidence);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Read package.json from asar error: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void ParsePackageJson(string jsonContent, AppRawEvidence evidence)
        {
            try
            {
                evidence.PackageJsonRaw = jsonContent;
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("name", out var nameEl))
                    evidence.PackageName = nameEl.GetString();

                if (root.TryGetProperty("productName", out var pnEl))
                    evidence.PackageProductName = pnEl.GetString();

                if (root.TryGetProperty("description", out var descEl))
                    evidence.PackageDescription = descEl.GetString();

                if (root.TryGetProperty("version", out var verEl))
                    evidence.PackageVersion = verEl.GetString();

                if (root.TryGetProperty("main", out var mainEl))
                    evidence.PackageMain = mainEl.GetString();

                if (root.TryGetProperty("homepage", out var hpEl))
                    evidence.PackageHomepage = hpEl.GetString();

                if (root.TryGetProperty("author", out var authorEl))
                {
                    if (authorEl.ValueKind == JsonValueKind.String)
                        evidence.PackageAuthor = authorEl.GetString();
                    else if (authorEl.ValueKind == JsonValueKind.Object && authorEl.TryGetProperty("name", out var aName))
                        evidence.PackageAuthor = aName.GetString();
                }

                // Bağımlılıklar (dependencies & devDependencies)
                if (root.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var d in depEl.EnumerateObject())
                    {
                        if (!evidence.PackageDependencies.Contains(d.Name))
                            evidence.PackageDependencies.Add(d.Name);
                    }
                }
                if (root.TryGetProperty("devDependencies", out var devDepEl) && devDepEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var d in devDepEl.EnumerateObject())
                    {
                        if (!evidence.PackageDependencies.Contains(d.Name))
                            evidence.PackageDependencies.Add(d.Name);
                    }
                }

                // Betikler (scripts)
                if (root.TryGetProperty("scripts", out var scriptsEl) && scriptsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var s in scriptsEl.EnumerateObject())
                    {
                        var sVal = s.Value.GetString() ?? "";
                        evidence.PackageScripts[s.Name] = sVal;
                        if (!evidence.DiscoveredOperations.Contains($"npm run {s.Name}"))
                            evidence.DiscoveredOperations.Add($"npm run {s.Name}");
                    }
                }

                // Anahtar Kelimeler (keywords)
                if (root.TryGetProperty("keywords", out var kwEl) && kwEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in kwEl.EnumerateArray())
                    {
                        var kwStr = k.GetString();
                        if (!string.IsNullOrWhiteSpace(kwStr) && !evidence.DiscoveredKeywords.Contains(kwStr.ToLower()))
                            evidence.DiscoveredKeywords.Add(kwStr.ToLower());
                    }
                }

                // Katkıda Bulunulan Komutlar (contributes.commands)
                if (root.TryGetProperty("contributes", out var contribEl) && contribEl.ValueKind == JsonValueKind.Object)
                {
                    if (contribEl.TryGetProperty("commands", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in cmdEl.EnumerateArray())
                        {
                            var cmdId = c.TryGetProperty("command", out var idProp) ? idProp.GetString() ?? "" : "";
                            var cmdTitle = c.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                            if (!string.IsNullOrWhiteSpace(cmdId))
                            {
                                evidence.ContributesCommands.Add((cmdId, cmdTitle));
                                if (!evidence.DiscoveredOperations.Contains(cmdId))
                                    evidence.DiscoveredOperations.Add(cmdId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Parse package.json error: {ex.Message}");
            }
        }

        // ==========================================
        // 3. GÜVENLİ İKİLİ SEMBOL & DİZE AYRIŞTIRICI
        // ==========================================
        public AppBinarySymbols ExtractBinarySymbolsSafely(string filePath)
        {
            var symbols = new AppBinarySymbols();
            try
            {
                using var stream = File.OpenRead(filePath);
                long fileLength = stream.Length;

                var regions = new List<(long offset, int size)>();

                // 1. PE Section Headers ile .rdata ve .rsrc bölümlerini hedefle
                try
                {
                    using var peReader = new PEReader(stream);
                    if (peReader.PEHeaders != null)
                    {
                        foreach (var section in peReader.PEHeaders.SectionHeaders)
                        {
                            var secName = section.Name.Trim('\0', ' ');
                            if (secName.Equals(".rdata", StringComparison.OrdinalIgnoreCase) ||
                                secName.Equals(".rsrc", StringComparison.OrdinalIgnoreCase) ||
                                secName.Equals(".data", StringComparison.OrdinalIgnoreCase))
                            {
                                if (section.PointerToRawData > 0 && section.SizeOfRawData > 0)
                                {
                                    long secOffset = section.PointerToRawData;
                                    int secSize = (int)Math.Min((long)section.SizeOfRawData, 6 * 1024 * 1024);
                                    if (secOffset + secSize <= fileLength)
                                    {
                                        regions.Add((secOffset, secSize));
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // PE bölümü bulunamadıysa pencereleme yap
                if (regions.Count == 0)
                {
                    if (fileLength <= 8 * 1024 * 1024)
                    {
                        regions.Add((0, (int)fileLength));
                    }
                    else
                    {
                        regions.Add((0, (int)Math.Min(fileLength, 4 * 1024 * 1024)));
                        long tailStart = Math.Max(0, fileLength - 2 * 1024 * 1024);
                        regions.Add((tailStart, (int)(fileLength - tailStart)));
                    }
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (offset, size) in regions)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                    int remaining = size;
                    int chunkSize = Math.Min(remaining, 512 * 1024);
                    byte[] buf = new byte[chunkSize];

                    while (remaining > 0)
                    {
                        int toRead = Math.Min(remaining, chunkSize);
                        int read = stream.Read(buf, 0, toRead);
                        if (read <= 0) break;

                        // ASCII / UTF-8 Tara
                        ExtractStringsFromBuffer(buf, read, false, symbols, seen);

                        // UTF-16LE Tara
                        ExtractStringsFromBuffer(buf, read, true, symbols, seen);

                        remaining -= read;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Safe symbol extraction error: {ex.Message}");
            }

            return symbols;
        }

        private void ExtractStringsFromBuffer(byte[] buf, int length, bool utf16, AppBinarySymbols symbols, HashSet<string> seen)
        {
            int step = utf16 ? 2 : 1;
            var sb = new StringBuilder(64);

            for (int i = 0; i <= length - step; i += step)
            {
                char c = utf16 ? (char)(buf[i] | (buf[i + 1] << 8)) : (char)buf[i];

                bool isPrintable = (c >= 0x20 && c <= 0x7E) || (c >= 0x00C0 && c <= 0x017F);

                if (isPrintable)
                {
                    sb.Append(c);
                    if (sb.Length > 80) sb.Clear();
                }
                else
                {
                    if (sb.Length >= 4 && sb.Length <= 60)
                    {
                        ProcessCandidateString(sb.ToString(), symbols, seen);
                    }
                    sb.Clear();
                }
            }

            if (sb.Length >= 4 && sb.Length <= 60)
            {
                ProcessCandidateString(sb.ToString(), symbols, seen);
            }
        }

        private void ProcessCandidateString(string raw, AppBinarySymbols symbols, HashSet<string> seen)
        {
            var val = raw.Trim();
            if (val.Length < 4 || val.Length > 60) return;

            // Gürültü / CRT / Compiler Sembol Filtresi
            if (val.StartsWith("?") || val.StartsWith("__") || val.StartsWith("$_") || val.StartsWith("??") || val.Contains("@@")) return;
            if (val.Contains("bad_alloc") || val.Contains("out_of_range") || val.Contains("invalid string") ||
                val.Contains("vector<") || val.Contains("map/set<") || val.Contains("runtime error") ||
                val.Contains("Unknown exception") || val.Contains("std::") || val.Contains("operator new")) return;
            if (val.StartsWith("Rtl") || val.StartsWith("Nt") || val.StartsWith("api-ms-") || val.StartsWith("ext-ms-") ||
                val.Contains("UnhandledException") || val.Contains("IsDebuggerPresent") || val.Contains("QueryPerformanceCounter")) return;
            if (Regex.IsMatch(val, "^[0-9a-fA-F]{16,}$") || Regex.IsMatch(val, "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}")) return;
            if (val.Contains(":\\Windows") || val.Contains(":\\Program Files") || val.Contains("/build/") || val.Contains("/work/")) return;

            if (!Regex.IsMatch(val, "[A-Za-zÇĞİÖŞÜçğıöşü]")) return;

            if (!seen.Add(val)) return;
            symbols.RawStrings.Add(val);

            // Kütüphaneler
            if (val.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!symbols.Libraries.Contains(val))
                    symbols.Libraries.Add(val);
                return;
            }

            // Operasyon / Metot / Komut Tespiti
            if (val.EndsWith("_Click", StringComparison.OrdinalIgnoreCase) ||
                val.EndsWith("Async", StringComparison.OrdinalIgnoreCase) ||
                val.EndsWith("Action", StringComparison.OrdinalIgnoreCase) ||
                val.EndsWith("Command", StringComparison.OrdinalIgnoreCase) ||
                val.EndsWith("Handler", StringComparison.OrdinalIgnoreCase) ||
                val.StartsWith("Run") || val.StartsWith("Scan") || val.StartsWith("Toggle") ||
                val.StartsWith("Start") || val.StartsWith("Stop") || val.StartsWith("Open") ||
                val.StartsWith("Close") || val.StartsWith("Create") || val.StartsWith("Delete") ||
                val.StartsWith("Save") || val.StartsWith("Load") || val.StartsWith("Fetch") ||
                val.StartsWith("Build") || val.StartsWith("Debug") || val.StartsWith("Connect") ||
                val.StartsWith("Launch") || val.StartsWith("Execute"))
            {
                if (val.Length >= 5 && !val.Contains("<") && !val.Contains("$"))
                {
                    symbols.Operations.Add(val);
                }
            }

            // UI Kontrol Tespiti
            if (Regex.IsMatch(val, @"^((btn|txt|cmb|grid|lbl|chk|tab|pnl|menu|nav|bar|view|dlg)[A-Z]\w*|(Btn|Txt|Cmb|Grid|Lbl|Chk|Tab|Pnl|Menu|Nav|Bar|View|Dlg)[A-Z]\w*)$"))
            {
                symbols.Controls.Add(val);
            }

            // Domain Kelimesi
            var lower = val.ToLower();
            if (lower is "ide" or "agent" or "editor" or "workspace" or "terminal" or "debug" or "code" or "mcp" or "compiler" or "git" or "bridge" or "language" or "server" or "client" or "workflow" or "project")
            {
                symbols.DomainKeywords.Add(lower);
            }
        }

        // ==========================================
        // 4. KANITA DAYALI DOMAIN TESPİTİ
        // ==========================================
        public string DetectDomainFromEvidence(AppRawEvidence evidence)
        {
            var text = $"{evidence.LiveMainWindowTitle} {string.Join(" ", evidence.LiveDiscoveredMenus)} {string.Join(" ", evidence.LiveDiscoveredTexts)} {evidence.PackageProductName} {evidence.ProductName} {evidence.FileDescription} {evidence.PackageDescription} {evidence.Comments} {string.Join(" ", evidence.DiscoveredKeywords)} {string.Join(" ", evidence.PackageDependencies)}".ToLower();

            if (text.Contains("not defteri") || text.Contains("notepad"))
            {
                return "Metin Düzenleme & Belge Not Defteri";
            }

            if (text.Contains("antigravity") || text.Contains("agentic") || text.Contains("ide") || text.Contains("code") ||
                evidence.PackageDependencies.Any(d => d.Contains("mcp") || d.Contains("connect") || d.Contains("protobuf")) ||
                evidence.AsarFilesList.Any(f => f.Contains("languageServer") || f.Contains("hostBridge")))
            {
                return "Yapay Zeka Destekli Kodlama & Ajan Masaüstü Geliştirme Platformu (IDE)";
            }

            if (text.Contains("video") || text.Contains("audio") || text.Contains("player") || text.Contains("media") || text.Contains("codec"))
            {
                return "Medya, Ses & Video İşleme";
            }

            if (text.Contains("cad") || text.Contains("render") || text.Contains("mesh") || text.Contains("3d"))
            {
                return "3D Tasarım & Görselleştirme";
            }

            if (text.Contains("browser") || text.Contains("chromium") || text.Contains("tarayıcı"))
            {
                return "Web Gezintisi & İnternet Tarayıcısı";
            }

            if (text.Contains("security") || text.Contains("antivirus") || text.Contains("güvenlik"))
            {
                return "Siber Güvenlik & Sistem Koruma";
            }

            if (text.Contains("fatura") || text.Contains("muhasebe") || text.Contains("cari") || text.Contains("bakiye") || text.Contains("ledger"))
            {
                return "Kurumsal Finans, Muhasebe & ERP";
            }

            var baseName = !string.IsNullOrWhiteSpace(evidence.PackageProductName)
                ? evidence.PackageProductName
                : !string.IsNullOrWhiteSpace(evidence.ProductName)
                    ? evidence.ProductName
                    : Path.GetFileNameWithoutExtension(evidence.FileName);

            if (baseName.Contains("Windows®", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(evidence.OriginalFilename))
            {
                baseName = Path.GetFileNameWithoutExtension(evidence.OriginalFilename);
            }

            if (evidence.IsElectronApp)
            {
                return $"{baseName} Modern Masaüstü Uygulaması";
            }

            if (evidence.IsDotNet)
            {
                return $"{baseName} .NET Masaüstü Uygulaması";
            }

            return $"{baseName} Windows Masaüstü Uygulaması";
        }

        // ==========================================
        // 5. AI DESTEKLİ GERÇEK SENTEZ (GEMINI & OLLAMA)
        // ==========================================
        private async Task<(List<AppScreenInfo> Screens, List<AppToolItem> Tools, string Domain, string Description, string Engine)> SynthesizeWithAIAsync(
            AppRawEvidence evidence,
            string productTitle,
            string description,
            string domain,
            string? selectedModel = null)
        {
            var screens = new List<AppScreenInfo>();
            var tools = new List<AppToolItem>();

            var evidenceSummary = new StringBuilder();
            evidenceSummary.AppendLine("=== DOSYA VE UYGULAMA BİLGİLERİ ===");
            evidenceSummary.AppendLine($"- Dosya Adı: {evidence.FileName}");
            evidenceSummary.AppendLine($"- Ürün Adı (ProductName): {productTitle}");
            evidenceSummary.AppendLine($"- Dosya Açıklaması (FileDescription): {description}");
            evidenceSummary.AppendLine($"- Geliştirici Şirket: {evidence.CompanyName}");
            evidenceSummary.AppendLine($"- Sürüm: {evidence.FileVersion}");
            evidenceSummary.AppendLine($"- Telif Hakkı: {evidence.LegalCopyright}");
            evidenceSummary.AppendLine($"- Mimari & Altsistem: {evidence.Architecture} / {evidence.Subsystem}");
            evidenceSummary.AppendLine($"- Uygulama Tipi: {(evidence.IsElectronApp ? "Electron / Chromium Modern Masaüstü Uygulaması" : evidence.IsDotNet ? ".NET Masaüstü Uygulaması" : "Windows Yerel Uygulaması")}");

            if (evidence.IsElectronApp)
            {
                evidenceSummary.AppendLine("\n=== ELECTRON / NODE (package.json) BİLGİLERİ ===");
                evidenceSummary.AppendLine($"- Paket Adı: {evidence.PackageName}");
                evidenceSummary.AppendLine($"- Paket Açıklaması: {evidence.PackageDescription}");
                evidenceSummary.AppendLine($"- Paket Sürümü: {evidence.PackageVersion}");
                evidenceSummary.AppendLine($"- Yazar: {evidence.PackageAuthor}");
                evidenceSummary.AppendLine($"- Ana Sayfa: {evidence.PackageHomepage}");
                evidenceSummary.AppendLine($"- Ana Giriş (Main): {evidence.PackageMain}");
                if (evidence.PackageDependencies.Count > 0)
                {
                    evidenceSummary.AppendLine($"- Tespit Edilen Bağımlılıklar: {string.Join(", ", evidence.PackageDependencies.Take(12))}");
                }
                if (evidence.PackageScripts.Count > 0)
                {
                    evidenceSummary.AppendLine($"- Tanımlı NPM Betikleri: {string.Join(", ", evidence.PackageScripts.Keys.Take(10))}");
                }
                if (evidence.ContributesCommands.Count > 0)
                {
                    evidenceSummary.AppendLine($"- Tanımlı Komutlar: {string.Join(", ", evidence.ContributesCommands.Take(12).Select(c => $"{c.Command} ({c.Title})"))}");
                }
                if (!string.IsNullOrEmpty(evidence.UpdaterUrl))
                {
                    evidenceSummary.AppendLine($"- Güncelleme Servisi: {evidence.UpdaterUrl}");
                }
            }

            if (evidence.AsarFilesList.Count > 0)
            {
                evidenceSummary.AppendLine("\n=== UYGULAMANIN İÇİNDE BULUNAN GERÇEK MODÜLLER ===");
                evidenceSummary.AppendLine(string.Join(", ", evidence.AsarFilesList.Take(35)));
            }

            if (evidence.ImportedDlls.Count > 0)
            {
                evidenceSummary.AppendLine("\n=== İÇE AKTARILAN KÜTÜPHANELER ===");
                evidenceSummary.AppendLine(string.Join(", ", evidence.ImportedDlls.Take(15)));
            }

            if (evidence.ImportedFunctions.Count > 0)
            {
                evidenceSummary.AppendLine("\n=== İÇE AKTARILAN SİSTEM / WIN32 APİ FONKSİYONLARI ===");
                evidenceSummary.AppendLine(string.Join(", ", evidence.ImportedFunctions.Take(25)));
            }

            if (evidence.DiscoveredOperations.Count > 0)
            {
                evidenceSummary.AppendLine("\n=== DOSYADAN ÇIKARILAN GERÇEK METOT / İŞLEMLER ===");
                evidenceSummary.AppendLine(string.Join(", ", evidence.DiscoveredOperations.Take(25)));
            }

            if (evidence.DiscoveredControls.Count > 0)
            {
                evidenceSummary.AppendLine("\n=== DOSYADAN ÇIKARILAN ARAYÜZ KONTROLLERİ ===");
                evidenceSummary.AppendLine(string.Join(", ", evidence.DiscoveredControls.Take(20)));
            }

            if (evidence.WasLiveInspected)
            {
                evidenceSummary.AppendLine("\n=== CANLI ÇALIŞMA & WINDOWS UI AUTOMATION KANITLARI ===");
                evidenceSummary.AppendLine($"- Canlı Ana Pencere Başlığı: {evidence.LiveMainWindowTitle}");
                if (evidence.LiveWindowTitles.Count > 0)
                    evidenceSummary.AppendLine($"- Tespit Edilen Canlı Pencereler: {string.Join(", ", evidence.LiveWindowTitles)}");
                if (evidence.LiveDiscoveredMenus.Count > 0)
                    evidenceSummary.AppendLine($"- Canlı Yakalanan Menüler: {string.Join(", ", evidence.LiveDiscoveredMenus.Take(15))}");
                if (evidence.LiveDiscoveredControls.Count > 0)
                    evidenceSummary.AppendLine($"- Canlı Yakalanan UI Kontrolleri: {string.Join(", ", evidence.LiveDiscoveredControls.Take(15).Select(c => $"{c.Name} ({c.Label})"))}");
                if (evidence.LiveDiscoveredTexts.Count > 0)
                    evidenceSummary.AppendLine($"- Canlı Ekran Metinleri: {string.Join(" | ", evidence.LiveDiscoveredTexts.Take(12))}");
            }

            var prompt = $@"{evidenceSummary}

GÖREV:
Yukarıdaki kanıtları inceleyerek bu uygulamanın GERÇEK amacını öğren ve yapay zeka ajanlarının bu uygulamayı yönetebilmesi için:
1. Kısa ve net uygulama tanımı (app_description).
2. Gerçek sektörel / fonksiyonel alan (domain). (Kanıtlara dayanan gerçek alan tanımı).
3. Bu uygulamaya ait 3 veya 4 adet mantıksal Ekran / Modül (screens).
4. Yapay zekanın kullanıcı adına çalıştırabileceği 3 veya 4 adet Function Calling Aracı (tools).

KESİNLİKLE UYULMASI GEREKEN KURALLAR:
- ASLA başka uygulamaların şablon verilerini (Ağ ping testi, hız testi, DoH oyun modu, fatura, cari hesap, muhasebe vb.) uydurma!
- YALNIZCA yukarıda sunulan gerçek kanıtlara dayan.
- SADECE ve YALNIZCA geçerli JSON formatında şu şemayla yanıt ver:
{{
  ""app_description"": ""..."",
  ""domain"": ""..."",
  ""screens"": [
    {{
      ""title"": ""..."",
      ""screen_name"": ""..."",
      ""purpose"": ""..."",
      ""controls"": [
        {{ ""name"": ""..."", ""type"": ""Button"", ""label"": ""..."", ""description"": ""..."" }}
      ],
      ""operations"": [ ""..."" ]
    }}
  ],
  ""tools"": [
    {{
      ""name"": ""slug_formatinda_tool_adi"",
      ""title"": ""..."",
      ""description"": ""..."",
      ""target_screen"": ""..."",
      ""parameters"": {{
        ""param1"": {{ ""type"": ""string"", ""description"": ""..."" }}
      }},
      ""required"": [ ""param1"" ]
    }}
  ]
}}";

            string? jsonResponse = null;
            string engineUsed = "";

            bool isGeminiSelected = !string.IsNullOrWhiteSpace(selectedModel) && selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);
            bool isOllamaSelected = !string.IsNullOrWhiteSpace(selectedModel) && !selectedModel.StartsWith("gemini:", StringComparison.OrdinalIgnoreCase);

            // 1. Kullanıcı Gemini seçtiyse
            if (isGeminiSelected && _geminiClient != null && !string.IsNullOrWhiteSpace(_geminiClient.ApiKey))
            {
                var modelId = selectedModel!.Substring("gemini:".Length);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    jsonResponse = await _geminiClient.GenerateTextAsync(modelId, prompt, "Sen uzman bir tersine mühendislik ve yazılım mimarisisin. Kesinlikle verilen kanıtlara sadık kal ve sadece geçerli JSON üret.", cts.Token);
                    engineUsed = $"Google Gemini ({modelId}) Destekli İkili Semantik Analiz Motoru";
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Gemini app analysis error: {ex.Message}");
                    jsonResponse = null;
                }
            }
            // 2. Kullanıcı Ollama modeli seçtiyse (örn. qwen2.5-coder:1.5b)
            else if (isOllamaSelected && _ollamaClient != null)
            {
                try
                {
                    if (await _ollamaClient.CheckConnectionAsync())
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        jsonResponse = await _ollamaClient.GenerateTextAsync(selectedModel!, prompt, cts.Token);
                        engineUsed = $"Yerel Ollama AI ({selectedModel}) Destekli Semantik Analiz Motoru";
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Selected Ollama app analysis error: {ex.Message}");
                    jsonResponse = null;
                }
            }

            // 3. Fallback cascade: Eğer seçilen model yanıt vermediyse veya model belirtilmediyse
            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                // Önce Gemini dene
                if (_geminiClient != null && !string.IsNullOrWhiteSpace(_geminiClient.ApiKey))
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        jsonResponse = await _geminiClient.GenerateTextAsync("gemini-3.7-flash", prompt, "Sen uzman bir tersine mühendislik ve yazılım mimarisisin. Kesinlikle verilen kanıtlara sadık kal ve sadece geçerli JSON üret.", cts.Token);
                        engineUsed = "Google Gemini AI Destekli İkili Semantik Analiz Motoru";
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Gemini fallback error: {ex.Message}");
                        jsonResponse = null;
                    }
                }

                // Gemini başarısız olduysa yerel Ollama ile dene
                if (string.IsNullOrWhiteSpace(jsonResponse) && _ollamaClient != null)
                {
                    try
                    {
                        if (await _ollamaClient.CheckConnectionAsync())
                        {
                            var models = await _ollamaClient.GetAvailableModelsAsync();
                            var modelToUse = models.FirstOrDefault(m => m.Contains("coder") || m.Contains("alp") || m.Contains("qwen") || m.Contains("gemma"))
                                             ?? models.FirstOrDefault() ?? "qwen2.5-coder:1.5b";

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            jsonResponse = await _ollamaClient.GenerateTextAsync(modelToUse, prompt, cts.Token);
                            engineUsed = $"Yerel Ollama AI ({modelToUse}) Destekli Semantik Analiz Motoru";
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ollama fallback error: {ex.Message}");
                        jsonResponse = null;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(jsonResponse))
            {
                try
                {
                    var cleanJson = jsonResponse.Trim();
                    if (cleanJson.StartsWith("```json")) cleanJson = cleanJson[7..];
                    if (cleanJson.StartsWith("```")) cleanJson = cleanJson[3..];
                    if (cleanJson.EndsWith("```")) cleanJson = cleanJson[..^3];
                    cleanJson = cleanJson.Trim();

                    using var doc = JsonDocument.Parse(cleanJson);
                    var root = doc.RootElement;

                    string newDesc = root.TryGetProperty("app_description", out var descEl) ? descEl.GetString() ?? "" : "";
                    string newDomain = root.TryGetProperty("domain", out var domEl) ? domEl.GetString() ?? "" : "";

                    if (root.TryGetProperty("screens", out var screensEl))
                    {
                        foreach (var sEl in screensEl.EnumerateArray())
                        {
                            var s = new AppScreenInfo
                            {
                                Title = sEl.GetProperty("title").GetString() ?? "",
                                ScreenName = sEl.TryGetProperty("screen_name", out var sn) ? sn.GetString() ?? "" : "ScreenForm",
                                Purpose = sEl.GetProperty("purpose").GetString() ?? ""
                            };

                            if (sEl.TryGetProperty("controls", out var ctrlsEl))
                            {
                                foreach (var cEl in ctrlsEl.EnumerateArray())
                                {
                                    s.Controls.Add(new AppControlInfo
                                    {
                                        Name = cEl.GetProperty("name").GetString() ?? "",
                                        ControlType = cEl.TryGetProperty("type", out var ct) ? ct.GetString() ?? "Button" : "Button",
                                        Label = cEl.TryGetProperty("label", out var cl) ? cl.GetString() ?? "" : "",
                                        ActionDescription = cEl.TryGetProperty("description", out var cd) ? cd.GetString() ?? "" : "",
                                        ParentScreen = s.ScreenName
                                    });
                                }
                            }

                            if (sEl.TryGetProperty("operations", out var opsEl))
                            {
                                foreach (var op in opsEl.EnumerateArray())
                                {
                                    s.Operations.Add(op.GetString() ?? "");
                                }
                            }

                            screens.Add(s);
                        }
                    }

                    if (root.TryGetProperty("tools", out var toolsEl))
                    {
                        var slug = Regex.Replace(productTitle.ToLower(), "[^a-z0-9]", "_").Trim('_');
                        if (string.IsNullOrWhiteSpace(slug)) slug = "app";

                        foreach (var tEl in toolsEl.EnumerateArray())
                        {
                            var rawName = tEl.GetProperty("name").GetString() ?? "";
                            var cleanName = Regex.Replace(rawName.ToLower(), "[^a-z0-9_]", "_").Trim('_');
                            if (!cleanName.StartsWith(slug))
                            {
                                cleanName = $"{slug}_{cleanName}";
                            }

                            var tool = new AppToolItem
                            {
                                Name = cleanName,
                                Title = tEl.GetProperty("title").GetString() ?? "",
                                Description = tEl.GetProperty("description").GetString() ?? "",
                                TargetScreen = tEl.TryGetProperty("target_screen", out var ts) ? ts.GetString() ?? "" : ""
                            };

                            if (tEl.TryGetProperty("parameters", out var paramsEl))
                            {
                                foreach (var prop in paramsEl.EnumerateObject())
                                {
                                    var propDict = new Dictionary<string, object>();
                                    foreach (var sub in prop.Value.EnumerateObject())
                                    {
                                        propDict[sub.Name] = sub.Value.ValueKind == JsonValueKind.String ? sub.Value.GetString()! : sub.Value.ToString()!;
                                    }
                                    tool.Parameters[prop.Name] = propDict;
                                }
                            }

                            if (tEl.TryGetProperty("required", out var reqEl))
                            {
                                foreach (var r in reqEl.EnumerateArray())
                                {
                                    tool.RequiredParameters.Add(r.GetString() ?? "");
                                }
                            }

                            tools.Add(tool);
                        }
                    }

                    if (screens.Count > 0 && tools.Count > 0)
                    {
                        return (screens, tools, newDomain, newDesc, engineUsed);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"JSON parsing error: {ex.Message}");
                }
            }

            return (screens, tools, domain, description, "");
        }

        // ==========================================
        // 6. KANITA DAYALI DETERMINİSTİK SENTEZ
        // KESİNLİKLE ŞABLON YOK! SADECE GERÇEK VERİ
        // ==========================================
        public (List<AppScreenInfo> Screens, List<AppToolItem> Tools, string Domain) SynthesizeFromEvidenceDeterministically(
            AppRawEvidence evidence,
            string productTitle,
            string description)
        {
            var slug = Regex.Replace(productTitle.ToLower(), "[^a-z0-9]", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(slug)) slug = "app";

            var screens = new List<AppScreenInfo>();
            var tools = new List<AppToolItem>();
            string domain = DetectDomainFromEvidence(evidence);

            // DURUM -1: Python / PyInstaller ve İlişkili Masaüstü Script Varlıkları (AlpFinans vb.)
            if (evidence.IsPythonPyInstallerApp && evidence.DiscoveredScriptScreens.Count > 0)
            {
                screens = evidence.DiscoveredScriptScreens;
                domain = !string.IsNullOrWhiteSpace(domain) && domain != "Genel / Masaüstü Uygulamaları" ? domain : "Finans / Masaüstü Uygulamaları";

                tools.Add(new AppToolItem
                {
                    Name = "fatura_olustur_ekle",
                    Title = $"{productTitle} Fatura Oluştur & Ekle",
                    Description = $"{productTitle} uygulamasına ve veritabanına yeni fatura veya gider kaydı ekler. KDV (%1, %10, %20), matrah ve genel toplam tutarlarını hesaplayarak muhasebe defterine kaydeder.",
                    TargetScreen = screens.FirstOrDefault(s => s.ScreenName.Contains("Expense"))?.Title ?? "Faturalar & Gider Hakları",
                    Parameters = new Dictionary<string, object>
                    {
                        ["tutar"] = new { type = "string", description = "Fatura tutarı (örn: '15000', '15 bin tl', '15.000 ₺')" },
                        ["kdv"] = new { type = "string", description = "KDV oranı (örn: '%1', '%10', '%20')" },
                        ["musteri"] = new { type = "string", description = "Cari / Müşteri / Firma adı (örn: 'Genel Müşteri', 'ABC Ltd.')" },
                        ["aciklama"] = new { type = "string", description = "Fatura açıklaması veya gider türü" },
                        ["faturaNo"] = new { type = "string", description = "İsteğe bağlı fatura numarası (örn: 'FAT-2026-0001')" }
                    },
                    RequiredParameters = new List<string> { "tutar", "kdv" }
                });

                tools.Add(new AppToolItem
                {
                    Name = "fatura_listele",
                    Title = $"{productTitle} Faturaları Listele",
                    Description = $"{productTitle} sisteminde ve veritabanında kayıtlı olan faturaları ve gider belgelerini listeler.",
                    TargetScreen = screens.FirstOrDefault(s => s.ScreenName.Contains("Expense"))?.Title ?? "Faturalar & Gider Hakları",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filtre"] = new { type = "string", description = "Arama veya filtreleme terimi" }
                    },
                    RequiredParameters = new List<string>()
                });

                tools.Add(new AppToolItem
                {
                    Name = "gelir_ekle",
                    Title = $"{productTitle} Gelir Kaydı Ekle",
                    Description = $"{productTitle} gelir modülü ve veritabanına yeni gelir veya hakediş kaydı ekler.",
                    TargetScreen = screens.FirstOrDefault(s => s.ScreenName.Contains("Income"))?.Title ?? "Gelirler & Hakediş",
                    Parameters = new Dictionary<string, object>
                    {
                        ["tutar"] = new { type = "string", description = "Gelir tutarı" },
                        ["baslik"] = new { type = "string", description = "Gelir başlığı veya hakediş açıklaması" },
                        ["musteri"] = new { type = "string", description = "Müşteri veya kaynak cari adı" }
                    },
                    RequiredParameters = new List<string> { "tutar" }
                });

                tools.Add(new AppToolItem
                {
                    Name = "vergi_finans_ozeti",
                    Title = $"{productTitle} Vergi & Finansal Durum Özeti",
                    Description = $"{productTitle} veritabanından toplam ciro, giderler, hesaplanan/indirilecek KDV ve ödenecek vergi durumunu sorgular.",
                    TargetScreen = screens.FirstOrDefault(s => s.ScreenName.Contains("Dashboard"))?.Title ?? "Genel Bakış & Vergi",
                    Parameters = new Dictionary<string, object>
                    {
                        ["donem"] = new { type = "string", description = "Vergi dönemi veya yılı (varsayılan: cari yıl)" }
                    },
                    RequiredParameters = new List<string>()
                });

                tools.Add(new AppToolItem
                {
                    Name = "uygulamayi_baslat",
                    Title = $"{productTitle} Uygulamasını Başlat",
                    Description = $"{productTitle} masaüstü uygulamasını gerçek yürütülebilir dosyasından başlatır ve pencereyi öne getirir.",
                    TargetScreen = screens[0].Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["parametre"] = new { type = "string", description = "Başlatma parametresi" }
                    },
                    RequiredParameters = new List<string>()
                });

                tools.Add(new AppToolItem
                {
                    Name = "desktop_mouse_keyboard_control",
                    Title = "Masaüstü Fare ve Klavye Kontrolü",
                    Description = "Masaüstünde fare tıklaması, hareket, sürükleme, kaydırma ve klavye kısayollarını yürütür.",
                    TargetScreen = screens[0].Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["action"] = new { type = "string", description = "Eylem: click, move, drag, scroll, hotkey, type_text" },
                        ["x"] = new { type = "integer", description = "Fare X koordinatı" },
                        ["y"] = new { type = "integer", description = "Fare Y koordinatı" },
                        ["button"] = new { type = "string", description = "Buton: left, right, double" },
                        ["hotkey"] = new { type = "string", description = "Kısayol: ctrl+s, alt+f4, enter vb." },
                        ["text"] = new { type = "string", description = "Yazılacak metin" }
                    },
                    RequiredParameters = new List<string> { "action" }
                });

                return (screens, tools, domain);
            }

            // DURUM 0: Canlı Çalıştırma & Windows UI Automation ile Keşfedilen Gerçek Ekranlar ve Kontroller (Native Win32, WPF vb.)
            if (evidence.WasLiveInspected && (evidence.LiveDiscoveredControls.Count > 0 || evidence.LiveDiscoveredMenus.Count > 0) && !evidence.IsElectronApp && (!evidence.IsDotNet || evidence.DotNetScreens.Count == 0))
            {
                var mainTitle = !string.IsNullOrWhiteSpace(evidence.LiveMainWindowTitle)
                    ? evidence.LiveMainWindowTitle
                    : (!string.IsNullOrWhiteSpace(evidence.LiveWindowTitles.FirstOrDefault()) ? evidence.LiveWindowTitles[0] : $"{productTitle} Ana Penceresi");

                // 1. Ana Canlı Arayüz Ekranı
                var mainScreenControls = new List<AppControlInfo>();
                mainScreenControls.AddRange(evidence.LiveDiscoveredControls.Take(15));

                var mainOps = new List<string>();
                mainOps.AddRange(evidence.LiveDiscoveredMenus.Take(6));
                if (mainOps.Count == 0 && evidence.DiscoveredOperations.Count > 0)
                {
                    mainOps.AddRange(evidence.DiscoveredOperations.Take(5));
                }
                if (mainOps.Count == 0)
                {
                    mainOps.Add("Ana Pencere Etkileşimi");
                }

                var sMain = new AppScreenInfo
                {
                    ScreenName = "LiveMainWindowView",
                    Title = $"{productTitle} ({mainTitle})",
                    Purpose = $"{productTitle} uygulamasının canlı çalışan ana arayüzü ('{mainTitle}'). Canlı Windows UI Automation ile {evidence.LiveDiscoveredControls.Count} adet kontrol ve {evidence.LiveDiscoveredMenus.Count} menü tespit edilmiştir.",
                    Controls = mainScreenControls,
                    Operations = mainOps
                };
                screens.Add(sMain);

                tools.Add(new AppToolItem
                {
                    Name = $"{slug}_arayuz_denetle",
                    Title = $"{productTitle} Arayüz Denetim Aracı",
                    Description = $"{productTitle} uygulamasının canlı penceresini ('{mainTitle}') odaklar, buton ve kontrollerini yönetir.",
                    TargetScreen = sMain.Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["eylem"] = new { type = "string", description = "Gerçekleştirilecek arayüz işlemi ('odakla', 'guncelle', 'tikla')" },
                        ["kontrol"] = new { type = "string", description = $"Hedef kontrol adı ({string.Join(", ", evidence.LiveDiscoveredControls.Take(4).Select(c => c.Label))})" }
                    },
                    RequiredParameters = new List<string>()
                });

                // 2. Menü ve Fonksiyonel İşlemler Modülü
                if (evidence.LiveDiscoveredMenus.Count > 0)
                {
                    var menuControls = evidence.LiveDiscoveredMenus.Take(8).Select(m => new AppControlInfo
                    {
                        Name = $"mnu_{Regex.Replace(m.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Menü Butonu)",
                        Label = m,
                        ActionDescription = $"{m} menü seçeneğini tetikler.",
                        ParentScreen = "LiveOperationsView"
                    }).ToList();

                    var sMenu = new AppScreenInfo
                    {
                        ScreenName = "LiveOperationsView",
                        Title = $"{productTitle} Menü ve Fonksiyonel İşlemler",
                        Purpose = $"{productTitle} uygulamasının canlı yakalanan menü seçenekleri ve komut ögeleri: {string.Join(", ", evidence.LiveDiscoveredMenus)}.",
                        Controls = menuControls,
                        Operations = evidence.LiveDiscoveredMenus.Take(8).ToList()
                    };
                    screens.Add(sMenu);

                    tools.Add(new AppToolItem
                    {
                        Name = $"{slug}_menu_islemi_sec",
                        Title = $"{productTitle} Menü Seçim Aracı",
                        Description = $"{productTitle} uygulamasındaki üst menüleri ({string.Join(", ", evidence.LiveDiscoveredMenus.Take(5))}) tetikler.",
                        TargetScreen = sMenu.Title,
                        Parameters = new Dictionary<string, object>
                        {
                            ["menuAdi"] = new { type = "string", description = $"Seçilecek menü adı ({string.Join(", ", evidence.LiveDiscoveredMenus.Take(4))})" }
                        },
                        RequiredParameters = new List<string>()
                    });
                }

                // 3. Veri / Metin Düzenleme veya Dosya Yönetimi Modülü
                bool hasEditControls = evidence.LiveDiscoveredControls.Any(c => c.ControlType.Contains("TextBox") || c.ControlType.Contains("Edit") || c.ControlType.Contains("Document"));
                if (hasEditControls)
                {
                    tools.Add(new AppToolItem
                    {
                        Name = $"{slug}_metin_ve_veri_duzenle",
                        Title = $"{productTitle} Metin ve Veri Düzenleme Aracı",
                        Description = $"{productTitle} uygulamasındaki aktif metin alanına veya editöre veri girişi ve düzenleme yapar.",
                        TargetScreen = sMain.Title,
                        Parameters = new Dictionary<string, object>
                        {
                            ["metin"] = new { type = "string", description = "Yazılacak veya düzenlenecek metin içeriği" },
                            ["tuslar"] = new { type = "string", description = "İletilecek klavye kısayolları (örneğin: {ENTER}, {TAB}, ^s)" }
                        },
                        RequiredParameters = new List<string>()
                    });
                }
                else
                {
                    tools.Add(new AppToolItem
                    {
                        Name = $"{slug}_dosya_ve_belge_islemleri",
                        Title = $"{productTitle} Dosya ve Belge Aracı",
                        Description = $"{productTitle} uygulamasında dosya açma, kaydetme ve çıktı alma işlemlerini yürütür.",
                        TargetScreen = sMain.Title,
                        Parameters = new Dictionary<string, object>
                        {
                            ["dosyaYolu"] = new { type = "string", description = "İşlem yapılacak dosya veya belge yolu" },
                            ["islem"] = new { type = "string", description = "Yürütülecek işlem ('ac', 'kaydet', 'yazdir')" }
                        },
                        RequiredParameters = new List<string>()
                    });
                }

                // 4. İkinci / Üçüncü Açık Pencere veya Dosya İşlemleri Modülü
                var secondaryWindows = evidence.LiveWindowTitles.Where(t => !t.Equals(mainTitle, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (secondaryWindows.Count > 0)
                {
                    int secIdx = 0;
                    foreach (var secTitle in secondaryWindows)
                    {
                        secIdx++;
                        var sSec = new AppScreenInfo
                        {
                            ScreenName = $"LiveSecondaryDialogView_{secIdx}",
                            Title = $"{productTitle} - {secTitle}",
                            Purpose = $"{productTitle} uygulamasının canlı tespit edilen alt pencere veya iletişim kutusu ('{secTitle}').",
                            Controls = new List<AppControlInfo>
                            {
                                new AppControlInfo
                                {
                                    Name = $"btn_dialog_action_{secIdx}",
                                    ControlType = "Button (Buton)",
                                    Label = $"{secTitle} İşlemi",
                                    ActionDescription = $"{secTitle} penceresi aksiyonu.",
                                    ParentScreen = secTitle
                                }
                            },
                            Operations = new List<string> { secTitle }
                        };
                        screens.Add(sSec);
                    }
                }
                else if (screens.Count < 3)
                {
                    // Dosya modülü ekle
                    var sIo = new AppScreenInfo
                    {
                        ScreenName = "FileOperationsView",
                        Title = $"{productTitle} Dosya & Belge Modülü",
                        Purpose = $"{productTitle} uygulamasının dosya açma, kaydetme ve veri işleme işlevleri.",
                        Controls = new List<AppControlInfo>
                        {
                            new AppControlInfo { Name = "btn_dosya_ac", ControlType = "Button (Buton)", Label = "Dosya Aç", ActionDescription = "Belge açar.", ParentScreen = "FileOperationsView" },
                            new AppControlInfo { Name = "btn_dosya_kaydet", ControlType = "Button (Buton)", Label = "Kaydet", ActionDescription = "Belge kaydeder.", ParentScreen = "FileOperationsView" }
                        },
                        Operations = new List<string> { "Dosya Aç", "Kaydet" }
                    };
                    screens.Add(sIo);
                }

                return (screens, tools, domain);
            }

            // DURUM 1: .NET Formları ve Ekranları Varsa Doğrudan Kullan
            if (evidence.IsDotNet && evidence.DotNetScreens.Count > 0)
            {
                screens = evidence.DotNetScreens;
                tools = GenerateToolsFromScreens(productTitle, productTitle, screens);
                return (screens, tools, domain);
            }

            // DURUM 2: Electron / Asar / Node.js Varlıkları Varsa
            if (evidence.IsElectronApp || evidence.AsarFilesList.Count > 0 || evidence.PackageDependencies.Count > 0)
            {
                // 1. Ana Modül (Giriş noktası ve birincil arayüz)
                var mainOps = new List<string>();
                if (evidence.PackageScripts.Count > 0)
                {
                    foreach (var kvp in evidence.PackageScripts.Take(3))
                    {
                        mainOps.Add($"npm run {kvp.Key}");
                    }
                }
                if (evidence.ContributesCommands.Count > 0)
                {
                    foreach (var c in evidence.ContributesCommands.Take(3))
                    {
                        mainOps.Add(c.Command);
                    }
                }
                if (mainOps.Count == 0)
                {
                    mainOps.Add(evidence.PackageMain ?? "main.js");
                }

                var mainControls = new List<AppControlInfo>();
                foreach (var op in mainOps.Take(3))
                {
                    mainControls.Add(new AppControlInfo
                    {
                        Name = $"btn_{Regex.Replace(op.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Buton)",
                        Label = BeautifyIdentifier(op),
                        ActionDescription = $"{op} komutunu tetikler.",
                        ParentScreen = "MainAppWorkspaceView"
                    });
                }

                var sMain = new AppScreenInfo
                {
                    ScreenName = "MainAppWorkspaceView",
                    Title = $"{productTitle} Ana Çalışma Alanı",
                    Purpose = $"{productTitle} ({evidence.FileVersion}) ana kullanıcı penceresi ve operasyon merkezi. {description}",
                    Controls = mainControls,
                    Operations = mainOps
                };
                screens.Add(sMain);

                tools.Add(new AppToolItem
                {
                    Name = $"{slug}_ana_islemi_yurut",
                    Title = $"{productTitle} Ana İşlem Aracı",
                    Description = $"{productTitle} uygulamasının ana çalışma alanını ve birincil komutlarını çalıştırır.",
                    TargetScreen = sMain.Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["komut"] = new { type = "string", description = $"Çalıştırılacak işlem ({string.Join(", ", mainOps.Take(3))})" }
                    },
                    RequiredParameters = new List<string>()
                });

                // 2. Asar içindeki gerçek dizin/modül kümelerini tara
                var moduleGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in evidence.AsarFilesList)
                {
                    var parts = f.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var modName = parts[0].Equals("src", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("dist", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("out", StringComparison.OrdinalIgnoreCase)
                            ? parts[1]
                            : parts[0];

                        if (!modName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) && modName.Length >= 3)
                        {
                            if (!moduleGroups.ContainsKey(modName))
                                moduleGroups[modName] = new List<string>();
                            if (moduleGroups[modName].Count < 10)
                                moduleGroups[modName].Add(Path.GetFileName(f));
                        }
                    }
                }

                // En çok dosyaya sahip 2 veya 3 gerçek modülü ekran yap
                foreach (var kvp in moduleGroups.OrderByDescending(g => g.Value.Count).Take(3))
                {
                    var modKey = kvp.Key;
                    var modFiles = kvp.Value;

                    var modTitle = BeautifyIdentifier(modKey);
                    var modScreenName = $"{char.ToUpper(modKey[0])}{modKey[1..]}ModuleView";

                    var modControls = modFiles.Take(3).Select(fn => new AppControlInfo
                    {
                        Name = $"ctrl_{Regex.Replace(fn.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Aksiyon)",
                        Label = BeautifyIdentifier(Path.GetFileNameWithoutExtension(fn)),
                        ActionDescription = $"{fn} modül bileşenini yönetir.",
                        ParentScreen = modScreenName
                    }).ToList();

                    var sMod = new AppScreenInfo
                    {
                        ScreenName = modScreenName,
                        Title = $"{productTitle} {modTitle} Modülü",
                        Purpose = $"{productTitle} uygulamasının {modTitle} operasyonlarını ve ilgili servislerini ({string.Join(", ", modFiles.Take(3))}) yürütür.",
                        Controls = modControls,
                        Operations = modFiles.Take(5).ToList()
                    };
                    screens.Add(sMod);

                    var toolSlug = Regex.Replace(modKey.ToLower(), "[^a-z0-9]", "_").Trim('_');
                    tools.Add(new AppToolItem
                    {
                        Name = $"{slug}_{toolSlug}_islemi",
                        Title = $"{productTitle} {modTitle} Aracı",
                        Description = $"{productTitle} {modTitle} modülü üzerinden ilgili aksiyonları tetikler.",
                        TargetScreen = sMod.Title,
                        Parameters = new Dictionary<string, object>
                        {
                            ["islem"] = new { type = "string", description = $"Yürütülecek işlem ({string.Join(", ", modFiles.Take(3))})" }
                        },
                        RequiredParameters = new List<string>()
                    });
                }

                // Eğer asar modülü az ise ve bağımlılıklar varsa, bağımlılıkları yansıtan servis modülü ekle
                if (screens.Count < 3 && evidence.PackageDependencies.Count > 0)
                {
                    var topDeps = evidence.PackageDependencies.Take(6).ToList();
                    var sDep = new AppScreenInfo
                    {
                        ScreenName = "ServicesIntegrationView",
                        Title = $"{productTitle} Entegrasyon & Servisler",
                        Purpose = $"{productTitle} uygulamasının entegre çalıştığı servisler ve kütüphaneler: {string.Join(", ", topDeps)}.",
                        Controls = topDeps.Take(3).Select(d => new AppControlInfo
                        {
                            Name = $"svc_{Regex.Replace(d.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                            ControlType = "Button (Servis)",
                            Label = BeautifyIdentifier(d.Replace("@", "").Replace("/", " ")),
                            ActionDescription = $"{d} servisini tetikler.",
                            ParentScreen = "ServicesIntegrationView"
                        }).ToList(),
                        Operations = topDeps
                    };
                    screens.Add(sDep);

                    tools.Add(new AppToolItem
                    {
                        Name = $"{slug}_servis_entegrasyonu",
                        Title = $"{productTitle} Servis Entegrasyon Aracı",
                        Description = $"{productTitle} servis ve kütüphane bağlantılarını denetler.",
                        TargetScreen = sDep.Title,
                        Parameters = new Dictionary<string, object>
                        {
                            ["servisAdi"] = new { type = "string", description = "Çağrılacak servis" }
                        },
                        RequiredParameters = new List<string>()
                    });
                }

                return (screens, tools, domain);
            }

            // DURUM 3: Native PE Dosyaları (İkili semboller ve Win32 API'leri)
            var allCandidateOps = new List<string>();
            allCandidateOps.AddRange(evidence.DiscoveredOperations);
            allCandidateOps.AddRange(evidence.ImportedFunctions);

            // Operasyonları mantıksal gruplara ayır (I/O, UI, Sistem/Ağ)
            var ioOps = allCandidateOps.Where(o => o.Contains("File") || o.Contains("Open") || o.Contains("Save") || o.Contains("Read") || o.Contains("Write") || o.Contains("Print")).Distinct().Take(6).ToList();
            var uiOps = allCandidateOps.Where(o => o.Contains("Window") || o.Contains("Text") || o.Contains("Dialog") || o.Contains("Menu") || o.Contains("Draw") || o.Contains("Paint") || o.Contains("Edit")).Distinct().Take(6).ToList();
            var sysOps = allCandidateOps.Where(o => !ioOps.Contains(o) && !uiOps.Contains(o)).Distinct().Take(6).ToList();

            if (ioOps.Count > 0)
            {
                var sIo = new AppScreenInfo
                {
                    ScreenName = "FileOperationsView",
                    Title = $"{productTitle} Dosya & Belge Modülü",
                    Purpose = $"{productTitle} uygulamasının dosya açma, kaydetme ve veri işleme işlevleri.",
                    Controls = ioOps.Take(3).Select(op => new AppControlInfo
                    {
                        Name = $"btn_{Regex.Replace(op.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Buton)",
                        Label = BeautifyIdentifier(op),
                        ActionDescription = $"{op} işlemini yürütür.",
                        ParentScreen = "FileOperationsView"
                    }).ToList(),
                    Operations = ioOps
                };
                screens.Add(sIo);

                tools.Add(new AppToolItem
                {
                    Name = $"{slug}_dosya_islemi_yurut",
                    Title = $"{productTitle} Dosya & Belge Aracı",
                    Description = $"{productTitle} üzerinde dosya açma, kaydetme ve okuma operasyonlarını yürütür.",
                    TargetScreen = sIo.Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["dosyaYolu"] = new { type = "string", description = "İşlem yapılacak dosya veya belge yolu" }
                    },
                    RequiredParameters = new List<string>()
                });
            }

            if (uiOps.Count > 0)
            {
                var sUi = new AppScreenInfo
                {
                    ScreenName = "UiManagementView",
                    Title = $"{productTitle} Arayüz & Görünüm Modülü",
                    Purpose = $"{productTitle} görsel pencereleri, metin düzenleme ve kullanıcı etkileşimlerini denetler.",
                    Controls = uiOps.Take(3).Select(op => new AppControlInfo
                    {
                        Name = $"ctrl_{Regex.Replace(op.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Aksiyon)",
                        Label = BeautifyIdentifier(op),
                        ActionDescription = $"{op} kontrolü.",
                        ParentScreen = "UiManagementView"
                    }).ToList(),
                    Operations = uiOps
                };
                screens.Add(sUi);

                tools.Add(new AppToolItem
                {
                    Name = $"{slug}_arayuz_denetle",
                    Title = $"{productTitle} Arayüz Kontrol Aracı",
                    Description = $"{productTitle} pencere ve ekran kontrollerini yönetir.",
                    TargetScreen = sUi.Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["eylem"] = new { type = "string", description = "Uygulanacak arayüz eylemi" }
                    },
                    RequiredParameters = new List<string>()
                });
            }

            if (screens.Count == 0 || sysOps.Count > 0)
            {
                var opsToUse = sysOps.Count > 0 ? sysOps : (allCandidateOps.Count > 0 ? allCandidateOps.Take(4).ToList() : new List<string> { $"{productTitle}.exe" });
                var sSys = new AppScreenInfo
                {
                    ScreenName = "MainCoreView",
                    Title = $"{productTitle} Ana Yürütme Paneli",
                    Purpose = $"{productTitle} ({evidence.FileVersion}, {evidence.Architecture}) temel fonksiyonel yürütme alanı.",
                    Controls = opsToUse.Take(3).Select(op => new AppControlInfo
                    {
                        Name = $"act_{Regex.Replace(op.ToLower(), "[^a-z0-9]", "_").Trim('_')}",
                        ControlType = "Button (Aksiyon)",
                        Label = BeautifyIdentifier(op),
                        ActionDescription = $"{op} fonksiyonunu çalıştırır.",
                        ParentScreen = "MainCoreView"
                    }).ToList(),
                    Operations = opsToUse
                };
                screens.Add(sSys);

                tools.Add(new AppToolItem
                {
                    Name = $"{slug}_ana_gorevi_calistir",
                    Title = $"{productTitle} Yürütme Aracı",
                    Description = $"{productTitle} ana görevini ve sistem fonksiyonlarını çalıştırır.",
                    TargetScreen = sSys.Title,
                    Parameters = new Dictionary<string, object>
                    {
                        ["parametre"] = new { type = "string", description = "Çalıştırma parametresi" }
                    },
                    RequiredParameters = new List<string>()
                });
            }

            return (screens, tools, domain);
        }

        private List<AppToolItem> GenerateToolsFromScreens(string appName, string productTitle, List<AppScreenInfo> screens)
        {
            var slug = Regex.Replace(appName.ToLower(), "[^a-z0-9]", "_").Trim('_');
            var list = new List<AppToolItem>();

            foreach (var s in screens)
            {
                var cleanTitle = Regex.Replace(s.Title.ToLower(), "[^a-z0-9]", "_").Trim('_');
                var toolName = $"{slug}_{cleanTitle}";

                var tool = new AppToolItem
                {
                    Name = toolName,
                    Title = $"{s.Title} Aracı",
                    Description = $"{productTitle} uygulamasında '{s.Title}' kontrollerini tetikleyerek işlem yapar.",
                    TargetScreen = s.Title
                };

                foreach (var c in s.Controls.Where(c => c.ControlType.Contains("Text") || c.ControlType.Contains("Combo")).Take(3))
                {
                    var paramKey = Regex.Replace(c.Name, "^(txt|cmb)_?", "", RegexOptions.IgnoreCase).ToLower();
                    if (string.IsNullOrEmpty(paramKey)) paramKey = "deger";
                    tool.Parameters[paramKey] = new { type = "string", description = $"{c.Label} değeri." };
                }

                if (tool.Parameters.Count == 0)
                {
                    tool.Parameters["komut"] = new { type = "string", description = "Çalıştırılacak arayüz komutu." };
                }

                list.Add(tool);
            }

            return list;
        }

        private List<(string Question, string Answer)> GenerateDomainTailoredQAs(
            string appName,
            string domain,
            List<AppScreenInfo> screens,
            List<AppControlInfo> controls,
            List<AppToolItem> tools,
            AppRawEvidence evidence)
        {
            var list = new List<(string, string)>
            {
                (
                    $"{appName} uygulaması nedir ve hangi sektörde/amaçla kullanılır?",
                    $"{appName}, '{domain}' alanında çalışan bir Windows yazılımıdır ({evidence.Architecture}, {evidence.FileVersion}). Sistem üzerinde {screens.Count} ana operasyon modülü ve {controls.Count} UI bileşeni tespit edilmiştir."
                ),
                (
                    $"{appName} uygulamasında hangi ekranlar ve modüller yer alır?",
                    $"{appName} bünyesinde şu başlıca modüller tespit edilmiştir: {string.Join(", ", screens.Select(s => s.Title))}. Her modül kendine ait işlevleri ve kontrolleri yönetir."
                ),
                (
                    $"{appName} uygulamasını yapay zeka ajanları nasıl otomatize edebilir?",
                    $"Yapay zeka modelleri, sisteme entegre edilen otonom araçlar ({string.Join(", ", tools.Select(t => t.Title))}) vasıtasıyla kullanıcı adına {appName} kontrollerini yönetebilir ve işlemler gerçekleştirebilir."
                )
            };

            if (evidence.IsElectronApp && evidence.PackageDependencies.Count > 0)
            {
                list.Add((
                    $"{appName} hangi kütüphane ve teknolojilerle entegre çalışır?",
                    $"Uygulamanın tespit edilen başlıca bağımlılıkları ve modülleri: {string.Join(", ", evidence.PackageDependencies.Take(8))}."
                ));
            }

            var topControls = controls.Take(6).Select(c => $"{c.Name} ({c.Label})").ToList();
            if (topControls.Count > 0)
            {
                list.Add((
                    $"{appName} arayüzündeki önemli kontroller ve işlevleri nelerdir?",
                    $"Analiz edilen kritik kontroller: {string.Join(", ", topControls)}. Bu kontroller ilgili işlevleri tetiklemek üzere yapay zeka araçlarına bağlanmıştır."
                ));
            }

            if (evidence.WasLiveInspected && (evidence.LiveDiscoveredControls.Count > 0 || !string.IsNullOrWhiteSpace(evidence.LiveMainWindowTitle)))
            {
                var liveControlsStr = evidence.LiveDiscoveredControls.Count > 0
                    ? string.Join(", ", evidence.LiveDiscoveredControls.Take(4).Select(c => c.Label))
                    : "Pencere kontrolleri";
                var liveMenusStr = evidence.LiveDiscoveredMenus.Count > 0
                    ? $" ve menü ögeleri ({string.Join(", ", evidence.LiveDiscoveredMenus.Take(4))})"
                    : "";

                list.Add((
                    $"{appName} uygulamasının canlı çalışma analizinde hangi arayüz bileşenleri yakalandı?",
                    $"Canlı Windows UI Automation incelemesinde '{evidence.LiveMainWindowTitle}' penceresi taranmış, canlı kontroller ({liveControlsStr}){liveMenusStr} tespit edilip yapay zeka hafızasına aktarılmıştır."
                ));
            }

            return list;
        }

        private string BeautifyIdentifier(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Genel Kontrol";
            var clean = Regex.Replace(raw, "^(btn|txt|cmb|grid|chk|dtp|lbl|pnl|col)_?", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, "([a-z])([A-Z])", "$1 $2");
            clean = clean.Replace("_", " ").Trim();
            return clean.Length > 0 ? clean : raw;
        }

        private string? CategorizeControl(string name)
        {
            var lower = name.ToLower();
            if (lower.StartsWith("btn") || lower.EndsWith("button") || lower.Contains("command") || lower.Contains("action"))
                return "Button (Buton)";
            if (lower.StartsWith("txt") || lower.EndsWith("box") || lower.Contains("input") || lower.Contains("entry") || lower.Contains("edit"))
                return "TextBox (Metin Girişi)";
            if (lower.StartsWith("grid") || lower.EndsWith("grid") || lower.Contains("datagrid") || lower.Contains("table") || lower.Contains("list"))
                return "DataGrid (Tablo)";
            if (lower.StartsWith("cmb") || lower.StartsWith("ddl") || lower.Contains("combo") || lower.Contains("dropdown") || lower.Contains("select"))
                return "ComboBox (Açılır Liste)";
            if (lower.StartsWith("chk") || lower.Contains("checkbox"))
                return "CheckBox (Onay Kutusu)";
            if (lower.StartsWith("dtp") || lower.Contains("date") || lower.Contains("calendar"))
                return "DateTimePicker (Tarih)";
            return null;
        }
    }
}
