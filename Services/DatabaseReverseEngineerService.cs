using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace EnterpriseAIApp.Services
{
    public class TableColumnInfo
    {
        public int Cid { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool NotNull { get; set; }
        public string? DefaultValue { get; set; }
        public bool IsPrimaryKey { get; set; }
    }

    public class ForeignKeyInfo
    {
        public int Id { get; set; }
        public string FromColumn { get; set; } = string.Empty;
        public string ToTable { get; set; } = string.Empty;
        public string ToColumn { get; set; } = string.Empty;
    }

    public class TableSchemaInfo
    {
        public string TableName { get; set; } = string.Empty;
        public long RowCount { get; set; }
        public List<TableColumnInfo> Columns { get; set; } = new();
        public List<ForeignKeyInfo> ForeignKeys { get; set; } = new();
        public List<string> Indexes { get; set; } = new();
        public List<Dictionary<string, object?>> SampleRows { get; set; } = new();
        public string InferredBusinessRole { get; set; } = "Genel Veri Tablosu";
        public List<string> SemanticTags { get; set; } = new();
    }

    public class DatabaseReverseEngineerResult
    {
        public bool Success { get; set; }
        public string DatabasePath { get; set; } = string.Empty;
        public string DatabaseEngine { get; set; } = "Bilinmiyor";
        public long FileSizeBytes { get; set; }
        public int TableCount => Tables.Count;
        public long TotalRecordCount => Tables.Sum(t => t.RowCount);
        public List<TableSchemaInfo> Tables { get; set; } = new();
        public string MarkdownReport { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class DatabaseReverseEngineerService
    {
        private readonly CompanyManager _companyManager;
        private readonly KnowledgeGraphManager? _knowledgeGraphManager;

        public DatabaseReverseEngineerService(CompanyManager companyManager, KnowledgeGraphManager? knowledgeGraphManager = null)
        {
            _companyManager = companyManager;
            _knowledgeGraphManager = knowledgeGraphManager;
        }

        public List<string> DiscoverDatabaseFiles(string? targetDir = null, string? companyId = null)
        {
            var discovered = new List<string>();
            var dirsToScan = new List<string>();

            if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
            {
                dirsToScan.Add(targetDir);
            }

            if (!string.IsNullOrWhiteSpace(companyId))
            {
                var resolvedDb = _companyManager.ResolveApplicationDatabasePath(companyId);
                if (!string.IsNullOrWhiteSpace(resolvedDb) && File.Exists(resolvedDb))
                {
                    discovered.Add(resolvedDb);
                }

                var companyDir = _companyManager.GetCompanyDirPath(companyId);
                if (Directory.Exists(companyDir))
                {
                    dirsToScan.Add(Path.Combine(companyDir, "workspace"));
                    dirsToScan.Add(Path.Combine(companyDir, "applications"));
                    dirsToScan.Add(companyDir);
                }
            }

            var extensions = new[] { "*.sqlite", "*.sqlite3", "*.db", "*.db3", "*.accdb", "*.mdb" };

            foreach (var dir in dirsToScan.Distinct())
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var ext in extensions)
                    {
                        var files = Directory.GetFiles(dir, ext, SearchOption.AllDirectories);
                        discovered.AddRange(files.Where(f => !f.Contains("EBWebView") && !f.Contains("cache.db")));
                    }
                }
                catch { }
            }

            return discovered.Distinct().ToList();
        }

        public DatabaseReverseEngineerResult ReverseEngineerDatabase(string? dbPath, string companyId, int maxSampleRows = 3, bool ingestToKnowledgeGraph = true)
        {
            var result = new DatabaseReverseEngineerResult();

            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                var candidates = DiscoverDatabaseFiles(companyId: companyId);
                if (candidates.Count > 0)
                {
                    dbPath = candidates[0];
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = $"Belirtilen veritabanı dosyası bulunamadı ve şirket dizininde otomatik veritabanı tespit edilemedi.";
                    return result;
                }
            }

            result.DatabasePath = Path.GetFullPath(dbPath);
            var fileInfo = new FileInfo(result.DatabasePath);
            result.FileSizeBytes = fileInfo.Length;

            var ext = fileInfo.Extension.ToLowerInvariant();
            if (ext is ".sqlite" or ".sqlite3" or ".db" or ".db3")
            {
                result.DatabaseEngine = "SQLite (Gömülü İlişkisel Veritabanı)";
                ReverseEngineerSqliteDatabase(result, maxSampleRows);
            }
            else if (ext is ".accdb" or ".mdb")
            {
                result.DatabaseEngine = "Microsoft Access / Jet Database";
                ReverseEngineerAccessDatabaseStub(result);
            }
            else
            {
                result.DatabaseEngine = "Genel Veritabanı Dosyası";
                ReverseEngineerGenericFile(result);
            }

            if (result.Success)
            {
                result.MarkdownReport = GenerateMarkdownReport(result);

                if (ingestToKnowledgeGraph && _knowledgeGraphManager != null && !string.IsNullOrWhiteSpace(companyId))
                {
                    IngestDatabaseToKnowledgeGraph(result, companyId);
                }
            }

            return result;
        }

        private void ReverseEngineerSqliteDatabase(DatabaseReverseEngineerResult result, int maxSampleRows)
        {
            try
            {
                var connString = new SqliteConnectionStringBuilder
                {
                    DataSource = result.DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                using var conn = new SqliteConnection(connString);
                conn.Open();

                var tableNames = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                }

                foreach (var tableName in tableNames)
                {
                    var tableSchema = new TableSchemaInfo { TableName = tableName };

                    // 1. Column definitions
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            tableSchema.Columns.Add(new TableColumnInfo
                            {
                                Cid = reader.GetInt32(0),
                                Name = reader.GetString(1),
                                Type = reader.IsDBNull(2) ? "TEXT" : reader.GetString(2),
                                NotNull = reader.GetInt32(3) == 1,
                                DefaultValue = reader.IsDBNull(4) ? null : reader.GetString(4),
                                IsPrimaryKey = reader.GetInt32(5) > 0
                            });
                        }
                    }

                    // 2. Foreign keys
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"PRAGMA foreign_key_list(\"{tableName}\");";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            tableSchema.ForeignKeys.Add(new ForeignKeyInfo
                            {
                                Id = reader.GetInt32(0),
                                ToTable = reader.GetString(2),
                                FromColumn = reader.GetString(3),
                                ToColumn = reader.GetString(4)
                            });
                        }
                    }

                    // 3. Indexes
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"PRAGMA index_list(\"{tableName}\");";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            tableSchema.Indexes.Add(reader.GetString(1));
                        }
                    }

                    // 4. Row count
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
                        var countObj = cmd.ExecuteScalar();
                        if (countObj != null && long.TryParse(countObj.ToString(), out var count))
                        {
                            tableSchema.RowCount = count;
                        }
                    }

                    // 5. Sample Rows
                    if (maxSampleRows > 0)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SELECT * FROM \"{tableName}\" LIMIT {maxSampleRows};";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var rowDict = new Dictionary<string, object?>();
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var colName = reader.GetName(i);
                                rowDict[colName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            }
                            tableSchema.SampleRows.Add(rowDict);
                        }
                    }

                    // 6. Semantic Role & Tags Inference
                    InferTableSemantics(tableSchema);

                    result.Tables.Add(tableSchema);
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"SQLite veritabanı analiz hatası: {ex.Message}";
            }
        }

        private void InferTableSemantics(TableSchemaInfo table)
        {
            var lowerName = table.TableName.ToLowerInvariant();
            var colNames = table.Columns.Select(c => c.Name.ToLowerInvariant()).ToList();

            if (lowerName.Contains("cari") || lowerName.Contains("musteri") || lowerName.Contains("customer") || lowerName.Contains("client"))
            {
                table.InferredBusinessRole = "👥 Müşteri & Cari Hesaplar Modülü";
                table.SemanticTags.AddRange(new[] { "cari", "musteri", "finans", "alacak_borc" });
            }
            else if (lowerName.Contains("fatura") || lowerName.Contains("invoice") || lowerName.Contains("irsaliye") || lowerName.Contains("bill"))
            {
                table.InferredBusinessRole = "📄 Fatura & Ticari Belge Kayıtları";
                table.SemanticTags.AddRange(new[] { "fatura", "muhasebe", "kdv", "finans" });
            }
            else if (lowerName.Contains("villa") || lowerName.Contains("otel") || lowerName.Contains("room") || lowerName.Contains("oda") || lowerName.Contains("konaklama"))
            {
                table.InferredBusinessRole = "🏡 Tesis, Villa & Konaklama Varlıkları";
                table.SemanticTags.AddRange(new[] { "turizm", "villa", "kapasite", "fiyat" });
            }
            else if (lowerName.Contains("rezervasyon") || lowerName.Contains("booking") || lowerName.Contains("reservation"))
            {
                table.InferredBusinessRole = "📅 Rezervasyon & Giriş-Çıkış Takvimi";
                table.SemanticTags.AddRange(new[] { "rezervasyon", "takvim", "turizm", "misafir" });
            }
            else if (lowerName.Contains("stok") || lowerName.Contains("urun") || lowerName.Contains("product") || lowerName.Contains("item"))
            {
                table.InferredBusinessRole = "📦 Stok & Ürün Kataloğu";
                table.SemanticTags.AddRange(new[] { "stok", "urun", "envanter" });
            }
            else if (lowerName.Contains("kasa") || lowerName.Contains("banka") || lowerName.Contains("odeme") || lowerName.Contains("payment") || lowerName.Contains("cash"))
            {
                table.InferredBusinessRole = "💰 Kasa, Banka & Nakit Akışı";
                table.SemanticTags.AddRange(new[] { "kasa", "banka", "nakit", "tahsilat" });
            }
            else if (lowerName.Contains("user") || lowerName.Contains("kullanici") || lowerName.Contains("personel") || lowerName.Contains("employee"))
            {
                table.InferredBusinessRole = "👤 Personel & Yetki Yönetimi";
                table.SemanticTags.AddRange(new[] { "personel", "kullanici", "yetki" });
            }
            else
            {
                table.InferredBusinessRole = "📊 Operasyonel Veri Tablosu";
                table.SemanticTags.Add("operasyon");
            }
        }

        private void ReverseEngineerAccessDatabaseStub(DatabaseReverseEngineerResult result)
        {
            result.Success = true;
            var stubTable = new TableSchemaInfo
            {
                TableName = "MS_ACCESS_SCHEMA",
                RowCount = 1,
                InferredBusinessRole = "Microsoft Access / Jet Veritabanı Dosyası",
                Columns = new List<TableColumnInfo>
                {
                    new() { Name = "DatabaseFile", Type = "VARCHAR", IsPrimaryKey = true },
                    new() { Name = "EngineType", Type = "Jet/ACE OLEDB" }
                }
            };
            result.Tables.Add(stubTable);
        }

        private void ReverseEngineerGenericFile(DatabaseReverseEngineerResult result)
        {
            result.Success = true;
            result.Tables.Add(new TableSchemaInfo
            {
                TableName = Path.GetFileNameWithoutExtension(result.DatabasePath),
                RowCount = 0,
                InferredBusinessRole = "Yapılandırılmamış Veritabanı / Veri Dosyası"
            });
        }

        private string GenerateMarkdownReport(DatabaseReverseEngineerResult result)
        {
            var sb = new StringBuilder();
            var fileName = Path.GetFileName(result.DatabasePath);
            var sizeKb = result.FileSizeBytes / 1024.0;

            sb.AppendLine($"# 🗄️ Tersine Mühendislik Veritabanı Raporu: `{fileName}`");
            sb.AppendLine($"- **Motor**: {result.DatabaseEngine}");
            sb.AppendLine($"- **Dosya Yolu**: `{result.DatabasePath}`");
            sb.AppendLine($"- **Dosya Boyutu**: {sizeKb:F1} KB");
            sb.AppendLine($"- **Tablo Sayısı**: {result.TableCount} Adet");
            sb.AppendLine($"- **Toplam Kayıt**: {result.TotalRecordCount:N0} Satır\n");

            sb.AppendLine("## 📊 Tablo ve Şema Mimarisi");

            foreach (var table in result.Tables)
            {
                sb.AppendLine($"### 📋 Tablo: `{table.TableName}` ({table.InferredBusinessRole})");
                sb.AppendLine($"- **Toplam Satır**: {table.RowCount:N0}");
                if (table.ForeignKeys.Count > 0)
                {
                    sb.AppendLine($"- **İlişkiler (Foreign Keys)**: {string.Join(", ", table.ForeignKeys.Select(f => $"`{f.FromColumn}` ➔ `{f.ToTable}.{f.ToColumn}`"))}");
                }

                sb.AppendLine("\n| Sütun | Veri Tipi | Zorunlu (NN) | Birincil Anahtar (PK) | Varsayılan |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var col in table.Columns)
                {
                    var pkIcon = col.IsPrimaryKey ? "🔑 Evet" : "-";
                    var nnIcon = col.NotNull ? "✅" : "-";
                    var defVal = col.DefaultValue ?? "-";
                    sb.AppendLine($"| `{col.Name}` | `{col.Type}` | {nnIcon} | {pkIcon} | {defVal} |");
                }

                if (table.SampleRows.Count > 0)
                {
                    sb.AppendLine("\n**Canlı Örnek Veri Satırları:**");
                    var sampleCols = table.Columns.Select(c => c.Name).ToList();
                    sb.AppendLine("| " + string.Join(" | ", sampleCols.Select(c => $"`{c}`")) + " |");
                    sb.AppendLine("|" + string.Join("|", sampleCols.Select(_ => "---")) + "|");

                    foreach (var row in table.SampleRows)
                    {
                        var rowVals = sampleCols.Select(c =>
                        {
                            if (!row.TryGetValue(c, out var val) || val == null) return "NULL";
                            var str = val.ToString() ?? "";
                            return str.Replace("\n", " ").Replace("|", "/");
                        });
                        sb.AppendLine("| " + string.Join(" | ", rowVals) + " |");
                    }
                }

                sb.AppendLine();
            }

            sb.AppendLine("## 💡 Otonom AI İş Zekası & Kullanım Tavsiyesi");
            sb.AppendLine("Yapay zeka bu veritabanının şemasına ve ilişkilerine tam hakimdir. Kullanıcı finans, ciro, cari veya rezervasyon sorduğunda `query_database` aracıyla doğrudan SQL sorgusu çalıştırarak canlı verileri getirebilir.");

            return sb.ToString();
        }

        private void IngestDatabaseToKnowledgeGraph(DatabaseReverseEngineerResult result, string companyId)
        {
            try
            {
                var dbName = Path.GetFileName(result.DatabasePath);
                var dbNodeId = $"db_{Math.Abs(dbName.GetHashCode()) % 100000}";

                var newNodes = new List<KnowledgeNode>();
                var newLinks = new List<KnowledgeLink>();

                // Root DB Node
                newNodes.Add(new KnowledgeNode
                {
                    Id = dbNodeId,
                    Label = $"🗄️ {dbName}",
                    Group = "data",
                    Size = 32,
                    Description = $"Tersine Mühendislikle Çözülmüş Veritabanı ({result.DatabaseEngine}). {result.TableCount} tablo, {result.TotalRecordCount} satır veri barındırır.",
                    DocSource = result.DatabasePath,
                    Tags = new List<string> { "veritabani", "sql", "tersine_muhendislik", "erp" }
                });

                foreach (var table in result.Tables)
                {
                    var tableNodeId = $"tbl_{Math.Abs((dbName + table.TableName).GetHashCode()) % 100000}";
                    var colSummary = string.Join(", ", table.Columns.Take(6).Select(c => c.Name));

                    newNodes.Add(new KnowledgeNode
                    {
                        Id = tableNodeId,
                        Label = $"📋 {table.TableName}",
                        Group = "data",
                        Size = 22,
                        Description = $"{table.InferredBusinessRole}. Toplam {table.RowCount:N0} satır. Sütunlar: {colSummary}",
                        DocSource = result.DatabasePath,
                        Tags = table.SemanticTags
                    });

                    newLinks.Add(new KnowledgeLink
                    {
                        Source = dbNodeId,
                        Target = tableNodeId,
                        Label = "barındırır",
                        Type = "dataflow",
                        Strength = 1.0
                    });

                    foreach (var fk in table.ForeignKeys)
                    {
                        var targetTableNodeId = $"tbl_{Math.Abs((dbName + fk.ToTable).GetHashCode()) % 100000}";
                        newLinks.Add(new KnowledgeLink
                        {
                            Source = tableNodeId,
                            Target = targetTableNodeId,
                            Label = $"bağlıdır ({fk.FromColumn}➔{fk.ToColumn})",
                            Type = "dependency",
                            Strength = 0.8
                        });
                    }
                }

                _knowledgeGraphManager?.AddBulkKnowledge(companyId, newNodes, newLinks);
            }
            catch { }
        }

        public (bool IsSuccess, string Output) ExecuteSafeQuery(string dbPath, string sqlQuery, int maxRows = 50)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return (false, "Hata: Belirtilen veritabanı dosyası bulunamadı.");
            }

            if (string.IsNullOrWhiteSpace(sqlQuery))
            {
                return (false, "Hata: SQL sorgusu boş olamaz.");
            }

            var trimmed = sqlQuery.Trim();

            // 1. Critical Data Loss Prevention Guardrail for DB queries
            var forbiddenKeywords = new[]
            {
                "DROP ", "DELETE ", "UPDATE ", "INSERT ", "ALTER ", "TRUNCATE ",
                "REPLACE ", "CREATE ", "ATTACH ", "DETACH ", "GRANT ", "REVOKE "
            };

            foreach (var kw in forbiddenKeywords)
            {
                if (Regex.IsMatch(trimmed, @"\b" + kw.Trim() + @"\b", RegexOptions.IgnoreCase))
                {
                    return (false, $"[GÜVENLİK KALKANI BLOKESİ]: Salt-okunur moddasınız! '{kw.Trim()}' gibi veri değiştirici veya yıkıcı SQL komutları şirketin veritabanı güvenliği için engellenmiştir. Yalnızca 'SELECT' ve 'PRAGMA' sorgularına izin verilir.");
                }
            }

            if (!Regex.IsMatch(trimmed, @"^(SELECT|PRAGMA|EXPLAIN)\b", RegexOptions.IgnoreCase))
            {
                return (false, "Hata: Yalnızca veri okuma sorguları (SELECT, PRAGMA, EXPLAIN) çalıştırılabilir.");
            }

            try
            {
                var connString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                using var conn = new SqliteConnection(connString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = trimmed;
                cmd.CommandTimeout = 15; // 15s timeout

                using var reader = cmd.ExecuteReader();
                var cols = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    cols.Add(reader.GetName(i));
                }

                var rows = new List<List<string>>();
                int rowCount = 0;

                while (reader.Read() && rowCount < maxRows)
                {
                    rowCount++;
                    var rowVals = new List<string>();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        if (reader.IsDBNull(i))
                        {
                            rowVals.Add("NULL");
                        }
                        else
                        {
                            var s = reader.GetValue(i).ToString() ?? "";
                            rowVals.Add(s.Replace("\n", " ").Replace("|", "/"));
                        }
                    }
                    rows.Add(rowVals);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"### 🔍 Canlı Veritabanı Sorgu Sonucu");
                sb.AppendLine($"- **Kaynak**: `{Path.GetFileName(dbPath)}`");
                sb.AppendLine($"- **Sorgu**: `{trimmed}`");
                sb.AppendLine($"- **Dönen Kayıt**: {rows.Count} satır (Limit: {maxRows})\n");

                if (cols.Count == 0 || rows.Count == 0)
                {
                    sb.AppendLine("*Sorgu başarıyla çalıştırıldı ancak eşleşen kayıt bulunamadı.*");
                    return (true, sb.ToString());
                }

                sb.AppendLine("| " + string.Join(" | ", cols.Select(c => $"**{c}**")) + " |");
                sb.AppendLine("|" + string.Join("|", cols.Select(_ => "---")) + "|");

                foreach (var r in rows)
                {
                    sb.AppendLine("| " + string.Join(" | ", r) + " |");
                }

                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"SQL Çalıştırma Hatası: {ex.Message}");
            }
        }
    }
}
