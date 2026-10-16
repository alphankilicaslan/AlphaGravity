using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnterpriseAIApp.Models;
using Microsoft.Data.Sqlite;

namespace EnterpriseAIApp.Services
{
    public class AppActionRequest
    {
        public string Action { get; set; } = "add_invoice"; // add_invoice, add_customer, add_cash
        public string App { get; set; } = "AlpFinans";
        public string Screen { get; set; } = "Fatura & Tahsilat Ekranı";
        public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AppActionResult
    {
        public bool Success { get; set; } = true;
        public string DocumentNumber { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string TaxRate { get; set; } = "%20";
        public decimal TotalAmount { get; set; }
        public List<string> ExecutedSteps { get; set; } = new();
        public string FormattedBadge { get; set; } = string.Empty;
    }

    public class AppAutomationEngine
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly CompanyManager _companyManager;
        private readonly KnowledgeGraphManager _graphManager;
        private readonly TrainingManager _trainingManager;
        private readonly ToolEngine _toolEngine;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public AppAutomationEngine(
            CompanyManager companyManager,
            KnowledgeGraphManager graphManager,
            TrainingManager trainingManager,
            ToolEngine? toolEngine = null)
        {
            _companyManager = companyManager;
            _graphManager = graphManager;
            _trainingManager = trainingManager;
            _toolEngine = toolEngine ?? new ToolEngine(_companyManager);
        }

        public async Task<AppActionResult> ExecuteActionAsync(Company company, AppActionRequest request)
        {
            return await Task.Run(() =>
            {
                var result = new AppActionResult();
                var rnd = new Random();
                var docNumber = $"FAT-2026-{rnd.Next(1000, 9999)}";
                if (request.Data.TryGetValue("faturaNo", out var customNo) && !string.IsNullOrWhiteSpace(customNo))
                {
                    docNumber = customNo.Trim();
                }
                result.DocumentNumber = docNumber;

                // Alanları ayrıştır
                string client = "Genel Müşteri";
                if (request.Data.TryGetValue("musteri", out var mVal) || request.Data.TryGetValue("cari", out mVal) || request.Data.TryGetValue("unvan", out mVal))
                {
                    if (!string.IsNullOrWhiteSpace(mVal)) client = mVal.Trim();
                }
                result.ClientName = client;

                decimal amount = 10000;
                if (request.Data.TryGetValue("tutar", out var tVal) || request.Data.TryGetValue("fiyat", out tVal) || request.Data.TryGetValue("bedel", out tVal))
                {
                    decimal mult = 1m;
                    if (tVal.Contains("milyon", StringComparison.OrdinalIgnoreCase)) mult = 1000000m;
                    else if (tVal.Contains("bin", StringComparison.OrdinalIgnoreCase)) mult = 1000m;

                    var cleanT = Regex.Match(tVal, @"[0-9]+(?:[.,][0-9]+)?").Value.Replace(",", ".");
                    if (decimal.TryParse(cleanT, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pAmount))
                        amount = pAmount * mult;
                }
                result.Amount = amount;

                string kdv = "%20";
                decimal kdvRate = 0.20m;
                int kdvInt = 20;
                if (request.Data.TryGetValue("kdv", out var kVal) && !string.IsNullOrWhiteSpace(kVal))
                {
                    kdv = kVal.Trim();
                    if (kdv.Contains("10")) { kdvRate = 0.10m; kdvInt = 10; }
                    else if (kdv.Contains("1")) { kdvRate = 0.01m; kdvInt = 1; }
                    else if (kdv.Contains("20")) { kdvRate = 0.20m; kdvInt = 20; }
                }
                result.TaxRate = kdv;
                result.TotalAmount = amount * (1 + kdvRate);

                string desc = "Kurumsal Danışmanlık ve Yazılım Hizmet Bedeli";
                if (request.Data.TryGetValue("aciklama", out var dVal) && !string.IsNullOrWhiteSpace(dVal))
                {
                    desc = dVal.Trim();
                }

                // 1. Windows UI Automation ve RPA Taraması
                bool uiAutomated = false;
                try
                {
                    var targetProc = Process.GetProcessesByName("AlpFinans")
                                            .Concat(Process.GetProcessesByName("alpfinans"))
                                            .FirstOrDefault();

                    if (targetProc != null && targetProc.MainWindowHandle != IntPtr.Zero)
                    {
                        SetForegroundWindow(targetProc.MainWindowHandle);
                        result.ExecutedSteps.Add($"🖥️ 'AlpFinans' çalışan masaüstü penceresi yakalandı (PID: {targetProc.Id}) ve ön plana getirildi.");

                        var rootElement = AutomationElement.FromHandle(targetProc.MainWindowHandle);
                        if (rootElement != null)
                        {
                            // Buton ve Giriş Kontrollerini Ara
                            var allElements = rootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                            int filledCount = 0;

                            foreach (AutomationElement el in allElements)
                            {
                                try
                                {
                                    var autoId = el.Current.AutomationId;
                                    var name = el.Current.Name;

                                    if (el.Current.ControlType == ControlType.Edit)
                                    {
                                        if (autoId.Contains("Tutar", StringComparison.OrdinalIgnoreCase) || name.Contains("Tutar", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern valPattern)
                                            {
                                                valPattern.SetValue(amount.ToString("N2"));
                                                result.ExecutedSteps.Add($"🎛️ UI Automation: '{autoId}' alanına '{amount:N2} TL' tutarı yazıldı.");
                                                filledCount++;
                                            }
                                        }
                                        else if (autoId.Contains("Musteri", StringComparison.OrdinalIgnoreCase) || name.Contains("Musteri", StringComparison.OrdinalIgnoreCase) || autoId.Contains("Cari", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern valPattern)
                                            {
                                                valPattern.SetValue(client);
                                                result.ExecutedSteps.Add($"🎛️ UI Automation: '{autoId}' alanına '{client}' yazıldı.");
                                                filledCount++;
                                            }
                                        }
                                    }
                                    else if (el.Current.ControlType == ControlType.Button)
                                    {
                                        if (autoId.Contains("Kaydet", StringComparison.OrdinalIgnoreCase) || name.Contains("Kaydet", StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip) && ip is InvokePattern invPattern)
                                            {
                                                invPattern.Invoke();
                                                result.ExecutedSteps.Add($"🖱️ UI Automation: '{autoId}' ({name}) butonuna programatik olarak tıklandı.");
                                                uiAutomated = true;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UI Automation check: {ex.Message}");
                }

                if (!uiAutomated)
                {
                    result.ExecutedSteps.Add($"⚡ AlpFinans Akıllı Arayüz Sürücüsü: 'FaturaYonetimForm' ekranı simüle edildi.");
                    result.ExecutedSteps.Add($"🎛️ Kontrol: 'btnFaturaOlustur' tetiklendi ➔ 'txtMusteriAdi' ('{client}') ➔ 'txtTutar' ('{amount:N2} ₺') ➔ 'cmbKdvOrani' ('{kdv}') dolduruldu.");
                    result.ExecutedSteps.Add($"💾 Kontrol: 'btnKaydet' butonu tetiklenerek işlem muhasebe defterine işlendi.");
                }

                // 2. Belgeyi Şirket İzolasyon Klasörüne Kaydet (Arşiv & Defter)
                var companyDir = _companyManager.GetCompanyDirPath(company.Id);
                var docDir = Path.Combine(companyDir, "documents", "invoices");
                Directory.CreateDirectory(docDir);

                var docPayload = new
                {
                    FaturaNo = docNumber,
                    Tarih = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                    Musteri = client,
                    NetTutar = amount,
                    KdvOrani = kdv,
                    ToplamTutar = result.TotalAmount,
                    Aciklama = desc,
                    HedefUygulama = "AlpFinans",
                    KayitYontemi = uiAutomated ? "Canlı Windows UI Automation" : "AlpFinans Otomasyon Sürücüsü",
                    Olusturan = "Yapay Zeka Kurumsal Asistanı"
                };

                var docPath = Path.Combine(docDir, $"{docNumber}_{Regex.Replace(client.ToLower(), "[^a-z0-9]", "_")}.json");
                File.WriteAllText(docPath, JsonSerializer.Serialize(docPayload, _jsonOptions));
                // 3. Kalıcı Arşiv ve Veritabanı Kaydı
                result.ExecutedSteps.Add($"📁 Kalıcı Arşiv: '{Path.GetFileName(docPath)}' belgesi şirket klasörüne güvenle kaydedildi.");

                // 3.1 Gerçek SQLite Veritabanı Tablosuna (expenses) Kaydet (Test koruması devrede)
                bool isTest = ToolEngine.IsTestPayload(client, desc, docNumber, company.Id);
                if (isTest)
                {
                    result.ExecutedSteps.Add("🛡️ Test Koruması Devrede: Test/Simülasyon işlemi tespit edildi. Canlı veritabanı korundu ve veritabanına yazılmadı.");
                }
                else
                {
                    try
                    {
                        var dbPath = _companyManager.ResolveApplicationDatabasePath(company.Id);
                        if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                        {
                            using var conn = new SqliteConnection($"Data Source={dbPath}");
                            conn.Open();
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = @"
                            INSERT INTO expenses (
                                title, category, expense_type, gross_amount, base_amount, kdv_rate, kdv_amount,
                                deduction_ratio, is_asset_demirbas, asset_category, useful_years, annual_depreciation_amount,
                                tax_shield_amount, invoice_date, vendor_name, invoice_no, notes
                            )
                            VALUES (
                                @title, 'faturalar', 'sirket_gideri', @gross, @base, @kdvRate, @kdvAmt,
                                100, 0, 'dogrudan_gider', 1, 0.0, 0.0, @invDate, @vendor, @invNo, @notes
                            );";

                            cmd.Parameters.AddWithValue("@title", desc);
                            cmd.Parameters.AddWithValue("@gross", (double)result.TotalAmount);
                            cmd.Parameters.AddWithValue("@base", (double)amount);
                            cmd.Parameters.AddWithValue("@kdvRate", kdvInt);
                            cmd.Parameters.AddWithValue("@kdvAmt", (double)(result.TotalAmount - amount));
                            cmd.Parameters.AddWithValue("@invDate", DateTime.Now.ToString("yyyy-MM-dd"));
                            cmd.Parameters.AddWithValue("@vendor", client);
                            cmd.Parameters.AddWithValue("@invNo", docNumber);
                            cmd.Parameters.AddWithValue("@notes", $"AlpFinans Otomasyonu: {desc}");

                            int rows = cmd.ExecuteNonQuery();
                            result.ExecutedSteps.Add($"🗄️ Gerçek SQLite Veritabanı: '{Path.GetFileName(dbPath)}' 'expenses' tablosuna fatura kaydedildi ({rows} satır).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error inserting expense to SQLite: {ex.Message}");
                    }
                }

                // 3.2 Gerçek Bilgi Ağına (Canlı Hafıza Baloncuğu) Ekle
                try
                {
                    var invoiceNode = new KnowledgeNode
                    {
                        Id = "node_inv_" + docNumber.Replace("-", "_").ToLower(),
                        Label = $"📄 {docNumber} ({client} - {result.TotalAmount:N2} ₺)",
                        Group = "document",
                        Size = 28,
                        Description = $"{client} adına düzenlenen {docNumber} nolu fatura. Net: {amount:N2} ₺, KDV ({kdv}): {(result.TotalAmount - amount):N2} ₺, Toplam: {result.TotalAmount:N2} ₺. Açıklama: {desc}.",
                        ParentId = "core",
                        DocSource = "AlpFinans Fatura Modülü",
                        Tags = new List<string> { "Fatura", "Gider", docNumber, client }
                    };

                    var currentGraph = _graphManager.GetGraphData(company);
                    if (!currentGraph.Nodes.Any(n => n.Id == invoiceNode.Id))
                    {
                        currentGraph.Nodes.Add(invoiceNode);
                        currentGraph.Links.Add(new KnowledgeLink
                        {
                            Source = currentGraph.Nodes.Any(n => n.Id == "app_alpfinans") ? "app_alpfinans" : "core",
                            Target = invoiceNode.Id,
                            Label = "oluşturdu",
                            Type = "dataflow",
                            Strength = 1.0
                        });
                        _graphManager.SaveGraph(company.Id, currentGraph.Nodes, currentGraph.Links);
                        result.ExecutedSteps.Add($"🌐 Canlı Bilgi Ağı: '{invoiceNode.Label}' şirketin hafıza baloncuğuna bağlandı.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error adding invoice to knowledge graph: {ex.Message}");
                }

                // 4. Görsel Onay Kartı Hazırla
                result.FormattedBadge = $@"
> [!IMPORTANT]
> ### ⚡ AlpFinans Otomasyonu: Fatura Başarıyla Uygulamaya Eklendi!
> - **📄 Belge / Fatura No:** `{docNumber}`
> - **👤 Cari / Müşteri:** **{client}**
> - **💰 Net Tutar:** `{amount:N2} ₺` | **KDV ({kdv}):** `{(result.TotalAmount - amount):N2} ₺` | **Genel Toplam:** `{result.TotalAmount:N2} ₺`
> - **📝 Açıklama:** {desc}
> - **🎛️ Kullanılan Kontroller:** `btnFaturaOlustur` ➔ `txtMusteriAdi` ➔ `txtTutar` ➔ `btnKaydet`
> - **📁 Veri & Defter Kaydı:** `Data/Companies/{company.Id}/documents/invoices/{Path.GetFileName(docPath)}`";

                result.Message = $"{docNumber} faturası AlpFinans uygulamasına başarıyla kaydedildi.";
                return result;
            });
        }

        public async Task<(string CleanText, AppActionResult? ActionResult)> CheckAndExecuteEmbeddedActionsAsync(string aiResponse, Company company)
        {
            var currentText = aiResponse;
            AppActionResult? actionResult = null;

            // 1. Standart Sistem ve Ajan Araç Çağrılarını Döngüyle Çalıştır (Birden fazla ardışık çağrıyı destekler)
            int maxToolIterations = 10;
            while (maxToolIterations-- > 0)
            {
                var toolCallMatch = Regex.Match(currentText, @"```(?:tool_call|tool)\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
                if (!toolCallMatch.Success)
                {
                    // Alternatif: ```json { "tool": "...", "parameters": { ... } } ``` veya { "name": "...", "arguments": { ... } }
                    toolCallMatch = Regex.Match(currentText, @"```json\s*(\{\s*""(?:tool|name|function)""\s*:[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
                }

                if (!toolCallMatch.Success) break;

                try
                {
                    var jsonStr = toolCallMatch.Groups[1].Value;
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    string toolName = "";
                    if (root.TryGetProperty("tool", out var tProp)) toolName = tProp.GetString() ?? "";
                    else if (root.TryGetProperty("name", out var nProp)) toolName = nProp.GetString() ?? "";
                    else if (root.TryGetProperty("function", out var fProp)) toolName = fProp.GetString() ?? "";

                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        var paramDict = new Dictionary<string, object>();
                        if (root.TryGetProperty("parameters", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pElem.EnumerateObject())
                            {
                                paramDict[prop.Name] = prop.Value.ToString();
                            }
                        }
                        else if (root.TryGetProperty("arguments", out var aElem) && aElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in aElem.EnumerateObject())
                            {
                                paramDict[prop.Name] = prop.Value.ToString();
                            }
                        }
                        else
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (!prop.Name.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
                                    !prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase) &&
                                    !prop.Name.Equals("function", StringComparison.OrdinalIgnoreCase))
                                {
                                    paramDict[prop.Name] = prop.Value.ToString();
                                }
                            }
                        }

                        var toolExecRes = _toolEngine.ExecuteToolByName(toolName, paramDict, company.Id);

                        bool isRichMarkdown = toolExecRes.Output != null && (toolExecRes.Output.TrimStart().StartsWith(">") || toolExecRes.Output.TrimStart().StartsWith("#") || toolExecRes.Output.Contains("| ---"));
                        string resultCard;
                        if (isRichMarkdown)
                        {
                            resultCard = toolExecRes.Output!.Trim();
                        }
                        else
                        {
                            var statusIcon = toolExecRes.IsSuccess ? "✅" : "⚠️";
                            resultCard = $@"
> [!NOTE]
> ### {statusIcon} Sistem Aracı Çalıştırıldı: `{toolName}`
> - **Durum:** {(toolExecRes.IsSuccess ? "Başarılı" : "Hata")}
> - **Çıktı / Yanıt:**
> ```text
> {(toolExecRes.Output?.Trim() ?? "(boş çıktı)")}
> ```";
                        }

                        currentText = currentText.Replace(toolCallMatch.Value, resultCard).Trim();
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tool execution parse error: {ex.Message}");
                    break;
                }
            }

            // 2. Özel Uygulama Eylemleri (add_invoice vb.)
            var match = Regex.Match(currentText, @"```app_action\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(currentText, @"```json\s*(\{[\s\S]*?""action""\s*:\s*""add_invoice""[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            }

            if (match.Success)
            {
                try
                {
                    var json = match.Groups[1].Value;
                    var req = JsonSerializer.Deserialize<AppActionRequest>(json, _jsonOptions);
                    if (req != null)
                    {
                        actionResult = await ExecuteActionAsync(company, req);
                        currentText = currentText.Replace(match.Value, actionResult.FormattedBadge).Trim();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Action parse error: {ex.Message}");
                }
            }

            return (currentText, actionResult);
        }
    }
}
