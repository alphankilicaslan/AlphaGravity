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
    public class KnowledgeNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Label { get; set; } = string.Empty;
        public string Group { get; set; } = "process"; // core, code, process, data, security, rule, document
        public int Size { get; set; } = 24;
        public string Description { get; set; } = string.Empty;
        public string ParentId { get; set; } = "core";
        public string DocSource { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class KnowledgeLink
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty; // e.g. "depolar", "korur", "kullanır", "tetikler", "tanımlar", "bağlıdır"
        public string Type { get; set; } = "dependency"; // hierarchy, dataflow, security, dependency, rule, action
        public double Strength { get; set; } = 1.0;
    }

    public class KnowledgeGraphData
    {
        public string CompanyId { get; set; } = string.Empty;
        public string CompanyName { get; set; } = string.Empty;
        public List<KnowledgeNode> Nodes { get; set; } = new();
        public List<KnowledgeLink> Links { get; set; } = new();
    }

    public class IngestScrapedDataResult
    {
        public bool Success { get; set; }
        public List<KnowledgeNode> CreatedOrUpdatedNodes { get; set; } = new();
        public List<KnowledgeLink> CreatedLinks { get; set; } = new();
    }

    public class KnowledgeGraphManager
    {
        private readonly CompanyManager _companyManager;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public KnowledgeGraphManager(CompanyManager companyManager)
        {
            _companyManager = companyManager;
        }

        public KnowledgeGraphData GetGraphData(Company company)
        {
            var datasetDir = Path.Combine(_companyManager.GetCompanyDirPath(company.Id), "dataset");
            Directory.CreateDirectory(datasetDir);

            var nodesFile = Path.Combine(datasetDir, "knowledge_nodes.json");
            var linksFile = Path.Combine(datasetDir, "knowledge_links.json");

            var graph = new KnowledgeGraphData
            {
                CompanyId = company.Id,
                CompanyName = company.Name
            };

            var nodes = new List<KnowledgeNode>();
            var links = new List<KnowledgeLink>();

            if (File.Exists(nodesFile))
            {
                try
                {
                    var json = File.ReadAllText(nodesFile);
                    var savedNodes = JsonSerializer.Deserialize<List<KnowledgeNode>>(json, _jsonOptions);
                    if (savedNodes != null && savedNodes.Count > 0)
                    {
                        nodes = savedNodes.Where(n => !string.IsNullOrEmpty(n.Id) && !n.Label.Contains('\0') && !n.Description.Contains('\0')).ToList();
                    }
                }
                catch { }
            }

            if (nodes.Count == 0)
            {
                // Varsayılan şirket bilgi & iş akışı şeması
                nodes = GetDefaultNodesForCompany(company);
                links = GetDefaultLinksForCompany(nodes);
                SaveGraph(company.Id, nodes, links);
            }
            else if (File.Exists(linksFile))
            {
                try
                {
                    var json = File.ReadAllText(linksFile);
                    var savedLinks = JsonSerializer.Deserialize<List<KnowledgeLink>>(json, _jsonOptions);
                    if (savedLinks != null && savedLinks.Count > 0)
                    {
                        var validNodeIds = new HashSet<string>(nodes.Select(n => n.Id));
                        links = savedLinks.Where(l => !string.IsNullOrEmpty(l.Source) && !string.IsNullOrEmpty(l.Target) && validNodeIds.Contains(l.Source) && validNodeIds.Contains(l.Target)).ToList();
                    }
                }
                catch { }
            }

            if (links.Count == 0 && nodes.Count > 0)
            {
                links = GetDefaultLinksForCompany(nodes);
                SaveGraph(company.Id, nodes, links);
            }

            // Şirket adı güncellendiyse merkezi AI Core baloncuğunun adını otomatik senkronize et
            var coreNode = nodes.FirstOrDefault(n => n.Id == "core");
            var expectedCoreLabel = $"{company.Name} AI Core";
            if (coreNode != null && coreNode.Label != expectedCoreLabel)
            {
                coreNode.Label = expectedCoreLabel;
                SaveGraph(company.Id, nodes, links);
            }

            // Chat'ten gelen gereksiz / kalabalık learned_ baloncuklarını temizle
            nodes.RemoveAll(n => n.Id.StartsWith("learned_"));
            links.RemoveAll(l => l.Source.StartsWith("learned_") || l.Target.StartsWith("learned_"));

            // Linklerin geçerli node'lara referans verdiğini doğrula (kopuk linkleri temizle)
            var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
            links = links.Where(l => nodeIds.Contains(l.Source) && nodeIds.Contains(l.Target)).ToList();

            graph.Nodes = nodes;
            graph.Links = links;
            return graph;
        }

        public void AddNode(string companyId, string label, string group, string description, string parentId = "core", string docSource = "", List<string>? tags = null)
        {
            var nodesFile = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset", "knowledge_nodes.json");
            var linksFile = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset", "knowledge_links.json");

            var nodes = new List<KnowledgeNode>();
            var links = new List<KnowledgeLink>();

            if (File.Exists(nodesFile))
            {
                try
                {
                    nodes = JsonSerializer.Deserialize<List<KnowledgeNode>>(File.ReadAllText(nodesFile), _jsonOptions) ?? new();
                }
                catch { }
            }
            if (File.Exists(linksFile))
            {
                try
                {
                    links = JsonSerializer.Deserialize<List<KnowledgeLink>>(File.ReadAllText(linksFile), _jsonOptions) ?? new();
                }
                catch { }
            }

            var newNodeId = "node_" + Guid.NewGuid().ToString("N")[..6];
            nodes.Add(new KnowledgeNode
            {
                Id = newNodeId,
                Label = label,
                Group = group,
                Size = group == "document" ? 28 : (group == "rule" ? 18 : 22),
                Description = description,
                ParentId = parentId,
                DocSource = docSource,
                Tags = tags ?? new(),
                CreatedAt = DateTime.UtcNow
            });

            if (!string.IsNullOrEmpty(parentId) && nodes.Any(n => n.Id == parentId))
            {
                links.Add(new KnowledgeLink
                {
                    Source = parentId,
                    Target = newNodeId,
                    Label = group == "rule" ? "kuralı" : (group == "document" ? "tanımlar" : "bağlıdır"),
                    Type = "hierarchy",
                    Strength = 0.9
                });
            }

            SaveGraph(companyId, nodes, links);
        }

        public void AddBulkKnowledge(string companyId, List<KnowledgeNode> newNodes, List<KnowledgeLink> newLinks)
        {
            var nodesFile = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset", "knowledge_nodes.json");
            var linksFile = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset", "knowledge_links.json");

            var nodes = new List<KnowledgeNode>();
            var links = new List<KnowledgeLink>();

            if (File.Exists(nodesFile))
            {
                try
                {
                    nodes = JsonSerializer.Deserialize<List<KnowledgeNode>>(File.ReadAllText(nodesFile), _jsonOptions) ?? new();
                }
                catch { }
            }
            if (File.Exists(linksFile))
            {
                try
                {
                    links = JsonSerializer.Deserialize<List<KnowledgeLink>>(File.ReadAllText(linksFile), _jsonOptions) ?? new();
                }
                catch { }
            }

            foreach (var n in newNodes)
            {
                if (!nodes.Any(x => x.Id == n.Id))
                {
                    nodes.Add(n);
                }
            }

            var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
            foreach (var l in newLinks)
            {
                if (nodeIds.Contains(l.Source) && nodeIds.Contains(l.Target))
                {
                    if (!links.Any(x => x.Source == l.Source && x.Target == l.Target))
                    {
                        links.Add(l);
                    }
                }
            }

            SaveGraph(companyId, nodes, links);
        }

        public void SaveGraph(string companyId, List<KnowledgeNode> nodes, List<KnowledgeLink> links)
        {
            var dir = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset");
            Directory.CreateDirectory(dir);

            var nodesFile = Path.Combine(dir, "knowledge_nodes.json");
            var linksFile = Path.Combine(dir, "knowledge_links.json");

            File.WriteAllText(nodesFile, JsonSerializer.Serialize(nodes, _jsonOptions));
            File.WriteAllText(linksFile, JsonSerializer.Serialize(links, _jsonOptions));
        }

        public IngestScrapedDataResult IngestScrapedMetrics(
            Company company,
            LiveDesktopScraperResult scraperResult)
        {
            var result = new IngestScrapedDataResult();
            if (scraperResult == null || company == null)
            {
                return result;
            }

            var graph = GetGraphData(company);
            var nodes = graph.Nodes;
            var links = graph.Links;

            // Determine parent node for links (e.g. app_alpfinans or core)
            string parentId = "core";
            if (nodes.Any(n => n.Id == "app_alpfinans")) parentId = "app_alpfinans";
            else if (nodes.Any(n => n.Id == "app_finance")) parentId = "app_finance";

            void UpsertMetricNode(string nodeId, string label, string group, int size, string description, List<string> tags)
            {
                var existing = nodes.FirstOrDefault(n => n.Id == nodeId);
                if (existing != null)
                {
                    existing.Label = label;
                    existing.Group = group;
                    existing.Size = size;
                    existing.Description = description;
                    existing.DocSource = "AlpFinans.exe (Canlı Arayüz)";
                    existing.Tags = tags;
                    existing.CreatedAt = DateTime.UtcNow;
                    result.CreatedOrUpdatedNodes.Add(existing);
                }
                else
                {
                    var newNode = new KnowledgeNode
                    {
                        Id = nodeId,
                        Label = label,
                        Group = group,
                        Size = size,
                        Description = description,
                        ParentId = parentId,
                        DocSource = "AlpFinans.exe (Canlı Arayüz)",
                        Tags = tags,
                        CreatedAt = DateTime.UtcNow
                    };
                    nodes.Add(newNode);
                    result.CreatedOrUpdatedNodes.Add(newNode);
                }

                // Add or ensure link
                if (!links.Any(l => l.Source == parentId && l.Target == nodeId))
                {
                    var link = new KnowledgeLink
                    {
                        Source = parentId,
                        Target = nodeId,
                        Label = "canlı veri",
                        Type = "dataflow",
                        Strength = 1.0
                    };
                    links.Add(link);
                    result.CreatedLinks.Add(link);
                }
            }

            if (scraperResult.NumericMetrics.TryGetValue("total_income_gross", out var gross))
            {
                UpsertMetricNode(
                    "node_metric_gelir_2026",
                    $"💰 2026 Brüt Hasılat: {gross:N2} ₺",
                    "data",
                    26,
                    $"AlpFinans canlı masaüstü arayüzünden okunan 2026 yılı brüt ticari hasılatı: {gross:N2} ₺.",
                    new List<string> { "canlı", "gelir", "2026", "hasılat" });
            }

            if (scraperResult.NumericMetrics.TryGetValue("total_company_expenses", out var exp))
            {
                UpsertMetricNode(
                    "node_metric_gider_2026",
                    $"📄 2026 Toplam Gider: {exp:N2} ₺",
                    "data",
                    24,
                    $"AlpFinans canlı masaüstü arayüzünden okunan 2026 yılı toplam faturalar ve giderler: {exp:N2} ₺.",
                    new List<string> { "canlı", "gider", "2026", "fatura" });
            }

            if (scraperResult.NumericMetrics.TryGetValue("net_profit", out var profit))
            {
                UpsertMetricNode(
                    "node_metric_kar_2026",
                    $"💵 2026 Net Kâr: {profit:N2} ₺",
                    "data",
                    26,
                    $"Giderler ve faturalar düşüldükten sonra şirketin cebe kalan net kâr ve kasa bakiyesi: {profit:N2} ₺.",
                    new List<string> { "canlı", "kâr", "kasa", "2026" });
            }

            if (scraperResult.NumericMetrics.TryGetValue("total_tax", out var tax))
            {
                UpsertMetricNode(
                    "node_metric_vergi_2026",
                    $"📋 2026 Gerçek Vergi: {tax:N2} ₺",
                    "security",
                    22,
                    $"Vergi dairesi indirimli gerçek vergi tahakkuku: {tax:N2} ₺.",
                    new List<string> { "canlı", "vergi", "2026", "tahakkuk" });
            }

            if (scraperResult.NumericMetrics.TryGetValue("remaining_tax", out var remTax))
            {
                UpsertMetricNode(
                    "node_metric_kalan_vergi_2026",
                    $"⏳ 2026 Kalan Vergi Borcu: {remTax:N2} ₺",
                    "security",
                    22,
                    $"Vergi dairesine ödenecek kalan net borç tutarı: {remTax:N2} ₺.",
                    new List<string> { "canlı", "vergi", "2026", "borç" });
            }

            // Immediately save graph to disk
            SaveGraph(company.Id, nodes, links);
            result.Success = true;

            return result;
        }

        public bool DeleteNode(string companyId, string nodeId)
        {
            var dir = Path.Combine(_companyManager.GetCompanyDirPath(companyId), "dataset");
            var nodesFile = Path.Combine(dir, "knowledge_nodes.json");
            var linksFile = Path.Combine(dir, "knowledge_links.json");

            if (!File.Exists(nodesFile)) return false;

            var nodes = JsonSerializer.Deserialize<List<KnowledgeNode>>(File.ReadAllText(nodesFile), _jsonOptions) ?? new();
            var links = File.Exists(linksFile)
                ? JsonSerializer.Deserialize<List<KnowledgeLink>>(File.ReadAllText(linksFile), _jsonOptions) ?? new()
                : new();

            var target = nodes.FirstOrDefault(n => n.Id == nodeId);
            if (target == null || target.Id == "core") return false;

            var toRemove = new HashSet<string> { nodeId };

            // Belge düğümü ise bu belgeden türetilen tüm alt kuralları ve kavramları da sil
            if (target.Group == "document" || target.Id.StartsWith("doc_"))
            {
                var docSource = target.DocSource;
                foreach (var child in nodes.Where(n => n.ParentId == nodeId || (!string.IsNullOrEmpty(docSource) && n.DocSource == docSource)))
                {
                    toRemove.Add(child.Id);
                }

                // documents klasöründeki fiziksel dosyayı da temizle
                try
                {
                    var docsDir = _companyManager.GetCompanyDocumentsPath(companyId);
                    if (!string.IsNullOrEmpty(target.DocSource))
                    {
                        var filePath = Path.Combine(docsDir, target.DocSource);
                        if (File.Exists(filePath)) File.Delete(filePath);
                    }
                }
                catch { }

                // training_pairs.jsonl içerisinden de bu belgeye ait satırları temizle
                try
                {
                    var datasetPath = _companyManager.GetCompanyDatasetPath(companyId);
                    if (File.Exists(datasetPath))
                    {
                        var lines = File.ReadAllLines(datasetPath);
                        var keptLines = lines.Where(l => !l.Contains(target.DocSource, StringComparison.OrdinalIgnoreCase)).ToList();
                        File.WriteAllLines(datasetPath, keptLines);
                    }
                }
                catch { }
            }

            nodes.RemoveAll(n => toRemove.Contains(n.Id));
            links.RemoveAll(l => toRemove.Contains(l.Source) || toRemove.Contains(l.Target));

            SaveGraph(companyId, nodes, links);
            return true;
        }

        public void ResetGraphToDefaults(string companyId, Company company)
        {
            var nodes = GetDefaultNodesForCompany(company);
            var links = GetDefaultLinksForCompany(nodes);
            SaveGraph(companyId, nodes, links);
        }

        private List<KnowledgeNode> GetDefaultNodesForCompany(Company company)
        {
            return new List<KnowledgeNode>
            {
                new() { Id = "core", Label = $"{company.Name} AI Core", Group = "core", Size = 38, Description = "Merkezi yapay zeka beyni ve kurumsal karar motoru.", ParentId = "" },

                // İş Akışları (Process)
                new() { Id = "flow_main", Label = "İş Akışları & Süreçler", Group = "process", Size = 28, Description = "Şirket içi operasyonel iş akışı ve görev dağılımı.", ParentId = "core" },
                new() { Id = "flow_order", Label = "Sipariş & Talep Onay Süreci", Group = "process", Size = 22, Description = "Müşteri talep girişinden faturalandırmaya kadar olan onay basamakları.", ParentId = "flow_main" },
                new() { Id = "flow_crm", Label = "Müşteri İletişim Protokolü", Group = "process", Size = 20, Description = "Müşteri destek kayıtları ve eskalasyon kuralları.", ParentId = "flow_main" },

                // Kodlama Standartları (Code)
                new() { Id = "code_std", Label = "Kodlama Standartları", Group = "code", Size = 28, Description = "Şirketin C#, .NET 9 ve Frontend yazılım kuralları.", ParentId = "core" },
                new() { Id = "code_api", Label = "REST API Mimarisi", Group = "code", Size = 22, Description = "Controller, Service, Repository katmanlı tasarım şablonu.", ParentId = "code_std" },
                new() { Id = "code_ui", Label = "Modern UI & Web Arayüzü", Group = "code", Size = 20, Description = "HTML5, Tailwind ve modern arayüz tasarım bileşenleri.", ParentId = "code_std" },

                // Veritabanı & Veri Modelleri (Data)
                new() { Id = "db_main", Label = "Veri Mimarisi & Şema", Group = "data", Size = 26, Description = "İlişkisel veritabanı tabloları, indexler ve DTO yapıları.", ParentId = "core" },
                new() { Id = "db_schema", Label = "PostgreSQL / SQL Tabloları", Group = "data", Size = 22, Description = "Kullanıcılar, Siparişler, Stok ve Log tabloları.", ParentId = "db_main" },

                // Güvenlik & İzolasyon (Security)
                new() { Id = "sec_main", Label = "Güvenlik & İzolasyon", Group = "security", Size = 26, Description = "Çoklu şirket veri izolasyonu ve yetkilendirme politikaları.", ParentId = "core" },
                new() { Id = "sec_iso", Label = "Tenant İzolasyon Kuralı", Group = "security", Size = 20, Description = "Şirket verilerinin dışarı sızmasını engelleyen mutlak kural.", ParentId = "sec_main" }
            };
        }

        private List<KnowledgeLink> GetDefaultLinksForCompany(List<KnowledgeNode> nodes)
        {
            var links = new List<KnowledgeLink>
            {
                // Hiyerarşik bağlantılar
                new() { Source = "core", Target = "flow_main", Label = "yönetir", Type = "hierarchy", Strength = 1.0 },
                new() { Source = "core", Target = "code_std", Label = "denetler", Type = "hierarchy", Strength = 1.0 },
                new() { Source = "core", Target = "db_main", Label = "erişir", Type = "hierarchy", Strength = 1.0 },
                new() { Source = "core", Target = "sec_main", Label = "uygular", Type = "hierarchy", Strength = 1.0 },

                new() { Source = "flow_main", Target = "flow_order", Label = "içerir", Type = "process", Strength = 0.9 },
                new() { Source = "flow_main", Target = "flow_crm", Label = "koordine eder", Type = "process", Strength = 0.9 },

                new() { Source = "code_std", Target = "code_api", Label = "kurallandırır", Type = "code", Strength = 0.9 },
                new() { Source = "code_std", Target = "code_ui", Label = "tasarlar", Type = "code", Strength = 0.9 },

                new() { Source = "db_main", Target = "db_schema", Label = "barındırır", Type = "data", Strength = 0.9 },
                new() { Source = "sec_main", Target = "sec_iso", Label = "zorunlu kılar", Type = "security", Strength = 0.9 },

                // Anlamsal Çapraz Bağlantılar (Gerçek mantıksal ilişkiler)
                new() { Source = "code_api", Target = "db_schema", Label = "sorgular", Type = "dataflow", Strength = 0.7 },
                new() { Source = "flow_order", Target = "db_schema", Label = "kaydeder", Type = "dataflow", Strength = 0.7 },
                new() { Source = "sec_iso", Target = "code_api", Label = "korur", Type = "security", Strength = 0.8 },
                new() { Source = "sec_iso", Target = "db_schema", Label = "izole eder", Type = "security", Strength = 0.85 },
                new() { Source = "code_ui", Target = "code_api", Label = "tüketir", Type = "dependency", Strength = 0.75 }
            };

            var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
            return links.Where(l => nodeIds.Contains(l.Source) && nodeIds.Contains(l.Target)).ToList();
        }


        private static string? _threeJsCache;
        private static string? _orbitControlsCache;

        private static string GetThreeJs()
        {
            if (_threeJsCache != null) return _threeJsCache;
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "three.min.js"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "three.min.js"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "three.min.js"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "three.min.js")
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        _threeJsCache = File.ReadAllText(path);
                        return _threeJsCache;
                    }
                    catch { }
                }
            }
            return string.Empty;
        }

        private static string GetOrbitControlsJs()
        {
            if (_orbitControlsCache != null) return _orbitControlsCache;
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "OrbitControls.js"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OrbitControls.js"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "OrbitControls.js"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "OrbitControls.js")
            };
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        _orbitControlsCache = File.ReadAllText(path);
                        return _orbitControlsCache;
                    }
                    catch { }
                }
            }
            return string.Empty;
        }

        public string GenerateHtmlVisualizer(Company company)
        {
            var graphData = GetGraphData(company);
            var graphJson = JsonSerializer.Serialize(graphData, _jsonOptions);

            var threeJs = GetThreeJs();
            var orbitControlsJs = GetOrbitControlsJs();

            string scriptTags;
            if (!string.IsNullOrEmpty(threeJs) && !string.IsNullOrEmpty(orbitControlsJs))
            {
                scriptTags = $"<script>{threeJs}</script>\n<script>{orbitControlsJs}</script>";
            }
            else
            {
                scriptTags = @"<script src=""https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js""></script>
<script src=""https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js""></script>";
            }

            return $@"<!DOCTYPE html>
<html lang=""tr"">
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Enterprise AI — 3D Sinaptik Bilgi Ağı</title>
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; user-select: none; -webkit-user-select: none; }}
        html, body {{
            background: #020408;
            color: #F8FAFC;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            overflow: hidden;
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0;
        }}
        #webgl-canvas {{
            position: absolute;
            top: 0; left: 0;
            width: 100%; height: 100%;
            display: block;
            cursor: grab;
        }}
        #webgl-canvas:active {{ cursor: grabbing; }}

        /* Top HUD Bar - Flexible & Responsive */
        .hud-bar {{
            position: absolute;
            top: 10px; left: 12px; right: 12px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            flex-wrap: wrap;
            gap: 8px;
            pointer-events: none;
            z-index: 20;
        }}
        .hud-stats {{
            display: flex;
            align-items: center;
            gap: 6px;
            background: rgba(8, 12, 20, 0.88);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(255, 255, 255, 0.10);
            border-radius: 14px;
            padding: 5px 12px;
            font-size: 11px;
            font-weight: 500;
            color: #94A3B8;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.6);
            pointer-events: auto;
            flex-shrink: 0;
        }}
        .stat-val {{ color: #F1F5F9; font-weight: 700; }}

        /* Search & Actions */
        .hud-tools {{
            display: flex;
            align-items: center;
            gap: 6px;
            pointer-events: auto;
            flex-wrap: wrap;
        }}
        .search-box {{
            display: flex;
            align-items: center;
            gap: 6px;
            background: rgba(8, 12, 20, 0.88);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(255, 255, 255, 0.12);
            border-radius: 14px;
            padding: 4px 10px;
            color: #F1F5F9;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.6);
        }}
        .search-box input {{
            background: transparent;
            border: none;
            outline: none;
            color: #F1F5F9;
            font-size: 11px;
            width: 85px;
            transition: width 0.2s;
        }}
        .search-box input:focus {{ width: 120px; }}
        .search-box input::placeholder {{ color: #64748B; }}

        .btn-hud {{
            background: rgba(8, 12, 20, 0.88);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(255, 255, 255, 0.12);
            border-radius: 14px;
            padding: 4px 10px;
            font-size: 11px;
            font-weight: 600;
            color: #E2E8F0;
            cursor: pointer;
            display: flex;
            align-items: center;
            gap: 5px;
            transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.6);
            white-space: nowrap;
        }}
        .btn-hud:hover {{
            background: rgba(30, 41, 59, 0.95);
            border-color: #38BDF8;
            color: #FFFFFF;
            transform: translateY(-1px);
        }}
        .btn-hud.active {{
            background: rgba(14, 165, 233, 0.22);
            border-color: #38BDF8;
            color: #38BDF8;
            box-shadow: 0 0 16px rgba(56, 189, 248, 0.35);
        }}

        /* Floating Tooltip */
        .hover-tooltip {{
            position: absolute;
            display: none;
            pointer-events: none;
            z-index: 30;
            background: rgba(8, 12, 20, 0.94);
            backdrop-filter: blur(16px);
            -webkit-backdrop-filter: blur(16px);
            border: 1px solid #38BDF8;
            border-radius: 10px;
            padding: 6px 14px;
            font-size: 11.5px;
            font-weight: 600;
            color: #F8FAFC;
            box-shadow: 0 8px 24px rgba(0,0,0,0.85), 0 0 16px rgba(56, 189, 248, 0.3);
            white-space: nowrap;
            transform: translate(-50%, -135%);
            transition: opacity 0.15s ease;
        }}

        /* Bottom Stage Breadcrumbs */
        .bottom-stages-bar {{
            position: absolute;
            bottom: 12px;
            left: 50%;
            transform: translateX(-50%);
            display: flex;
            align-items: center;
            gap: 6px;
            background: rgba(8, 12, 20, 0.90);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-radius: 16px;
            padding: 5px 14px;
            z-index: 20;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.6);
            max-width: calc(100vw - 24px);
            overflow-x: auto;
            scrollbar-width: none;
        }}
        .bottom-stages-bar::-webkit-scrollbar {{ display: none; }}

        .stage-crumb {{
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 1px;
            cursor: pointer;
            padding: 3px 8px;
            border-radius: 8px;
            transition: all 0.18s;
            white-space: nowrap;
        }}
        .stage-crumb:hover {{
            background: rgba(255, 255, 255, 0.08);
        }}
        .stage-crumb.active {{
            background: rgba(56, 189, 248, 0.15);
            border: 1px solid rgba(56, 189, 248, 0.4);
        }}
        .stage-title {{
            font-size: 10px;
            font-weight: 700;
            letter-spacing: 0.4px;
        }}
        .stage-sub {{
            font-size: 7.5px;
            color: #64748B;
        }}
        .stage-arrow {{
            color: #334155;
            font-size: 11px;
        }}

        /* Responsive Media Queries */
        @media (max-width: 900px) {{
            .stage-sub {{
                display: none !important;
            }}
            .stage-title {{
                font-size: 8.5px;
            }}
            .stage-crumb {{
                padding: 2px 6px;
            }}
            .bottom-stages-bar {{
                padding: 4px 8px;
                gap: 4px;
            }}
        }}

        @media (max-width: 680px) {{
            .hud-bar {{
                justify-content: center;
                gap: 6px;
            }}
            .hud-stats {{
                display: none !important;
            }}
            .hud-tools {{
                width: 100%;
                justify-content: center;
            }}
        }}

        /* Node Inspector Card - Compact, Sleek & Minimal */
        .inspector-card {{
            position: absolute;
            top: 52px; right: 14px;
            width: 220px;
            max-width: calc(100vw - 28px);
            background: rgba(8, 12, 20, 0.90);
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            border: 1px solid rgba(56, 189, 248, 0.35);
            border-radius: 12px;
            padding: 10px 12px;
            display: none;
            flex-direction: column;
            gap: 6px;
            z-index: 30;
            box-shadow: 0 12px 30px rgba(0, 0, 0, 0.75), 0 0 16px rgba(56, 189, 248, 0.15);
            animation: slideIn 0.2s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        @keyframes slideIn {{
            from {{ transform: translateX(16px); opacity: 0; }}
            to {{ transform: translateX(0); opacity: 1; }}
        }}
        .inspector-header {{
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 8px;
        }}
        .node-category-badge {{
            font-size: 8px;
            font-weight: 700;
            letter-spacing: 0.5px;
            padding: 2px 6px;
            border-radius: 5px;
            text-transform: uppercase;
            color: #FFF;
            display: inline-block;
            white-space: nowrap;
        }}
        .close-btn {{
            background: transparent;
            border: none;
            color: #64748B;
            font-size: 14px;
            cursor: pointer;
            padding: 1px 4px;
            border-radius: 4px;
            line-height: 1;
        }}
        .close-btn:hover {{ color: #F1F5F9; background: rgba(255,255,255,0.1); }}

        .node-name {{
            font-size: 12px;
            font-weight: 700;
            color: #F8FAFC;
            margin: 0;
            line-height: 1.3;
        }}
        .node-desc {{
            font-size: 10px;
            color: #94A3B8;
            line-height: 1.35;
            max-height: 52px;
            overflow-y: auto;
            scrollbar-width: none;
        }}
        .node-desc::-webkit-scrollbar {{ display: none; }}

        .inspector-meta-grid {{
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 5px;
            margin-top: 2px;
        }}
        .meta-item {{
            background: rgba(255, 255, 255, 0.04);
            border: 1px solid rgba(255, 255, 255, 0.07);
            border-radius: 6px;
            padding: 4px 6px;
            display: flex;
            flex-direction: column;
            gap: 1px;
            overflow: hidden;
        }}
        .meta-k {{
            font-size: 7.5px;
            font-weight: 700;
            color: #64748B;
            text-transform: uppercase;
            letter-spacing: 0.4px;
        }}
        .meta-v {{
            font-size: 9px;
            color: #CBD5E1;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }}

        @media (max-width: 680px) {{
            .inspector-card {{
                width: 190px;
                padding: 8px 10px;
                top: 48px;
                right: 8px;
            }}
            .node-name {{
                font-size: 11px;
            }}
            .node-desc {{
                font-size: 9px;
            }}
        }}
    </style>
</head>
<body>
    <canvas id=""webgl-canvas""></canvas>

    <!-- Top HUD - Clean & Responsive (No Blue Badge) -->
    <div class=""hud-bar"">
        <div class=""hud-stats"">
            <span id=""txtNodeCount"" class=""stat-val"">32</span> Nöron
            <span>•</span>
            <span id=""txtLinkCount"" class=""stat-val"">--</span> Sinaps
            <span>•</span>
            <span class=""stat-val"">5</span> Katman
        </div>

        <div class=""hud-tools"">
            <div class=""search-box"">
                <span>&#x1F50D;</span>
                <input id=""nodeSearch"" type=""text"" placeholder=""Nöron ara..."" autocomplete=""off"" />
            </div>
            <button id=""btnReset"" class=""btn-hud"" title=""Görüntüyü Sıfırla"">
                &#x1F3AF; Odak
            </button>
            <button id=""btnRotate"" class=""btn-hud active"" title=""Otomatik Dönüş"">
                &#x1F504; Tur
            </button>
            <button id=""btnPulse"" class=""btn-hud active"" title=""Sinaptik Dalga"">
                &#x26A1; Dalga
            </button>
        </div>
    </div>

    <!-- Floating Hover Tooltip -->
    <div id=""hoverTooltip"" class=""hover-tooltip""></div>

    <!-- Bottom Stage Navigator - Clean & Meaningful Enterprise Architecture -->
    <div class=""bottom-stages-bar"">
        <div class=""stage-crumb"" onclick=""focusStage(0)"">
            <span class=""stage-title"" style=""color: #38BDF8;"">VERİ ALTYAPISI</span>
            <span class=""stage-sub"">SQL &amp; Tablolar</span>
        </div>
        <span class=""stage-arrow"">&#x2192;</span>
        <div class=""stage-crumb"" onclick=""focusStage(1)"">
            <span class=""stage-title"" style=""color: #818CF8;"">GÜVENLİK &amp; SİSTEM</span>
            <span class=""stage-sub"">İzinler &amp; Kurallar</span>
        </div>
        <span class=""stage-arrow"">&#x2192;</span>
        <div class=""stage-crumb"" onclick=""focusStage(2)"">
            <span class=""stage-title"" style=""color: #34D399;"">FİNANS &amp; MUHASEBE</span>
            <span class=""stage-sub"">Kasa, Kâr &amp; Vergi</span>
        </div>
        <span class=""stage-arrow"">&#x2192;</span>
        <div class=""stage-crumb"" onclick=""focusStage(3)"">
            <span class=""stage-title"" style=""color: #FBBF24;"">İŞ SÜREÇLERİ</span>
            <span class=""stage-sub"">Sipariş &amp; Entegrasyon</span>
        </div>
        <span class=""stage-arrow"">&#x2192;</span>
        <div class=""stage-crumb"" onclick=""focusStage(4)"">
            <span class=""stage-title"" style=""color: #F43F5E;"">AI BEYİN KÖKÜ</span>
            <span class=""stage-sub"">Karar &amp; Yönetim</span>
        </div>
    </div>

    <!-- Right Side Compact Inspector Card -->
    <div id=""inspector"" class=""inspector-card"">
        <div class=""inspector-header"">
            <span id=""insBadge"" class=""node-category-badge"">KATMAN</span>
            <button class=""close-btn"" onclick=""closeInspector()"">&#x2715;</button>
        </div>
        <div id=""insTitle"" class=""node-name"">Nöron Adı</div>
        <div id=""insDesc"" class=""node-desc"">Açıklama</div>

        <div class=""inspector-meta-grid"">
            <div class=""meta-item"">
                <span class=""meta-k"">Kaynak</span>
                <span id=""insDocSource"" class=""meta-v"">--</span>
            </div>
            <div class=""meta-item"">
                <span class=""meta-k"">Sinaps</span>
                <span id=""insDegree"" class=""meta-v"">--</span>
            </div>
        </div>
    </div>

    {scriptTags}

    <script>
        const rawData = {graphJson};

        // 5 Logical Stages: Ultra compact spacing (Z gap = 5 units, width/height = 7-8 units)
        // Highly dense, integrated nano-brain synaptic cluster
        const stagesConfig = [
            {{
                idx: 0,
                id: 'input',
                name: 'VERİ ALTYAPISI',
                tag: 'Data & SQL',
                x: 0, y: 0, z: -10,
                color: 0x38BDF8,
                colorHex: '#38BDF8',
                w: 7.0, h: 5.0
            }},
            {{
                idx: 1,
                id: 'hidden1',
                name: 'GÜVENLİK & SİSTEM',
                tag: 'Security & Rules',
                x: 0, y: 0, z: -5,
                color: 0x818CF8,
                colorHex: '#818CF8',
                w: 7.5, h: 5.5
            }},
            {{
                idx: 2,
                id: 'hidden2',
                name: 'FİNANS & MUHASEBE',
                tag: 'Finance & Ledger',
                x: 0, y: 0, z: 0,
                color: 0x34D399,
                colorHex: '#34D399',
                w: 8.0, h: 6.0
            }},
            {{
                idx: 3,
                id: 'hidden3',
                name: 'İŞ SÜREÇLERİ',
                tag: 'Operations & Workflow',
                x: 0, y: 0, z: 5,
                color: 0xFBBF24,
                colorHex: '#FBBF24',
                w: 7.5, h: 5.5
            }},
            {{
                idx: 4,
                id: 'output',
                name: 'AI BEYİN KÖKÜ',
                tag: 'Executive AI',
                x: 0, y: 0, z: 10,
                color: 0xF43F5E,
                colorHex: '#F43F5E',
                w: 7.0, h: 5.0
            }}
        ];

        // Three.js Scene Setup
        const canvas = document.getElementById('webgl-canvas');
        const scene = new THREE.Scene();
        scene.fog = new THREE.FogExp2(0x020408, 0.0035);

        const camera = new THREE.PerspectiveCamera(40, window.innerWidth / window.innerHeight, 1, 2000);
        // Clean 3D Isometric View framing the ultra-compact nano-brain cluster
        const homeCameraPos = new THREE.Vector3(22, 12, 28);
        const homeCameraTarget = new THREE.Vector3(0, 0, 0);
        camera.position.copy(homeCameraPos);

        const renderer = new THREE.WebGLRenderer({{
            canvas: canvas,
            antialias: true,
            alpha: true,
            powerPreference: ""high-performance""
        }});
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        renderer.setSize(window.innerWidth, window.innerHeight);
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.35;

        const controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.05;
        controls.minDistance = 6;
        controls.maxDistance = 180;
        controls.enablePan = true;
        controls.panSpeed = 0.8;
        controls.rotateSpeed = 0.8;
        controls.zoomSpeed = 1.0;
        controls.target.copy(homeCameraTarget);

        // Lighting
        const ambientLight = new THREE.AmbientLight(0xFFFFFF, 1.4);
        scene.add(ambientLight);

        const dirLight = new THREE.DirectionalLight(0xFFFFFF, 1.2);
        dirLight.position.set(30, 40, 40);
        scene.add(dirLight);

        const pointLight = new THREE.PointLight(0xFFFFFF, 2.0, 150);
        pointLight.position.set(0, 12, 10);
        scene.add(pointLight);

        // 3D Starfield Background Particles
        const starGeo = new THREE.BufferGeometry();
        const starCount = 350;
        const starPos = new Float32Array(starCount * 3);
        for (let i = 0; i < starCount * 3; i += 3) {{
            starPos[i] = (Math.random() - 0.5) * 400;
            starPos[i+1] = (Math.random() - 0.5) * 400;
            starPos[i+2] = (Math.random() - 0.5) * 400;
        }}
        starGeo.setAttribute('position', new THREE.BufferAttribute(starPos, 3));
        const starMat = new THREE.PointsMaterial({{
            color: 0x94A3B8,
            size: 0.8,
            transparent: true,
            opacity: 0.20
        }});
        scene.add(new THREE.Points(starGeo, starMat));

        // Soft Glow Texture
        function createHaloTexture(colorHex) {{
            const size = 128;
            const c = document.createElement('canvas');
            c.width = size;
            c.height = size;
            const ctx = c.getContext('2d');

            const grad = ctx.createRadialGradient(size/2, size/2, 0, size/2, size/2, size/2);
            grad.addColorStop(0, colorHex);
            grad.addColorStop(0.35, colorHex);
            grad.addColorStop(1, 'rgba(0,0,0,0)');

            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, size, size);
            return new THREE.CanvasTexture(c);
        }}

        // Stage Header Sprite - Compact & Crisp
        function createStageHeaderSprite(title, colorHex) {{
            const c = document.createElement('canvas');
            c.width = 220;
            c.height = 38;
            const ctx = c.getContext('2d');

            const w = c.width, h = c.height, r = 8;
            ctx.beginPath();
            ctx.moveTo(r, 2);
            ctx.arcTo(w-2, 2, w-2, h-2, r);
            ctx.arcTo(w-2, h-2, 2, h-2, r);
            ctx.arcTo(2, h-2, 2, 2, r);
            ctx.arcTo(2, 2, w-2, 2, r);
            ctx.closePath();

            ctx.fillStyle = 'rgba(6, 10, 16, 0.94)';
            ctx.fill();
            ctx.strokeStyle = colorHex;
            ctx.lineWidth = 1.6;
            ctx.stroke();

            ctx.font = 'bold 14px -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillStyle = colorHex;
            ctx.fillText(title, w / 2, h / 2);

            const texture = new THREE.CanvasTexture(c);
            texture.minFilter = THREE.LinearFilter;
            const mat = new THREE.SpriteMaterial({{ map: texture, transparent: true, depthWrite: false }});
            const sprite = new THREE.Sprite(mat);
            sprite.scale.set(4.6, 0.80, 1);
            return sprite;
        }}

        // Categorize 32+ Nodes into 5 Stages
        const nodesData = rawData.Nodes || [];
        const linksData = rawData.Links || [];

        document.getElementById('txtNodeCount').innerText = nodesData.length;

        const stageBuckets = [[], [], [], [], []];

        nodesData.forEach(n => {{
            const cat = (n.Category || '').toLowerCase();
            const id = (n.Id || '').toLowerCase();
            const grp = (n.Group || '').toLowerCase();

            if (id === 'core') {{
                stageBuckets[4].push(n);
            }} else if (cat.includes('veritabanı') || cat.includes('database') || cat.includes('sql') || id.startsWith('db_')) {{
                stageBuckets[0].push(n);
            }} else if (cat.includes('güvenlik') || cat.includes('security') || cat.includes('yetki') || cat.includes('auth') || id.startsWith('sec_')) {{
                stageBuckets[1].push(n);
            }} else if (cat.includes('finans') || cat.includes('muhasebe') || cat.includes('finance') || id.startsWith('fin_') || grp.includes('finans')) {{
                stageBuckets[2].push(n);
            }} else if (cat.includes('iş') || cat.includes('operasyon') || cat.includes('sipariş') || cat.includes('workflow') || id.startsWith('biz_')) {{
                stageBuckets[3].push(n);
            }} else {{
                // Balance other nodes across hidden stages
                const targetIdx = 1 + (stageBuckets[1].length <= stageBuckets[2].length ? 0 : (stageBuckets[2].length <= stageBuckets[3].length ? 1 : 2));
                stageBuckets[targetIdx].push(n);
            }}
        }});

        const nodeMap = new Map();
        const nodeSpheres = [];
        const stageGroups = [];
        const animatedLinks = [];
        window.nodeSpheres = nodeSpheres;
        window.selectNode = selectNode;
        window.closeInspector = closeInspector;

        // Build Stage Visuals: ULTRA TIGHT, DENSE Synaptic Clusters!
        stagesConfig.forEach(st => {{
            const bucket = stageBuckets[st.idx];

            const slabGroup = new THREE.Group();
            slabGroup.position.set(0, 0, st.z);
            scene.add(slabGroup);

            // Floating Stage Header Badge above Cluster
            const headerSprite = createStageHeaderSprite(st.name, st.colorHex);
            headerSprite.position.set(0, st.h / 2 + 0.9, 0);
            slabGroup.add(headerSprite);

            stageGroups.push({{ group: slabGroup, config: st }});

            // Place Nodes in a Tight 2D Matrix Grid on the Plane (perpendicular to Z axis)
            const count = bucket.length;
            const cols = (count <= 4) ? 2 : (count <= 6 ? 3 : 4);
            const rows = Math.ceil(count / cols);

            // Ultra tight node spacing!
            const cellX = (st.w * 0.70) / Math.max(1, cols - 1);
            const cellY = (st.h * 0.65) / Math.max(1, rows - 1);

            bucket.forEach((n, idx) => {{
                const r = Math.floor(idx / cols);
                const c = idx % cols;

                let localX = (cols === 1) ? 0 : (c - (cols - 1) / 2) * cellX;
                let localY = (rows === 1) ? 0 : ((rows - 1) / 2 - r) * cellY;

                // Center single core node
                if (st.idx === 4 && n.Id === 'core') {{
                    localX = 0; localY = 0;
                }}

                const worldPos = new THREE.Vector3(localX, localY, st.z);
                const isCore = (n.Id === 'core');
                // Kullanıcı isteği: nano küre yarıçapı 0.50, AI küresi 1.0!
                const radius = isCore ? 1.0 : 0.50;

                createNodeObject(n, worldPos, radius, isCore, st, slabGroup, localX, localY, 0);
            }});
        }});

        function createNodeObject(n, worldPos, radius, isCore, stage, parentGroup, localX, localY, localZ) {{
            const nodeMeshGroup = new THREE.Group();
            nodeMeshGroup.position.set(localX, localY, localZ);
            parentGroup.add(nodeMeshGroup);

            // Glossy Sphere (Nano küre: 0.50, AI: 1.0)
            const sphereGeo = new THREE.SphereGeometry(radius, 20, 20);
            const sphereMat = new THREE.MeshStandardMaterial({{
                color: stage.color,
                emissive: stage.color,
                emissiveIntensity: isCore ? 0.95 : 0.70,
                roughness: 0.15,
                metalness: 0.10,
                transparent: false,
                opacity: 1.0
            }});
            const sphere = new THREE.Mesh(sphereGeo, sphereMat);
            sphere.userData = {{ nodeData: n, stage: stage, radius: radius }};
            nodeMeshGroup.add(sphere);
            nodeSpheres.push(sphere);

            // Glowing Halo
            const haloMat = new THREE.SpriteMaterial({{
                map: createHaloTexture(stage.colorHex),
                color: stage.color,
                transparent: true,
                opacity: isCore ? 0.70 : 0.35,
                blending: THREE.AdditiveBlending,
                depthWrite: false
            }});
            const halo = new THREE.Sprite(haloMat);
            halo.scale.set(radius * 2.3, radius * 2.3, 1);
            nodeMeshGroup.add(halo);

            // Core Cyber Rings
            let coreRing1 = null, coreRing2 = null;
            if (isCore) {{
                const ring1Geo = new THREE.TorusGeometry(radius * 1.35, 0.045, 16, 48);
                const ring1Mat = new THREE.MeshBasicMaterial({{ color: 0xF43F5E, wireframe: true, transparent: true, opacity: 0.80 }});
                coreRing1 = new THREE.Mesh(ring1Geo, ring1Mat);
                coreRing1.rotation.x = Math.PI / 3;
                nodeMeshGroup.add(coreRing1);

                const ring2Geo = new THREE.TorusGeometry(radius * 1.65, 0.03, 16, 48);
                const ring2Mat = new THREE.MeshBasicMaterial({{ color: 0x38BDF8, transparent: true, opacity: 0.65 }});
                coreRing2 = new THREE.Mesh(ring2Geo, ring2Mat);
                coreRing2.rotation.y = Math.PI / 4;
                nodeMeshGroup.add(coreRing2);
            }}

            nodeMap.set(n.Id, {{
                id: n.Id,
                data: n,
                stage: stage,
                worldPos: worldPos,
                sphere: sphere,
                group: nodeMeshGroup,
                radius: radius,
                isCore: isCore,
                ring1: coreRing1,
                ring2: coreRing2,
                baseEmissive: isCore ? 0.95 : 0.70,
                pulseOffset: Math.random() * Math.PI * 2
            }});
        }}

        // Build Synaptic Links
        const linkObjects = [];
        const particlePool = [];
        let linkCounter = 0;

        // 1. Cross-Stage Synaptic Weft (Inter-layer connections)
        for (let s = 0; s < 4; s++) {{
            const currentNodes = stageBuckets[s];
            const nextNodes = stageBuckets[s + 1];
            if (!currentNodes.length || !nextNodes.length) continue;

            currentNodes.forEach((srcNode, i) => {{
                nextNodes.forEach((tgtNode, j) => {{
                    // Connect nearest neighbors or key projections
                    if ((i + j) % 2 === 0 || (i === j) || (s === 3)) {{
                        createSynapticCurve(srcNode.Id, tgtNode.Id, true);
                    }}
                }});
            }});
        }}

        // 2. Explicit Domain Links from Knowledge Graph
        linksData.forEach(l => {{
            if (nodeMap.has(l.Source) && nodeMap.has(l.Target)) {{
                createSynapticCurve(l.Source, l.Target, false);
            }}
        }});

        // KULLANICI İSTEĞİ: ""bağ ipleri çizdiğim gibi içten gitsin dışa bükük olmasın""
        // Eğriler dışa taşmayacak; merkeze / omurgaya doğru içten kavis alarak derli toplu bağlanacak.
        function createSynapticCurve(srcId, tgtId, isDenseWeft) {{
            const src = nodeMap.get(srcId);
            const tgt = nodeMap.get(tgtId);
            if (!src || !tgt) return;

            linkCounter++;

            const p1 = src.worldPos;
            const p2 = tgt.worldPos;

            // Midpoint between source and target
            const mid = new THREE.Vector3().addVectors(p1, p2).multiplyScalar(0.5);
            const dist = p1.distanceTo(p2);

            // İçten gitsin: Ortadaki noktayı merkeze (0, 0, mid.z) doğru hafifçe çekiyoruz (asla dışa bükülmez!)
            const centerPoint = new THREE.Vector3(0, 0, mid.z);
            const toCenter = new THREE.Vector3().subVectors(centerPoint, mid);
            const distToCenter = toCenter.length();

            if (distToCenter > 0.05) {{
                // Dışa taşmayı önleyip içe doğru organik yay yapmasını sağlıyoruz
                const pullAmount = Math.min(distToCenter * 0.28, dist * 0.10);
                mid.add(toCenter.normalize().multiplyScalar(pullAmount));
            }}

            const curve = new THREE.QuadraticBezierCurve3(p1, mid, p2);
            const pts = curve.getPoints(16);

            // 3D yerine hafif, kasmayan ve 2-3 piksel kalınlık veren çoklu hat (Multi-line) grubu
            const group = new THREE.Group();
            const lineColor = tgt.isCore ? 0xF43F5E : (isDenseWeft ? 0x64748B : src.stage.color);
            const baseOp = isDenseWeft ? 0.35 : 0.70;

            const mat = new THREE.LineBasicMaterial({{
                color: lineColor,
                transparent: true,
                opacity: baseOp,
                blending: THREE.AdditiveBlending,
                depthWrite: false
            }});

            // Ana merkez hat
            const geo1 = new THREE.BufferGeometry().setFromPoints(pts);
            group.add(new THREE.Line(geo1, mat));

            // Birkaç piksel daha kalınlık veren hafif ofsetli 2. ve 3. hatlar
            const offsets = isDenseWeft ? [0.016] : [0.020, -0.020];
            offsets.forEach(off => {{
                const ptsOff = pts.map(p => new THREE.Vector3(p.x, p.y + off, p.z));
                const geoOff = new THREE.BufferGeometry().setFromPoints(ptsOff);
                group.add(new THREE.Line(geoOff, mat));
            }});

            group.baseOpacity = baseOp;
            group.isDenseWeft = isDenseWeft;
            group.srcId = srcId;
            group.tgtId = tgtId;
            group.mat = mat;
            scene.add(group);
            linkObjects.push(group);

            // Synaptic Signal Pulse Particle along curve (Nano boyutu: 0.13)
            if (!isDenseWeft || Math.random() < 0.45) {{
                const pGeo = new THREE.SphereGeometry(0.13, 8, 8);
                const pMat = new THREE.MeshBasicMaterial({{
                    color: tgt.stage.color,
                    transparent: true,
                    opacity: 0.90,
                    blending: THREE.AdditiveBlending
                }});
                const particle = new THREE.Mesh(pGeo, pMat);
                scene.add(particle);

                particlePool.push({{
                    mesh: particle,
                    curve: curve,
                    speed: 0.003 + Math.random() * 0.005,
                    progress: Math.random(),
                    color: tgt.stage.color,
                    srcId: srcId,
                    tgtId: tgtId
                }});
            }}
        }}

        document.getElementById('txtLinkCount').innerText = linkCounter;

        // Interaction & Raycasting
        const raycaster = new THREE.Raycaster();
        const mouse = new THREE.Vector2(-999, -999);
        const tooltip = document.getElementById('hoverTooltip');
        const inspector = document.getElementById('inspector');
        let hoveredSphere = null;
        let selectedSphere = null;
        let isRotating = true;
        let isPulsing = true;

        // Sürükleme ve Tıklama Ayrımı (Drag / Orbit esnasında seçimin bozulmasını engeller!)
        let pointerDownPos = {{ x: 0, y: 0 }};
        let isPointerDown = false;

        window.addEventListener('pointerdown', (e) => {{
            pointerDownPos = {{ x: e.clientX, y: e.clientY }};
            isPointerDown = true;
        }});

        window.addEventListener('pointermove', (e) => {{
            mouse.x = (e.clientX / window.innerWidth) * 2 - 1;
            mouse.y = -(e.clientY / window.innerHeight) * 2 + 1;

            if (hoveredSphere) {{
                tooltip.style.left = e.clientX + 'px';
                tooltip.style.top = e.clientY + 'px';
            }}
        }});

        window.addEventListener('pointerup', (e) => {{
            if (!isPointerDown) return;
            isPointerDown = false;

            // Kullanıcı fareyi 5 pikselden fazla hareket ettirdiyse bu bir sahne döndürme (orbit/drag) hareketidir!
            // SEÇİM ASLA KAPANMAZ!
            const dragDist = Math.hypot(e.clientX - pointerDownPos.x, e.clientY - pointerDownPos.y);
            if (dragDist > 5) {{
                return;
            }}

            if (e.target.closest('.hud-bar') || e.target.closest('.bottom-stages-bar') || e.target.closest('.inspector-card')) {{
                return;
            }}

            mouse.x = (e.clientX / window.innerWidth) * 2 - 1;
            mouse.y = -(e.clientY / window.innerHeight) * 2 + 1;

            raycaster.setFromCamera(mouse, camera);
            const hits = raycaster.intersectObjects(nodeSpheres);

            if (hits.length > 0) {{
                const sphere = hits[0].object;
                // KULLANICI İSTEĞİ: ""aynı balona tekrar basınca kapansın"" (Toggle mantığı)
                if (selectedSphere && selectedSphere === sphere) {{
                    closeInspector();
                }} else {{
                    selectNode(sphere);
                }}
            }}
        }});

        function selectNode(sphere) {{
            // KULLANICI İSTEĞİ: ""aynı balona tekrar basınca kapansın"" (Toggle mantığı)
            if (selectedSphere && selectedSphere === sphere) {{
                closeInspector();
                return;
            }}
            selectedSphere = sphere;
            const n = sphere.userData.nodeData;
            const st = sphere.userData.stage;

            // Sadece seçili nöronun bağlı olduğu ipler belli kalsın!
            highlightNodeNetwork(n.Id);

            // Kullanıcı isteği: ""yakınlaşmayı kapat"" - Balon seçildiğinde kamera hareket ettirilmez!

            // Populate Inspector Card
            document.getElementById('insBadge').innerText = st.name;
            document.getElementById('insBadge').style.background = st.colorHex;
            document.getElementById('insTitle').innerText = n.Label || n.Id;
            document.getElementById('insDesc').innerText = n.Description || 'Sinaptik kurumsal zeka düğümü.';
            const srcText = n.DocSource || 'Sistem (Genel)';
            const docSrcEl = document.getElementById('insDocSource');
            docSrcEl.innerText = srcText;
            docSrcEl.title = srcText;

            // Degree count
            const connCount = linkObjects.filter(l => l.srcId === n.Id || l.tgtId === n.Id).length;
            const degEl = document.getElementById('insDegree');
            degEl.innerText = `${{connCount}} Aktif`;
            degEl.title = `${{connCount}} Aktif Sinaps Bağlantısı`;

            inspector.style.display = 'flex';
        }}

        function closeInspector() {{
            inspector.style.display = 'none';
            selectedSphere = null;
            resetHighlights();
        }}

        // SADECE SEÇİLİ NÖRONUN BAĞLI OLDUĞU İPLER VE KOMŞULARI GÖZÜKSÜN
        function highlightNodeNetwork(nodeId) {{
            const connectedNodes = new Set([nodeId]);

            // 1. İpler: Yalnızca seçili nöronun bağlı olduğu ipler görünür kalır (ASLA scale.set YAPILMAZ, uzama/sapıtma olmaz!)
            linkObjects.forEach(l => {{
                const isConn = (l.srcId === nodeId || l.tgtId === nodeId);
                if (isConn) {{
                    connectedNodes.add(l.srcId);
                    connectedNodes.add(l.tgtId);
                    l.visible = true;
                    l.mat.opacity = 1.0;
                }} else {{
                    l.visible = false;
                }}
            }});

            // 2. Parçacıklar: Yalnızca bağlı iplerdeki sinyal parçacıkları çalışır
            particlePool.forEach(p => {{
                const isConn = (p.srcId === nodeId || p.tgtId === nodeId);
                p.mesh.visible = isPulsing && isConn;
            }});

            // 3. Düğümler: Seçili düğüm ve doğrudan bağlı komşuları belirginleşir, diğerleri silikleşir
            nodeSpheres.forEach(s => {{
                const sId = s.userData.nodeData.Id;
                if (connectedNodes.has(sId)) {{
                    s.material.transparent = false;
                    s.material.opacity = 1.0;
                    s.material.emissiveIntensity = (sId === nodeId) ? 1.4 : 0.95;
                    s.scale.set(sId === nodeId ? 1.35 : 1.15, sId === nodeId ? 1.35 : 1.15, sId === nodeId ? 1.35 : 1.15);
                }} else {{
                    s.material.transparent = true;
                    s.material.opacity = 0.15;
                    s.material.emissiveIntensity = 0.10;
                    s.scale.set(0.65, 0.65, 0.65);
                }}
            }});
        }}

        function resetHighlights() {{
            nodeSpheres.forEach(s => {{
                const isCore = s.userData.nodeData.Id === 'core';
                s.material.transparent = false;
                s.material.opacity = 1.0;
                s.material.emissiveIntensity = isCore ? 0.95 : 0.70;
                s.scale.set(1, 1, 1);
            }});

            linkObjects.forEach(l => {{
                l.visible = true;
                l.mat.opacity = l.baseOpacity;
            }});

            particlePool.forEach(p => {{
                p.mesh.visible = isPulsing;
            }});
        }}

        // Camera Smooth Fly-To (yalnızca Odak butonu veya Alt Aşama butonlarına basıldığında)
        let targetCamPos = null;
        let targetLookAt = null;
        let flyProgress = 1;

        function flyToPosition(targetPos, distanceOffset) {{
            targetLookAt = targetPos.clone();
            const dir = camera.position.clone().sub(controls.target).normalize();
            targetCamPos = targetPos.clone().add(dir.multiplyScalar(distanceOffset));
            flyProgress = 0;
        }}

        function focusStage(stageIdx) {{
            const st = stagesConfig[stageIdx];
            if (!st) return;

            // Highlight breadcrumb
            document.querySelectorAll('.stage-crumb').forEach((el, i) => {{
                el.classList.toggle('active', i === stageIdx);
            }});

            flyToPosition(new THREE.Vector3(0, 0, st.z), 12);
        }}

        // HUD Controls
        document.getElementById('btnReset').addEventListener('click', () => {{
            closeInspector();
            document.querySelectorAll('.stage-crumb').forEach(el => el.classList.remove('active'));
            targetCamPos = homeCameraPos.clone();
            targetLookAt = homeCameraTarget.clone();
            flyProgress = 0;
        }});

        const btnRotate = document.getElementById('btnRotate');
        btnRotate.addEventListener('click', () => {{
            isRotating = !isRotating;
            btnRotate.classList.toggle('active', isRotating);
        }});

        const btnPulse = document.getElementById('btnPulse');
        btnPulse.addEventListener('click', () => {{
            isPulsing = !isPulsing;
            btnPulse.classList.toggle('active', isPulsing);
            particlePool.forEach(p => p.mesh.visible = isPulsing);
        }});

        // Search Autocomplete
        const searchInput = document.getElementById('nodeSearch');
        searchInput.addEventListener('input', (e) => {{
            const q = e.target.value.trim().toLowerCase();
            if (!q) {{
                resetHighlights();
                return;
            }}

            const match = nodeSpheres.find(s => {{
                const n = s.userData.nodeData;
                return (n.Label || '').toLowerCase().includes(q) || (n.Id || '').toLowerCase().includes(q);
            }});

            if (match) {{
                selectNode(match);
            }}
        }});

        // Animation Loop
        let clock = new THREE.Clock();

        function animate() {{
            requestAnimationFrame(animate);

            const dt = clock.getDelta();
            const time = clock.getElapsedTime();

            // Smooth camera flight
            if (flyProgress < 1) {{
                flyProgress += dt * 2.2;
                if (flyProgress > 1) flyProgress = 1;
                const ease = Math.sin((flyProgress * Math.PI) / 2);
                camera.position.lerpVectors(camera.position, targetCamPos, 0.12);
                controls.target.lerpVectors(controls.target, targetLookAt, 0.12);
            }}

            // Gentle Orbit Rotation around whole synaptic brain
            if (isRotating && flyProgress >= 1 && !selectedSphere) {{
                scene.rotation.y = Math.sin(time * 0.18) * 0.18;
                scene.rotation.x = Math.cos(time * 0.14) * 0.08;
            }}

            // Spin Core Rings
            nodeMap.forEach(n => {{
                if (n.isCore) {{
                    if (n.ring1) n.ring1.rotation.z += 0.016;
                    if (n.ring2) n.ring2.rotation.x += 0.012;
                }}
            }});

            // Signal Pulses along Synapses
            if (isPulsing) {{
                particlePool.forEach(p => {{
                    if (p.mesh.visible) {{
                        p.progress += p.speed;
                        if (p.progress > 1) p.progress = 0;
                        const pos = p.curve.getPoint(p.progress);
                        p.mesh.position.copy(pos);
                    }}
                }});
            }}

            // Node subtle respiration glow
            nodeMap.forEach(n => {{
                if (!selectedSphere || selectedSphere === n.sphere) {{
                    const pulse = Math.sin(time * 2.2 + n.pulseOffset) * 0.15;
                    n.sphere.material.emissiveIntensity = n.baseEmissive + pulse;
                }}
            }});

            // Raycasting hover check
            raycaster.setFromCamera(mouse, camera);
            const intersects = raycaster.intersectObjects(nodeSpheres);

            if (intersects.length > 0) {{
                const sphere = intersects[0].object;
                if (hoveredSphere !== sphere) {{
                    hoveredSphere = sphere;
                    const n = sphere.userData.nodeData;
                    tooltip.innerText = n.Label || n.Id;
                    tooltip.style.display = 'block';
                    tooltip.style.borderColor = sphere.userData.stage.colorHex;
                    document.body.style.cursor = 'pointer';
                }}
            }} else {{
                if (hoveredSphere) {{
                    hoveredSphere = null;
                    tooltip.style.display = 'none';
                    document.body.style.cursor = 'default';
                }}
            }}

            controls.update();
            renderer.render(scene, camera);
        }}

        // Double Click Reset
        window.addEventListener('dblclick', () => {{
            closeInspector();
            targetCamPos = homeCameraPos.clone();
            targetLookAt = homeCameraTarget.clone();
            flyProgress = 0;
        }});

        window.addEventListener('resize', () => {{
            camera.aspect = window.innerWidth / window.innerHeight;
            camera.updateProjectionMatrix();
            renderer.setSize(window.innerWidth, window.innerHeight);
        }});

        animate();
    </script>
</body>
</html>
";
        }
    }
}
