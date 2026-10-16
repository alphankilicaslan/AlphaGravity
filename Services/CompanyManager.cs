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
    public class CompanyManager
    {
        private readonly string _baseDataDir;
        private readonly string _companiesConfigFile;
        private List<Company> _companies = new();
        private Company? _activeCompany;

        public event Action<Company?>? ActiveCompanyChanged;

        public CompanyManager(string? baseDataDir = null)
        {
            if (!string.IsNullOrWhiteSpace(baseDataDir))
            {
                _baseDataDir = baseDataDir;
            }
            else
            {
                var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                if (!File.Exists(Path.Combine(candidate, "companies.json")))
                {
                    var curr = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory);
                    for (int i = 0; i < 4 && curr != null; i++)
                    {
                        var check = Path.Combine(curr.FullName, "Data");
                        if (File.Exists(Path.Combine(check, "companies.json")))
                        {
                            candidate = check;
                            break;
                        }
                        curr = curr.Parent;
                    }
                }
                _baseDataDir = candidate;
            }

            Directory.CreateDirectory(_baseDataDir);
            _companiesConfigFile = Path.Combine(_baseDataDir, "companies.json");

            LoadCompanies();
            EnsureDefaultCompanies();
        }

        public IReadOnlyList<Company> GetAllCompanies() => _companies.AsReadOnly();

        public Company? ActiveCompany
        {
            get => _activeCompany;
            set
            {
                if (_activeCompany != value)
                {
                    _activeCompany = value;
                    ActiveCompanyChanged?.Invoke(_activeCompany);
                }
            }
        }

        private void LoadCompanies()
        {
            if (File.Exists(_companiesConfigFile))
            {
                try
                {
                    var json = File.ReadAllText(_companiesConfigFile, System.Text.Encoding.UTF8);
                    _companies = JsonSerializer.Deserialize<List<Company>>(json) ?? new();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading companies: {ex.Message}");
                }
            }
        }

        public void SaveCompanies()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                };
                var json = JsonSerializer.Serialize(_companies, options);
                File.WriteAllText(_companiesConfigFile, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving companies: {ex.Message}");
            }
        }

        private void EnsureDefaultCompanies()
        {
            if (_companies.Count == 0)
            {
                // 1. Örnek Şirket A: Yazılım & Teknoloji A.Ş.
                var compA = new Company
                {
                    Id = "tech_corp",
                    Name = "Atlas Yazılım & Savunma A.Ş.",
                    Industry = "Yazılım / Siber Güvenlik",
                    Description = "Kurumsal ERP, mikroservis mimarileri ve C#/React projeleri geliştiren teknoloji şirketi.",
                    SelectedModel = "qwen2.5-coder:1.5b",
                    SystemPrompt = "Biz .NET 9, C#, React, TypeScript ve PostgreSQL teknolojilerini kullanıyoruz. Kodları Clean Architecture standartlarında, modüler ve açıklamalı olarak üret. Her zaman akıcı ve doğru Türkçe ile yanıt ver."
                };
                AddCompany(compA);

                // 2. Örnek Şirket B: Turizm & Gayrimenkul Ltd.
                var compB = new Company
                {
                    Id = "dalyan_realty",
                    Name = "Dalyan Villa & Gayrimenkul Ltd.",
                    Industry = "Turizm / Emlak",
                    Description = "Dalyan ve Muğla bölgesinde villa kiralama, portföy yönetimi ve müşteri ilişkileri.",
                    SelectedModel = "qwen2.5-coder:1.5b",
                    SystemPrompt = "Biz villa kiralama ve gayrimenkul şirketiyiz. Müşteri teklifleri, sözleşmeler, villa tanıtım metinleri ve modern web sayfaları hazırla. Her zaman akıcı ve doğru Türkçe kullan."
                };
                AddCompany(compB);
            }

            ActiveCompany = _companies.FirstOrDefault();
        }

        public void AddCompany(Company company)
        {
            // Şirkete özel izole dosya alanlarını oluştur
            company.IsolatedDataPath = Path.Combine(_baseDataDir, "Companies", company.Id);
            Directory.CreateDirectory(Path.Combine(company.IsolatedDataPath, "workspace"));
            Directory.CreateDirectory(Path.Combine(company.IsolatedDataPath, "dataset"));
            Directory.CreateDirectory(Path.Combine(company.IsolatedDataPath, "chat_history"));

            _companies.RemoveAll(c => c.Id == company.Id);
            _companies.Add(company);
            SaveCompanies();
        }

        public bool DeleteCompany(string companyId, bool deleteDataFiles = true)
        {
            var company = _companies.FirstOrDefault(c => c.Id == companyId);
            if (company == null) return false;

            _companies.Remove(company);
            SaveCompanies();

            if (deleteDataFiles)
            {
                try
                {
                    var companyDir = Path.Combine(_baseDataDir, "Companies", companyId);
                    if (Directory.Exists(companyDir))
                    {
                        Directory.Delete(companyDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deleting company directory '{companyId}': {ex.Message}");
                }
            }

            if (_activeCompany?.Id == companyId)
            {
                ActiveCompany = _companies.FirstOrDefault();
            }

            return true;
        }

        public string GetCompanyDirPath(string companyId)
        {
            var path = Path.Combine(_baseDataDir, "Companies", companyId);
            Directory.CreateDirectory(path);
            return path;
        }

        public string GetCompanyDocumentsPath(string companyId)
        {
            var path = Path.Combine(_baseDataDir, "Companies", companyId, "documents");
            Directory.CreateDirectory(path);
            return path;
        }

        public string GetCompanyWorkspace(string companyId)
        {
            var path = Path.Combine(_baseDataDir, "Companies", companyId, "workspace");
            Directory.CreateDirectory(path);

            // Uygulama çalıştırıcılarını (batch/ps1/copy) workspace dizinine otomatik bağla
            EnsureWorkspaceAppLaunchers(companyId, path);

            return path;
        }

        public void EnsureWorkspaceAppLaunchers(string companyId, string workspacePath)
        {
            try
            {
                var company = _companies.FirstOrDefault(c => c.Id == companyId);
                var resolvedExe = ResolveApplicationPath(companyId, company?.ApplicationExecutable);

                if (companyId != "sirket_3" && !string.IsNullOrWhiteSpace(resolvedExe) && Path.GetFileName(resolvedExe).Equals("AlpFinans.exe", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe))
                {
                    var exeName = Path.GetFileName(resolvedExe);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(resolvedExe);

                    // 1. AlpFinans.cmd / .bat Wrapper
                    var cmdPath = Path.Combine(workspacePath, $"{nameWithoutExt}.cmd");
                    var batPath = Path.Combine(workspacePath, $"{nameWithoutExt}.bat");
                    var launcherContent = $"@echo off\r\nstart \"\" \"{resolvedExe}\" %*\r\n";
                    if (!File.Exists(cmdPath)) File.WriteAllText(cmdPath, launcherContent, System.Text.Encoding.ASCII);
                    if (!File.Exists(batPath)) File.WriteAllText(batPath, launcherContent, System.Text.Encoding.ASCII);

                    // 2. PowerShell Script Wrapper
                    var ps1Path = Path.Combine(workspacePath, $"{nameWithoutExt}.ps1");
                    var ps1Content = $"param([Parameter(ValueFromRemainingArguments=$true)]$args)\r\nStart-Process '{resolvedExe}' -ArgumentList $args\r\n";
                    if (!File.Exists(ps1Path)) File.WriteAllText(ps1Path, ps1Content, System.Text.Encoding.UTF8);

                    // 3. Küçük boyutlu (.exe) ise veya doğrudan çalıştırma için kopyala
                    var destExe = Path.Combine(workspacePath, exeName);
                    var fi = new FileInfo(resolvedExe);
                    if (fi.Length > 0 && fi.Length < 30 * 1024 * 1024) // 30 MB altındaysa workspace'e de koy
                    {
                        if (!File.Exists(destExe) || new FileInfo(destExe).Length != fi.Length)
                        {
                            File.Copy(resolvedExe, destExe, overwrite: true);
                        }
                    }

                    // 4. Şirket applications/ klasörüne de ekle
                    var appsDir = Path.Combine(GetCompanyDirPath(companyId), "applications");
                    Directory.CreateDirectory(appsDir);
                    var appDest = Path.Combine(appsDir, exeName);
                    if (fi.Length > 0 && fi.Length < 30 * 1024 * 1024 && (!File.Exists(appDest) || new FileInfo(appDest).Length != fi.Length))
                    {
                        File.Copy(resolvedExe, appDest, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnsureWorkspaceAppLaunchers error: {ex.Message}");
            }
        }

        public string ResolveApplicationPath(string companyId, string? appNameOrPath = null)
        {
            var company = _companies.FirstOrDefault(c => c.Id == companyId);
            if (company != null && !string.IsNullOrWhiteSpace(company.ApplicationPath) && File.Exists(company.ApplicationPath))
            {
                // Katı güvenlik kontrolü: sirket_3 harici hiçbir kiracı AlpFinans.exe'ye bağlanamaz
                if (companyId != "sirket_3" && (company.ApplicationPath.Contains(@"\sınır\", StringComparison.OrdinalIgnoreCase) ||
                                                company.ApplicationPath.Contains(@"\AlpFinans\", StringComparison.OrdinalIgnoreCase) ||
                                                company.ApplicationPath.Contains("AlpFinans.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    company.ApplicationPath = string.Empty;
                    company.ApplicationExecutable = string.Empty;
                }
                else
                {
                    return company.ApplicationPath;
                }
            }

            var candidateNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(appNameOrPath))
            {
                if (companyId == "sirket_3" || !appNameOrPath.Contains("AlpFinans", StringComparison.OrdinalIgnoreCase))
                {
                    candidateNames.Add(appNameOrPath);
                    if (!appNameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        candidateNames.Add($"{appNameOrPath}.exe");
                }
            }
            if (company != null && !string.IsNullOrWhiteSpace(company.Name))
            {
                if (companyId == "sirket_3" || !company.Name.Contains("AlpFinans", StringComparison.OrdinalIgnoreCase))
                {
                    candidateNames.Add($"{company.Name}.exe");
                    candidateNames.Add(company.Name);
                }
            }
            if (companyId == "sirket_3")
            {
                candidateNames.Add("AlpFinans.exe");
            }
            else if (companyId == "sirket_4")
            {
                candidateNames.Add("Antigravity.exe");
            }

            var searchDirs = new List<string>
            {
                Path.Combine(_baseDataDir, "Companies", companyId, "applications"),
                Path.Combine(_baseDataDir, "Companies", companyId, "workspace")
            };

            if (companyId == "sirket_3")
            {
                searchDirs.Add(@"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans");
                searchDirs.Add(@"C:\Users\Alphan.MonsterAlphan\Desktop\sınır");
            }
            else if (companyId == "sirket_4")
            {
                searchDirs.Add(@"C:\Users\Alphan.MonsterAlphan\AppData\Local\Programs\Antigravity");
                searchDirs.Add(@"C:\Users\Alphan.MonsterAlphan\Downloads");
            }

            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var name in candidateNames)
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p))
                    {
                        // Katı kural: Okuma sorgularında asla kiracı durumu mutasyona uğratılmaz (SaveCompanies() çağrılmaz)
                        return p;
                    }
                }
            }

            return company?.ApplicationPath ?? string.Empty;
        }

        public string ResolveApplicationDatabasePath(string companyId)
        {
            var company = _companies.FirstOrDefault(c => c.Id == companyId);
            bool isAlpFinansTenant = companyId == "sirket_3";

            // 1. Kiracının açıkça konfigüre edilmiş veritabanı yolu varsa kontrol et
            if (company != null && !string.IsNullOrWhiteSpace(company.ApplicationDatabasePath) && File.Exists(company.ApplicationDatabasePath))
            {
                // Katı güvenlik kontrolü: Sirket_3 dışındaki kiracılar asla AlpFinans veritabanına erişemez
                if (!isAlpFinansTenant && (company.ApplicationDatabasePath.Contains(@"\sınır\", StringComparison.OrdinalIgnoreCase) ||
                                           company.ApplicationDatabasePath.Contains(@"\AlpFinans\", StringComparison.OrdinalIgnoreCase) ||
                                           company.ApplicationDatabasePath.Contains("finans_veritabani.sqlite", StringComparison.OrdinalIgnoreCase)))
                {
                    company.ApplicationDatabasePath = string.Empty;
                    return string.Empty;
                }

                var tenantDir = Path.GetFullPath(GetCompanyDirPath(companyId));
                if (!isAlpFinansTenant && !Path.GetFullPath(company.ApplicationDatabasePath).StartsWith(tenantDir, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return company.ApplicationDatabasePath;
            }

            // 2. Sirket_3 dışındaki kiracılar için finans_veritabani.sqlite asla çözümlenmez
            if (!isAlpFinansTenant)
            {
                return string.Empty;
            }

            // 3. Yalnızca sirket_3 (yetkili AlpFinans kiracısı) için lokasyonları çözümle
            if (isAlpFinansTenant)
            {
                var sirket3Paths = new List<string>
                {
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\Veriler_ve_Faturalar\finans_veritabani.sqlite",
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\AlpFinans\1_kisisel_finans_ve_vergi\finans_veritabani.sqlite",
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\Veriler_ve_Faturalar\finans_veritabani.sqlite",
                    @"C:\Users\Alphan.MonsterAlphan\Desktop\sınır\1_kisisel_finans_ve_vergi\finans_veritabani.sqlite",
                    Path.Combine(_baseDataDir, "Companies", "sirket_3", "documents", "finans_veritabani.sqlite"),
                    Path.Combine(_baseDataDir, "Companies", "sirket_3", "workspace", "finans_veritabani.sqlite")
                };

                foreach (var p in sirket3Paths)
                {
                    if (File.Exists(p))
                    {
                        return p;
                    }
                }
            }

            // Diğer kiracılar için asla global fallback yapılmaz, boş string döndürülür
            return string.Empty;
        }

        public string GetCompanyDatasetPath(string companyId)
        {
            var path = Path.Combine(_baseDataDir, "Companies", companyId, "dataset");
            Directory.CreateDirectory(path);
            return Path.Combine(path, "training_pairs.jsonl");
        }

        public string GetCompanyChatHistoryPath(string companyId)
        {
            var path = Path.Combine(_baseDataDir, "Companies", companyId, "chat_history");
            Directory.CreateDirectory(path);
            return Path.Combine(path, "history.json");
        }
    }
}
