using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EnterpriseAIApp.Services
{
    public class WebActionStep
    {
        public string Action { get; set; } = "navigate"; // navigate, click, type, select, submit, wait, extract, screenshot, evaluate
        public string? Url { get; set; }
        public string? Selector { get; set; }
        public string? Text { get; set; }
        public int Milliseconds { get; set; } = 1000;
        public string? Expression { get; set; }
    }

    public class WebActionResult
    {
        public bool IsSuccess { get; set; }
        public string CurrentUrl { get; set; } = string.Empty;
        public List<string> ExecutedSteps { get; set; } = new();
        public string ExtractedContent { get; set; } = string.Empty;
        public string? ScreenshotBase64 { get; set; }
        public string Output { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    public class WebActionEngine
    {
        private readonly CompanyManager _companyManager;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        public WebActionEngine(CompanyManager companyManager)
        {
            _companyManager = companyManager;
        }

        private static string? FindChromiumBrowserPath()
        {
            var candidates = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static int GetAvailablePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        }

        private static bool TryGetEvaluationValue(JsonElement cdpResponse, out JsonElement value)
        {
            value = default;
            if (cdpResponse.ValueKind != JsonValueKind.Object) return false;

            if (cdpResponse.TryGetProperty("result", out var outerRes))
            {
                if (outerRes.TryGetProperty("result", out var innerRes))
                {
                    if (innerRes.TryGetProperty("value", out value))
                    {
                        return true;
                    }
                    if (innerRes.TryGetProperty("description", out value))
                    {
                        return true;
                    }
                }
                else if (outerRes.TryGetProperty("value", out value))
                {
                    return true;
                }
            }
            return false;
        }

        private static string GetEvaluationString(JsonElement cdpResponse)
        {
            if (TryGetEvaluationValue(cdpResponse, out var val))
            {
                if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? "";
                return val.ToString();
            }
            return string.Empty;
        }

        private static bool GetEvaluationBool(JsonElement cdpResponse)
        {
            if (TryGetEvaluationValue(cdpResponse, out var val))
            {
                if (val.ValueKind is JsonValueKind.True) return true;
                if (val.ValueKind is JsonValueKind.False) return false;
                if (val.ValueKind == JsonValueKind.String && bool.TryParse(val.GetString(), out var b)) return b;
            }
            return false;
        }

        public async Task<WebActionResult> ExecuteWorkflowAsync(List<WebActionStep> steps, string? companyId = null, int timeoutSeconds = 60)
        {
            var result = new WebActionResult();
            if (steps == null || steps.Count == 0)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Hata: Çalıştırılacak herhangi bir web eylemi (adım) belirtilmedi.";
                result.Output = result.ErrorMessage;
                return result;
            }

            var browserPath = FindChromiumBrowserPath();
            if (string.IsNullOrEmpty(browserPath))
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Hata: Sistemde Microsoft Edge veya Google Chrome tarayıcısı bulunamadı.";
                result.Output = result.ErrorMessage;
                return result;
            }

            int port = GetAvailablePort();
            string profileDir = Path.Combine(Path.GetTempPath(), "AlphaGravity_WebProfile_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(profileDir);

            Process? proc = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = $"--headless=new --remote-debugging-port={port} --allow-file-access-from-files --allow-file-access --disable-web-security --disable-gpu --no-first-run --no-default-browser-check --disable-sync --disable-features=Translate,msEdgeSidebarV2,msEdgeFre --guest --disable-background-networking --user-data-dir=\"{profileDir}\" about:blank",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                proc = Process.Start(psi);
                if (proc == null)
                {
                    result.IsSuccess = false;
                    result.ErrorMessage = "Hata: Chromium tarayıcı işlemi başlatılamadı.";
                    result.Output = result.ErrorMessage;
                    return result;
                }

                // Query /json/list to get the actual PAGE target (ignoring internal edge:// or chrome:// dialogs)
                string? wsUrl = null;
                for (int i = 0; i < 25; i++)
                {
                    if (cts.IsCancellationRequested) break;
                    try
                    {
                        var listJson = await _httpClient.GetStringAsync($"http://127.0.0.1:{port}/json/list", cts.Token);
                        using var listDoc = JsonDocument.Parse(listJson);
                        if (listDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in listDoc.RootElement.EnumerateArray())
                            {
                                var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : "";
                                var itemUrl = item.TryGetProperty("url", out var uProp) ? uProp.GetString() ?? "" : "";
                                if (type == "page" &&
                                    !itemUrl.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) &&
                                    !itemUrl.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) &&
                                    item.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                                {
                                    wsUrl = wsProp.GetString();
                                    if (!string.IsNullOrEmpty(wsUrl)) break;
                                }
                            }
                            if (!string.IsNullOrEmpty(wsUrl)) break;
                        }
                    }
                    catch
                    {
                        await Task.Delay(200, cts.Token);
                    }
                }

                // If no page target found yet, create one via /json/new
                if (string.IsNullOrEmpty(wsUrl))
                {
                    try
                    {
                        var newJson = await _httpClient.GetStringAsync($"http://127.0.0.1:{port}/json/new", cts.Token);
                        using var newDoc = JsonDocument.Parse(newJson);
                        if (newDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                        {
                            wsUrl = wsProp.GetString();
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(wsUrl))
                {
                    throw new InvalidOperationException("Chromium DevTools Page WebSocket hedefine bağlanılamadı.");
                }

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

                int msgId = 1;
                async Task<JsonElement> SendCdpCommandAsync(string method, object? @params = null)
                {
                    int currentId = msgId++;
                    var payload = new Dictionary<string, object>
                    {
                        ["id"] = currentId,
                        ["method"] = method
                    };
                    if (@params != null) payload["params"] = @params;

                    var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                    await ws.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, cts.Token);

                    while (!cts.IsCancellationRequested)
                    {
                        var buffer = new byte[65536 * 4];
                        var ms = new MemoryStream();
                        WebSocketReceiveResult recv;

                        do
                        {
                            recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                            ms.Write(buffer, 0, recv.Count);
                        } while (!recv.EndOfMessage);

                        var respText = Encoding.UTF8.GetString(ms.ToArray());
                        using var respDoc = JsonDocument.Parse(respText);
                        var root = respDoc.RootElement;

                        // Check if this is the matching command response
                        if (root.TryGetProperty("id", out var idProp) && idProp.GetInt32() == currentId)
                        {
                            return root.Clone();
                        }

                        // Otherwise it is an asynchronous CDP event notification (e.g. Page.loadEventFired); keep waiting for our command response
                    }

                    throw new OperationCanceledException("CDP komut yanıtı zaman aşımına uğradı.");
                }

                // Enable essential CDP domains
                await SendCdpCommandAsync("Page.enable");
                await SendCdpCommandAsync("Runtime.enable");

                // Execute action steps sequentially
                foreach (var step in steps)
                {
                    if (cts.IsCancellationRequested) break;
                    var act = (step.Action ?? "").Trim().ToLowerInvariant();

                    switch (act)
                    {
                        case "navigate":
                        case "goto":
                        case "open":
                            var rawUrl = step.Url ?? "";
                            var targetUrl = rawUrl;
                            if (!targetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                                !targetUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                                !targetUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                                !targetUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                targetUrl = "https://" + targetUrl;
                            }

                            // If target is a local file, convert to data URI so Chromium renders it instantly without file-scheme sandbox restrictions
                            if (targetUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var fUri = new Uri(targetUrl);
                                    if (File.Exists(fUri.LocalPath))
                                    {
                                        var fileHtml = File.ReadAllText(fUri.LocalPath);
                                        targetUrl = "data:text/html;charset=utf-8," + Uri.EscapeDataString(fileHtml);
                                    }
                                }
                                catch { }
                            }

                            result.CurrentUrl = rawUrl;
                            await SendCdpCommandAsync("Page.navigate", new { url = targetUrl });
                            result.ExecutedSteps.Add($"🌐 Sayfaya gidildi: `{rawUrl}`");

                            // Wait until location changes from about:blank and document is ready
                            for (int w = 0; w < 30; w++)
                            {
                                await Task.Delay(100, cts.Token);
                                try
                                {
                                    var hrefRes = await SendCdpCommandAsync("Runtime.evaluate", new { expression = "window.location.href", returnByValue = true });
                                    var currentHref = GetEvaluationString(hrefRes);
                                    if (!string.IsNullOrEmpty(currentHref) && !currentHref.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var rState = await SendCdpCommandAsync("Runtime.evaluate", new { expression = "document.readyState", returnByValue = true });
                                        var ready = GetEvaluationString(rState);
                                        if (ready is "complete" or "interactive")
                                        {
                                            break;
                                        }
                                    }
                                }
                                catch { }
                            }

                            await Task.Delay(Math.Clamp(step.Milliseconds, 200, 15000), cts.Token);
                            break;

                        case "type":
                        case "fill":
                        case "input":
                            var fillScript = $@"
(() => {{
    const sel = {JsonSerializer.Serialize(step.Selector ?? "input")};
    const val = {JsonSerializer.Serialize(step.Text ?? "")};
    let el = document.querySelector(sel);
    if (!el && sel.startsWith('#')) {{
        el = document.getElementById(sel.substring(1));
    }}
    if (!el) {{
        el = document.querySelector(`[name=""${{sel.replace('#','')}}""]`) || document.querySelector(`[id=""${{sel.replace('#','')}}""]`);
    }}
    if (!el) return false;
    el.focus();
    el.value = val;
    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
    return true;
}})()";
                            bool filled = false;
                            for (int r = 0; r < 15; r++)
                            {
                                var fillRes = await SendCdpCommandAsync("Runtime.evaluate", new { expression = fillScript, returnByValue = true });
                                if (GetEvaluationBool(fillRes))
                                {
                                    filled = true;
                                    break;
                                }
                                await Task.Delay(150, cts.Token);
                            }

                            if (filled)
                            {
                                result.ExecutedSteps.Add($"✍️ Alan dolduruldu: `{step.Selector}` ➔ \"{step.Text}\"");
                            }
                            else
                            {
                                result.ExecutedSteps.Add($"⚠️ Uyarı: `{step.Selector}` alanı bulunamadı.");
                            }
                            await Task.Delay(200, cts.Token);
                            break;

                        case "click":
                        case "press":
                            var clickScript = $@"
(() => {{
    const sel = {JsonSerializer.Serialize(step.Selector ?? "button")};
    let el = document.querySelector(sel);
    if (!el && sel.startsWith('#')) {{
        el = document.getElementById(sel.substring(1));
    }}
    if (!el) {{
        el = document.querySelector(`[name=""${{sel.replace('#','')}}""]`) || document.querySelector(`[id=""${{sel.replace('#','')}}""]`);
    }}
    if (!el) {{
        // Fallback: search by visible text in buttons and links
        const targetText = sel.trim().toLowerCase();
        const candidates = Array.from(document.querySelectorAll('button, a, input[type=""submit""], input[type=""button""], [role=""button""]'));
        el = candidates.find(c => (c.innerText || c.value || '').trim().toLowerCase().includes(targetText));
    }}
    if (!el) return false;
    el.scrollIntoView({{ behavior: 'instant', block: 'center' }});
    el.click();
    return true;
}})()";
                            bool clicked = false;
                            for (int r = 0; r < 15; r++)
                            {
                                var clickRes = await SendCdpCommandAsync("Runtime.evaluate", new { expression = clickScript, returnByValue = true });
                                if (GetEvaluationBool(clickRes))
                                {
                                    clicked = true;
                                    break;
                                }
                                await Task.Delay(150, cts.Token);
                            }

                            if (clicked)
                            {
                                result.ExecutedSteps.Add($"🖱️ Butona tıklandı: `{step.Selector}`");
                            }
                            else
                            {
                                result.ExecutedSteps.Add($"⚠️ Uyarı: Tıklanacak `{step.Selector}` öğesi bulunamadı.");
                            }
                            await Task.Delay(Math.Clamp(step.Milliseconds, 200, 10000), cts.Token);
                            break;

                        case "select":
                            var selectScript = $@"
(() => {{
    const sel = {JsonSerializer.Serialize(step.Selector ?? "select")};
    const val = {JsonSerializer.Serialize(step.Text ?? "")};
    let el = document.querySelector(sel);
    if (!el && sel.startsWith('#')) {{
        el = document.getElementById(sel.substring(1));
    }}
    if (!el) return false;
    el.value = val;
    el.dispatchEvent(new Event('change', {{ bubbles: true }}));
    return true;
}})()";
                            await SendCdpCommandAsync("Runtime.evaluate", new { expression = selectScript });
                            result.ExecutedSteps.Add($"📋 Seçim yapıldı: `{step.Selector}` ➔ \"{step.Text}\"");
                            break;

                        case "submit":
                            var submitScript = $@"
(() => {{
    const sel = {JsonSerializer.Serialize(step.Selector ?? "form")};
    let el = document.querySelector(sel);
    if (!el && sel.startsWith('#')) {{
        el = document.getElementById(sel.substring(1));
    }}
    if (el && typeof el.submit === 'function') {{
        el.submit();
        return true;
    }}
    const btn = document.querySelector('button[type=""submit""], input[type=""submit""]');
    if (btn) {{
        btn.click();
        return true;
    }}
    return false;
}})()";
                            await SendCdpCommandAsync("Runtime.evaluate", new { expression = submitScript });
                            result.ExecutedSteps.Add($"🚀 Form gönderildi (Submit)");
                            await Task.Delay(Math.Clamp(step.Milliseconds, 500, 10000), cts.Token);
                            break;

                        case "wait":
                        case "sleep":
                            int waitTime = Math.Clamp(step.Milliseconds, 200, 30000);
                            result.ExecutedSteps.Add($"⏳ Beklendi: {waitTime} ms");
                            await Task.Delay(waitTime, cts.Token);
                            break;

                        case "extract":
                        case "get_content":
                        case "read":
                            var extractScript = $@"
(() => {{
    const sel = {JsonSerializer.Serialize(step.Selector ?? "body")};
    let el = document.querySelector(sel);
    if (!el && sel.startsWith('#')) {{
        el = document.getElementById(sel.substring(1));
    }}
    if (!el) {{
        el = document.querySelector(`[name=""${{sel.replace('#','')}}""]`) || document.querySelector(`[id=""${{sel.replace('#','')}}""]`);
    }}
    el = el || document.body;
    return el ? (el.innerText || el.textContent || '') : '';
}})()";
                            for (int r = 0; r < 10; r++)
                            {
                                var extRes = await SendCdpCommandAsync("Runtime.evaluate", new { expression = extractScript, returnByValue = true });
                                var extracted = GetEvaluationString(extRes);
                                if (!string.IsNullOrWhiteSpace(extracted))
                                {
                                    result.ExtractedContent = extracted;
                                    if (extracted != "Beklemede")
                                    {
                                        break;
                                    }
                                }
                                await Task.Delay(150, cts.Token);
                            }
                            result.ExecutedSteps.Add($"📄 Sayfadan içerik çekildi ({result.ExtractedContent.Length} karakter)");
                            break;

                        case "screenshot":
                            var ssRes = await SendCdpCommandAsync("Page.captureScreenshot", new { format = "png" });
                            if (ssRes.TryGetProperty("result", out var sr) && sr.TryGetProperty("data", out var sd))
                            {
                                result.ScreenshotBase64 = sd.GetString();
                                result.ExecutedSteps.Add("📸 Ekran görüntüsü (Screenshot) yakalandı.");
                            }
                            break;

                        case "evaluate":
                        case "evaluate_js":
                            if (!string.IsNullOrWhiteSpace(step.Expression))
                            {
                                var jsRes = await SendCdpCommandAsync("Runtime.evaluate", new { expression = step.Expression, returnByValue = true });
                                result.ExtractedContent = GetEvaluationString(jsRes);
                                result.ExecutedSteps.Add($"⚙️ Özel JavaScript çalıştırıldı.");
                            }
                            break;
                    }
                }

                // If no extraction was explicitly called, capture current page text
                if (string.IsNullOrWhiteSpace(result.ExtractedContent))
                {
                    try
                    {
                        var autoExtract = await SendCdpCommandAsync("Runtime.evaluate", new { expression = "document.body.innerText", returnByValue = true });
                        result.ExtractedContent = GetEvaluationString(autoExtract);
                    }
                    catch { }
                }

                result.IsSuccess = true;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorMessage = $"Web otomasyon eylemi sırasında hata: {ex.Message}";
            }
            finally
            {
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }

                try
                {
                    if (Directory.Exists(profileDir))
                    {
                        Directory.Delete(profileDir, recursive: true);
                    }
                }
                catch { }
            }

            // Build structured output
            var sb = new StringBuilder();
            sb.AppendLine("### 🌐 İki Yönlü Web Eylem & Otomasyon Raporu");
            sb.AppendLine($"- **Durum**: {(result.IsSuccess ? "✅ Başarılı" : "❌ Hata")}");
            if (!string.IsNullOrEmpty(result.CurrentUrl))
            {
                sb.AppendLine($"- **Hedef Web Adresi**: `{result.CurrentUrl}`");
            }
            sb.AppendLine($"- **Yürütülen Adımlar ({result.ExecutedSteps.Count} Adım)**:");
            foreach (var stepStr in result.ExecutedSteps)
            {
                sb.AppendLine($"  - {stepStr}");
            }

            if (!string.IsNullOrWhiteSpace(result.ExtractedContent))
            {
                sb.AppendLine("\n**📄 Sayfadan Elde Edilen Güncel İçerik / Form Sonucu:**");
                var preview = result.ExtractedContent.Length > 3000
                    ? result.ExtractedContent.Substring(0, 3000) + "\n\n...[İçerik Devamı Kesildi]"
                    : result.ExtractedContent;
                sb.AppendLine(preview);
            }

            if (!string.IsNullOrEmpty(result.ScreenshotBase64))
            {
                sb.AppendLine($"\n**📸 Anlık Görsel**: [Ekran Görüntüsü Base64 ({result.ScreenshotBase64.Length} bayt)]");
            }

            if (!result.IsSuccess && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                sb.AppendLine($"\n**Hata Detayı**: {result.ErrorMessage}");
            }

            result.Output = sb.ToString();
            return result;
        }
    }
}
