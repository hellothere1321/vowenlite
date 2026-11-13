using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Markdown -> HTML for the answer box and the Ask AI panel (both WebView2): headings, nested lists, tables, code blocks, quotes,
// inline bold/italic/code/links, and diagrams: a ```svg block (or a bare <svg> element) is shown as a picture after being stripped of
// anything active (scripts, event handlers, external references).
static class Md
{
    public const string Css = @"
:root{--bg:#0c0d10;--s2:#15171b;--s3:#24272e;--ink:#f3f4f6;--text:#c9ccd3;--soft:#9a9fa9;--faint:#7a7f89;--ice:#a9d8ff}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--text);font:14.5px/1.55 'Segoe UI',system-ui,sans-serif;-webkit-font-smoothing:antialiased}
::-webkit-scrollbar{width:10px;height:10px}::-webkit-scrollbar-thumb{background:#2a2e36;border-radius:5px;border:2px solid var(--bg)}::-webkit-scrollbar-thumb:hover{background:#3a3f49}
.md>*:first-child{margin-top:0}.md>*:last-child{margin-bottom:0}
.md p{margin:0 0 10px}.md h1,.md h2,.md h3,.md h4{color:var(--ink);font-weight:600;margin:16px 0 6px;line-height:1.3}
.md h1{font-size:17px}.md h2{font-size:15.5px}.md h3,.md h4{font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:var(--faint)}
.md ul,.md ol{margin:0 0 10px;padding-left:20px}.md li{margin:3px 0}.md li::marker{color:var(--faint)}
.md strong{color:var(--ink);font-weight:600}.md a{color:var(--ice);text-decoration:none}.md a:hover{text-decoration:underline}
.md code{font:12.5px 'Cascadia Mono',Consolas,monospace;background:var(--s2);border-radius:5px;padding:1px 5px;color:var(--ink)}
.md pre{background:var(--s2);border:1px solid var(--s3);border-radius:10px;padding:10px 12px;overflow:auto;margin:0 0 10px}.md pre code{background:none;padding:0;color:var(--text);white-space:pre}
.md blockquote{margin:0 0 10px;padding:2px 0 2px 12px;border-left:2px solid var(--s3);color:var(--soft)}
.md table{border-collapse:collapse;margin:0 0 10px;font-size:13.5px;display:block;overflow-x:auto;max-width:100%}
.md th,.md td{border-bottom:1px solid var(--s3);padding:5px 12px 5px 0;text-align:left;vertical-align:top}.md th{color:var(--faint);font-weight:600;font-size:11.5px;letter-spacing:.06em;text-transform:uppercase}
.md hr{border:0;border-top:1px solid var(--s3);margin:12px 0}
.md .fig{background:var(--s2);border:1px solid var(--s3);border-radius:12px;padding:12px;margin:4px 0 12px;text-align:center;overflow:auto}.md .fig svg{max-width:100%;height:auto}
.md .ts{font:11.5px 'Cascadia Mono',Consolas,monospace;color:var(--ice);background:rgba(169,216,255,.08);border-radius:4px;padding:0 4px}
";

    static string E(string s) => WebUtility.HtmlEncode(s);

    static readonly Regex InlineRx = new(@"`([^`]+)`|\*\*(.+?)\*\*|(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])|\[([^\]]+)\]\((https?://[^)\s]+)\)|(?<![\w/])(https?://[^\s<>)\]]+)|\[(\d{1,2}:\d{2}(?::\d{2})?)\]");

    static string Inline(string text)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            sb.Append(E(text.Substring(pos, m.Index - pos)));
            if (m.Groups[1].Success) sb.Append("<code>").Append(E(m.Groups[1].Value)).Append("</code>");
            else if (m.Groups[2].Success) sb.Append("<strong>").Append(Inline(m.Groups[2].Value)).Append("</strong>");
            else if (m.Groups[3].Success) sb.Append("<em>").Append(Inline(m.Groups[3].Value)).Append("</em>");
            else if (m.Groups[4].Success) sb.Append("<a href=\"").Append(E(m.Groups[5].Value)).Append("\">").Append(E(m.Groups[4].Value)).Append("</a>");
            else if (m.Groups[6].Success) { string u = m.Groups[6].Value.TrimEnd('.', ',', ';'); sb.Append("<a href=\"").Append(E(u)).Append("\">").Append(E(u)).Append("</a>").Append(E(m.Groups[6].Value.Substring(u.Length))); }
            else sb.Append("<span class=\"ts\">").Append(E(m.Groups[7].Value)).Append("</span>");
            pos = m.Index + m.Length;
        }
        sb.Append(E(text.Substring(pos)));
        return sb.ToString();
    }

    // Keeps the drawing, drops anything that could run or load: scripts, foreignObject, event handlers, external hrefs.
    public static string SafeSvg(string svg)
    {
        var m = Regex.Match(svg, @"<svg\b.*?</svg>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string s = m.Value;
        s = Regex.Replace(s, @"<(script|foreignObject|iframe|object|embed|style)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(script|foreignObject|iframe|object|embed)\b[^>]*/?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\son\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s(?:xlink:)?href\s*=\s*(""(?!#)[^""]*""|'(?!#)[^']*')", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"javascript:", "", RegexOptions.IgnoreCase);
        return s;
    }

    public static string ToHtml(string md)
    {
        var sb = new StringBuilder("<div class=\"md\">");
        var lines = (md ?? "").Replace("\r", "").Split('\n');
        var para = new List<string>();
        var lists = new Stack<(int Indent, string Tag)>();

        void FlushPara() { if (para.Count > 0) { sb.Append("<p>").Append(Inline(string.Join(" ", para))).Append("</p>"); para.Clear(); } }
        void CloseLists(int toIndent = -1) { while (lists.Count > 0 && lists.Peek().Indent > toIndent) sb.Append("</li></").Append(lists.Pop().Tag).Append('>'); }

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i], line = raw.TrimEnd();
            string trimmed = line.TrimStart();

            // fenced code / svg
            var fence = Regex.Match(trimmed, @"^```\s*([\w+-]*)\s*$");
            if (fence.Success)
            {
                FlushPara(); CloseLists();
                var block = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && !lines[j].TrimStart().StartsWith("```"); j++) block.Add(lines[j]);
                i = j;
                string body = string.Join("\n", block), lang = fence.Groups[1].Value.ToLowerInvariant();
                string svg = lang is "svg" or "xml" or "html" or "" && body.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ? SafeSvg(body) : null;
                if (svg != null) sb.Append("<div class=\"fig\">").Append(svg).Append("</div>");
                else sb.Append("<pre><code>").Append(E(body)).Append("</code></pre>");
                continue;
            }
            // a bare <svg> ... </svg> outside a fence
            if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            {
                int j = i; var block = new List<string>();
                for (; j < lines.Length; j++) { block.Add(lines[j]); if (lines[j].Contains("</svg>", StringComparison.OrdinalIgnoreCase)) break; }
                string svg = SafeSvg(string.Join("\n", block));
                if (svg != null) { FlushPara(); CloseLists(); sb.Append("<div class=\"fig\">").Append(svg).Append("</div>"); i = j; continue; }
            }
            if (trimmed.Length == 0) { FlushPara(); if (i + 1 < lines.Length && !Regex.IsMatch(lines[i + 1], @"^\s*(?:[-*•]|\d+[.)])\s+")) CloseLists(); continue; }
            if (Regex.IsMatch(trimmed, @"^(?:---+|\*\*\*+|___+)$")) { FlushPara(); CloseLists(); sb.Append("<hr>"); continue; }

            var h = Regex.Match(trimmed, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (h.Success) { FlushPara(); CloseLists(); int lv = Math.Min(4, h.Groups[1].Length); sb.Append("<h").Append(lv).Append('>').Append(Inline(h.Groups[2].Value)).Append("</h").Append(lv).Append('>'); continue; }

            // table: a | row followed by a |---| row
            if (trimmed.StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$"))
            {
                FlushPara(); CloseLists();
                static string[] Cells(string r) => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                sb.Append("<table><thead><tr>");
                foreach (var c in Cells(trimmed)) sb.Append("<th>").Append(Inline(c)).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                int j = i + 2;
                for (; j < lines.Length && lines[j].TrimStart().StartsWith("|"); j++)
                {
                    sb.Append("<tr>");
                    foreach (var c in Cells(lines[j])) sb.Append("<td>").Append(Inline(c)).Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                i = j - 1;
                continue;
            }

            var li = Regex.Match(line, @"^(\s*)(?:([-*•])|(\d+)[.)])\s+(.*)$");
            if (li.Success)
            {
                FlushPara();
                int indent = li.Groups[1].Value.Replace("\t", "    ").Length;
                string tag = li.Groups[3].Success ? "ol" : "ul";
                if (lists.Count == 0 || indent > lists.Peek().Indent) { sb.Append('<').Append(tag).Append("><li>"); lists.Push((indent, tag)); }
                else
                {
                    CloseLists(indent);
                    if (lists.Count > 0 && lists.Peek().Indent == indent) sb.Append("</li><li>");
                    else { sb.Append('<').Append(tag).Append("><li>"); lists.Push((indent, tag)); }
                }
                sb.Append(Inline(li.Groups[4].Value));
                continue;
            }
            if (trimmed.StartsWith(">")) { FlushPara(); CloseLists(); sb.Append("<blockquote>").Append(Inline(trimmed.TrimStart('>', ' '))).Append("</blockquote>"); continue; }

            if (lists.Count > 0 && raw.StartsWith(" ")) { sb.Append(' ').Append(Inline(trimmed)); continue; }   // continuation of a list item
            CloseLists();
            para.Add(trimmed);
        }
        FlushPara(); CloseLists();
        sb.Append("</div>");
        return sb.ToString();
    }

    public static string Plain(string md)
    {
        string t = Regex.Replace(md ?? "", @"```.*?```", " ", RegexOptions.Singleline);
        t = Regex.Replace(t, @"<svg\b.*?</svg>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"(?m)^\s*(?:#{1,6}|[-*•]|\d+[.)]|>)\s+", "");
        t = Regex.Replace(t, @"[*_`|]+", "");
        t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }
}
