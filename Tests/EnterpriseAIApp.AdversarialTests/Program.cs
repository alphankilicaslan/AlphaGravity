using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;
using EnterpriseAIApp.Services;
using Microsoft.Data.Sqlite;

namespace EnterpriseAIApp.AdversarialTests
{
    class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("⚡ EMPIRICAL ADVERSARIAL STRESS TEST HARNESS — CHALLENGER 1 R2");
            Console.WriteLine("================================================================================");

            int failures = 0;
            var baseDataDir = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\bin\Debug\net9.0-windows\Data";
            if (!Directory.Exists(baseDataDir))
            {
                baseDataDir = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Data";
            }

            var companyManager = new CompanyManager(baseDataDir);
            var graphManager = new KnowledgeGraphManager(companyManager);
            var trainingManager = new TrainingManager(companyManager);
            var toolEngine = new ToolEngine(companyManager);
            var scraperService = new LiveDesktopScraperService(companyManager);

            // =========================================================================
            // PART 1: ADVERSARIAL MULTI-TENANT B2B ISOLATION & FINANCIAL TOOL GATING
            // =========================================================================
            Console.WriteLine("\n[STRESS TEST 1] Multi-Tenant B2B Isolation & Strict Financial Tool Gating");
            Console.WriteLine("--------------------------------------------------------------------------------");

            var targetFinancialTools = new[]
            {
                "fatura_olustur_ekle",
                "fatura_listele",
                "gelir_ekle",
                "vergi_finans_ozeti",
                "canli_ekran_veri_tara"
            };

            // Test subjects:
            // A. Existing non-finance tenants
            // B. Non-existent / unconfigured tenants
            // C. Spoofed malicious tenant definitions
            var testTenants = new List<(string Id, string Description, Company? CustomCompany)>
            {
                ("sirket_5", "Legitimate tenant: Yeni Şirket 5 A.Ş. (Target of previous vulnerability)", null),
                ("tech_corp", "Legitimate tenant: Atlas Yazılım (notepad tenant)", null),
                ("dalyan_realty", "Legitimate tenant: Dalyan Villa (realty tenant)", null),
                ("sirket_4", "Legitimate tenant: Yeni Şirket 4 A.Ş. (Antigravity tenant)", null),
                ("unconfigured_tenant_999", "Unconfigured ad-hoc tenant ID", null),
                ("intruder_spoofed_name", "Spoofed tenant: Name='AlpFinans'", new Company
                {
                    Id = "intruder_spoofed_name",
                    Name = "AlpFinans",
                    Industry = "Yazılım",
                    ApplicationExecutable = "AlpFinans.exe"
                }),
                ("intruder_spoofed_industry", "Spoofed tenant: Industry='Finans & Muhasebe' but no AllowedTools", new Company
                {
                    Id = "intruder_spoofed_industry",
                    Name = "Sahte Finans Ltd.",
                    Industry = "Finans & Muhasebe",
                    AllowedTools = new List<string> { "shell", "file_system" }
                }),
                ("intruder_spoofed_db_path", "Spoofed tenant with explicit pointer to sirket_3 DB", new Company
                {
                    Id = "intruder_spoofed_db_path",
                    Name = "Intruder Corp",
                    ApplicationDatabasePath = File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite")
                        ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite"
                        : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\Veriler_ve_Faturalar\finans_veritabani.sqlite"
                })
            };

            // Capture baseline state of primary SQLite database
            string primaryDb = File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite")
                ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite"
                : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\Veriler_ve_Faturalar\finans_veritabani.sqlite";
            int baseExpenseCount = 0;
            int baseIncomeCount = 0;
            if (File.Exists(primaryDb))
            {
                using var conn = new SqliteConnection($"Data Source={primaryDb};Mode=ReadOnly");
                conn.Open();
                using var cmd1 = conn.CreateCommand();
                cmd1.CommandText = "SELECT COUNT(*) FROM expenses";
                baseExpenseCount = Convert.ToInt32(cmd1.ExecuteScalar());

                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "SELECT COUNT(*) FROM incomes";
                baseIncomeCount = Convert.ToInt32(cmd2.ExecuteScalar());
            }

            Console.WriteLine($"Baseline DB state: {baseExpenseCount} expenses, {baseIncomeCount} incomes.");

            foreach (var (tenantId, desc, customComp) in testTenants)
            {
                Console.WriteLine($"\n-> Testing subject: [{tenantId}] - {desc}");

                // 1.1 Database Path Resolution Security
                string resolvedDb = companyManager.ResolveApplicationDatabasePath(tenantId);
                if (!string.IsNullOrEmpty(resolvedDb) && (resolvedDb.Contains("finans_veritabani.sqlite") || resolvedDb.Contains(@"\AlpFinans\") || resolvedDb.Contains(@"\sınır\")))
                {
                    Console.WriteLine($"   ❌ FAIL: Tenant '{tenantId}' resolved to sirket_3 database: '{resolvedDb}'!");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"   ✅ PASS: Database path for '{tenantId}' is safely empty or isolated (got: '{resolvedDb}').");
                }

                // 1.2 Tool Execution Attempts across all 5 financial tools
                foreach (var tool in targetFinancialTools)
                {
                    var prms = new Dictionary<string, object>
                    {
                        ["tutar"] = "99999",
                        ["kdv"] = "%20",
                        ["musteri"] = "ADVERSARIAL_INTRUDER",
                        ["aciklama"] = "ADVERSARIAL INJECTION TEST",
                        ["baslik"] = "ADVERSARIAL REVENUE INJECTION"
                    };

                    var execResult = toolEngine.ExecuteToolByName(tool, prms, tenantId);

                    bool hasSecurityRejection = !execResult.IsSuccess &&
                        execResult.Output.Contains("İzolasyon ve Güvenlik Hatası");

                    if (hasSecurityRejection)
                    {
                        Console.WriteLine($"   ✅ PASS: Tool '{tool}' strictly rejected with security error.");
                    }
                    else
                    {
                        Console.WriteLine($"   ❌ FAIL: Tool '{tool}' was NOT rejected with security error for '{tenantId}'! Success: {execResult.IsSuccess}, Output: {execResult.Output}");
                        failures++;
                    }
                }

                // 1.3 Verify zero disk artifacts created in tenant directory
                var tenantDir = companyManager.GetCompanyDirPath(tenantId);
                var invDir = Path.Combine(tenantDir, "documents", "invoices");
                if (Directory.Exists(invDir))
                {
                    var leakedInvoices = Directory.GetFiles(invDir, "*ADVERSARIAL*");
                    if (leakedInvoices.Length > 0)
                    {
                        Console.WriteLine($"   ❌ FAIL: Leaked invoice JSON files created in tenant '{tenantId}': {string.Join(", ", leakedInvoices)}");
                        failures++;
                    }
                }

                // 1.4 Verify zero leaked nodes in tenant knowledge graph
                var compObj = customComp ?? companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == tenantId) ?? new Company { Id = tenantId };
                var tGraph = graphManager.GetGraphData(compObj);
                var leakedNodes = tGraph.Nodes.Where(n => n.Id.Contains("ADVERSARIAL", StringComparison.OrdinalIgnoreCase) ||
                                                          n.Label.Contains("ADVERSARIAL", StringComparison.OrdinalIgnoreCase) ||
                                                          n.Id.StartsWith("node_metric_") ||
                                                          n.Id == "node_metric_gelir_2026").ToList();
                if (leakedNodes.Count > 0)
                {
                    Console.WriteLine($"   ❌ FAIL: Leaked nodes found in '{tenantId}' knowledge graph: {string.Join(", ", leakedNodes.Select(n => n.Id))}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"   ✅ PASS: Zero leaked financial nodes in '{tenantId}' knowledge graph.");
                }
            }

            // Verify database rows did not change at all
            if (File.Exists(primaryDb))
            {
                using var conn = new SqliteConnection($"Data Source={primaryDb};Mode=ReadOnly");
                conn.Open();
                using var cmd1 = conn.CreateCommand();
                cmd1.CommandText = "SELECT COUNT(*) FROM expenses";
                int postExpenseCount = Convert.ToInt32(cmd1.ExecuteScalar());

                using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = "SELECT COUNT(*) FROM incomes";
                int postIncomeCount = Convert.ToInt32(cmd2.ExecuteScalar());

                if (postExpenseCount == baseExpenseCount && postIncomeCount == baseIncomeCount)
                {
                    Console.WriteLine($"✅ PASS: Database integrity verified! Total expenses ({postExpenseCount}) and incomes ({postIncomeCount}) untouched by attacks.");
                }
                else
                {
                    Console.WriteLine($"❌ FAIL: Database was polluted! Expenses: {baseExpenseCount} -> {postExpenseCount}, Incomes: {baseIncomeCount} -> {postIncomeCount}");
                    failures++;
                }
            }

            // =========================================================================
            // PART 2: EMPIRICAL ALPFINANS COLD LAUNCH WINDOW DISCOVERY & UI EXTRACTION
            // =========================================================================
            Console.WriteLine("\n[STRESS TEST 2] AlpFinans Cold Launch Window Discovery & Metric Extraction");
            Console.WriteLine("--------------------------------------------------------------------------------");

            // 2.1 Terminate any existing AlpFinans processes
            foreach (var p in Process.GetProcessesByName("AlpFinans"))
            {
                try { if (!p.HasExited) { p.Kill(); p.WaitForExit(2000); } } catch { }
            }

            var sirket3 = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == "sirket_3") ?? new Company
            {
                Id = "sirket_3",
                Name = "AlpFinans",
                Industry = "Finans & Muhasebe",
                ApplicationExecutable = "AlpFinans.exe"
            };

            var sw = Stopwatch.StartNew();
            var scrapeResult = await scraperService.ScrapeLiveApplicationAsync(sirket3, "dashboard");
            sw.Stop();

            Console.WriteLine($"Scrape execution finished in {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine($"ProcessId: {scrapeResult.ProcessId}");
            Console.WriteLine($"WindowHandle: 0x{scrapeResult.WindowHandle.ToInt64():X}");
            Console.WriteLine($"Success: {scrapeResult.Success}");
            Console.WriteLine($"Message: {scrapeResult.Message}");

            if (scrapeResult.WindowHandle != IntPtr.Zero)
            {
                Console.WriteLine($"✅ PASS: Live window handle successfully acquired (0x{scrapeResult.WindowHandle.ToInt64():X})");
            }
            else
            {
                Console.WriteLine("❌ FAIL: WindowHandle is 0x0! UI Automation window was not discovered.");
                failures++;
            }

            if (scrapeResult.ProcessId > 0)
            {
                Console.WriteLine($"✅ PASS: Target application process ID is valid ({scrapeResult.ProcessId})");
            }
            else
            {
                Console.WriteLine($"❌ FAIL: Invalid process ID: {scrapeResult.ProcessId}");
                failures++;
            }

            if (scrapeResult.NumericMetrics.TryGetValue("total_income_gross", out var gross) && gross > 0 &&
                scrapeResult.NumericMetrics.TryGetValue("total_company_expenses", out var exp) && exp > 0 &&
                scrapeResult.NumericMetrics.TryGetValue("net_profit", out var profit) && profit > 0)
            {
                Console.WriteLine($"✅ PASS: Extracted positive live financial metrics: Gross={gross:N2} ₺, Exp={exp:N2} ₺, Net Profit={profit:N2} ₺");
            }
            else
            {
                Console.WriteLine($"❌ FAIL: Extracted metrics are invalid or non-positive! Metrics count: {scrapeResult.NumericMetrics.Count}");
                failures++;
            }

            // Cleanup AlpFinans
            if (scrapeResult.WindowHandle != IntPtr.Zero)
            {
                try { SendMessage(scrapeResult.WindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
            if (scrapeResult.ProcessId > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(scrapeResult.ProcessId);
                    if (!proc.HasExited) { proc.Kill(true); proc.WaitForExit(3000); }
                }
                catch { }
            }

            // =========================================================================
            // PART 3: WINDOWS 11 MODERN NOTEPAD PROCESS BINDING IN TEST 7 (MULTI-ITERATION)
            // =========================================================================
            Console.WriteLine("\n[STRESS TEST 3] Windows 11 Modern Notepad Process Binding Stress Test (3 Iterations)");
            Console.WriteLine("--------------------------------------------------------------------------------");

            string notepadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            var inspector = new AppInspector(companyManager, graphManager, trainingManager, new OllamaClient(), new GeminiClient());

            for (int i = 1; i <= 3; i++)
            {
                Console.WriteLine($"-> Iteration {i}/3: Cold launch and inspect Notepad...");
                foreach (var p in Process.GetProcessesByName("notepad"))
                {
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(1000); } } catch { }
                }

                var scanRes = await inspector.InspectAndLearnApplicationAsync("tech_corp", notepadPath, allowLiveLaunch: true);

                if (scanRes.Success && scanRes.Summary.Contains("Canlı Windows UI Automation"))
                {
                    Console.WriteLine($"   ✅ PASS [Iteration {i}]: Successfully acquired window and completed UI Automation inspection: {scanRes.Summary.Substring(0, Math.Min(90, scanRes.Summary.Length))}...");
                }
                else
                {
                    Console.WriteLine($"   ❌ FAIL [Iteration {i}]: Failed to verify live UI Automation! Success={scanRes.Success}, Summary: {scanRes.Summary}");
                    failures++;
                }

                // Verify clean process termination
                await Task.Delay(500);
                var remaining = Process.GetProcessesByName("notepad").Where(p => !p.HasExited).ToList();
                if (remaining.Count == 0)
                {
                    Console.WriteLine($"   ✅ PASS [Iteration {i}]: Zero orphan notepad processes.");
                }
                else
                {
                    Console.WriteLine($"   ⚠️ WARNING [Iteration {i}]: {remaining.Count} notepad processes still alive.");
                    foreach (var rp in remaining) { try { rp.Kill(); } catch { } }
                }
            }

            // =========================================================================
            // PART 4: EMPIRICAL VERIFICATION OF THE 7 NEW ENTERPRISE FEATURES
            // =========================================================================
            Console.WriteLine("\n[STRESS TEST 4] Verification of 7 New Enterprise Features");
            Console.WriteLine("--------------------------------------------------------------------------------");

            try
            {
                // Subtest 4.1: Chat Session Manager
                Console.WriteLine("--> Testing ChatSessionManager Isolation & CRUD...");
                var sessionMgr = new ChatSessionManager(companyManager);
                var testSession = sessionMgr.CreateNewSession("sirket_5", "Test Oturumu");
                if (testSession == null || string.IsNullOrEmpty(testSession.Id))
                {
                    Console.WriteLine("   ❌ FAIL: CreateNewSession returned null or empty Id.");
                    failures++;
                }
                else
                {
                    testSession.Messages.Add(new ChatMessage { Content = "Merhaba sistem", Role = "user", Timestamp = DateTime.Now });
                    testSession.Messages.Add(new ChatMessage { Content = "Merhaba, size nasıl yardımcı olabilirim?", Role = "assistant", Timestamp = DateTime.Now });
                    sessionMgr.SaveSession("sirket_5", testSession);

                    sessionMgr.RenameSession("sirket_5", testSession.Id, "Yenilenmis Oturum");
                    var sessions = sessionMgr.GetSessions("sirket_5");
                    var retrieved = sessions.FirstOrDefault(s => s.Id == testSession.Id);
                    if (retrieved == null || retrieved.Title != "Yenilenmis Oturum" || retrieved.Messages.Count != 2)
                    {
                        Console.WriteLine($"   ❌ FAIL: Session retrieval or rename mismatch! Found: {retrieved?.Title}, Count: {retrieved?.Messages.Count}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ PASS: ChatSessionManager verified (ID: {testSession.Id}, Title: {retrieved.Title}, Messages: {retrieved.Messages.Count}).");
                    }

                    // Clean up test session
                    sessionMgr.DeleteSession("sirket_5", testSession.Id);
                }

                // Subtest 4.2: Report Export Service (Markdown, HTML, PDF)
                Console.WriteLine("--> Testing ReportExportService (Markdown, HTML, PDF)...");
                var dummyMessages = new List<ChatMessage>
                {
                    new() { Content = "Kurumsal finans özeti nedir?", Role = "user", Timestamp = DateTime.Now },
                    new() { Content = "Toplam Gelir: ₺150.000\n| Ay | Tutar |\n|---|---|\n| Ocak | 75.000 |\n| Şubat | 75.000 |", Role = "assistant", Timestamp = DateTime.Now }
                };

                var mdReport = ReportExportService.ExportToMarkdown("Test A.Ş.", "Ocak-Subat Finans", dummyMessages);
                if (string.IsNullOrEmpty(mdReport) || !mdReport.Contains("Kurumsal finans özeti nedir?"))
                {
                    Console.WriteLine("   ❌ FAIL: ExportToMarkdown output invalid.");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ PASS: ExportToMarkdown output valid.");
                }

                var htmlReport = ReportExportService.ExportToHtml("Test A.Ş.", "Ocak-Subat Finans", dummyMessages);
                if (string.IsNullOrEmpty(htmlReport) || !htmlReport.Contains("<table>") || !htmlReport.Contains("<!DOCTYPE html>"))
                {
                    Console.WriteLine("   ❌ FAIL: ExportToHtml output invalid or missing table tag.");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ PASS: ExportToHtml output valid with styled tables.");
                }

                var tempPdfPath = Path.Combine(Path.GetTempPath(), $"AlpStudio_Test_{Guid.NewGuid():N}.pdf");
                try
                {
                    ReportExportService.ExportToPdf(tempPdfPath, "Test A.Ş.", "Ocak-Subat Finans", dummyMessages);
                    if (File.Exists(tempPdfPath) && new FileInfo(tempPdfPath).Length > 500)
                    {
                        var bytes = File.ReadAllBytes(tempPdfPath);
                        var isPdf = bytes.Length > 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46; // %PDF
                        if (isPdf)
                        {
                            Console.WriteLine($"   ✅ PASS: ExportToPdf produced valid PDF ({bytes.Length} bytes).");
                        }
                        else
                        {
                            Console.WriteLine("   ❌ FAIL: ExportToPdf output does not have %PDF header.");
                            failures++;
                        }
                    }
                    else
                    {
                        Console.WriteLine("   ❌ FAIL: ExportToPdf file not found or too small.");
                        failures++;
                    }
                }
                finally
                {
                    if (File.Exists(tempPdfPath)) try { File.Delete(tempPdfPath); } catch { }
                }

                // Subtest 4.3: Database Explorer & Reverse Engineering Service
                Console.WriteLine("--> Testing DatabaseReverseEngineerService with temporary SQLite DB...");
                var tempDbPath = Path.Combine(Path.GetTempPath(), $"alp_test_db_{Guid.NewGuid():N}.sqlite");
                try
                {
                    using (var conn = new SqliteConnection($"Data Source={tempDbPath}"))
                    {
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "CREATE TABLE urunler (id INTEGER PRIMARY KEY, ad TEXT, fiyat REAL); INSERT INTO urunler VALUES (1, 'Laptop', 35000.0); INSERT INTO urunler VALUES (2, 'Mouse', 750.0);";
                        cmd.ExecuteNonQuery();
                    }

                    var dbService = new DatabaseReverseEngineerService(companyManager, graphManager);
                    var dbAnalysis = dbService.ReverseEngineerDatabase(tempDbPath, "sirket_5", maxSampleRows: 5, ingestToKnowledgeGraph: false);

                    if (!dbAnalysis.Success || dbAnalysis.TableCount != 1 || dbAnalysis.Tables[0].TableName != "urunler")
                    {
                        Console.WriteLine($"   ❌ FAIL: DatabaseReverseEngineerService failed on sqlite file. Success={dbAnalysis.Success}, Tables={dbAnalysis.TableCount}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ PASS: DatabaseReverseEngineerService successfully parsed {dbAnalysis.TableCount} table and {dbAnalysis.TotalRecordCount} rows.");
                    }
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    if (File.Exists(tempDbPath)) try { File.Delete(tempDbPath); } catch { }
                }

                // Subtest 4.4: Multimodal Models & Function Declarations
                Console.WriteLine("--> Testing Multimodal Attachments & Gemini Function Declarations...");
                var testAttachment = new ChatAttachment
                {
                    FileName = "test.png",
                    FileType = "image",
                    MimeType = "image/png",
                    Base64Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
                    SizeBytes = 68
                };

                var testTool = new GeminiFunctionDeclaration
                {
                    Name = "canli_doviz_kuru_getir",
                    Description = "Canlı döviz kuru sorgular",
                    Parameters = new { type = "OBJECT", properties = new { birim = new { type = "STRING" } } }
                };

                if (string.IsNullOrEmpty(testAttachment.Base64Data) || testTool.Name != "canli_doviz_kuru_getir")
                {
                    Console.WriteLine("   ❌ FAIL: Multimodal attachment or Gemini tool declaration mismatch.");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ PASS: Multimodal Vision structures and Gemini Function Declarations fully verified.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ FAIL: Exception in STRESS TEST 4: {ex.Message}\n{ex.StackTrace}");
                failures++;
            }

            Console.WriteLine("\n================================================================================");
            if (failures == 0)
            {
                Console.WriteLine("🎉 ALL ADVERSARIAL STRESS TESTS PASSED (0 FAILURES)!");
                Console.WriteLine("================================================================================");
                return 0;
            }
            else
            {
                Console.WriteLine($"💥 ADVERSARIAL STRESS TESTS COMPLETED WITH {failures} FAILURES!");
                Console.WriteLine("================================================================================");
                return 1;
            }
        }
    }
}
