using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using EnterpriseAIApp.Models;

namespace EnterpriseAIApp.Services
{
    public class McpServerConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public List<string> Args { get; set; } = new();
        public Dictionary<string, string> Env { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public string Description { get; set; } = string.Empty;
    }

    public class McpConfigFile
    {
        public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    }

    public class McpToolDefinition
    {
        public string ServerName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JsonElement InputSchema { get; set; }
    }

    public class McpClientManager : IDisposable
    {
        private readonly string _configFilePath;
        private readonly ConcurrentDictionary<string, Process> _runningProcesses = new();
        private readonly ConcurrentDictionary<string, List<McpToolDefinition>> _serverTools = new();
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            WriteIndented = true
        };

        public McpClientManager(string? baseDataDir = null)
        {
            var dir = !string.IsNullOrWhiteSpace(baseDataDir)
                ? baseDataDir
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

            if (!File.Exists(Path.Combine(dir, "mcp_config.json")))
            {
                var curr = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 4 && curr != null; i++)
                {
                    var check = Path.Combine(curr.FullName, "Data");
                    if (File.Exists(Path.Combine(check, "mcp_config.json")))
                    {
                        dir = check;
                        break;
                    }
                    curr = curr.Parent;
                }
            }

            Directory.CreateDirectory(dir);
            _configFilePath = Path.Combine(dir, "mcp_config.json");
            EnsureDefaultConfigFile();
        }

        public string ConfigFilePath => _configFilePath;

        private void EnsureDefaultConfigFile()
        {
            if (!File.Exists(_configFilePath))
            {
                var defaultCfg = new McpConfigFile();
                defaultCfg.McpServers["chrome-devtools-mcp"] = new McpServerConfig
                {
                    Name = "chrome-devtools-mcp",
                    Command = "npx",
                    Args = new List<string> { "-y", "chrome-devtools-mcp@latest" },
                    Enabled = false,
                    Description = "Google Chrome Headless DevTools Protokolü ile web sayfalarını denetleme ve ekran görüntüsü alma"
                };

                try
                {
                    var json = JsonSerializer.Serialize(defaultCfg, _jsonOptions);
                    File.WriteAllText(_configFilePath, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        public List<McpServerConfig> GetConfiguredServers()
        {
            var list = new List<McpServerConfig>();
            if (!File.Exists(_configFilePath)) return list;

            try
            {
                var json = File.ReadAllText(_configFilePath, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("mcpServers", out var serversEl))
                {
                    foreach (var prop in serversEl.EnumerateObject())
                    {
                        var cfg = JsonSerializer.Deserialize<McpServerConfig>(prop.Value.GetRawText(), _jsonOptions);
                        if (cfg != null)
                        {
                            cfg.Name = prop.Name;
                            list.Add(cfg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading mcp_config.json: {ex.Message}");
            }

            return list;
        }

        public async Task<List<McpToolDefinition>> GetAvailableMcpToolsAsync()
        {
            var allTools = new List<McpToolDefinition>();
            var servers = GetConfiguredServers().Where(s => s.Enabled).ToList();

            foreach (var s in servers)
            {
                try
                {
                    var tools = await DiscoverServerToolsAsync(s);
                    allTools.AddRange(tools);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MCP Server '{s.Name}' discovery error: {ex.Message}");
                }
            }

            return allTools;
        }

        private async Task<List<McpToolDefinition>> DiscoverServerToolsAsync(McpServerConfig config)
        {
            if (_serverTools.TryGetValue(config.Name, out var cached))
            {
                return cached;
            }

            var proc = GetOrStartServerProcess(config);
            if (proc == null || proc.HasExited) return new();

            try
            {
                // 1. JSON-RPC Initialize
                var initReq = new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new { },
                        clientInfo = new { name = "AlphaGravity", version = "1.0.0" }
                    }
                };

                await SendJsonRpcAsync(proc, initReq);
                var initResp = await ReadJsonRpcResponseAsync(proc, 1, timeoutMs: 5000);

                // 2. Notification: initialized
                var initializedNotif = new { jsonrpc = "2.0", method = "notifications/initialized" };
                await SendJsonRpcAsync(proc, initializedNotif);

                // 3. Request: tools/list
                var listReq = new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } };
                await SendJsonRpcAsync(proc, listReq);
                var listResp = await ReadJsonRpcResponseAsync(proc, 2, timeoutMs: 7000);

                var discovered = new List<McpToolDefinition>();
                if (listResp != null && listResp.RootElement.TryGetProperty("result", out var resEl) &&
                    resEl.TryGetProperty("tools", out var toolsArr) && toolsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in toolsArr.EnumerateArray())
                    {
                        var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var desc = t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var schema = t.TryGetProperty("inputSchema", out var s) ? s.Clone() : default;

                        if (!string.IsNullOrEmpty(name))
                        {
                            discovered.Add(new McpToolDefinition
                            {
                                ServerName = config.Name,
                                Name = name,
                                Description = desc,
                                InputSchema = schema
                            });
                        }
                    }
                }

                _serverTools[config.Name] = discovered;
                return discovered;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiscoverServerToolsAsync failed for {config.Name}: {ex.Message}");
                return new();
            }
        }

        public async Task<string> CallMcpToolAsync(string serverName, string toolName, Dictionary<string, object> arguments, int timeoutMs = 30000)
        {
            var servers = GetConfiguredServers();
            var cfg = servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
            if (cfg == null)
            {
                return $"Hata: '{serverName}' adlı MCP sunucusu mcp_config.json dosyasında tanımlı değil.";
            }

            var proc = GetOrStartServerProcess(cfg);
            if (proc == null || proc.HasExited)
            {
                return $"Hata: '{serverName}' MCP sunucu süreci başlatılamadı veya kapandı.";
            }

            try
            {
                int reqId = new Random().Next(100, 99999);
                var callReq = new
                {
                    jsonrpc = "2.0",
                    id = reqId,
                    method = "tools/call",
                    @params = new
                    {
                        name = toolName,
                        arguments = arguments
                    }
                };

                await SendJsonRpcAsync(proc, callReq);
                var resp = await ReadJsonRpcResponseAsync(proc, reqId, timeoutMs);

                if (resp == null)
                {
                    return $"Hata: '{serverName}/{toolName}' MCP çağrısı zaman aşımına uğradı ({timeoutMs} ms).";
                }

                if (resp.RootElement.TryGetProperty("error", out var errEl))
                {
                    var msg = errEl.TryGetProperty("message", out var m) ? m.GetString() : errEl.GetRawText();
                    return $"MCP Sunucu Hatası ({serverName}/{toolName}): {msg}";
                }

                if (resp.RootElement.TryGetProperty("result", out var resEl))
                {
                    if (resEl.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in contentArr.EnumerateArray())
                        {
                            if (item.TryGetProperty("text", out var textEl))
                            {
                                sb.AppendLine(textEl.GetString());
                            }
                        }
                        return sb.ToString().Trim();
                    }
                    return resEl.GetRawText();
                }

                return resp.RootElement.GetRawText();
            }
            catch (Exception ex)
            {
                return $"MCP Araç Yürütme İstisnası: {ex.Message}";
            }
        }

        private Process? GetOrStartServerProcess(McpServerConfig config)
        {
            if (_runningProcesses.TryGetValue(config.Name, out var existing) && !existing.HasExited)
            {
                return existing;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = config.Command,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = Encoding.UTF8
                };

                foreach (var arg in config.Args)
                {
                    psi.ArgumentList.Add(arg);
                }

                foreach (var kvp in config.Env)
                {
                    psi.Environment[kvp.Key] = kvp.Value;
                }

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _runningProcesses[config.Name] = proc;
                    return proc;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start MCP server {config.Name}: {ex.Message}");
            }

            return null;
        }

        private static async Task SendJsonRpcAsync(Process proc, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            await proc.StandardInput.WriteLineAsync(json);
            await proc.StandardInput.FlushAsync();
        }

        private static async Task<JsonDocument?> ReadJsonRpcResponseAsync(Process proc, int expectedId, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested && !proc.HasExited)
            {
                var lineTask = proc.StandardOutput.ReadLineAsync(cts.Token).AsTask();
                var line = await lineTask;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == expectedId)
                    {
                        return doc;
                    }
                }
                catch { }
            }
            return null;
        }

        public async Task<ToolExecution?> TryExecuteToolAsync(string toolName, Dictionary<string, object> arguments)
        {
            var servers = GetConfiguredServers().Where(s => s.Enabled).ToList();
            if (servers.Count == 0) return null;

            foreach (var s in servers)
            {
                var tools = await DiscoverServerToolsAsync(s);
                var match = tools.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    var resultText = await CallMcpToolAsync(s.Name, match.Name, arguments);
                    return new ToolExecution
                    {
                        ToolName = $"mcp_{s.Name}_{match.Name}",
                        Parameters = JsonSerializer.Serialize(arguments, _jsonOptions),
                        Output = resultText,
                        IsSuccess = !resultText.StartsWith("Hata:") && !resultText.StartsWith("MCP Sunucu Hatası"),
                        ExecutedAt = DateTime.Now
                    };
                }
            }

            return null;
        }

        public void Dispose()
        {
            foreach (var proc in _runningProcesses.Values)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    proc.Dispose();
                }
                catch { }
            }
            _runningProcesses.Clear();
        }
    }
}
