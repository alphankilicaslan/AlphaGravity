using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EnterpriseAIApp.Models;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace EnterpriseAIApp.Services
{
    public class WindowsFontResolver : IFontResolver
    {
        private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public byte[]? GetFont(string faceName)
        {
            try
            {
                var target = Path.Combine(FontsFolder, faceName);
                if (File.Exists(target))
                {
                    return File.ReadAllBytes(target);
                }

                // Fallback to Arial
                var fallback = Path.Combine(FontsFolder, "arial.ttf");
                if (File.Exists(fallback))
                {
                    return File.ReadAllBytes(fallback);
                }
            }
            catch { }
            return null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var lower = familyName.ToLowerInvariant();
            if (lower.Contains("segoe"))
            {
                if (isBold) return new FontResolverInfo("segoeuib.ttf");
                return new FontResolverInfo("segoeui.ttf");
            }
            if (isBold) return new FontResolverInfo("arialbd.ttf");
            return new FontResolverInfo("arial.ttf");
        }
    }

    public class ReportExportService
    {
        static ReportExportService()
        {
            try
            {
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new WindowsFontResolver();
                }
            }
            catch { }
        }

        public static string ExportToMarkdown(string companyName, string sessionTitle, List<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Kurumsal Rapor & Sohbet Geçmişi: {sessionTitle}");
            sb.AppendLine($"**Şirket:** {companyName}  ");
            sb.AppendLine($"**Oluşturulma Tarihi:** {DateTime.Now:dd.MM.yyyy HH:mm:ss}  ");
            sb.AppendLine($"**Sistem:** Alp Studio — Kurumsal Otonom Masaüstü Ajanı\n");
            sb.AppendLine("---\n");

            foreach (var msg in messages)
            {
                var roleName = msg.IsUser ? "👤 Kullanıcı" : "🤖 Alp Asistan";
                sb.AppendLine($"### {roleName} ({msg.Timestamp:HH:mm})");
                sb.AppendLine(msg.Content.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public static string ExportToHtml(string companyName, string sessionTitle, List<ChatMessage> messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"tr\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine($"  <title>{sessionTitle} — {companyName}</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #161618; color: #F5F5F7; margin: 0; padding: 30px; line-height: 1.6; }");
            sb.AppendLine("    .container { max-width: 900px; margin: 0 auto; background: #1E1E20; border: 1px solid #333336; border-radius: 12px; padding: 30px; box-shadow: 0 8px 24px rgba(0,0,0,0.4); }");
            sb.AppendLine("    .header { border-bottom: 1px solid #333336; padding-bottom: 16px; margin-bottom: 24px; }");
            sb.AppendLine("    h1 { margin: 0 0 8px 0; font-size: 24px; color: #0A84FF; }");
            sb.AppendLine("    .meta { font-size: 13px; color: #86868B; }");
            sb.AppendLine("    .message { margin-bottom: 24px; padding: 16px; border-radius: 8px; }");
            sb.AppendLine("    .user-msg { background: #252528; border-left: 4px solid #0A84FF; }");
            sb.AppendLine("    .ai-msg { background: #242426; border-left: 4px solid #30D158; }");
            sb.AppendLine("    .role { font-weight: 600; font-size: 14px; margin-bottom: 8px; }");
            sb.AppendLine("    .role.user { color: #0A84FF; }");
            sb.AppendLine("    .role.ai { color: #30D158; }");
            sb.AppendLine("    .content { font-size: 14px; white-space: pre-wrap; word-break: break-word; }");
            sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin: 14px 0; background: #252528; border-radius: 6px; overflow: hidden; }");
            sb.AppendLine("    th, td { border: 1px solid #38383A; padding: 8px 12px; text-align: left; font-size: 13px; }");
            sb.AppendLine("    th { background: #2C2C30; color: #0A84FF; font-weight: 600; }");
            sb.AppendLine("    tr:nth-child(even) { background: #1E1E20; }");
            sb.AppendLine("    code { background: #121214; padding: 2px 6px; border-radius: 4px; font-family: Consolas, monospace; font-size: 12px; color: #FF9F0A; }");
            sb.AppendLine("    pre { background: #121214; padding: 12px; border-radius: 6px; overflow-x: auto; }");
            sb.AppendLine("    .footer { margin-top: 30px; text-align: center; font-size: 12px; color: #636366; border-top: 1px solid #333336; padding-top: 16px; }");
            sb.AppendLine("    @media print { body { background: #fff; color: #000; } .container { box-shadow: none; border: none; } }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <div class=\"container\">");
            sb.AppendLine("    <div class=\"header\">");
            sb.AppendLine($"      <h1>{sessionTitle}</h1>");
            sb.AppendLine($"      <div class=\"meta\">Şirket: <strong>{companyName}</strong> • Rapor Tarihi: {DateTime.Now:dd.MM.yyyy HH:mm} • Alp Studio Kurumsal AI Ajanı</div>");
            sb.AppendLine("    </div>");

            foreach (var msg in messages)
            {
                var isUser = msg.IsUser;
                var cls = isUser ? "user-msg" : "ai-msg";
                var roleCls = isUser ? "user" : "ai";
                var roleText = isUser ? "👤 Kullanıcı" : "🤖 Alp Asistan";

                sb.AppendLine($"    <div class=\"message {cls}\">");
                sb.AppendLine($"      <div class=\"role {roleCls}\">{roleText} <span style=\"font-size: 11px; color: #86868B; font-weight: normal;\">({msg.Timestamp:HH:mm})</span></div>");
                sb.AppendLine($"      <div class=\"content\">{FormatMarkdownForHtml(msg.Content)}</div>");
                sb.AppendLine("    </div>");
            }

            sb.AppendLine("    <div class=\"footer\">");
            sb.AppendLine($"      Alp Studio Kurumsal Otonom Masaüstü Ajanı tarafından oluşturulmuştur. © {DateTime.Now.Year}");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        private static string FormatMarkdownForHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            // Convert tables
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var inTable = false;
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
                {
                    if (!inTable)
                    {
                        sb.AppendLine("<table>");
                        inTable = true;
                    }

                    if (trimmed.Contains("---"))
                    {
                        continue; // Skip separator line
                    }

                    var cells = trimmed.Split('|').Skip(1).Take(trimmed.Split('|').Length - 2).Select(c => c.Trim()).ToList();
                    sb.Append("<tr>");
                    foreach (var cell in cells)
                    {
                        sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(cell)}</td>");
                    }
                    sb.AppendLine("</tr>");
                }
                else
                {
                    if (inTable)
                    {
                        sb.AppendLine("</table>");
                        inTable = false;
                    }
                    sb.AppendLine(System.Net.WebUtility.HtmlEncode(line));
                }
            }

            if (inTable)
            {
                sb.AppendLine("</table>");
            }

            return sb.ToString();
        }

        public static void ExportToPdf(string filePath, string companyName, string sessionTitle, List<ChatMessage> messages)
        {
            var document = new PdfDocument();
            document.Info.Title = $"{sessionTitle} — {companyName}";
            document.Info.Author = "Alp Studio Kurumsal AI";

            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            var gfx = XGraphics.FromPdfPage(page);

            var fontTitle = new XFont("Segoe UI", 16, XFontStyleEx.Bold);
            var fontSub = new XFont("Segoe UI", 9, XFontStyleEx.Regular);
            var fontRole = new XFont("Segoe UI", 11, XFontStyleEx.Bold);
            var fontContent = new XFont("Segoe UI", 10, XFontStyleEx.Regular);

            double yPoint = 40;
            double margin = 40;
            double pageWidth = page.Width.Point - (margin * 2);

            // 1. Header
            gfx.DrawString(sessionTitle, fontTitle, XBrushes.Navy, new XPoint(margin, yPoint));
            yPoint += 22;
            gfx.DrawString($"Şirket: {companyName}  •  Tarih: {DateTime.Now:dd.MM.yyyy HH:mm}  •  Alp Studio AI", fontSub, XBrushes.DarkGray, new XPoint(margin, yPoint));
            yPoint += 14;

            gfx.DrawLine(new XPen(XColor.FromArgb(200, 200, 200), 1), margin, yPoint, page.Width.Point - margin, yPoint);
            yPoint += 20;

            // 2. Messages
            foreach (var msg in messages)
            {
                if (yPoint > page.Height.Point - 60)
                {
                    page = document.AddPage();
                    page.Size = PdfSharp.PageSize.A4;
                    gfx = XGraphics.FromPdfPage(page);
                    yPoint = 40;
                }

                var roleText = msg.IsUser ? "👤 Kullanıcı:" : "🤖 Alp Asistan:";
                var roleBrush = msg.IsUser ? XBrushes.DarkBlue : XBrushes.DarkGreen;

                gfx.DrawString($"{roleText} ({msg.Timestamp:HH:mm})", fontRole, roleBrush, new XPoint(margin, yPoint));
                yPoint += 16;

                var cleanContent = Regex.Replace(msg.Content, @"```[\s\S]*?```", "[Kod / Araç Bloğu]");
                var lines = cleanContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    if (yPoint > page.Height.Point - 50)
                    {
                        page = document.AddPage();
                        page.Size = PdfSharp.PageSize.A4;
                        gfx = XGraphics.FromPdfPage(page);
                        yPoint = 40;
                    }

                    var textLine = line.Length > 100 ? line[..100] + "..." : line;
                    gfx.DrawString(textLine, fontContent, XBrushes.Black, new XPoint(margin + 10, yPoint));
                    yPoint += 14;
                }

                yPoint += 10;
            }

            document.Save(filePath);
        }
    }
}
