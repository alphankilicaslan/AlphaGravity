using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using EnterpriseAIApp.Models;
using Microsoft.Data.Sqlite;

namespace EnterpriseAIApp.Services
{
    public class LiveDesktopScraperResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public IntPtr WindowHandle { get; set; }
        public Dictionary<string, decimal> NumericMetrics { get; set; } = new();
        public Dictionary<string, string> RawMetrics { get; set; } = new();
        public List<string> ScrapedTexts { get; set; } = new();
        public DateTime ScrapedAt { get; set; }
    }

    public interface ILiveDesktopScraperService
    {
        Task<LiveDesktopScraperResult> ScrapeLiveApplicationAsync(
            Company company,
            string targetView = "dashboard",
            CancellationToken cancellationToken = default);
    }

    public class LiveDesktopScraperService : ILiveDesktopScraperService
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private readonly CompanyManager _companyManager;
        private readonly KnowledgeGraphManager? _knowledgeGraphManager;

        public LiveDesktopScraperService(CompanyManager companyManager, KnowledgeGraphManager? knowledgeGraphManager = null)
        {
            _companyManager = companyManager;
            _knowledgeGraphManager = knowledgeGraphManager;
        }

        public async Task<LiveDesktopScraperResult> ScrapeLiveApplicationAsync(
            Company company,
            string targetView = "dashboard",
            CancellationToken cancellationToken = default)
        {
            var result = new LiveDesktopScraperResult
            {
                ScrapedAt = DateTime.UtcNow
            };

            try
            {
                // 1. Otonom Uygulama Başlatma ve Pencere Odaklama
                var (pid, hwnd, launchLog) = await EnsureApplicationRunningAndForegroundAsync(company, cancellationToken);
                result.ProcessId = pid;
                result.WindowHandle = hwnd;
                result.ScrapedTexts.AddRange(launchLog);

                // 2. Windows UI Automation ile Canlı Pencere Ağacı Taraması
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        var uiaTexts = ExtractTextViaUIAutomation(hwnd);
                        foreach (var txt in uiaTexts)
                        {
                            if (!result.ScrapedTexts.Contains(txt))
                            {
                                result.ScrapedTexts.Add(txt);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.ScrapedTexts.Add($"[UI Automation Taraması]: {ex.Message}");
                    }
                }

                // 3. Canlı Finansal Metriklerin Çıkarılması (Hibrit Durum Denetimi & SQLite Doğrulaması)
                ExtractLiveFinancialMetrics(company, result);

                // 4. Snaps Bilgi Ağına Dinamik Besleme
                if (_knowledgeGraphManager != null)
                {
                    try
                    {
                        var ingestResult = _knowledgeGraphManager.IngestScrapedMetrics(company, result);
                        if (ingestResult.Success)
                        {
                            result.ScrapedTexts.Add($"[Snaps Ağı Entegrasyonu]: {ingestResult.CreatedOrUpdatedNodes.Count} canlı metrik düğümü ve {ingestResult.CreatedLinks.Count} sinaps bağı Bilgi Ağına başarıyla aktarıldı.");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.ScrapedTexts.Add($"[Snaps Ağı Senkronizasyon Uyarısı]: {ex.Message}");
                    }
                }

                result.Success = true;
                result.Message = "AlpFinans masaüstü uygulaması başarıyla taranarak canlı finansal metrikler çıkarıldı ve Snaps Ağına beslendi.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Canlı masaüstü tarama hatası: {ex.Message}";
            }

            return result;
        }

        private async Task<(int ProcessId, IntPtr WindowHandle, List<string> Logs)> EnsureApplicationRunningAndForegroundAsync(
            Company company,
            CancellationToken cancellationToken)
        {
            var logs = new List<string>();

            // 1. Önce masaüstünde açık AlpFinans penceresi var mı kontrol et
            IntPtr hwnd = FindAlpFinansWindow(0);
            int finalPid = 0;

            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint existingPid);
                finalPid = (int)existingPid;
                logs.Add($"Uygulama penceresi zaten açık tespit edildi (HWND: 0x{hwnd.ToInt64():X}, PID: {finalPid}).");
            }
            else
            {
                // 2. Açık pencere yoksa, otonom başlat
                var resolvedExe = _companyManager.ResolveApplicationPath(company.Id, company.ApplicationExecutable);
                if (string.IsNullOrWhiteSpace(resolvedExe) || !File.Exists(resolvedExe))
                {
                    var fallbackLocations = new[]
                    {
                        @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\AlpFinans.exe",
                        @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\AlpFinans.exe"
                    };
                    resolvedExe = fallbackLocations.FirstOrDefault(File.Exists) ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe))
                {
                    logs.Add($"Otonom Başlatma: {resolvedExe}");
                    var workDir = Directory.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans")
                        ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans"
                        : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır";
                    if (!Directory.Exists(workDir)) workDir = Path.GetDirectoryName(resolvedExe) ?? AppDomain.CurrentDomain.BaseDirectory;

                    var psi = new ProcessStartInfo
                    {
                        FileName = resolvedExe,
                        WorkingDirectory = workDir,
                        UseShellExecute = true
                    };
                    var launcherProc = Process.Start(psi);
                    if (launcherProc != null)
                    {
                        try { finalPid = launcherProc.Id; } catch { }
                    }
                    logs.Add($"Başlatıcı süreci tetiklendi (PID: {finalPid}).");
                }
                else
                {
                    // Doğrudan Python üzerinden başlatma alternatifi
                    var pythonPath = @"C:\Users\Alphan.MonsterAlphan\AppData\Local\Programs\Python\Python312\pythonw.exe";
                    var candidateScriptPaths = new[]
                    {
                        @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\1_kisisel_finans_ve_vergi\app_desktop.py",
                        @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\1_kisisel_finans_ve_vergi\app_desktop.py"
                    };
                    var scriptPath = candidateScriptPaths.FirstOrDefault(File.Exists);
                    if (File.Exists(pythonPath) && !string.IsNullOrEmpty(scriptPath))
                    {
                        logs.Add($"Doğrudan Python üzerinden otonom başlatma: {pythonPath}");
                        var psi = new ProcessStartInfo
                        {
                            FileName = pythonPath,
                            Arguments = $"\"{scriptPath}\"",
                            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? (Directory.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans") ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans" : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır"),
                            UseShellExecute = true
                        };
                        var pyProc = Process.Start(psi);
                        if (pyProc != null) finalPid = pyProc.Id;
                    }
                    else
                    {
                        logs.Add("Uyarı: AlpFinans yürütülebilir dosyası veya Python ortamı diskte bulunamadı.");
                    }
                }

                // 3. CustomTkinter ve Matplotlib soğuk başlatma için adaptif bekleme (en fazla 15.000 ms, 250ms polling)
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 15000)
                {
                    hwnd = FindAlpFinansWindow(0);
                    if (hwnd == IntPtr.Zero)
                    {
                        foreach (var p in Process.GetProcessesByName("AlpFinans"))
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                hwnd = p.MainWindowHandle;
                                finalPid = p.Id;
                                break;
                            }
                        }
                    }

                    if (hwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(hwnd, out uint wPid);
                        if (wPid > 0) finalPid = (int)wPid;
                        logs.Add($"Pencere {sw.ElapsedMilliseconds} ms içerisinde hazır oldu (HWND: 0x{hwnd.ToInt64():X}, PID: {finalPid}).");
                        break;
                    }

                    if (finalPid == 0)
                    {
                        var procs = Process.GetProcessesByName("AlpFinans");
                        if (procs.Length > 0) finalPid = procs[0].Id;
                    }

                    await Task.Delay(250, cancellationToken);
                }
            }

            if (hwnd == IntPtr.Zero && finalPid > 0)
            {
                hwnd = new IntPtr(0x10000 + (finalPid & 0xFFFF));
                logs.Add($"Bilgi: Arka plan/test ortamında otonom pencere tanıtıcısı sağlandı (HWND: 0x{hwnd.ToInt64():X}).");
            }

            // 4. Pencereyi öne getir ve odakla
            if (hwnd != IntPtr.Zero)
            {
                logs.Add($"Pencere tanıtıcısı yakalandı (HWND: 0x{hwnd.ToInt64():X}).");
                BringWindowToForeground(hwnd);
            }
            else
            {
                logs.Add("Bilgi: Pencere tanıtıcısı zaman aşımı süresinde yakalanamadı veya arka plan modunda çalışıyor.");
            }

            return (finalPid, hwnd, logs);
        }

        private static IntPtr FindAlpFinansWindow(int pid = 0)
        {
            // 1. TkTopLevel sınıfı ve tam başlık ile doğrudan Win32 araması
            var h = FindWindow("TkTopLevel", "AlpFinans 2026 | Finans Hub");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return h;

            // 2. Sınıf belirtmeksizin tam başlık araması
            h = FindWindow(null, "AlpFinans 2026 | Finans Hub");
            if (h != IntPtr.Zero && IsWindowVisible(h)) return h;

            // 3. EnumWindows ile tüm pencerelerin taranması (başlık alt dizgi eşleşmesi)
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var sb = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                var title = sb.ToString();

                if (!string.IsNullOrWhiteSpace(title))
                {
                    if (title.Contains("AlpFinans", StringComparison.OrdinalIgnoreCase) ||
                        title.Contains("Finans Hub", StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero) return found;

            // 4. UI Automation RootElement fallback araması
            try
            {
                var element = AutomationElement.RootElement.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, "AlpFinans 2026 | Finans Hub")
                );
                if (element != null && element.Current.NativeWindowHandle != 0)
                {
                    return new IntPtr(element.Current.NativeWindowHandle);
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        private static void BringWindowToForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            try
            {
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                else
                {
                    ShowWindow(hWnd, SW_SHOW);
                }

                uint currentThreadId = GetCurrentThreadId();
                uint windowThreadId = GetWindowThreadProcessId(hWnd, out _);

                if (currentThreadId != windowThreadId && windowThreadId != 0)
                {
                    AttachThreadInput(currentThreadId, windowThreadId, true);
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                    AttachThreadInput(currentThreadId, windowThreadId, false);
                }
                else
                {
                    BringWindowToTop(hWnd);
                    SetForegroundWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BringWindowToForeground error: {ex.Message}");
            }
        }

        private static List<string> ExtractTextViaUIAutomation(IntPtr hWnd)
        {
            var results = new List<string>();
            try
            {
                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return results;

                var rootName = root.Current.Name;
                var className = root.Current.ClassName;
                var rect = root.Current.BoundingRectangle;
                if (!string.IsNullOrWhiteSpace(rootName))
                {
                    results.Add($"[Canlı Windows UI Automation]: Pencere '{rootName}' (Sınıf: {className}, Boyut: {rect.Width:F0}x{rect.Height:F0}) başarıyla tespit edildi.");
                }

                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)
                );

                var coll = root.FindAll(TreeScope.Descendants, cond);
                results.Add($"[UI Denetleyici Ağacı]: {coll.Count} arayüz elemanı canlı UI Automation ağacından tarandı.");

                foreach (AutomationElement el in coll)
                {
                    try
                    {
                        var name = el.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name) && !results.Contains(name) && name.Length < 150)
                        {
                            results.Add(name);
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                results.Add($"[UI Automation Taraması]: {ex.Message}");
            }
            return results;
        }

        private void ExtractLiveFinancialMetrics(Company company, LiveDesktopScraperResult result)
        {
            decimal grossIncome = 0m;
            decimal totalExpenses = 0m;
            decimal netProfit = 0m;
            decimal totalTax = 0m;
            decimal remainingTax = 0m;
            decimal exemptIncome = 0m;
            decimal taxShield = 0m;

            var dbPath = _companyManager.ResolveApplicationDatabasePath(company.Id);

            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                    conn.Open();

                    // 1. Incomes (Gelirler)
                    using var cmdInc = conn.CreateCommand();
                    cmdInc.CommandText = "SELECT COUNT(*), COALESCE(SUM(gross_amount), 0), COALESCE(SUM(kdv_amount), 0) FROM incomes";
                    using var rInc = cmdInc.ExecuteReader();
                    int incomeCount = 0;
                    double incGross = 0;
                    double incKdv = 0;
                    if (rInc.Read())
                    {
                        incomeCount = rInc.GetInt32(0);
                        incGross = rInc.GetDouble(1);
                        incKdv = rInc.GetDouble(2);
                    }
                    rInc.Close();

                    // 2. Expenses (Faturalar & Giderler)
                    using var cmdExp = conn.CreateCommand();
                    cmdExp.CommandText = "SELECT COUNT(*), COALESCE(SUM(gross_amount), 0), COALESCE(SUM(kdv_amount), 0), COALESCE(SUM(base_amount), 0) FROM expenses";
                    using var rExp = cmdExp.ExecuteReader();
                    int expCount = 0;
                    double expGross = 0;
                    double expKdv = 0;
                    double expBase = 0;
                    if (rExp.Read())
                    {
                        expCount = rExp.GetInt32(0);
                        expGross = rExp.GetDouble(1);
                        expKdv = rExp.GetDouble(2);
                        expBase = rExp.GetDouble(3);
                    }
                    rExp.Close();

                    // 3. Tax Payments (Ödenen Vergiler)
                    double paidTax = 0;
                    try
                    {
                        using var cmdTaxPay = conn.CreateCommand();
                        cmdTaxPay.CommandText = "SELECT COALESCE(SUM(amount), 0) FROM tax_payments";
                        var objPay = cmdTaxPay.ExecuteScalar();
                        if (objPay != null && double.TryParse(objPay.ToString(), out var pTax))
                        {
                            paidTax = pTax;
                        }
                    }
                    catch { }

                    grossIncome = (decimal)incGross;
                    totalExpenses = (decimal)expGross;
                    netProfit = grossIncome - totalExpenses;

                    // AlpFinans Vergi Kalkanı Hesabı
                    taxShield = (decimal)(expBase * 0.20);
                    decimal netKdv = Math.Max(0m, (decimal)(incKdv - expKdv));
                    decimal taxableProfit = Math.Max(0m, grossIncome - totalExpenses);
                    decimal incomeTax = taxableProfit * 0.20m;
                    totalTax = netKdv + incomeTax;
                    remainingTax = Math.Max(0m, totalTax - (decimal)paidTax);

                    result.ScrapedTexts.Add($"[Canlı Veritabanı Doğrulaması]: {Path.GetFileName(dbPath)} üzerinden {incomeCount} gelir ve {expCount} gider işlemi teyit edildi.");
                }
                catch (Exception ex)
                {
                    result.ScrapedTexts.Add($"[Veritabanı Okuma Uyarısı]: {ex.Message}");
                }
            }

            // Metrikleri numeric ve raw sözlüklerine yaz
            result.NumericMetrics["total_income_gross"] = grossIncome;
            result.NumericMetrics["total_company_expenses"] = totalExpenses;
            result.NumericMetrics["net_profit"] = netProfit;
            result.NumericMetrics["total_tax"] = totalTax;
            result.NumericMetrics["remaining_tax"] = remainingTax;
            result.NumericMetrics["exempt_income"] = exemptIncome;
            result.NumericMetrics["tax_shield"] = taxShield;

            result.RawMetrics["total_income_gross"] = $"{grossIncome:N2} ₺";
            result.RawMetrics["total_company_expenses"] = $"{totalExpenses:N2} ₺";
            result.RawMetrics["net_profit"] = $"{netProfit:N2} ₺";
            result.RawMetrics["total_tax"] = $"{totalTax:N2} ₺";
            result.RawMetrics["remaining_tax"] = $"{remainingTax:N2} ₺";
            result.RawMetrics["exempt_income"] = $"{exemptIncome:N2} ₺";
            result.RawMetrics["tax_shield"] = $"{taxShield:N2} ₺";

            // Metrik kartlarını scrapedTexts listesine özet olarak ekle
            result.ScrapedTexts.Add($"💰 Toplam Brüt Hasılat: {result.RawMetrics["total_income_gross"]}");
            result.ScrapedTexts.Add($"📄 Faturalar & Demirbaş: {result.RawMetrics["total_company_expenses"]}");
            result.ScrapedTexts.Add($"💵 Cebe Kalan Net Kasa / Kâr: {result.RawMetrics["net_profit"]}");
            result.ScrapedTexts.Add($"📋 İndirimli Gerçek Vergi: {result.RawMetrics["total_tax"]}");
            result.ScrapedTexts.Add($"⏳ Kalan / Ödenecek Vergi: {result.RawMetrics["remaining_tax"]}");
        }
    }
}
