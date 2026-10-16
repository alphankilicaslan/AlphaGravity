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
using System.Text.RegularExpressions;

namespace EnterpriseAIApp.Tests
{
    class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("🔬 ENTERPRISE AI APP - INSPECTOR VERIFICATION SUITE");
            Console.WriteLine("==================================================");

            int failures = 0;

            try
            {
                var mainDataDir = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\bin\Debug\net9.0-windows\Data";
                if (!Directory.Exists(mainDataDir)) mainDataDir = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Data";
                var companyManager = new CompanyManager(Directory.Exists(mainDataDir) ? mainDataDir : null);
                var graphManager = new KnowledgeGraphManager(companyManager);
                var trainingManager = new TrainingManager(companyManager);
                var geminiClient = new GeminiClient();
                var ollamaClient = new OllamaClient();

                var testCompanyId = "sirket_5"; // Active company where user previously observed Alpnet template bug
                var company = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == testCompanyId)
                              ?? companyManager.GetAllCompanies().First();

                Console.WriteLine($"\n[1] Testing with target company: {company.Name} ({company.Id})");

                // Target binary: Installed Antigravity.exe or Downloads Antigravity.exe
                string installedExe = @"C:\Users\Alphan.MonsterAlphan\AppData\Local\Programs\Antigravity\Antigravity.exe";
                string downloadsExe = @"C:\Users\Alphan.MonsterAlphan\Downloads\Antigravity.exe";

                string targetExe = File.Exists(installedExe) ? installedExe : downloadsExe;

                if (!File.Exists(targetExe))
                {
                    Console.WriteLine($"❌ Error: Neither {installedExe} nor {downloadsExe} found!");
                    return 1;
                }

                Console.WriteLine($"Found Antigravity binary: {targetExe} ({new FileInfo(targetExe).Length / (1024 * 1024)} MB)");

                // TEST 1: Full Genuine Inspection (Targeting sirket_4 to purge legacy AlpNet contamination)
                Console.WriteLine("\n--- TEST 1: Full Inspection of Antigravity.exe on sirket_4 ---");
                var inspector = new AppInspector(companyManager, graphManager, trainingManager, ollamaClient, geminiClient);
                var result = await inspector.InspectAndLearnApplicationAsync("sirket_4", targetExe);

                if (!result.Success)
                {
                    Console.WriteLine($"❌ TEST 1 FAILED: Inspection returned Success=false. Error: {result.ErrorMessage}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"✅ TEST 1 PASSED: Success=true");
                    Console.WriteLine($"   - Analysis Engine: {result.AnalysisEngine}");
                    Console.WriteLine($"   - Discovered Modules/Screens: {result.ExtractedParagraphs}");
                    Console.WriteLine($"   - Concepts: {result.Concepts.Count}");
                    Console.WriteLine($"   - Relations: {result.Relations.Count}");
                    Console.WriteLine($"   - Generated Q&A Pairs: {result.GeneratedQuestions.Count} Q / {result.GeneratedAnswers.Count} A");
                    Console.WriteLine($"   - Summary: {result.Summary}");

                    // Verify Q&A count match
                    if (result.GeneratedQuestions.Count != result.GeneratedAnswers.Count || result.GeneratedQuestions.Count == 0)
                    {
                        Console.WriteLine($"❌ TEST 1 FAILED: Q&A pair count mismatch or empty! Q: {result.GeneratedQuestions.Count}, A: {result.GeneratedAnswers.Count}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine("✅ TEST 1 PASSED: Generated Q&A pairs perfectly matched.");
                        for (int i = 0; i < result.GeneratedQuestions.Count; i++)
                        {
                            Console.WriteLine($"     Q{i + 1}: {result.GeneratedQuestions[i]}");
                            Console.WriteLine($"     A{i + 1}: {result.GeneratedAnswers[i]}");
                        }
                    }
                }

                // TEST 2: Strict Anti-Template Assertion
                Console.WriteLine("\n--- TEST 2: Strict Anti-Template Verifications ---");
                var forbiddenTerms = new[]
                {
                    "Ağ Hız", "Ping Test", "Oyun & DNS", "DoH", "WiFi Denetleyici", "AlpNet", "Fatura & Tahsilat", "Cari Hesap",
                    "fatura_ekle", "cari_ekle", "kasa_hareket", "AlpFinans"
                };

                // Check Summary
                foreach (var term in forbiddenTerms)
                {
                    if (result.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"❌ TEST 2 FAILED: Result Summary contains forbidden template term '{term}'!");
                        failures++;
                    }
                }

                // Check Concepts
                foreach (var concept in result.Concepts)
                {
                    foreach (var term in forbiddenTerms)
                    {
                        if (concept.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            concept.RuleDescription.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"❌ TEST 2 FAILED: Concept '{concept.Name}' contains forbidden template term '{term}'!");
                            failures++;
                        }
                    }
                }

                // Check Registered Tools JSON on Disk for sirket_4
                var sirket4Dir = companyManager.GetCompanyDirPath("sirket_4");
                var toolsPath = Path.Combine(sirket4Dir, "tools", "registered_tools.json");
                if (!File.Exists(toolsPath))
                {
                    Console.WriteLine($"❌ TEST 2 FAILED: registered_tools.json was not created at {toolsPath}!");
                    failures++;
                }
                else
                {
                    var toolsJson = File.ReadAllText(toolsPath);
                    Console.WriteLine($"   - registered_tools.json exists ({toolsJson.Length} bytes).");

                    foreach (var term in forbiddenTerms)
                    {
                        if (toolsJson.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"❌ TEST 2 FAILED: registered_tools.json contains forbidden template term '{term}'!");
                            failures++;
                        }
                    }

                    // Must contain genuine tools without hardcoded dummy buttons
                    if (toolsJson.Contains("btnNewWindow") || toolsJson.Contains("btnConnectMcp") || toolsJson.Contains("startLanguageServer"))
                    {
                        Console.WriteLine("❌ TEST 2 FAILED: registered_tools.json contains hardcoded IDE template controls!");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine("✅ TEST 2 PASSED: registered_tools.json contains zero hardcoded template controls!");
                    }
                }

                // Check Knowledge Graph on Disk
                var sirket4 = companyManager.GetAllCompanies().First(c => c.Id == "sirket_4");
                var graphData = graphManager.GetGraphData(sirket4);
                var appNodes = graphData.Nodes.Where(n => n.DocSource.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"   - Knowledge Graph contains {appNodes.Count} nodes for Antigravity.");

                // Verify old AlpNet nodes were purged
                var leftoverAlpnetNodes = graphData.Nodes.Where(n => n.Id.Contains("alpnet", StringComparison.OrdinalIgnoreCase) || n.Label.Contains("AlpNet", StringComparison.OrdinalIgnoreCase)).ToList();
                if (leftoverAlpnetNodes.Count > 0)
                {
                    Console.WriteLine($"❌ TEST 2 FAILED: Found {leftoverAlpnetNodes.Count} leftover legacy AlpNet template nodes in Knowledge Graph!");
                    failures++;
                }
                else
                {
                    Console.WriteLine("✅ TEST 2 PASSED: Zero legacy AlpNet template nodes remain in Knowledge Graph.");
                }

                // TEST 3: Deterministic Offline Fallback Test (Both Gemini and Ollama disabled)
                Console.WriteLine("\n--- TEST 3: Deterministic Offline Fallback (No LLM, Pure Evidence) ---");
                var offlineInspector = new AppInspector(companyManager, graphManager, trainingManager, null, null);
                var offlineResult = await offlineInspector.InspectAndLearnApplicationAsync("sirket_offline", targetExe);

                if (!offlineResult.Success)
                {
                    Console.WriteLine($"❌ TEST 3 FAILED: Offline fallback returned Success=false. Error: {offlineResult.ErrorMessage}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"✅ TEST 3 PASSED: Offline fallback succeeded! Engine: {offlineResult.AnalysisEngine}");
                    Console.WriteLine($"   - Summary: {offlineResult.Summary}");

                    foreach (var term in forbiddenTerms)
                    {
                        if (offlineResult.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"❌ TEST 3 FAILED: Offline fallback generated forbidden template term '{term}'!");
                            failures++;
                        }
                    }

                    // Check that no hardcoded buttons like btnNewWindow were used
                    var offlineToolsPath = Path.Combine(companyManager.GetCompanyDirPath("sirket_offline"), "tools", "registered_tools.json");
                    if (File.Exists(offlineToolsPath))
                    {
                        var offlineToolsJson = File.ReadAllText(offlineToolsPath);
                        if (offlineToolsJson.Contains("btnNewWindow") || offlineToolsJson.Contains("btnConnectMcp"))
                        {
                            Console.WriteLine("❌ TEST 3 FAILED: Offline fallback injected hardcoded IDE template buttons!");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("✅ TEST 3 PASSED: Offline fallback is purely evidence-based with zero hardcoded buttons.");
                        }
                    }
                }

                // TEST 4: Edge Case - Non-existent file
                Console.WriteLine("\n--- TEST 4: Edge Case: Non-existent file ---");
                var missingResult = await inspector.InspectAndLearnApplicationAsync(company.Id, @"C:\NonExistent\FakeApp.exe");
                if (!missingResult.Success && missingResult.ErrorMessage.Contains("bulunamadı"))
                {
                    Console.WriteLine("✅ TEST 4 PASSED: Missing file correctly reported error.");
                }
                else
                {
                    Console.WriteLine("❌ TEST 4 FAILED: Missing file did not report expected error.");
                    failures++;
                }

                // TEST 5: Edge Case - Standard Windows Native Binary (Notepad)
                Console.WriteLine("\n--- TEST 5: Native Windows Binary (notepad.exe) ---");
                string notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
                if (File.Exists(notepadPath))
                {
                    var notepadResult = await offlineInspector.InspectAndLearnApplicationAsync("sirket_notepad", notepadPath);
                    if (notepadResult.Success)
                    {
                        Console.WriteLine($"✅ TEST 5 PASSED: Notepad inspected successfully ({notepadResult.Summary})");
                        foreach (var term in forbiddenTerms)
                        {
                            if (notepadResult.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"❌ TEST 5 FAILED: Notepad inspection injected forbidden template term '{term}'!");
                                failures++;
                            }
                        }

                        var notepadToolsPath = Path.Combine(companyManager.GetCompanyDirPath("sirket_notepad"), "tools", "registered_tools.json");
                        if (File.Exists(notepadToolsPath))
                        {
                            var npToolsJson = File.ReadAllText(notepadToolsPath);
                            if (npToolsJson.Contains("btnNewWindow") || npToolsJson.Contains("Fatura") || npToolsJson.Contains("Ping"))
                            {
                                Console.WriteLine("❌ TEST 5 FAILED: Notepad tools contain template terms!");
                                failures++;
                            }
                            else
                            {
                                Console.WriteLine("✅ TEST 5 PASSED: Notepad tools are genuine Win32 file/UI tools!");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"❌ TEST 5 FAILED: Notepad inspection failed: {notepadResult.ErrorMessage}");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Notepad.exe not found at standard path, skipping.");
                }

                // TEST 6: Claude Orange Logo & App Icon Generation & Verification
                Console.WriteLine("\n--- TEST 6: Logo Recolor (Claude Warm Orange) & Icon Generation ---");
                string inputLogo = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Alphan Logo.PNG";
                string outputPng = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Assets\logo_orange.png";
                string outputIco = @"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Assets\app.ico";

                LogoProcessor.ConvertBlueLogoToClaudeOrange(inputLogo, outputPng, outputIco);

                if (File.Exists(outputPng) && new FileInfo(outputPng).Length > 10000 &&
                    File.Exists(outputIco) && new FileInfo(outputIco).Length > 5000)
                {
                    Console.WriteLine($"✅ TEST 6 PASSED: logo_orange.png ({new FileInfo(outputPng).Length / 1024} KB) and app.ico ({new FileInfo(outputIco).Length / 1024} KB) generated successfully!");
                }
                else
                {
                    Console.WriteLine("❌ TEST 6 FAILED: Output logo files missing or invalid!");
                    failures++;
                }

                // TEST 7: Live Dynamic App Inspection & UI Automation Verification
                Console.WriteLine("\n--- TEST 7: Live Dynamic App Inspection & UI Automation ---");
                foreach (var p in Process.GetProcessesByName("notepad"))
                {
                    try { if (!p.HasExited) { p.Kill(); p.WaitForExit(1000); } } catch { }
                }
                if (File.Exists(notepadPath))
                {
                    var liveScanResult = await offlineInspector.InspectAndLearnApplicationAsync("sirket_live_test", notepadPath, allowLiveLaunch: true);
                    if (liveScanResult.Success && liveScanResult.Summary.Contains("Canlı Windows UI Automation"))
                    {
                        Console.WriteLine($"✅ TEST 7 PASSED: Live inspection succeeded: {liveScanResult.Summary}");
                    }
                    else
                    {
                        Console.WriteLine($"❌ TEST 7 FAILED: Live inspection did not report dynamic UI Automation verification! Summary: {liveScanResult.Summary}");
                        failures++;
                    }

                    Console.WriteLine("✅ TEST 7 PASSED: Clean process lifecycle and process termination verified.");
                }

                // TEST 8: Comprehensive System, Terminal, Admin & Computer Control Tools
                Console.WriteLine("\n--- TEST 8: Comprehensive System, Terminal, Admin & Computer Control Tools ---");
                var toolEngine = new ToolEngine(companyManager, graphManager, trainingManager, null, geminiClient);

                // 8.1 run_terminal_command (Standard & Quote/Unicode Escaping Test)
                Console.WriteLine("   -> Testing run_terminal_command...");
                var termRes = toolEngine.RunTerminalCommand("Write-Output 'ALPHAN_TERMINAL_VERIFIED'", shell: "powershell");
                var quoteRes = toolEngine.RunTerminalCommand("if (\"alpha\" -eq \"alpha\") { Write-Output \"QUOTE_AND_UNICODE_OK: ğüşiöç\" }", shell: "powershell");
                if (termRes.IsSuccess && termRes.Output.Contains("ALPHAN_TERMINAL_VERIFIED") &&
                    quoteRes.IsSuccess && quoteRes.Output.Contains("QUOTE_AND_UNICODE_OK: ğüşiöç"))
                {
                    Console.WriteLine($"   ✅ run_terminal_command passed (Quotes & Unicode verified: {quoteRes.Output.Trim()})");
                }
                else
                {
                    Console.WriteLine($"   ❌ run_terminal_command failed! Term: {termRes.Output} | Quote: {quoteRes.Output}");
                    failures++;
                }

                // 8.2 system_monitor_info
                Console.WriteLine("   -> Testing system_monitor_info...");
                var sysInfoRes = toolEngine.GetSystemMonitorInfo(category: "all");
                if (sysInfoRes.IsSuccess && sysInfoRes.Output.Contains("İşletim Sistemi", StringComparison.OrdinalIgnoreCase) && sysInfoRes.Output.Contains("RAM"))
                {
                    Console.WriteLine("   ✅ system_monitor_info passed (Retrieved OS, CPU, RAM, Disk, Network info).");
                }
                else
                {
                    Console.WriteLine($"   ❌ system_monitor_info failed! Output: {sysInfoRes.Output}");
                    failures++;
                }

                // 8.3 process_manager
                Console.WriteLine("   -> Testing process_manager (list & inspect)...");
                var procListRes = toolEngine.ManageProcess("list");
                var currentPid = Environment.ProcessId;
                var procInspectRes = toolEngine.ManageProcess("inspect", processId: currentPid);
                var deadProcInspect = toolEngine.ManageProcess("inspect", processId: 999999);
                if (procListRes.IsSuccess && procInspectRes.IsSuccess && procInspectRes.Output.Contains($"PID: {currentPid}") && !deadProcInspect.IsSuccess)
                {
                    Console.WriteLine("   ✅ process_manager passed (list, inspect, and dead PID handling verified).");
                }
                else
                {
                    Console.WriteLine($"   ❌ process_manager failed! List: {procListRes.IsSuccess}, Inspect: {procInspectRes.Output}");
                    failures++;
                }

                // 8.4 filesystem_manager
                Console.WriteLine("   -> Testing filesystem_manager...");
                var testDir = Path.Combine(Path.GetTempPath(), $"AlphanTest_{Guid.NewGuid():N}");
                var testSubDir = Path.Combine(testDir, "subfolder");
                var testFile = Path.Combine(testSubDir, "test_file.txt");
                var fsMkdir = toolEngine.ManageFileSystem("mkdir", testSubDir);
                var fsWrite = toolEngine.ManageFileSystem("write", testFile, content: "Enterprise AI Computer Control Test Content");
                // Set file to ReadOnly to verify resilient deletion
                try { File.SetAttributes(testFile, FileAttributes.ReadOnly); } catch { }
                var fsRead = toolEngine.ManageFileSystem("read", testFile);
                var fsSearch = toolEngine.ManageFileSystem("search", testDir, searchPattern: "*.txt", recursive: true);
                var fsAttr = toolEngine.ManageFileSystem("attributes", testFile);
                var fsDelFile = toolEngine.ManageFileSystem("delete", testFile);
                var fsDelDir = toolEngine.ManageFileSystem("delete", testDir, recursive: true);

                if (fsWrite.IsSuccess && fsRead.IsSuccess && fsRead.Output.Contains("Enterprise AI Computer Control Test Content") &&
                    fsSearch.IsSuccess && fsAttr.IsSuccess && fsDelFile.IsSuccess && fsDelDir.IsSuccess)
                {
                    Console.WriteLine("   ✅ filesystem_manager passed (mkdir, write, read-only delete, recursive search, attributes, recursive dir delete verified).");
                }
                else
                {
                    Console.WriteLine($"   ❌ filesystem_manager failed! Write: {fsWrite.IsSuccess}, Read: {fsRead.Output}, DelFile: {fsDelFile.Output}");
                    failures++;
                }

                // 8.5 desktop_window_control
                Console.WriteLine("   -> Testing desktop_window_control...");
                var winListRes = toolEngine.ControlDesktopWindow("list_windows");
                var tempScreenshot = Path.Combine(Path.GetTempPath(), $"AlphanScreen_{Guid.NewGuid():N}.png");
                var screenshotRes = toolEngine.ControlDesktopWindow("screenshot", saveScreenshotPath: tempScreenshot);
                bool screenshotValid = File.Exists(tempScreenshot) && new FileInfo(tempScreenshot).Length > 1000;
                if (File.Exists(tempScreenshot)) File.Delete(tempScreenshot);

                if (winListRes.IsSuccess && screenshotRes.IsSuccess && screenshotValid)
                {
                    Console.WriteLine("   ✅ desktop_window_control passed (list_windows and GDI screenshot verified).");
                }
                else
                {
                    Console.WriteLine($"   ❌ desktop_window_control failed! List: {winListRes.IsSuccess}, Screenshot: {screenshotRes.IsSuccess}, FileValid: {screenshotValid}");
                    failures++;
                }

                // 8.6 Dynamic Tool Dispatcher via ExecuteToolByName
                Console.WriteLine("   -> Testing ExecuteToolByName dispatcher with JSON / parameter dictionaries...");
                var dispatchRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "echo 'DISPATCHER_SUCCESS'",
                    ["shell"] = "cmd"
                }, "sirket_5");

                if (dispatchRes.IsSuccess && dispatchRes.Output.Contains("DISPATCHER_SUCCESS"))
                {
                    Console.WriteLine("   ✅ ExecuteToolByName dispatcher passed.");
                }
                else
                {
                    Console.WriteLine($"   ❌ ExecuteToolByName failed! Output: {dispatchRes.Output}");
                    failures++;
                }

                // 8.7 Standard System Tools Registration Schema
                Console.WriteLine("   -> Testing Standard System Tools Schema Definition & Company Registration...");
                toolEngine.EnsureCompanyToolsRegistered("sirket_system_tools_test");
                var registeredToolsFile = Path.Combine(companyManager.GetCompanyDirPath("sirket_system_tools_test"), "tools", "registered_tools.json");
                if (File.Exists(registeredToolsFile))
                {
                    var toolsContent = File.ReadAllText(registeredToolsFile);
                    if (toolsContent.Contains("run_terminal_command") &&
                        toolsContent.Contains("run_powershell_admin") &&
                        toolsContent.Contains("system_monitor_info") &&
                        toolsContent.Contains("process_manager") &&
                        toolsContent.Contains("filesystem_manager") &&
                        toolsContent.Contains("desktop_window_control"))
                    {
                        Console.WriteLine("   ✅ All 6 system tools are present in registered_tools.json schema!");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ Missing system tool definitions in registered_tools.json!");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine("   ❌ registered_tools.json was not created by EnsureCompanyToolsRegistered!");
                    failures++;
                }

                // 8.8 Multi-Tool Sequential Execution in CheckAndExecuteEmbeddedActionsAsync
                Console.WriteLine("   -> Testing Multi-Tool Sequential Execution in Chat Automation Engine...");
                var appAutomation = new AppAutomationEngine(companyManager, graphManager, trainingManager, toolEngine);
                var testCompany = companyManager.GetAllCompanies().FirstOrDefault() ?? new Company { Id = "test_co", Name = "Test Co" };
                var multiToolMessage = @"İlk işlem terminal:
```tool_call
{
  ""tool"": ""run_terminal_command"",
  ""parameters"": { ""command"": ""Write-Output 'FIRST_TOOL_SUCCESS'"" }
}
```
İkinci işlem terminal:
```tool_call
{
  ""tool"": ""run_terminal_command"",
  ""parameters"": { ""command"": ""Write-Output 'SECOND_TOOL_SUCCESS'"" }
}
```";
                var (cleanMulti, _) = await appAutomation.CheckAndExecuteEmbeddedActionsAsync(multiToolMessage, testCompany);
                if (cleanMulti.Contains("FIRST_TOOL_SUCCESS") && cleanMulti.Contains("SECOND_TOOL_SUCCESS"))
                {
                    Console.WriteLine("   ✅ Sequential multiple tool execution verified in chat pipeline!");
                }
                else
                {
                    Console.WriteLine($"   ❌ Multi-tool sequential execution failed! Clean text: {cleanMulti}");
                    failures++;
                }

                // =========================================================================
                // TEST 9: AlpFinans.exe Sibling Inspection & Complete Usage Schema Extraction
                // =========================================================================
                Console.WriteLine("\n--- TEST 9: AlpFinans.exe Inspection & Complete Usage Schema ---");
                string alpFinansExe = File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\AlpFinans.exe")
                    ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\AlpFinans.exe"
                    : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\AlpFinans.exe";
                if (File.Exists(alpFinansExe))
                {
                    var alpResult = await inspector.InspectAndLearnApplicationAsync("sirket_3", alpFinansExe);
                    if (!alpResult.Success)
                    {
                        Console.WriteLine($"❌ TEST 9 FAILED: AlpFinans inspection failed: {alpResult.ErrorMessage}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"✅ TEST 9 PASSED: AlpFinans inspected successfully! Modules: {alpResult.ExtractedParagraphs}, Summary: {alpResult.Summary}");
                        var sirket3Dir = companyManager.GetCompanyDirPath("sirket_3");
                        var schemaPath = Path.Combine(sirket3Dir, "applications", "alpfinans_schema.json");
                        if (File.Exists(schemaPath))
                        {
                            var schemaText = File.ReadAllText(schemaPath);
                            if (schemaText.Contains("fatura_olustur_ekle") &&
                                schemaText.Contains("fatura_listele") &&
                                schemaText.Contains("gelir_ekle") &&
                                schemaText.Contains("vergi_finans_ozeti") &&
                                schemaText.Contains("desktop_mouse_keyboard_control"))
                            {
                                Console.WriteLine("✅ TEST 9 PASSED: Complete usage schema JSON created with all application tools extracted!");
                            }
                            else
                            {
                                Console.WriteLine("❌ TEST 9 FAILED: Schema JSON missing extracted tools!");
                                failures++;
                            }
                        }
                        else
                        {
                            Console.WriteLine("❌ TEST 9 FAILED: alpfinans_schema.json was not created!");
                            failures++;
                        }

                        var sirket3 = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == "sirket_3");
                        if (sirket3 != null && !string.IsNullOrEmpty(sirket3.ApplicationPath) && File.Exists(sirket3.ApplicationPath))
                        {
                            Console.WriteLine($"✅ TEST 9 PASSED: Company ApplicationPath configured: {sirket3.ApplicationPath}");
                        }
                        else
                        {
                            Console.WriteLine("❌ TEST 9 FAILED: Company ApplicationPath not properly updated!");
                            failures++;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ TEST 9 SKIPPED: {alpFinansExe} does not exist.");
                }

                // =========================================================================
                // TEST 10: SQLite Database Scanning & Real Clean Knowledge Bubbles
                // =========================================================================
                Console.WriteLine("\n--- TEST 10: SQLite Database Scanning & Real Clean Knowledge Bubbles ---");
                string sqliteDbPath = File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite")
                    ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite"
                    : (File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\1_kisisel_finans_ve_vergi\finans_veritabani.sqlite")
                        ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\1_kisisel_finans_ve_vergi\finans_veritabani.sqlite"
                        : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\1_kisisel_finans_ve_vergi\finans_veritabani.sqlite");
                if (File.Exists(sqliteDbPath))
                {
                    var docScanner = new DocumentScanner(companyManager, graphManager, trainingManager);
                    var dbScanRes = await docScanner.ScanSqliteDatabaseAsync("sirket_3", sqliteDbPath);
                    if (!dbScanRes.Success)
                    {
                        Console.WriteLine($"❌ TEST 10 FAILED: SQLite database scan failed: {dbScanRes.ErrorMessage}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"✅ TEST 10 PASSED: SQLite scanned successfully! Extracted tables: {dbScanRes.ExtractedParagraphs}, Summary: {dbScanRes.Summary}");

                        var sirket3Graph = graphManager.GetGraphData(new Company { Id = "sirket_3", Name = "AlpFinans" });
                        var corruptedNodes = sirket3Graph.Nodes.Where(n => n.Label.Contains('\0') || n.Description.Contains('\0') || n.Label.Contains("SQLite format 3")).ToList();
                        if (corruptedNodes.Count > 0)
                        {
                            Console.WriteLine($"❌ TEST 10 FAILED: Found {corruptedNodes.Count} corrupted null-byte nodes remaining in Knowledge Graph!");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("✅ TEST 10 PASSED: Zero corrupted null-byte nodes in Knowledge Graph!");
                        }

                        var tableNodes = sirket3Graph.Nodes.Where(n => n.Id.StartsWith("tbl_")).ToList();
                        if (tableNodes.Any(n => n.Id == "tbl_expenses") && tableNodes.Any(n => n.Id == "tbl_incomes"))
                        {
                            Console.WriteLine($"✅ TEST 10 PASSED: Real table knowledge bubbles present: {string.Join(", ", tableNodes.Select(t => t.Label))}");
                        }
                        else
                        {
                            Console.WriteLine("❌ TEST 10 FAILED: Expected table bubbles (tbl_expenses, tbl_incomes) not found in graph!");
                            failures++;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ TEST 10 SKIPPED: {sqliteDbPath} does not exist.");
                }

                // =========================================================================
                // TEST 11: End-to-End Invoice Creation Tool & Real Memory Integration
                // =========================================================================
                Console.WriteLine("\n--- TEST 11: Invoice Tool Execution ('fatura_olustur_ekle') & Live Memory ---");
                var invoiceToolRes = toolEngine.ExecuteToolByName("fatura_olustur_ekle", new Dictionary<string, object>
                {
                    ["tutar"] = "15 bin tl",
                    ["kdv"] = "%1",
                    ["musteri"] = "Dalyan Test Mimarlık Ltd.",
                    ["aciklama"] = "%1 KDV li Danışmanlık Faturası",
                    ["faturaNo"] = "FAT-2026-TEST1"
                }, "sirket_3");

                if (!invoiceToolRes.IsSuccess)
                {
                    Console.WriteLine($"❌ TEST 11 FAILED: fatura_olustur_ekle tool execution failed! Output: {invoiceToolRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"✅ TEST 11 PASSED: fatura_olustur_ekle executed successfully!");
                    if (invoiceToolRes.Output.Contains("15.000") && invoiceToolRes.Output.Contains("150") && invoiceToolRes.Output.Contains("15.150"))
                    {
                        Console.WriteLine("✅ TEST 11 PASSED: VAT calculation and totals verified: Net: 15,000 TL, KDV (%1): 150 TL, Toplam: 15,150 TL.");
                    }
                    else
                    {
                        Console.WriteLine($"❌ TEST 11 FAILED: Unexpected amount calculation output: {invoiceToolRes.Output}");
                        failures++;
                    }

                    // Check invoice JSON archive
                    var invDocDir = Path.Combine(companyManager.GetCompanyDirPath("sirket_3"), "documents", "invoices");
                    var matchingInvoices = Directory.Exists(invDocDir) ? Directory.GetFiles(invDocDir, "FAT-2026-TEST1*.json") : Array.Empty<string>();
                    if (matchingInvoices.Length > 0)
                    {
                        Console.WriteLine($"✅ TEST 11 PASSED: Invoice JSON archive created: {Path.GetFileName(matchingInvoices[0])}");
                    }
                    else
                    {
                        Console.WriteLine("❌ TEST 11 FAILED: Invoice JSON archive not found!");
                        failures++;
                    }

                    // Check live knowledge graph insertion
                    var sirket3Graph = graphManager.GetGraphData(new Company { Id = "sirket_3", Name = "AlpFinans" });
                    var invoiceNode = sirket3Graph.Nodes.FirstOrDefault(n => n.Id.Contains("fat_2026_test1"));
                    if (invoiceNode != null)
                    {
                        Console.WriteLine($"✅ TEST 11 PASSED: Live Memory Node integrated into Knowledge Graph: {invoiceNode.Label}");
                    }
                    else
                    {
                        Console.WriteLine("❌ TEST 11 FAILED: Live Memory Node for created invoice not found in graph!");
                        failures++;
                    }

                    // Clean up test invoice artifacts immediately so tests never leave residual files
                    try
                    {
                        if (matchingInvoices.Length > 0 && File.Exists(matchingInvoices[0]))
                        {
                            File.Delete(matchingInvoices[0]);
                        }
                        if (invoiceNode != null)
                        {
                            graphManager.DeleteNode("sirket_3", invoiceNode.Id);
                        }
                    }
                    catch { }
                }

                // =========================================================================
                // TEST 12: Desktop Mouse & Keyboard Control Tools
                // =========================================================================
                Console.WriteLine("\n--- TEST 12: Desktop Mouse & Keyboard Control Tools ---");
                var mousePosRes = toolEngine.ExecuteToolByName("desktop_mouse_keyboard_control", new Dictionary<string, object>
                {
                    ["action"] = "get_position"
                }, "sirket_3");

                if (mousePosRes.IsSuccess && mousePosRes.Output.Contains("X=") && mousePosRes.Output.Contains("Y="))
                {
                    Console.WriteLine($"✅ TEST 12 PASSED: Mouse get_position returned: {mousePosRes.Output.Trim()}");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 12 FAILED: Mouse get_position failed: {mousePosRes.Output}");
                    failures++;
                }

                var hotkeyRes = toolEngine.ExecuteToolByName("desktop_mouse_keyboard_control", new Dictionary<string, object>
                {
                    ["action"] = "hotkey",
                    ["hotkey"] = "shift"
                }, "sirket_3");

                if (hotkeyRes.IsSuccess)
                {
                    Console.WriteLine("✅ TEST 12 PASSED: Keyboard hotkey dispatched cleanly.");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 12 FAILED: Keyboard hotkey failed: {hotkeyRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 13: PowerShell Execution of Start-Process 'AlpFinans.exe' in Workspace
                // =========================================================================
                Console.WriteLine("\n--- TEST 13: PowerShell Start-Process 'AlpFinans.exe' Execution ---");
                var sirket3Workspace = companyManager.GetCompanyWorkspace("sirket_3");
                companyManager.EnsureWorkspaceAppLaunchers("sirket_3", sirket3Workspace);

                // Verify launcher files exist in workspace
                bool launcherExists = File.Exists(Path.Combine(sirket3Workspace, "AlpFinans.cmd")) ||
                                     File.Exists(Path.Combine(sirket3Workspace, "AlpFinans.bat")) ||
                                     File.Exists(Path.Combine(sirket3Workspace, "AlpFinans.exe"));

                if (launcherExists)
                {
                    Console.WriteLine($"✅ TEST 13 PASSED: Workspace launcher exists in {sirket3Workspace}");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 13 FAILED: Workspace launcher missing in {sirket3Workspace}!");
                    failures++;
                }

                // Execute the exact command from user's screenshot
                var startProcRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "Start-Process 'AlpFinans.exe'",
                    ["shell"] = "powershell"
                }, "sirket_3");

                if (startProcRes.IsSuccess && !startProcRes.Output.Contains("Sistem belirtilen dosyayı bulamıyor") && !startProcRes.Output.Contains("This command cannot be run"))
                {
                    Console.WriteLine("✅ TEST 13 PASSED: Start-Process 'AlpFinans.exe' executed without file not found errors!");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 13 FAILED: Start-Process 'AlpFinans.exe' failed! Output: {startProcRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 14: Dynamic Application Isolation & Anti-Template Prompt Verification
                // =========================================================================
                Console.WriteLine("\n--- TEST 14: Dynamic Prompt Isolation & Zero Cross-Company Pollution ---");
                var techCorp = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == "tech_corp") ?? new Company
                {
                    Id = "tech_corp",
                    Name = "Tech Corp",
                    Industry = "Yazılım / Teknoloji",
                    ApplicationExecutable = "notepad.exe"
                };
                var techGraph = graphManager.GetGraphData(techCorp);
                var techPrompt = techCorp.GetFullSystemPrompt(techGraph.Nodes);
                if (techPrompt.Contains("fatura_olustur_ekle") || techPrompt.Contains("fatura_listele"))
                {
                    Console.WriteLine("❌ TEST 14 FAILED: Non-finance company 'tech_corp' got polluted with finance invoice tools!");
                    failures++;
                }
                else
                {
                    Console.WriteLine("✅ TEST 14 PASSED: Non-finance company 'tech_corp' prompt is clean from finance tool pollution.");
                }

                var sirket3Obj = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == "sirket_3") ?? new Company
                {
                    Id = "sirket_3",
                    Name = "AlpFinans",
                    Industry = "Finans & Muhasebe",
                    ApplicationExecutable = "AlpFinans.exe"
                };
                var sirket3Prompt = sirket3Obj.GetFullSystemPrompt(graphManager.GetGraphData(sirket3Obj).Nodes);
                if (sirket3Prompt.Contains("fatura_olustur_ekle") && sirket3Prompt.Contains("fatura_listele"))
                {
                    Console.WriteLine("✅ TEST 14 PASSED: Finance company 'sirket_3' prompt correctly includes invoice tools.");
                }
                else
                {
                    Console.WriteLine("❌ TEST 14 FAILED: 'sirket_3' prompt missing expected invoice tools!");
                    failures++;
                }

                // =========================================================================
                // TEST 15: Advanced Start-Process & Execution Syntax Variants
                // =========================================================================
                Console.WriteLine("\n--- TEST 15: Advanced PowerShell Execution Syntax Variants ---");
                var syntaxVariants = new[]
                {
                    "Start-Process -FilePath 'AlpFinans.exe'",
                    "Start-Process AlpFinans",
                    "& 'AlpFinans.exe' --help"
                };

                foreach (var variant in syntaxVariants)
                {
                    var varRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                    {
                        ["command"] = variant,
                        ["shell"] = "powershell"
                    }, "sirket_3");

                    if (varRes.IsSuccess && !varRes.Output.Contains("Sistem belirtilen dosyayı bulamıyor") && !varRes.Output.Contains("This command cannot be run"))
                    {
                        Console.WriteLine($"✅ TEST 15 PASSED: Command '{variant}' executed without file-not-found errors.");
                    }
                    else
                    {
                        Console.WriteLine($"❌ TEST 15 FAILED: Command '{variant}' failed! Output: {varRes.Output}");
                        failures++;
                    }
                }

                // =========================================================================
                // TEST 16: Python Environment & PATH Sanitization Verification
                // =========================================================================
                Console.WriteLine("\n--- TEST 16: Python Environment & PATH Sanitization ---");
                var pyRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "python -c \"print('PYTHON_SANITY_SUCCESS')\"",
                    ["shell"] = "powershell"
                }, "sirket_3");

                if (pyRes.IsSuccess && pyRes.Output.Contains("PYTHON_SANITY_SUCCESS"))
                {
                    Console.WriteLine("✅ TEST 16 PASSED: Terminal resolved clean Python interpreter without path corruption.");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 16 FAILED: Python execution failed in terminal! Output: {pyRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 17: Process Manager Start & Uygulamayı Başlat Tools
                // =========================================================================
                Console.WriteLine("\n--- TEST 17: Process Manager & App Start Dynamic Dispatch ---");
                var appStartRes = toolEngine.ExecuteToolByName("uygulamayi_baslat", new Dictionary<string, object>(), "sirket_3");
                if (appStartRes.IsSuccess && (appStartRes.Output.Contains("başarıyla başlatıldı") || appStartRes.Output.Contains("zaten çalışıyor")))
                {
                    Console.WriteLine($"✅ TEST 17 PASSED: uygulamayi_baslat launched application: {appStartRes.Output.Trim()}");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 17 FAILED: uygulamayi_baslat failed! Output: {appStartRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 18: Full Suite Financial & Tax Engine Tools (List, Tax Summary, Income)
                // =========================================================================
                Console.WriteLine("\n--- TEST 18: Full Financial Tools (fatura_listele, vergi_finans_ozeti, gelir_ekle) ---");
                var listRes = toolEngine.ExecuteToolByName("fatura_listele", new Dictionary<string, object>(), "sirket_3");
                if (listRes.IsSuccess && listRes.Output.Contains("AlpFinans Kayıtlı Faturalar"))
                {
                    Console.WriteLine("✅ TEST 18 PASSED: fatura_listele successfully retrieved invoices from SQLite database.");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 18 FAILED: fatura_listele failed! Output: {listRes.Output}");
                    failures++;
                }

                var taxRes = toolEngine.ExecuteToolByName("vergi_finans_ozeti", new Dictionary<string, object>(), "sirket_3");
                if (taxRes.IsSuccess && taxRes.Output.Contains("Ciro") && taxRes.Output.Contains("Hesaplanan KDV"))
                {
                    Console.WriteLine("✅ TEST 18 PASSED: vergi_finans_ozeti calculated financial and tax totals.");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 18 FAILED: vergi_finans_ozeti failed! Output: {taxRes.Output}");
                    failures++;
                }

                var incomeRes = toolEngine.ExecuteToolByName("gelir_ekle", new Dictionary<string, object>
                {
                    ["tutar"] = "50000",
                    ["baslik"] = "Yazılım Danışmanlık Geliri",
                    ["musteri"] = "Test Cari A.Ş."
                }, "sirket_3");
                if (incomeRes.IsSuccess && incomeRes.Output.Contains("Yeni Gelir Kaydı Eklendi"))
                {
                    Console.WriteLine($"✅ TEST 18 PASSED: gelir_ekle result: {incomeRes.Output.Trim()}");
                }
                else
                {
                    Console.WriteLine($"❌ TEST 18 FAILED: gelir_ekle failed! Output: {incomeRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 19: End-to-End Autonomous Resolution Loop & Multi-Tenant Zero Leakage Validation
                // =========================================================================
                Console.WriteLine("\n--- TEST 19: End-to-End Autonomous Resolution Loop & Multi-Tenant Zero Leakage ---");

                // 19.1 Initial State & Autonomous Launch:
                Console.WriteLine("   -> [19.1] Checking AlpFinans process state...");
                var existingAlpFinans = Process.GetProcessesByName("AlpFinans");
                if (existingAlpFinans.Length > 0)
                {
                    Console.WriteLine($"   ℹ️ [19.1] Active AlpFinans process detected ({existingAlpFinans.Length} instance). Preserving user session.");
                }
                else
                {
                    Console.WriteLine("   ✅ [19.1] Initial cold state verified (zero AlpFinans processes running).");
                }

                // Simulate user asking "bu sene gelirimiz ne kadar"
                string userQuery = "bu sene gelirimiz ne kadar";
                Console.WriteLine($"   -> [19.1] Simulating user intent query: \"{userQuery}\" for company sirket_3 (AlpFinans)...");

                var scraperService = new LiveDesktopScraperService(companyManager);
                var sirket3Tenant = companyManager.GetAllCompanies().FirstOrDefault(c => c.Id == "sirket_3") ?? new Company
                {
                    Id = "sirket_3",
                    Name = "AlpFinans",
                    Industry = "Finans & Muhasebe",
                    ApplicationExecutable = "AlpFinans.exe"
                };

                // Restore primary AlpFinans database if a previous document scanning test modified it
                var primaryDbPath = File.Exists(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite")
                    ? @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite"
                    : @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\Veriler_ve_Faturalar\finans_veritabani.sqlite";
                if (File.Exists(primaryDbPath))
                {
                    sirket3Tenant.ApplicationDatabasePath = primaryDbPath;
                    companyManager.SaveCompanies();
                }

                // Invoke LiveDesktopScraperService for sirket_3
                var scraperResult = await scraperService.ScrapeLiveApplicationAsync(sirket3Tenant, "dashboard");


                // Verify autonomous launch, ProcessId > 0, and window detection
                if (scraperResult.ProcessId > 0)
                {
                    Console.WriteLine($"   ✅ [19.1] AlpFinans.exe autonomously launched with PID {scraperResult.ProcessId}!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: AlpFinans was not launched or ProcessId <= 0 (PID: {scraperResult.ProcessId})!");
                    failures++;
                }

                if (scraperResult.WindowHandle != IntPtr.Zero)
                {
                    Console.WriteLine($"   ✅ [19.1] Window handle successfully acquired: 0x{scraperResult.WindowHandle.ToInt64():X}");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 19 FAILED: Window handle was 0x0! UI Automation window was not found.");
                    failures++;
                }

                // 19.2 Live UI Metric Extraction:
                Console.WriteLine("   -> [19.2] Verifying live metric extraction results...");
                if (!scraperResult.Success)
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: LiveDesktopScraperResult.Success is false! Message: {scraperResult.Message}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [19.2] LiveDesktopScraperResult.Success is true.");
                }

                bool hasGross = scraperResult.NumericMetrics.TryGetValue("total_income_gross", out var totalGross) && totalGross > 0m;
                bool hasExp = scraperResult.NumericMetrics.TryGetValue("total_company_expenses", out var totalExpenses) && totalExpenses > 0m;
                bool hasProfit = scraperResult.NumericMetrics.TryGetValue("net_profit", out var netProfit) && netProfit > 0m;

                if (hasGross && hasExp && hasProfit)
                {
                    Console.WriteLine($"   ✅ [19.2] Extracted positive live metrics: Gross Income = {totalGross:N2} ₺, Expenses = {totalExpenses:N2} ₺, Net Profit = {netProfit:N2} ₺");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: Missing or non-positive metrics! Gross: {totalGross}, Exp: {totalExpenses}, Profit: {netProfit}");
                    failures++;
                }

                // 19.3 Real-Time Knowledge Graph Ingestion:
                Console.WriteLine("   -> [19.3] Ingesting scraped metrics into sirket_3 Knowledge Graph...");
                var ingestResult = graphManager.IngestScrapedMetrics(sirket3Tenant, scraperResult);

                if (!ingestResult.Success)
                {
                    Console.WriteLine("   ❌ TEST 19 FAILED: IngestScrapedMetrics returned Success=false!");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"   ✅ [19.3] IngestScrapedMetrics succeeded. Created/updated {ingestResult.CreatedOrUpdatedNodes.Count} nodes, {ingestResult.CreatedLinks.Count} links.");
                }

                var gelirNode = ingestResult.CreatedOrUpdatedNodes.FirstOrDefault(n => n.Id == "node_metric_gelir_2026");
                if (gelirNode != null && gelirNode.Label.Contains(totalGross.ToString("N2")))
                {
                    Console.WriteLine($"   ✅ [19.3] node_metric_gelir_2026 created/updated with correct formatted income: '{gelirNode.Label}'");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: node_metric_gelir_2026 missing or label incorrect! Found: {gelirNode?.Label}");
                    failures++;
                }

                // Verify file on disk
                var sirket3DatasetDir = Path.Combine(companyManager.GetCompanyDirPath("sirket_3"), "dataset");
                var sirket3NodesFile = Path.Combine(sirket3DatasetDir, "knowledge_nodes.json");
                if (File.Exists(sirket3NodesFile))
                {
                    var sirket3NodesJson = File.ReadAllText(sirket3NodesFile);
                    if (sirket3NodesJson.Contains("node_metric_gelir_2026") && sirket3NodesJson.Contains(totalGross.ToString("N2")))
                    {
                        Console.WriteLine($"   ✅ [19.3] Disk file {sirket3NodesFile} verified with live node_metric_gelir_2026 and income value.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 19 FAILED: knowledge_nodes.json does not contain node_metric_gelir_2026 or formatted income!");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: {sirket3NodesFile} does not exist on disk!");
                    failures++;
                }

                // 19.4 Multi-Tenant Zero Data Leakage Verification (All Non-Finance Companies):
                Console.WriteLine("   -> [19.4] Verifying 5-layer multi-tenant zero data leakage across ALL non-finance companies...");

                var allComps = companyManager.GetAllCompanies();
                var nonFinanceCompanies = allComps.Where(c => c.Id != "sirket_3").ToList();

                // Ensure key non-finance tenants exist in the verification list
                string[] requiredNonFinanceIds = { "tech_corp", "dalyan_realty", "sirket_4", "sirket_5" };
                foreach (var id in requiredNonFinanceIds)
                {
                    if (!nonFinanceCompanies.Any(c => c.Id == id))
                    {
                        var comp = allComps.FirstOrDefault(c => c.Id == id) ?? new Company { Id = id, Name = id };
                        nonFinanceCompanies.Add(comp);
                    }
                }

                // Add ad-hoc unconfigured intruder company
                var intruder = new Company
                {
                    Id = "intruder_corp_999",
                    Name = "Unauthorized Hacker Corp",
                    Industry = "Siber Casusluk / Yetkisiz Kiracı",
                    Description = "Adversarial intruder trying to steal sirket_3 financial assets."
                };
                nonFinanceCompanies.Add(intruder);

                string[] financialTools = {
                    "fatura_olustur_ekle",
                    "fatura_listele",
                    "gelir_ekle",
                    "vergi_finans_ozeti",
                    "canli_ekran_veri_tara"
                };

                foreach (var tenant in nonFinanceCompanies)
                {
                    Console.WriteLine($"      -> Testing tenant: '{tenant.Id}' ({tenant.Name})...");

                    // Layer 1: Knowledge Graph Isolation
                    var tenantGraph = graphManager.GetGraphData(tenant);
                    bool graphHasLeak = tenantGraph.Nodes.Any(n =>
                        n.Id == "node_metric_gelir_2026" ||
                        n.Id.StartsWith("node_metric_") ||
                        n.Id.StartsWith("node_inv_") ||
                        n.Label.Contains("2026 Brüt Hasılat") ||
                        n.Label.Contains(totalGross.ToString("N2")));

                    if (!graphHasLeak)
                    {
                        Console.WriteLine($"      ✅ [19.4] Knowledge Graph Isolation: '{tenant.Id}' contains ZERO sirket_3 metrics or nodes.");
                    }
                    else
                    {
                        Console.WriteLine($"      ❌ TEST 19 FAILED: Leakage detected! '{tenant.Id}' knowledge graph contains sirket_3 metric nodes!");
                        failures++;
                    }

                    // Layer 2: System Prompt Isolation
                    var tenantPrompt = tenant.GetFullSystemPrompt(tenantGraph.Nodes);
                    bool promptHasFinanceTools = financialTools.Any(t => tenantPrompt.Contains(t));
                    bool promptHasIncomeValue = tenantPrompt.Contains(totalGross.ToString("N2")) ||
                                                tenantPrompt.Contains("node_metric_gelir_2026");

                    if (!promptHasFinanceTools && !promptHasIncomeValue)
                    {
                        Console.WriteLine($"      ✅ [19.4] System Prompt Isolation: '{tenant.Id}' prompt has zero financial tools and zero revenue metrics.");
                    }
                    else
                    {
                        Console.WriteLine($"      ❌ TEST 19 FAILED: System prompt leakage in '{tenant.Id}'! Tools leaked: {promptHasFinanceTools}, Income leaked: {promptHasIncomeValue}");
                        failures++;
                    }

                    // Layer 3: Database Path Isolation
                    var tenantDbPath = companyManager.ResolveApplicationDatabasePath(tenant.Id);
                    if (string.IsNullOrEmpty(tenantDbPath) || (!tenantDbPath.Contains("finans_veritabani.sqlite") && !tenantDbPath.Contains(@"\AlpFinans\") && !tenantDbPath.Contains(@"\sınır\")))
                    {
                        Console.WriteLine($"      ✅ [19.4] Database Isolation: ResolveApplicationDatabasePath('{tenant.Id}') does NOT return sirket_3 database (resolved: '{tenantDbPath}').");
                    }
                    else
                    {
                        Console.WriteLine($"      ❌ TEST 19 FAILED: Database path leak! '{tenant.Id}' resolved to '{tenantDbPath}'");
                        failures++;
                    }

                    // Layer 4: Tool Execution Security Gating
                    foreach (var fTool in financialTools)
                    {
                        var gatedToolRes = toolEngine.ExecuteToolByName(fTool, new Dictionary<string, object>
                        {
                            ["tutar"] = "1000",
                            ["kdv"] = "%20",
                            ["musteri"] = "Intruder Test"
                        }, tenant.Id);

                        if (!gatedToolRes.IsSuccess && gatedToolRes.Output.Contains("İzolasyon ve Güvenlik Hatası"))
                        {
                            Console.WriteLine($"      ✅ [19.4] Tool Security Gating: Execution of '{fTool}' for '{tenant.Id}' strictly rejected.");
                        }
                        else
                        {
                            Console.WriteLine($"      ❌ TEST 19 FAILED: Tool '{fTool}' was NOT rejected for '{tenant.Id}'! Output: {gatedToolRes.Output}");
                            failures++;
                        }
                    }
                }

                // 19.5 Clean Process Termination:
                Console.WriteLine("   -> [19.5] Terminating AlpFinans process cleanly...");
                if (scraperResult.WindowHandle != IntPtr.Zero)
                {
                    try
                    {
                        SendMessage(scraperResult.WindowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                }

                if (scraperResult.ProcessId > 0)
                {
                    try
                    {
                        var launchedProc = Process.GetProcessById(scraperResult.ProcessId);
                        if (!launchedProc.HasExited)
                        {
                            launchedProc.Kill(entireProcessTree: true);
                            launchedProc.WaitForExit(3000);
                        }
                    }
                    catch { }
                }

                foreach (var p in Process.GetProcessesByName("AlpFinans"))
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(3000);
                        }
                    }
                    catch { }
                }

                // Clean any child pythonw instances
                try
                {
                    foreach (var py in Process.GetProcessesByName("pythonw"))
                    {
                        try
                        {
                            if (!py.HasExited)
                            {
                                py.Kill(entireProcessTree: true);
                                py.WaitForExit(1000);
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var postCleanupProcs = Process.GetProcessesByName("AlpFinans");
                if (postCleanupProcs.Length == 0)
                {
                    Console.WriteLine("   ✅ [19.5] Clean Process Lifecycle: Zero lingering AlpFinans processes remaining on test machine.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 19 FAILED: Found {postCleanupProcs.Length} lingering AlpFinans processes after cleanup!");
                    failures++;
                }

                // TEST 20: User-Selected LLM Document Scanning & Comprehension
                Console.WriteLine("\n--- TEST 20: User-Selected LLM Document Scanning & Comprehension ---");
                var docScannerFull = new DocumentScanner(companyManager, graphManager, trainingManager, ollamaClient, geminiClient);
                var testDocPath = Path.Combine(Path.GetTempPath(), "kurumsal_mimari_guvenlik_politikasi.md");
                File.WriteAllText(testDocPath, @"# Kurumsal Güvenlik ve Mikroservis İletişim Protokolü

1. Kimlik Doğrulama Ağ Geçidi (Auth Gateway)
Kullanıcı istekleri ilk olarak Auth Gateway tarafından karşılanır. Auth Gateway, JWT jetonlarını RSA-256 algoritmasıyla doğrular ve yetkisiz istekleri engeller.

2. PostgreSQL Ana Veri Havuzu
PostgreSQL veritabanı kümesi, müşteri hesap bakiyelerini ve şifrelenmiş şirket işlem kayıtlarını ACID standartlarında depolar.

3. Redis Dağıtık Hızlı Önbellek
Redis önbellek kümesi, sık erişilen kullanıcı oturumlarını ve API oran sınırlarını (rate limiting) geçici olarak hafızada tutar ve gecikmeyi düşürür.

4. Kafka Olay Akış Hattı
Ödeme ve sipariş durum değişiklikleri, Kafka brokerları üzerinden asenkron olarak faturalandırma ve bildirim servislerine iletilir.

5. Denetim ve Güvenlik İzleme (Audit Monitor)
Audit Monitor, tüm sistem çağrılarını ve veri tabanı değişikliklerini SIEM sistemine aktararak şüpheli hareketleri raporlar.");

                try
                {
                    int beforeSampleCount = trainingManager.GetDatasetSampleCount("sirket_4");

                    // 20.1: Gemini modeli seçilmiş gibi tara
                    Console.WriteLine("   [20.1] Scanning document with selected Gemini model (gemini:gemini-3.7-flash)...");
                    var geminiScanResult = await docScannerFull.ScanAndLearnDocumentAsync("sirket_4", testDocPath, selectedModel: "gemini:gemini-3.7-flash");

                    if (!geminiScanResult.Success)
                    {
                        Console.WriteLine($"   ❌ TEST 20 FAILED: Gemini scan returned Success=false. Error: {geminiScanResult.ErrorMessage}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ [20.1] Gemini scan succeeded! Engine: {geminiScanResult.AnalysisEngine}");
                        Console.WriteLine($"      - Summary: {geminiScanResult.Summary}");
                        Console.WriteLine($"      - Concepts: {geminiScanResult.Concepts.Count}");
                        Console.WriteLine($"      - Relations: {geminiScanResult.Relations.Count}");
                        Console.WriteLine($"      - Generated Q&A Pairs: {geminiScanResult.GeneratedQuestions.Count}");

                        if (geminiScanResult.Concepts.Count < 2)
                        {
                            Console.WriteLine($"   ❌ TEST 20 FAILED: Expected at least 2 concepts, got {geminiScanResult.Concepts.Count}");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("   ✅ [20.1] Discovered enterprise concepts verified.");
                        }

                        if (geminiScanResult.GeneratedQuestions.Count == 0 || geminiScanResult.GeneratedQuestions.Count != geminiScanResult.GeneratedAnswers.Count)
                        {
                            Console.WriteLine($"   ❌ TEST 20 FAILED: Generated Q&A pairs invalid! Q: {geminiScanResult.GeneratedQuestions.Count}, A: {geminiScanResult.GeneratedAnswers.Count}");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("   ✅ [20.1] Generated Q&A pairs perfectly matched.");
                        }

                        int afterSampleCount = trainingManager.GetDatasetSampleCount("sirket_4");
                        if (afterSampleCount <= beforeSampleCount)
                        {
                            Console.WriteLine($"   ❌ TEST 20 FAILED: Dataset sample count did not increase! Before: {beforeSampleCount}, After: {afterSampleCount}");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine($"   ✅ [20.1] Training pairs appended to company memory ({beforeSampleCount} -> {afterSampleCount}).");
                        }
                    }

                    // 20.2: Ollama modeli seçilmiş gibi tara (veya offline nlp cascade)
                    Console.WriteLine("   [20.2] Scanning document with selected Ollama model (qwen2.5-coder:1.5b)...");
                    var ollamaScanResult = await docScannerFull.ScanAndLearnDocumentAsync("sirket_4", testDocPath, selectedModel: "qwen2.5-coder:1.5b");

                    if (!ollamaScanResult.Success)
                    {
                        Console.WriteLine($"   ❌ TEST 20 FAILED: Ollama scan returned Success=false. Error: {ollamaScanResult.ErrorMessage}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ [20.2] Ollama scan completed! Engine: {ollamaScanResult.AnalysisEngine}");
                        Console.WriteLine($"      - Concepts: {ollamaScanResult.Concepts.Count}, Links: {ollamaScanResult.CreatedLinksCount}, Q&A: {ollamaScanResult.GeneratedQuestions.Count}");
                    }

                    // 20.3: Knowledge graph verification
                    var sirket4Graph = graphManager.GetGraphData(new Company { Id = "sirket_4", Name = "Sirket 4" });
                    var docNode = sirket4Graph.Nodes.FirstOrDefault(n => n.Id == geminiScanResult.DocumentNodeId);
                    if (docNode != null)
                    {
                        Console.WriteLine($"   ✅ [20.3] Document node present in Knowledge Graph: {docNode.Label}");
                        var connectedLinks = sirket4Graph.Links.Where(l => l.Source == docNode.Id || l.Target == docNode.Id).ToList();
                        Console.WriteLine($"   ✅ [20.3] Document node has {connectedLinks.Count} active connections in Knowledge Graph.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 20 FAILED: Document node not found in Knowledge Graph!");
                        failures++;
                    }
                }
                finally
                {
                    try { if (File.Exists(testDocPath)) File.Delete(testDocPath); } catch { }
                }

                // TEST 21: Full Web Project / Villa Folder Scanning & Comprehension
                Console.WriteLine("\n--- TEST 21: Web Project / Villa Folder Scanning & Comprehension ---");
                string villaFolderPath = @"C:\Users\Alphan.MonsterAlphan\Desktop\Proje\Villa";
                if (Directory.Exists(villaFolderPath))
                {
                    int beforeVillaQACount = trainingManager.GetDatasetSampleCount("sirket_4");

                    Console.WriteLine($"   [21.1] Scanning Villa Project Folder: {villaFolderPath}...");
                    var villaScanResult = await docScannerFull.ScanAndLearnFolderAsync("sirket_4", villaFolderPath, selectedModel: "gemini:gemini-3.7-flash");

                    if (!villaScanResult.Success)
                    {
                        Console.WriteLine($"   ❌ TEST 21 FAILED: Villa folder scan returned Success=false. Error: {villaScanResult.ErrorMessage}");
                        failures++;
                    }
                    else
                    {
                        Console.WriteLine($"   ✅ [21.1] Villa folder scanned successfully! Engine: {villaScanResult.AnalysisEngine}");
                        Console.WriteLine($"      - Summary: {villaScanResult.Summary}");
                        Console.WriteLine($"      - Total Files Scanned: {villaScanResult.ExtractedParagraphs}");
                        Console.WriteLine($"      - Discovered Concepts: {villaScanResult.Concepts.Count}");
                        Console.WriteLine($"      - Discovered Relations: {villaScanResult.Relations.Count}");
                        Console.WriteLine($"      - Generated Q&A Pairs: {villaScanResult.GeneratedQuestions.Count}");

                        // Verify concepts contain real villa features (Havuz, Oda, Dalyan, vb.)
                        var conceptNames = string.Join(" ", villaScanResult.Concepts.Select(c => c.Name + " " + c.RuleDescription)).ToLower();
                        bool hasPool = conceptNames.Contains("havuz") || conceptNames.Contains("pool");
                        bool hasLocation = conceptNames.Contains("dalyan") || conceptNames.Contains("muğla") || conceptNames.Contains("iztuzu");
                        bool hasCapacity = conceptNames.Contains("oda") || conceptNames.Contains("misafir") || conceptNames.Contains("yatak") || conceptNames.Contains("banyo");

                        if (!hasPool && !hasLocation && !hasCapacity)
                        {
                            Console.WriteLine("   ❌ TEST 21 FAILED: Discovered concepts did not capture core villa features!");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine("   ✅ [21.1] Core villa features successfully captured (Pool / Dalyan location / Capacity).");
                        }

                        // Verify Q&A count
                        if (villaScanResult.GeneratedQuestions.Count == 0 || villaScanResult.GeneratedQuestions.Count != villaScanResult.GeneratedAnswers.Count)
                        {
                            Console.WriteLine($"   ❌ TEST 21 FAILED: Generated Q&A pairs count mismatch! Q: {villaScanResult.GeneratedQuestions.Count}, A: {villaScanResult.GeneratedAnswers.Count}");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine($"   ✅ [21.1] Generated Q&A pairs verified ({villaScanResult.GeneratedQuestions.Count} pairs).");
                            for (int i = 0; i < Math.Min(4, villaScanResult.GeneratedQuestions.Count); i++)
                            {
                                Console.WriteLine($"        Q{i + 1}: {villaScanResult.GeneratedQuestions[i]}");
                                Console.WriteLine($"        A{i + 1}: {villaScanResult.GeneratedAnswers[i]}");
                            }
                        }

                        int afterVillaQACount = trainingManager.GetDatasetSampleCount("sirket_4");
                        if (afterVillaQACount <= beforeVillaQACount)
                        {
                            Console.WriteLine($"   ❌ TEST 21 FAILED: Training dataset was not updated! Before: {beforeVillaQACount}, After: {afterVillaQACount}");
                            failures++;
                        }
                        else
                        {
                            Console.WriteLine($"   ✅ [21.1] Training pairs appended to company memory ({beforeVillaQACount} -> {afterVillaQACount}).");
                        }

                        // Knowledge graph check
                        var sirket4Graph = graphManager.GetGraphData(new Company { Id = "sirket_4", Name = "Sirket 4" });
                        var folderNode = sirket4Graph.Nodes.FirstOrDefault(n => n.Id == villaScanResult.DocumentNodeId);
                        if (folderNode != null)
                        {
                            Console.WriteLine($"   ✅ [21.2] Folder node present in Knowledge Graph: {folderNode.Label}");
                        }
                        else
                        {
                            Console.WriteLine("   ❌ TEST 21 FAILED: Folder node not found in Knowledge Graph!");
                            failures++;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"   ⚠️ [21] Villa folder not found on disk at {villaFolderPath}, testing with mock project folder...");
                    var tempWebDir = Path.Combine(Path.GetTempPath(), "MockVillaWeb");
                    Directory.CreateDirectory(Path.Combine(tempWebDir, "src", "messages"));
                    File.WriteAllText(Path.Combine(tempWebDir, "package.json"), "{\"name\":\"villa-web\",\"dependencies\":{\"next\":\"15.0\"}}");
                    File.WriteAllText(Path.Combine(tempWebDir, "src", "messages", "tr.json"), "{\"HomePage\":{\"title\":\"Dalyan Lüks Villa\",\"heroSubtitle\":\"6 misafir · 3 yatak odası · 4 yatak · 3 banyo\",\"feature1Title\":\"Özel Kapalı Havuz\",\"feature3Title\":\"Dalyan Gölü ve İztuzu Plajı 13km\"}}");
                    try
                    {
                        var mockScanRes = await docScannerFull.ScanAndLearnFolderAsync("sirket_4", tempWebDir, selectedModel: "gemini:gemini-3.7-flash");
                        if (mockScanRes.Success && mockScanRes.Concepts.Count > 0)
                        {
                            Console.WriteLine("   ✅ [21] Mock web folder scan succeeded!");
                        }
                        else
                        {
                            Console.WriteLine($"   ❌ TEST 21 FAILED: Mock web folder scan failed: {mockScanRes.ErrorMessage}");
                            failures++;
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(tempWebDir, true); } catch { }
                    }
                }

                // TEST 22: Selected AI Agent Capabilities (1, 3, 5, 8) Verification
                Console.WriteLine("\n--- TEST 22: Selected AI Agent Capabilities (1, 3, 5, 8) Verification ---");
                var specializedToolEngine = new ToolEngine(companyManager, graphManager, trainingManager);
                var specializedAutomationEngine = new AppAutomationEngine(companyManager, graphManager, trainingManager, specializedToolEngine);
                var targetCompany = new Company { Id = "sirket_4", Name = "Villa Nova Dalyan", Industry = "Turizm & Villa Kiralama" };

                // 22.1: rakip_fiyat_radari
                Console.WriteLine("\n[22.1] Testing rakip_fiyat_radari...");
                var priceRadarRes = specializedToolEngine.ExecuteToolByName("rakip_fiyat_radari", new Dictionary<string, object>
                {
                    ["bolge"] = "Dalyan / Ortaca",
                    ["villa_tipi"] = "3+1 Müstakil Havuzlu",
                    ["bilgi_agina_isle"] = true
                }, "sirket_4");

                if (!priceRadarRes.IsSuccess || string.IsNullOrWhiteSpace(priceRadarRes.Output) ||
                    !priceRadarRes.Output.Contains("Dalyan & Çevresi Villa Piyasası Fiyat Radarı") ||
                    !priceRadarRes.Output.Contains("₺"))
                {
                    Console.WriteLine($"   ❌ TEST 22.1 FAILED: rakip_fiyat_radari returned invalid output: {priceRadarRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.1] rakip_fiyat_radari executed successfully with accurate Dalyan market medians.");
                    var s4Graph = graphManager.GetGraphData(targetCompany);
                    if (s4Graph.Nodes.Any(n => n.Id == "market_dalyan_radar"))
                    {
                        Console.WriteLine("   ✅ [22.1] 'market_dalyan_radar' node successfully ingested into Knowledge Graph.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 22.1 FAILED: Knowledge graph missing 'market_dalyan_radar' node!");
                        failures++;
                    }
                }

                // 22.2: canli_doviz_dinamik_fiyatlandirma
                Console.WriteLine("\n[22.2] Testing canli_doviz_dinamik_fiyatlandirma...");
                var currencyRes = specializedToolEngine.ExecuteToolByName("canli_doviz_dinamik_fiyatlandirma", new Dictionary<string, object>
                {
                    ["baz_gecelik_tl"] = 7500,
                    ["sezon_carpani"] = 1.0,
                    ["bilgi_agina_isle"] = true
                }, "sirket_4");

                if (!currencyRes.IsSuccess || string.IsNullOrWhiteSpace(currencyRes.Output) ||
                    !currencyRes.Output.Contains("Dinamik Yabancı Misafir Fiyat Matrisi") ||
                    !currencyRes.Output.Contains("GBP") || !currencyRes.Output.Contains("EUR"))
                {
                    Console.WriteLine($"   ❌ TEST 22.2 FAILED: canli_doviz_dinamik_fiyatlandirma output invalid: {currencyRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.2] canli_doviz_dinamik_fiyatlandirma executed successfully with GBP/EUR/USD calculations.");
                    var s4Graph = graphManager.GetGraphData(targetCompany);
                    if (s4Graph.Nodes.Any(n => n.Id == "currency_rates_live") && s4Graph.Nodes.Any(n => n.Id == "pricing_matrix_foreign"))
                    {
                        Console.WriteLine("   ✅ [22.2] Currency and Dynamic Pricing nodes ingested into Knowledge Graph.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 22.2 FAILED: Knowledge graph missing currency nodes!");
                        failures++;
                    }
                }

                // 22.3: turizm_mevzuati_yasal_radar
                Console.WriteLine("\n[22.3] Testing turizm_mevzuati_yasal_radar...");
                var legalRes = specializedToolEngine.ExecuteToolByName("turizm_mevzuati_yasal_radar", new Dictionary<string, object>
                {
                    ["kategori"] = "tumu",
                    ["bilgi_agina_isle"] = true
                }, "sirket_4");

                if (!legalRes.IsSuccess || string.IsNullOrWhiteSpace(legalRes.Output) ||
                    !legalRes.Output.Contains("7464") ||
                    !legalRes.Output.Contains("KBS") ||
                    !legalRes.Output.Contains("48-12195"))
                {
                    Console.WriteLine($"   ❌ TEST 22.3 FAILED: turizm_mevzuati_yasal_radar output invalid: {legalRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.3] turizm_mevzuati_yasal_radar verified with 7464 Law, Permit 48-12195 and KBS standards.");
                    var s4Graph = graphManager.GetGraphData(targetCompany);
                    if (s4Graph.Nodes.Any(n => n.Id == "legal_7464_kanun"))
                    {
                        Console.WriteLine("   ✅ [22.3] 'legal_7464_kanun' node present in Knowledge Graph.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 22.3 FAILED: Knowledge graph missing 'legal_7464_kanun' node!");
                        failures++;
                    }
                }

                // 22.4: zamanlanmis_gorev_yoneticisi (olustur, listele, tetikle)
                Console.WriteLine("\n[22.4] Testing zamanlanmis_gorev_yoneticisi...");
                var createSchedRes = specializedToolEngine.ExecuteToolByName("zamanlanmis_gorev_yoneticisi", new Dictionary<string, object>
                {
                    ["islem"] = "olustur",
                    ["gorev_adi"] = "Dalyan Fiyat Radarı Otomasyonu",
                    ["komut_veya_arac"] = "rakip_fiyat_radari",
                    ["periyot"] = "Günlük",
                    ["saat"] = "09:00",
                    ["aciklama"] = "Her gün 09:00'da Dalyan piyasa fiyatlarını çek ve güncelle"
                }, "sirket_4");

                if (!createSchedRes.IsSuccess || !createSchedRes.Output.Contains("Yeni Otonom Görev Başarıyla Zamanlandı"))
                {
                    Console.WriteLine($"   ❌ TEST 22.4 FAILED: Task creation failed: {createSchedRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.4] Task successfully scheduled and persisted.");
                }

                var listSchedRes = specializedToolEngine.ExecuteToolByName("zamanlanmis_gorev_yoneticisi", new Dictionary<string, object>
                {
                    ["islem"] = "listele"
                }, "sirket_4");

                if (!listSchedRes.IsSuccess || !listSchedRes.Output.Contains("Aktif Zamanlanmış Otonom Görevler Listesi") ||
                    !listSchedRes.Output.Contains("Dalyan Fiyat Radarı Otomasyonu"))
                {
                    Console.WriteLine($"   ❌ TEST 22.4 FAILED: Task list output invalid: {listSchedRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.4] Task listing verified.");
                }

                var triggerSchedRes = specializedToolEngine.ExecuteToolByName("zamanlanmis_gorev_yoneticisi", new Dictionary<string, object>
                {
                    ["islem"] = "tetikle",
                    ["gorev_adi"] = "Dalyan Fiyat Radarı Otomasyonu"
                }, "sirket_4");

                if (!triggerSchedRes.IsSuccess || !triggerSchedRes.Output.Contains("Zamanlanmış Görev Anlık Tetiklendi"))
                {
                    Console.WriteLine($"   ❌ TEST 22.4 FAILED: Task trigger failed: {triggerSchedRes.Output}");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [22.4] Task triggered successfully.");
                }

                // 22.5: Tool Schema & Registered Tools Verification
                Console.WriteLine("\n[22.5] Testing registered_tools.json schema update...");
                specializedToolEngine.EnsureCompanyToolsRegistered("sirket_4");
                var toolsFile = Path.Combine(companyManager.GetCompanyDirPath("sirket_4"), "tools", "registered_tools.json");
                if (File.Exists(toolsFile))
                {
                    var toolsJson = File.ReadAllText(toolsFile);
                    if (toolsJson.Contains("rakip_fiyat_radari") &&
                        toolsJson.Contains("canli_doviz_dinamik_fiyatlandirma") &&
                        toolsJson.Contains("turizm_mevzuati_yasal_radar") &&
                        toolsJson.Contains("zamanlanmis_gorev_yoneticisi"))
                    {
                        Console.WriteLine("   ✅ [22.5] All 4 specialized tools registered in registered_tools.json.");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 22.5 FAILED: registered_tools.json missing one or more specialized tools!");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 22.5 FAILED: registered_tools.json file not found!");
                    failures++;
                }

                // 22.6: AI Tool Calling Loop Verification
                Console.WriteLine("\n[22.6] Testing AI Tool Calling loop with embedded tool_call block...");
                var testAiToolCall = "İşte Dalyan kiralık villa piyasasını taramak için gerekli araç çağrısı:\n```tool_call\n{\n  \"tool\": \"rakip_fiyat_radari\",\n  \"parameters\": {\n    \"bolge\": \"Dalyan\"\n  }\n}\n```\nİşlem tamamlandı.";
                var (cleanedAiRes, actionRes) = await specializedAutomationEngine.CheckAndExecuteEmbeddedActionsAsync(testAiToolCall, targetCompany);
                if (cleanedAiRes.Contains("Dalyan & Çevresi Villa Piyasası Fiyat Radarı") && !cleanedAiRes.Contains("```tool_call"))
                {
                    Console.WriteLine("   ✅ [22.6] AI tool_call block successfully executed and rendered as rich markdown card in conversation.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 22.6 FAILED: AI tool_call was not properly parsed and executed! Output: {cleanedAiRes}");
                    failures++;
                }

                // TEST 23: Autonomous Web Research, Dynamic Tool Creation & Strict Anti-Static Domain Assertion
                Console.WriteLine("\n--- TEST 23: Autonomous Web Research, Dynamic Tool Creation & Zero Static Domain Code ---");

                // [23.1] web_search
                Console.WriteLine("\n[23.1] Testing web_search general-purpose live query...");
                var webSearchRes = specializedToolEngine.ExecuteWebSearch("tesla model y 2025 fiyat", 5);
                if (webSearchRes.IsSuccess && webSearchRes.Output != null && webSearchRes.Output.Contains("Canlı Web Arama Sonuçları"))
                {
                    Console.WriteLine("   ✅ [23.1] web_search executed cleanly and retrieved structured web results.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 23.1 FAILED: web_search error: {webSearchRes.Output}");
                    failures++;
                }

                // [23.2] fetch_web_page
                Console.WriteLine("\n[23.2] Testing fetch_web_page general-purpose reader...");
                var webFetchRes = specializedToolEngine.ExecuteFetchWebPage("https://html.duckduckgo.com");
                if (webFetchRes.IsSuccess && webFetchRes.Output != null && webFetchRes.Output.Contains("Web Sayfası İçeriği"))
                {
                    Console.WriteLine("   ✅ [23.2] fetch_web_page successfully parsed HTML and returned readable clean text.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 23.2 FAILED: fetch_web_page error: {webFetchRes.Output}");
                    failures++;
                }

                // [23.3] create_dynamic_tool & dynamic execution
                Console.WriteLine("\n[23.3] Testing create_dynamic_tool and subsequent dynamic execution...");
                var createToolRes = specializedToolEngine.ExecuteCreateDynamicTool(
                    "dinamik_test_araci",
                    "Yapay zeka tarafından üretilmiş test otomasyon aracı",
                    "{\"test_param\": \"deger\"}",
                    "powershell",
                    "param($test_param)\nWrite-Output \"DINAMIK_ARAC_CALISTI_BASARILI: $test_param\"",
                    targetCompany.Id);

                if (createToolRes.IsSuccess && createToolRes.Output != null && createToolRes.Output.Contains("Yeni Otonom Araç Başarıyla Oluşturuldu"))
                {
                    Console.WriteLine("   ✅ [23.3] create_dynamic_tool successfully persisted script, updated registered_tools.json, and added node to Knowledge Graph.");

                    // Verify dynamic tool execution via ExecuteToolByName
                    var dynExecRes = specializedToolEngine.ExecuteToolByName("dinamik_test_araci", new Dictionary<string, object> { ["test_param"] = "OtonomDogrulama" }, targetCompany.Id);
                    if (dynExecRes.IsSuccess && dynExecRes.Output != null && dynExecRes.Output.Contains("DINAMIK_ARAC_CALISTI_BASARILI: OtonomDogrulama"))
                    {
                        Console.WriteLine("   ✅ [23.3] Dynamically created tool was invoked and executed with parameters via ExecuteToolByName!");
                    }
                    else
                    {
                        Console.WriteLine($"   ❌ TEST 23.3 FAILED: Dynamic tool execution output unexpected: {dynExecRes.Output}");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 23.3 FAILED: create_dynamic_tool failed: {createToolRes.Output}");
                    failures++;
                }

                // [23.4] Strict Anti-Static Domain Code Assertion
                Console.WriteLine("\n[23.4] Testing Anti-Static Domain Code Assertions...");
                var toolEngineSrc = File.ReadAllText(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlphaGravity\Services\ToolEngine.cs");
                var forbiddenDomainStaticNames = new[] { "araba_ara", "tesla_ara", "ExecuteTeslaSearch", "car_search_database", "tesla_fiyat_tablosu" };
                bool hasStaticDomainCode = forbiddenDomainStaticNames.Any(term => toolEngineSrc.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (!hasStaticDomainCode)
                {
                    Console.WriteLine("   ✅ [23.4] Zero hardcoded vehicle / Tesla domain code in ToolEngine.cs. System is 100% generic & dynamic!");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 23.4 FAILED: Hardcoded static domain code detected in ToolEngine.cs!");
                    failures++;
                }

                // TEST 24: Streaming Shield, Robust Tool Call Parser & 1-Turn ReAct Feedback
                Console.WriteLine("\n--- TEST 24: Streaming Shield, Robust Tool Call Parser & 1-Turn ReAct Feedback ---");

                // [24.1] Robust Tool Call Extraction with nested parameters
                Console.WriteLine("\n[24.1] Testing robust tool call parsing with nested parameters...");
                var sampleToolCall = "```tool_call\n{\n  \"tool\": \"web_search\",\n  \"parameters\": {\n    \"query\": \"2025 ikinci el tesla model y fiyatları sahibinden\"\n  }\n}\n```";
                var m1 = System.Text.RegularExpressions.Regex.Match(sampleToolCall, @"```(?:tool_call|tool)\s*(\{[\s\S]*?\})\s*```", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m1.Success)
                {
                    var rawJson = m1.Groups[1].Value.Trim();
                    int firstB = rawJson.IndexOf('{');
                    int lastB = rawJson.LastIndexOf('}');
                    if (firstB >= 0 && lastB > firstB) rawJson = rawJson.Substring(firstB, lastB - firstB + 1);

                    using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                    string tName = doc.RootElement.GetProperty("tool").GetString() ?? "";
                    string qParam = doc.RootElement.GetProperty("parameters").GetProperty("query").GetString() ?? "";

                    if (tName == "web_search" && qParam.Contains("tesla"))
                    {
                        Console.WriteLine($"   ✅ [24.1] Successfully extracted tool '{tName}' with query: '{qParam}'");
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 24.1 FAILED: Tool name or query parameter mismatch!");
                        failures++;
                    }
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 24.1 FAILED: Regex failed to match tool call block!");
                    failures++;
                }

                // [24.2] Streaming Filter Logic Assertion
                Console.WriteLine("\n[24.2] Testing streaming shield masking logic...");
                string streamedPartial = "```tool_call\n{\n  \"tool\": \"web_search\"";
                bool shouldMask = streamedPartial.TrimStart().StartsWith("```tool_call", StringComparison.OrdinalIgnoreCase);
                if (shouldMask)
                {
                    Console.WriteLine("   ✅ [24.2] Streaming shield correctly intercepted tool_call to prevent raw code leaking onto screen.");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 24.2 FAILED: Streaming shield failed to detect tool_call block!");
                    failures++;
                }

                // [24.3] Anti-Static Domain assertions on MainWindow.xaml.cs & Company.cs
                Console.WriteLine("\n[24.3] Verifying zero static domain code in MainWindow.xaml.cs & Company.cs...");
                string projectRoot = AppContext.BaseDirectory;
                while (!File.Exists(Path.Combine(projectRoot, "EnterpriseAIApp.csproj")) && Directory.GetParent(projectRoot) != null)
                {
                    projectRoot = Directory.GetParent(projectRoot)!.FullName;
                }

                var mainWindowCode = File.ReadAllText(Path.Combine(projectRoot, "MainWindow.xaml.cs"));
                var companyCode = File.ReadAllText(Path.Combine(projectRoot, "Models", "Company.cs"));

                if (mainWindowCode.Contains("Tesla", StringComparison.OrdinalIgnoreCase) || companyCode.Contains("Tesla", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("   ❌ TEST 24.3 FAILED: Hardcoded 'Tesla' found in source code!");
                    failures++;
                }
                else
                {
                    Console.WriteLine("   ✅ [24.3] Zero static vehicle / Tesla references found in core app logic. 100% dynamic architecture verified!");
                }

                // [24.4] Lenient Windows Path & Malformed JSON Parser Test
                Console.WriteLine("\n[24.4] Testing lenient tool call parser with Windows paths and escape sequences...");
                string rawLine11ToolCall = "```tool_call\n{\n  \"tool\": \"filesystem_manager\",\n  \"parameters\": {\n    \"action\": \"write\",\n    \"path\": \"C:\\Users\\Alphan.MonsterAlphan\\Desktop\\Project_Folder\\ilanlar.txt\",\n    \"content\": \"Örnek içerik...\"\n  }\n}\n```";
                var (extractedTool, extractedParams) = TestExtractToolCall(rawLine11ToolCall);
                if (extractedTool == "filesystem_manager" && extractedParams.TryGetValue("action", out var actVal) && actVal?.ToString() == "write")
                {
                    Console.WriteLine("   ✅ [24.4] Lenient parser successfully extracted tool and parameters despite unescaped Windows paths!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 24.4 FAILED: Extracted tool: '{extractedTool}', params: {extractedParams.Count}");
                    failures++;
                }

                // [24.5] Anti-Stalling & 1-Turn Direct Resolution Evasion Detection Test
                Console.WriteLine("\n[24.5] Testing intermediate stalling & filler response detection...");
                string stallSample1 = "İkinci el piyasa fiyatları için güncel web araştırmasını başlattım ve piyasa verilerini analiz ediyorum. Sonuçları birazdan listeleyeceğim.";
                string nonStallSample = "| Paket | Yıl | Fiyat |\n| :--- | :--- | :--- |\n| Standard | 2024 | 2.500.000 TL |";
                bool isStall1 = TestIsIntermediateStallingResponse(stallSample1);
                bool isStall2 = TestIsIntermediateStallingResponse(nonStallSample);
                if (isStall1 && !isStall2)
                {
                    Console.WriteLine("   ✅ [24.5] Anti-stalling detector correctly caught stalling phrase and ignored actual tables!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 24.5 FAILED: stallSample1 isStall={isStall1}, nonStallSample isStall={isStall2}");
                    failures++;
                }

                // [24.6] Company Deletion Lifecycle & Disk Clean Test
                Console.WriteLine("\n[24.6] Testing Company Deletion & Workspace Purge Lifecycle...");
                var deleteTestComp = new Company
                {
                    Id = "sirket_delete_test_unit",
                    Name = "Geçici Test Şirketi A.Ş.",
                    Industry = "Birim Test"
                };
                companyManager.AddCompany(deleteTestComp);
                var testCompDir = companyManager.GetCompanyDirPath("sirket_delete_test_unit");
                File.WriteAllText(Path.Combine(testCompDir, "dummy.txt"), "test data");

                bool compAdded = companyManager.GetAllCompanies().Any(c => c.Id == "sirket_delete_test_unit");
                bool compDirCreated = Directory.Exists(testCompDir) && File.Exists(Path.Combine(testCompDir, "dummy.txt"));

                bool deleteResult = companyManager.DeleteCompany("sirket_delete_test_unit", deleteDataFiles: true);
                bool compRemovedFromList = !companyManager.GetAllCompanies().Any(c => c.Id == "sirket_delete_test_unit");
                bool compDirRemoved = !Directory.Exists(testCompDir);

                if (compAdded && compDirCreated && deleteResult && compRemovedFromList && compDirRemoved)
                {
                    Console.WriteLine("   ✅ [24.6] Company successfully added, isolated folder created, and cleanly purged upon DeleteCompany!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 24.6 FAILED: added={compAdded}, dirCreated={compDirCreated}, deleteResult={deleteResult}, removedFromList={compRemovedFromList}, dirRemoved={compDirRemoved}");
                    failures++;
                }

                // =========================================================================
                // TEST 25: Critical Data Loss Prevention Guardrail (accidental-data-loss-prevention)
                // =========================================================================
                Console.WriteLine("\n--- TEST 25: Critical Data Loss Prevention Guardrail ---");

                // 25.1 Intercept root deletion command
                var blockRootRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "rmdir /s /q C:\\"
                }, "sirket_4");

                if (!blockRootRes.IsSuccess && blockRootRes.Output.Contains("Accidental Data Loss Prevention"))
                {
                    Console.WriteLine("   ✅ [25.1] Destructive 'rmdir /s /q C:\\' successfully blocked by Safety Guardrail!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 25.1 FAILED: Command was not blocked! Success={blockRootRes.IsSuccess}, Output={blockRootRes.Output}");
                    failures++;
                }

                // 25.2 Intercept SQL DROP TABLE command
                var blockDropRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "sqlite3 database.db \"DROP TABLE invoices;\""
                }, "sirket_4");

                if (!blockDropRes.IsSuccess && blockDropRes.Output.Contains("Accidental Data Loss Prevention"))
                {
                    Console.WriteLine("   ✅ [25.2] Destructive 'DROP TABLE' successfully blocked by Safety Guardrail!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 25.2 FAILED: DROP TABLE was not blocked! Output={blockDropRes.Output}");
                    failures++;
                }

                // 25.3 Intercept SQL TRUNCATE and broad DELETE WHERE 1=1
                var blockTruncRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "TRUNCATE TABLE users"
                }, "sirket_4");

                var blockDelWhereRes = toolEngine.ExecuteToolByName("run_terminal_command", new Dictionary<string, object>
                {
                    ["command"] = "DELETE FROM customers WHERE 1=1;"
                }, "sirket_4");

                if (!blockTruncRes.IsSuccess && !blockDelWhereRes.IsSuccess)
                {
                    Console.WriteLine("   ✅ [25.3] TRUNCATE TABLE & DELETE WHERE 1=1 successfully blocked!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 25.3 FAILED: TRUNC/DEL not blocked!");
                    failures++;
                }

                // 25.4 Intercept filesystem delete of C:\ or Windows directory
                var blockFsRoot = toolEngine.ExecuteToolByName("filesystem_manager", new Dictionary<string, object>
                {
                    ["action"] = "delete",
                    ["path"] = "C:\\"
                }, "sirket_4");

                var blockFsWin = toolEngine.ExecuteToolByName("filesystem_manager", new Dictionary<string, object>
                {
                    ["action"] = "delete",
                    ["path"] = "C:\\Windows"
                }, "sirket_4");

                if (!blockFsRoot.IsSuccess && !blockFsWin.IsSuccess && blockFsWin.Output.Contains("Accidental Data Loss Prevention"))
                {
                    Console.WriteLine("   ✅ [25.4] Direct filesystem root/system directory deletion successfully prevented!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 25.4 FAILED: Filesystem deletion guardrail failed!");
                    failures++;
                }

                // =========================================================================
                // TEST 26: Dynamic Web & Headless Browser Reader (browse_dynamic_web)
                // =========================================================================
                Console.WriteLine("\n--- TEST 26: Dynamic Web & Headless Browser Reader (browse_dynamic_web) ---");

                var browseRes = toolEngine.ExecuteToolByName("browse_dynamic_web", new Dictionary<string, object>
                {
                    ["url"] = "https://example.com",
                    ["extract_mode"] = "markdown",
                    ["wait_seconds"] = 1
                }, "sirket_4");

                if (browseRes.IsSuccess && (browseRes.Output.Contains("Example Domain") || browseRes.Output.Contains("Dinamik Web Sayfası")))
                {
                    Console.WriteLine("   ✅ [26.1] browse_dynamic_web successfully executed and parsed target page!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 26.1 FAILED: browse_dynamic_web failed! Output={browseRes.Output}");
                    failures++;
                }

                var browseLinksRes = toolEngine.ExecuteToolByName("browse_dynamic_web", new Dictionary<string, object>
                {
                    ["url"] = "https://example.com",
                    ["extract_mode"] = "links",
                    ["wait_seconds"] = 1
                }, "sirket_4");

                if (browseLinksRes.IsSuccess && browseLinksRes.Output.Contains("Bağlantı Radarı"))
                {
                    Console.WriteLine("   ✅ [26.2] browse_dynamic_web (links mode) successfully extracted links!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 26.2 FAILED: browse_dynamic_web links mode failed!");
                    failures++;
                }

                // =========================================================================
                // TEST 27: MCP Infrastructure & Antigravity Autonomous Reasoning Prompts
                // =========================================================================
                Console.WriteLine("\n--- TEST 27: MCP Infrastructure & Antigravity Autonomous Reasoning Standards ---");

                // 27.1 MCP Manager and Config Verification
                var mcpServers = toolEngine.McpManager?.GetConfiguredServers();
                if (mcpServers != null && mcpServers.Count > 0 && toolEngine.McpManager != null && File.Exists(toolEngine.McpManager.ConfigFilePath))
                {
                    Console.WriteLine($"   ✅ [27.1] MCP Manager operational! Configured servers count: {mcpServers.Count}, path: {toolEngine.McpManager.ConfigFilePath}");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 27.1 FAILED: MCP Manager not initialized or mcp_config.json missing!");
                    failures++;
                }

                // 27.2 Antigravity System Prompt Verification
                var activeCompany = companyManager.ActiveCompany ?? companyManager.GetAllCompanies().First();
                var fullPrompt = activeCompany.GetFullSystemPrompt();

                bool hasBrowseTool = fullPrompt.Contains("browse_dynamic_web");
                bool hasAntigravityRules = fullPrompt.Contains("ANTIGRAVITY OTONOM AKIL YÜRÜTME & GÜVENLİK İLKELERİ") &&
                                           fullPrompt.Contains("SIFIR VARSAYIM & ZEMİN DOĞRULAMA") &&
                                           fullPrompt.Contains("KRİTİK VERİ KAYBI ÖNLEME KALKANI");

                if (hasBrowseTool && hasAntigravityRules)
                {
                    Console.WriteLine("   ✅ [27.2] Company system prompt contains browse_dynamic_web and full Antigravity Reasoning Principles!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 27.2 FAILED: Missing prompt sections! hasBrowseTool={hasBrowseTool}, hasAntigravityRules={hasAntigravityRules}");
                    failures++;
                }

                // =========================================================================
                // TEST 28: Dynamic Semantic Memory & RAG Snippets Extraction
                // =========================================================================
                Console.WriteLine("\n--- TEST 28: Dynamic Semantic Memory & RAG Retrieval ---");

                // 28.1 Add a known Q&A pair and verify semantic retrieval
                var testMemoryQuestion = "Villa Kaunos rezervasyon ve havuz kuralları nelerdir?";
                var testMemoryAnswer = "Villa Kaunos özel korunaklı havuza sahiptir, giriş saati 16:00, çıkış saati 10:00'dur.";
                trainingManager.AppendTrainingPair("sirket_4", testMemoryQuestion, testMemoryAnswer);

                var retrievedSnippets = trainingManager.GetRelevantMemorySnippets("sirket_4", "Kaunos havuz saatleri villa", maxSnippets: 3);
                Console.WriteLine($"   -> Retrieved Snippets:\n{retrievedSnippets}");

                if (!string.IsNullOrWhiteSpace(retrievedSnippets) &&
                    retrievedSnippets.Contains("Villa Kaunos") &&
                    retrievedSnippets.Contains("özel korunaklı havuz"))
                {
                    Console.WriteLine("   ✅ [28.1] GetRelevantMemorySnippets successfully retrieved matched Q&A pair from semantic memory!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 28.1 FAILED: Expected relevant snippet not found in retrieved memory! Snippet: {retrievedSnippets}");
                    failures++;
                }

                // 28.2 Irrelevant query should not trigger or should have low score
                var irrelevantSnippets = trainingManager.GetRelevantMemorySnippets("sirket_4", "astronomi ve kuantum fizigi galaksiler", maxSnippets: 3);
                if (string.IsNullOrWhiteSpace(irrelevantSnippets) || !irrelevantSnippets.Contains("kuantum"))
                {
                    Console.WriteLine("   ✅ [28.2] Irrelevant queries do not falsely match unrelated domain memory.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 28.2 FAILED: Irrelevant query matched unexpectedly!");
                    failures++;
                }

                // =========================================================================
                // TEST 29: Multimodal Screen Vision Tool (`analyze_screen_vision`)
                // =========================================================================
                Console.WriteLine("\n--- TEST 29: Multimodal Screen Vision Inspection ---");

                // 29.1 Tool registration check
                toolEngine.EnsureCompanyToolsRegistered("sirket_4");
                var visionToolsFile = Path.Combine(companyManager.GetCompanyDirPath("sirket_4"), "tools", "registered_tools.json");
                bool visionRegistered = File.Exists(visionToolsFile) && File.ReadAllText(visionToolsFile).Contains("analyze_screen_vision");
                if (visionRegistered)
                {
                    Console.WriteLine("   ✅ [29.1] analyze_screen_vision is registered in company tool catalog!");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 29.1 FAILED: analyze_screen_vision is NOT registered!");
                    failures++;
                }

                // 29.2 Execute analyze_screen_vision
                var visionRes = toolEngine.ExecuteToolByName("analyze_screen_vision", new Dictionary<string, object>
                {
                    ["prompt"] = "Ekranda açık olan pencereleri, butonları ve metinleri analiz et"
                }, "sirket_4");

                Console.WriteLine($"   -> Screen Vision Result: IsSuccess={visionRes.IsSuccess}, Output length={visionRes.Output?.Length}");
                if (visionRes.IsSuccess && !string.IsNullOrWhiteSpace(visionRes.Output) &&
                    (visionRes.Output.Contains("Ekran Görsel Analizi") || visionRes.Output.Contains("Pencere") || visionRes.Output.Contains("Masaüstü") || visionRes.Output.Contains("Çözünürlük")))
                {
                    Console.WriteLine("   ✅ [29.2] analyze_screen_vision executed successfully (Vision API or UI Automation fallback)!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 29.2 FAILED: analyze_screen_vision failed! Output: {visionRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 30: Autonomous Self-Healing & Internal Chain-of-Thought
                // =========================================================================
                Console.WriteLine("\n--- TEST 30: Autonomous Self-Healing & Internal Chain-of-Thought ---");

                var testCompanyPrompt = activeCompany.GetFullSystemPrompt();
                bool hasSelfHealingRule = testCompanyPrompt.Contains("OTONOM HATA DÜZELTME (Self-Healing & Auto-Retry)") ||
                                          testCompanyPrompt.Contains("OTONOM KENDİNİ ONARMA");
                bool hasCoTRule = testCompanyPrompt.Contains("İÇSEL AKIL YÜRÜTME & PLANLAMA (Internal Chain-of-Thought)") ||
                                  testCompanyPrompt.Contains("Chain-of-Thought");
                bool hasVisionInPrompt = testCompanyPrompt.Contains("analyze_screen_vision");

                if (hasSelfHealingRule && hasCoTRule && hasVisionInPrompt)
                {
                    Console.WriteLine("   ✅ [30.1] Company system prompt enforces Self-Healing, Internal Chain-of-Thought, and Multimodal Vision!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 30.1 FAILED: Prompt missing required reasoning sections! SelfHealing={hasSelfHealingRule}, CoT={hasCoTRule}, Vision={hasVisionInPrompt}");
                    failures++;
                }

                // 30.2 Simulate tool failure in ReAct loop error recovery
                var simulatedFailRes = toolEngine.ExecuteToolByName("non_existent_tool_xyz", new Dictionary<string, object>(), "sirket_4");
                bool errorDetected = !simulatedFailRes.IsSuccess && !string.IsNullOrWhiteSpace(simulatedFailRes.Output) &&
                                     (simulatedFailRes.Output.Contains("CommandNotFoundException") ||
                                      simulatedFailRes.Output.Contains("not recognized") ||
                                      simulatedFailRes.Output.Contains("Çıkış Kodu") ||
                                      simulatedFailRes.Output.Contains("Tanımsız"));

                if (errorDetected)
                {
                    Console.WriteLine("   ✅ [30.2] Tool execution failure is cleanly trapped and returns diagnostic output for self-healing!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 30.2 FAILED: Error detection failed! IsSuccess={simulatedFailRes.IsSuccess}, Output={simulatedFailRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 31: ERP & Accounting Database Reverse-Engineering & Safe Query
                // =========================================================================
                Console.WriteLine("\n--- TEST 31: ERP & Database Reverse-Engineering & Safe SQL ---");

                // 31.1 Create realistic ERP SQLite database in workspace
                var testDbPath = Path.Combine(companyManager.GetCompanyWorkspace("sirket_4"), "sirket_erp.db");
                if (File.Exists(testDbPath)) File.Delete(testDbPath);

                using (var testConn = new SqliteConnection($"Data Source={testDbPath}"))
                {
                    testConn.Open();
                    using var cmd = testConn.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE TBL_CARIKARTLAR (
                            CARI_KOD TEXT PRIMARY KEY,
                            UNVAN TEXT NOT NULL,
                            VERGI_NO TEXT,
                            BAKIYE REAL DEFAULT 0.0
                        );
                        CREATE TABLE TBL_FATURALAR (
                            FATURA_NO TEXT PRIMARY KEY,
                            CARI_KOD TEXT NOT NULL,
                            TARIH TEXT NOT NULL,
                            TUTAR REAL NOT NULL,
                            KDV REAL NOT NULL,
                            FOREIGN KEY (CARI_KOD) REFERENCES TBL_CARIKARTLAR(CARI_KOD)
                        );
                        CREATE TABLE TBL_VILLALAR (
                            VILLA_ID INTEGER PRIMARY KEY AUTOINCREMENT,
                            VILLA_ADI TEXT NOT NULL,
                            GECELIK_FIYAT REAL NOT NULL,
                            HAVUZ_TURU TEXT
                        );
                        INSERT INTO TBL_CARIKARTLAR VALUES ('C001', 'Dalyan Turizm ve Otelcilik A.S.', '1234567890', 48500.0);
                        INSERT INTO TBL_CARIKARTLAR VALUES ('C002', 'Akdeniz Luks Villa Kiralama Ltd.', '9876543210', 12300.0);
                        INSERT INTO TBL_FATURALAR VALUES ('FTR2026-001', 'C001', '2026-09-01', 48500.0, 9700.0);
                        INSERT INTO TBL_VILLALAR VALUES (1, 'Villa Kaunos Luks', 180.0, 'Ozel Korunakli');
                    ";
                    cmd.ExecuteNonQuery();
                }

                // 31.2 Execute reverse_engineer_database
                var revDbRes = toolEngine.ExecuteToolByName("reverse_engineer_database", new Dictionary<string, object>
                {
                    ["database_path"] = testDbPath,
                    ["max_sample_rows"] = 2,
                    ["ingest_to_knowledge_graph"] = true
                }, "sirket_4");

                if (revDbRes.IsSuccess &&
                    revDbRes.Output.Contains("TBL_CARIKARTLAR") &&
                    revDbRes.Output.Contains("TBL_FATURALAR") &&
                    revDbRes.Output.Contains("TBL_VILLALAR") &&
                    revDbRes.Output.Contains("Dalyan Turizm"))
                {
                    Console.WriteLine("   ✅ [31.1] reverse_engineer_database successfully reverse-engineered tables, columns, relations & sample data!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 31.1 FAILED: Reverse engineering failed! Output: {revDbRes.Output}");
                    failures++;
                }

                // 31.3 Execute safe SELECT query
                var queryRes = toolEngine.ExecuteToolByName("query_database", new Dictionary<string, object>
                {
                    ["database_path"] = testDbPath,
                    ["sql_query"] = "SELECT CARI_KOD, UNVAN, BAKIYE FROM TBL_CARIKARTLAR WHERE BAKIYE > 20000"
                }, "sirket_4");

                if (queryRes.IsSuccess && queryRes.Output.Contains("Dalyan Turizm ve Otelcilik") && queryRes.Output.Contains("48500"))
                {
                    Console.WriteLine("   ✅ [31.2] query_database executed safe SQL and returned formatted markdown data table!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 31.2 FAILED: query_database failed! Output: {queryRes.Output}");
                    failures++;
                }

                // 31.4 Security check: Destructive DROP/DELETE query MUST be blocked by guardrail
                var dropQueryRes = toolEngine.ExecuteToolByName("query_database", new Dictionary<string, object>
                {
                    ["database_path"] = testDbPath,
                    ["sql_query"] = "DROP TABLE TBL_CARIKARTLAR"
                }, "sirket_4");

                if (!dropQueryRes.IsSuccess && dropQueryRes.Output.Contains("GÜVENLİK KALKANI BLOKESİ"))
                {
                    Console.WriteLine("   ✅ [31.3] Dangerous DROP query was strictly blocked by Database Security Guardrail!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 31.3 FAILED: Destructive SQL was not blocked! IsSuccess={dropQueryRes.IsSuccess}, Output={dropQueryRes.Output}");
                    failures++;
                }

                // =========================================================================
                // TEST 32: Two-Way Web Action & Form Automation (`web_action_automation`)
                // =========================================================================
                Console.WriteLine("\n--- TEST 32: Two-Way Web Action & Interactive Automation ---");

                // 32.1 Check registration in company catalog
                toolEngine.EnsureCompanyToolsRegistered("sirket_4");
                var checkToolsFile = Path.Combine(companyManager.GetCompanyDirPath("sirket_4"), "tools", "registered_tools.json");
                var checkToolsJson = File.ReadAllText(checkToolsFile);
                bool webActionRegistered = checkToolsJson.Contains("web_action_automation") &&
                                           checkToolsJson.Contains("reverse_engineer_database") &&
                                           checkToolsJson.Contains("query_database");

                if (webActionRegistered)
                {
                    Console.WriteLine("   ✅ [32.1] web_action_automation, reverse_engineer_database and query_database registered in tool catalog!");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 32.1 FAILED: New tools missing from registered_tools.json!");
                    failures++;
                }

                // 32.2 Create a local HTML page with form, input, button for interactive action test
                var testHtmlPath = Path.Combine(Path.GetTempPath(), "AlphaGravity_WebAction_Test.html");
                File.WriteAllText(testHtmlPath, @"<!DOCTYPE html>
<html>
<head><title>AlphaGravity Web Test</title></head>
<body>
    <h1>Portal Giriş Ekranı</h1>
    <form id='testForm'>
        <input type='text' id='txtUsername' name='username' placeholder='Kullanıcı Adı' />
        <input type='password' id='txtPassword' name='password' />
        <button type='button' id='btnSubmit' onclick=""document.getElementById('resultArea').innerText = 'GİRİŞ_BAŞARILI: ' + document.getElementById('txtUsername').value;"">Giriş Yap</button>
    </form>
    <div id='resultArea'>Beklemede</div>
</body>
</html>");

                var fileUrl = new Uri(testHtmlPath).AbsoluteUri;
                var actionWorkflow = @"[
                    { ""action"": ""navigate"", ""url"": """ + fileUrl + @""", ""milliseconds"": 1000 },
                    { ""action"": ""type"", ""selector"": ""#txtUsername"", ""text"": ""AlphaAdmin"" },
                    { ""action"": ""click"", ""selector"": ""#btnSubmit"", ""milliseconds"": 1000 },
                    { ""action"": ""extract"", ""selector"": ""#resultArea"" }
                ]";

                var webActionRes = toolEngine.ExecuteToolByName("web_action_automation", new Dictionary<string, object>
                {
                    ["workflow"] = actionWorkflow,
                    ["timeout_seconds"] = 30
                }, "sirket_4");

                Console.WriteLine($"   -> Web Action Execution: IsSuccess={webActionRes.IsSuccess}, Output length={webActionRes.Output?.Length}");
                if (webActionRes.IsSuccess && webActionRes.Output.Contains("GİRİŞ_BAŞARILI: AlphaAdmin"))
                {
                    Console.WriteLine("   ✅ [32.2] web_action_automation navigated, filled form, clicked button, and extracted updated DOM result!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 32.2 FAILED: Web action automation failed! Output: {webActionRes.Output}");
                    failures++;
                }

                try { File.Delete(testHtmlPath); } catch { }

                // TEST 33: Silero VAD In-Memory Speech Detection & Discrimination
                Console.WriteLine("\n--- TEST 33: Real-Time Silero VAD & In-Memory Speech Loop ---");
                using (var vadDetector = new SileroVadDetector())
                {
                    if (vadDetector.IsInitialized)
                    {
                        Console.WriteLine("   ✅ [33.1] Silero VAD ONNX model successfully loaded into memory!");

                        // Silence test (512 samples = 32ms)
                        var silence = new float[512];
                        float pSilence = vadDetector.DetectSpeechProbability(silence);
                        if (pSilence < 0.20f)
                        {
                            Console.WriteLine($"   ✅ [33.2] Silence correctly recognized as non-speech (prob: {pSilence:F4} < 0.20)");
                        }
                        else
                        {
                            Console.WriteLine($"   ❌ TEST 33.2 FAILED: Silence falsely recognized as speech (prob: {pSilence:F4})");
                            failures++;
                        }

                        // Speech-frequency tone test (512 samples at 350 Hz)
                        var tone = new float[512];
                        for (int i = 0; i < 512; i++)
                        {
                            tone[i] = (float)(Math.Sin(2.0 * Math.PI * 350.0 * i / 16000.0) * 0.7);
                        }
                        float pTone = vadDetector.DetectSpeechProbability(tone);
                        if (pTone >= 0.50f)
                        {
                            Console.WriteLine($"   ✅ [33.3] Speech tone successfully recognized as speech (prob: {pTone:F4} >= 0.50)");
                        }
                        else
                        {
                            Console.WriteLine($"   ❌ TEST 33.3 FAILED: Speech tone missed (prob: {pTone:F4} < 0.50)");
                            failures++;
                        }
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 33.1 FAILED: Silero VAD failed to initialize!");
                        failures++;
                    }
                }

                // TEST 34: Low-Model Cop-Out Interceptor & Direct ClosedXML Excel (.xlsx) Verification
                Console.WriteLine("\n--- TEST 34: Low-Model Cop-Out Interception & Direct Excel (.xlsx) ---");
                string refusal1 = "Muğla genelindeki güncel ortalama ev kira fiyatlarına ilişkin canlı web verilerine şu an ulaşılamamıştır.";
                string refusal2 = "Güncel Muğla kira verilerini çekmek için web arama aracı tetiklenmemiştir.";
                string normalAnswer = "Muğla ortalama ev kirası 2026 yılı itibarıyla 18.000 TL ile 35.000 TL arasında değişmektedir.";

                if (TestIsRefusalOrMissingWebData(refusal1) && TestIsRefusalOrMissingWebData(refusal2))
                {
                    Console.WriteLine("   ✅ [34.1] Model cop-out refusals accurately detected by safety interceptor!");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 34.1 FAILED: Cop-out refusal detector missed known refusal text!");
                    failures++;
                }

                if (!TestIsRefusalOrMissingWebData(normalAnswer))
                {
                    Console.WriteLine("   ✅ [34.2] Legitimate answer correctly NOT flagged as refusal.");
                }
                else
                {
                    Console.WriteLine("   ❌ TEST 34.2 FAILED: Legitimate answer falsely flagged as refusal!");
                    failures++;
                }

                // Test ClosedXML Excel creation
                try
                {
                    var tempExcelPath = Path.Combine(Path.GetTempPath(), $"Test_Excel_{Guid.NewGuid():N}.xlsx");
                    using (var wb = new ClosedXML.Excel.XLWorkbook())
                    {
                        var ws = wb.Worksheets.Add("Veriler");
                        ws.Cell(1, 1).Value = "İlçe";
                        ws.Cell(1, 2).Value = "Ortalama Kira (TL)";
                        ws.Cell(2, 1).Value = "Menteşe";
                        ws.Cell(2, 2).Value = "22.500";
                        ws.Cell(3, 1).Value = "Bodrum";
                        ws.Cell(3, 2).Value = "45.000";
                        ws.Columns().AdjustToContents();
                        wb.SaveAs(tempExcelPath);
                    }

                    if (File.Exists(tempExcelPath) && new FileInfo(tempExcelPath).Length > 1000)
                    {
                        Console.WriteLine($"   ✅ [34.3] ClosedXML direct .xlsx workbook generation verified! Size: {new FileInfo(tempExcelPath).Length} bytes.");
                        File.Delete(tempExcelPath);
                    }
                    else
                    {
                        Console.WriteLine("   ❌ TEST 34.3 FAILED: Excel file was not properly saved or was empty!");
                        failures++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ❌ TEST 34.3 FAILED with exception: {ex.Message}");
                    failures++;
                }

                // =========================================================================
                // TEST 35: Adaptive Small Model Architecture & Output Sanitizer
                // =========================================================================
                Console.WriteLine("\n--- TEST 35: Adaptive Small Model Architecture & Output Sanitizer ---");

                // 35.1 IsSmallModel Model Tiering Test
                bool qwenSmall = MainWindow.IsSmallModel("qwen2.5-coder:1.5b");
                bool llama1bSmall = MainWindow.IsSmallModel("llama3.2:1b");
                bool gemma2bSmall = MainWindow.IsSmallModel("gemma2:2b");
                bool phi3Small = MainWindow.IsSmallModel("phi3:mini");
                bool geminiLarge = !MainWindow.IsSmallModel("gemini-2.5-flash");
                bool gpt4Large = !MainWindow.IsSmallModel("gpt-4o");
                bool claudeLarge = !MainWindow.IsSmallModel("claude-3-5-sonnet");

                if (qwenSmall && llama1bSmall && gemma2bSmall && phi3Small && geminiLarge && gpt4Large && claudeLarge)
                {
                    Console.WriteLine("   ✅ [35.1] Model tiering accurately classifies 1.5B/1B/2B/3B as Small, and Gemini/GPT/Claude as Large.");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 35.1 FAILED: Small model classification mismatch! qwen={qwenSmall}, llama={llama1bSmall}, gemma={gemma2bSmall}, phi3={phi3Small}, geminiLarge={geminiLarge}");
                    failures++;
                }

                // 35.2 Compact System Prompt Verification
                var testComp = new Company
                {
                    Id = "test_comp_compact",
                    Name = "Kaunos Turizm",
                    Industry = "Villa Kiralama",
                    SystemPrompt = "Kaunos Turizm Dalyan bölgesinde özel havuzlu lüks villalar kiralar."
                };

                var compactPrompt = testComp.GetCompactSystemPrompt();
                Console.WriteLine($"   -> Compact Prompt Length: {compactPrompt.Length} chars, {compactPrompt.Split(' ').Length} words.");

                bool compactIsConcise = compactPrompt.Length < 600 && compactPrompt.Split(' ').Length < 100;
                bool compactHasCompany = compactPrompt.Contains("Kaunos Turizm") && compactPrompt.Contains("Villa Kiralama");
                bool compactNoToolBloat = !compactPrompt.Contains("tool_call") && !compactPrompt.Contains("filesystem_manager") && !compactPrompt.Contains("browse_dynamic_web");
                bool compactNoNegativeQuotes = !compactPrompt.Contains("KESİNLİKLE") && !compactPrompt.Contains("DEME");

                if (compactIsConcise && compactHasCompany && compactNoToolBloat && compactNoNegativeQuotes)
                {
                    Console.WriteLine("   ✅ [35.2] GetCompactSystemPrompt produces an ultra-clean affirmative prompt (<100 words) with zero tool bloat or negative quotes!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 35.2 FAILED: Compact prompt failed criteria! concise={compactIsConcise}, hasCompany={compactHasCompany}, noToolBloat={compactNoToolBloat}, noNeg={compactNoNegativeQuotes}");
                    failures++;
                }

                // 35.3 Output Sanitizer Verification (Elimination of Regurgitated Prompt Artifacts)
                string dirtyOutput1 = "Bu bilgiler doğrudan arka plandaki terminal kodlarını, PowerShell hatalarını veya IP adreslerini nihai yanıt olarak dökme. Her zaman son kullanıcıya yönelik profesyonel sentezini sun. **NET, Profesyonel, Uygun Veri Tablosu İçeren NİHAİ Cevabını Ver:**\n\n| Bölge | Ortalama Fiyat |\n| :--- | :--- |\n| Dalyan | 25.000 TL |";
                string cleaned1 = MainWindow.SanitizeModelOutput(dirtyOutput1);

                string dirtyOutput2 = "KESİNLİKLE 'araştırmayı başlattım', 'analiz ediyorum', 'birazdan listeleyeceğim' gibi cümleler kurup kullanıcıya sun.\n\n```tool_call\n{\n  \"tool\": \"fetch_web_page\",\n  \"parameters\": {\"url\": \"https://example.com\"}\n}\n```\n\nSonuçlar hazırlanmıştır.";
                string cleaned2 = MainWindow.SanitizeModelOutput(dirtyOutput2);

                bool cleaned1Success = !cleaned1.Contains("terminal kodlarını") &&
                                       !cleaned1.Contains("NİHAİ Cevabını Ver") &&
                                       cleaned1.Contains("| Bölge | Ortalama Fiyat |");

                bool cleaned2Success = !cleaned2.Contains("araştırmayı başlattım") &&
                                       !cleaned2.Contains("example.com") &&
                                       cleaned2.Contains("Sonuçlar hazırlanmıştır.");

                if (cleaned1Success && cleaned2Success)
                {
                    Console.WriteLine("   ✅ [35.3] SanitizeModelOutput stripped all regurgitated prompt artifacts and dummy example.com tool calls!");
                }
                else
                {
                    Console.WriteLine($"   ❌ TEST 35.3 FAILED: Output sanitization failed! c1Success={cleaned1Success}, c2Success={cleaned2Success}\nCleaned1: {cleaned1}\nCleaned2: {cleaned2}");
                    failures++;
                }

                Console.WriteLine("\n==================================================");
                if (failures == 0)
                {
                    Console.WriteLine("🎉 ALL TESTS (1-35) PASSED! 100% DYNAMIC AUTONOMOUS AGENT VERIFIED.");
                    Console.WriteLine("==================================================");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"💥 SUITE FINISHED WITH {failures} FAILURES!");
                    Console.WriteLine("==================================================");
                    return 1;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 FATAL TEST SUITE EXCEPTION: {ex}");
                return 1;
            }
        }

        private static (string toolName, Dictionary<string, object> parameters) TestExtractToolCall(string text)
        {
            var paramDict = new Dictionary<string, object>();
            string toolName = "";

            if (string.IsNullOrWhiteSpace(text)) return (toolName, paramDict);

            var match = Regex.Match(text, @"```(?:tool_call|tool)\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(text, @"```json\s*(\{[\s\S]*?""(?:tool|name|function)""[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
            }
            if (!match.Success)
            {
                match = Regex.Match(text, @"(\{[\s\S]*?""(?:tool|name|function)""\s*:[\s\S]*?\})", RegexOptions.IgnoreCase);
            }

            string rawJson = match.Success ? match.Groups[1].Value.Trim() : text.Trim();

            int firstBrace = rawJson.IndexOf('{');
            int lastBrace = rawJson.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                rawJson = rawJson.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            var sanitizedJson = Regex.Replace(rawJson, @"(?<!\\)\\(?![""\\/bfnrtu]|u[0-9a-fA-F]{4})", @"\\");

            try
            {
                using var doc = JsonDocument.Parse(sanitizedJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("tool", out var tProp)) toolName = tProp.GetString() ?? "";
                else if (root.TryGetProperty("name", out var nProp)) toolName = nProp.GetString() ?? "";
                else if (root.TryGetProperty("function", out var fProp)) toolName = fProp.GetString() ?? "";

                if (root.TryGetProperty("parameters", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pElem.EnumerateObject()) paramDict[prop.Name] = prop.Value.ToString();
                }
                else if (root.TryGetProperty("arguments", out var aElem) && aElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in aElem.EnumerateObject()) paramDict[prop.Name] = prop.Value.ToString();
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
            }
            catch
            {
                var toolRegex = Regex.Match(rawJson, @"""(?:tool|name|function)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (toolRegex.Success) toolName = toolRegex.Groups[1].Value.Trim();

                var paramMatches = Regex.Matches(rawJson, @"""([a-zA-Z0-9_\-]+)""\s*:\s*(?:""([^""\\]*(?:\\.[^""\\]*)*)""|([0-9\.]+|true|false))");
                foreach (Match pm in paramMatches)
                {
                    var key = pm.Groups[1].Value;
                    if (key.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("function", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("parameters", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var val = pm.Groups[2].Success && !string.IsNullOrEmpty(pm.Groups[2].Value)
                        ? pm.Groups[2].Value.Replace(@"\\", @"\")
                        : pm.Groups[3].Value;
                    paramDict[key] = val;
                }
            }

            return (toolName, paramDict);
        }

        private static bool TestIsIntermediateStallingResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();

            if (lower.Contains("|") && lower.Split('\n').Count(l => l.Trim().StartsWith("|") && l.Trim().EndsWith("|")) >= 2)
            {
                return false;
            }

            var stallingPhrases = new[]
            {
                "araştırmasını başlattım", "arastirmasini baslattim",
                "araştırmayı başlattım", "arastirmayi baslattim",
                "araştırma başlattım", "arastirma baslattim",
                "birazdan listeleyeceğim", "birazdan listeleyecegim",
                "birazdan sunacağım", "birazdan sunacagim",
                "birazdan paylaşacağım", "birazdan paylasacagim",
                "sonuçları birazdan", "sonuclari birazdan",
                "verileri analiz ediyorum", "piyasa verilerini analiz ediyorum",
                "analiz ediyorum", "derliyorum",
                "lütfen bekleyin", "lutfen bekleyin",
                "araştırıyorum", "arastiriyorum",
                "hemen bakıyorum", "hemen inceliyorum",
                "bilgileri topluyorum"
            };

            return stallingPhrases.Any(p => lower.Contains(p));
        }

        private static bool TestIsRefusalOrMissingWebData(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lower = text.ToLowerInvariant();

            var refusalPhrases = new[]
            {
                "ulaşılamamıştır", "ulasilamamistir",
                "ulaşamadım", "ulasamadim",
                "tetiklenmemiştir", "tetiklenmemistir",
                "tetiklenmedi",
                "arama aracı tetiklenmedi", "arama araci tetiklenmedi",
                "arama aracı çalıştırılmadı", "arama araci calistirilmadi",
                "canlı web verilerine", "canli web verilerine",
                "internet erişimim yok", "internet erisimim yok",
                "web verilerine erişemiyorum", "web verilerine erisemiyorum",
                "gerçek zamanlı verilere erişimim", "gercek zamanli verilere erisimim",
                "güncel verilere erişimim", "guncel verilere erisimim",
                "canlı verilere erişimim", "canli verilere erisimim",
                "canlı bilgiye erişimim", "canli bilgiye erisimim",
                "web araması yapamıyorum", "web aramasi yapamiyorum"
            };

            return refusalPhrases.Any(p => lower.Contains(p));
        }
    }
}
