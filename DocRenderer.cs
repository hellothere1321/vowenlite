using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Turns summary.md + transcript.md into RTF for the read-only viewer: small caps section labels,
// bullets, and the transcript as a [time] [speaker] [text] table with a hanging indent.
static class DocRenderer
{
    // Colour table indexes
    const int Ink = 1, Text2 = 2, Soft = 3, Faint = 4, Ice = 5, Err = 6;
    const int RightPad = 700;                 // twips kept clear on the right
    const int TimeCol = 0, SpeakerCol = 1060, TextCol = 2000;

    static readonly Regex Turn = new(@"^\[(\d+(?::\d+)+)\]\s*(You|Others)\s*:\s*(.*)$");
    static readonly Regex Inline = new(@"\*\*(.+?)\*\*|`(.+?)`|(?<![*\w])\*(?!\s)(.+?)(?<!\s)\*(?![*\w])");
    static readonly Regex Heading = new(@"^#{1,6}\s+(.*)$");
    static readonly Regex Bullet = new(@"^\s*(?:[-*•]|(\d+)[.)])\s+(.*)$");

    static string Head() =>
        @"{\rtf1\ansi\ansicpg1252\uc1\deff0{\fonttbl{\f0 " + Theme.Body + @";}{\f1 " + Theme.BodyBold + @";}{\f2 " + Theme.Display + @";}{\f3 " + Theme.Mono + @";}}" +
        @"{\colortbl ;" + C(Theme.Ink) + C(Theme.Text2) + C(Theme.Soft) + C(Theme.Faint) + C(Theme.Ice) + C(Theme.Err) + "}";

    static string C(Color c) => $@"\red{c.R}\green{c.G}\blue{c.B};";

    public static string Message(string title, string sub) =>
        Head() + @"\pard\qc\sb2400\sa120\ri0\f2\fs40\cf1 " + Esc(title) + @"\par" +
        @"\pard\qc\sl320\slmult1\f0\fs22\cf3 " + Esc(sub) + @"\par}";

    // Markdown reply (Luna): paragraphs and bullets, no section labels unless the text has headings.
    public static string Answer(string md)
    {
        var sb = new StringBuilder(Head());
        bool first = true;
        Summary(sb, md, ref first, false);
        sb.Append('}');
        return sb.ToString();
    }

    public static string Document(string summaryMd, string transcriptMd)
    {
        var sb = new StringBuilder(Head());
        bool first = true;
        if (!string.IsNullOrWhiteSpace(summaryMd)) { Summary(sb, summaryMd, ref first); }
        if (!string.IsNullOrWhiteSpace(transcriptMd)) { Transcript(sb, transcriptMd, ref first); }
        sb.Append('}');
        return sb.ToString();
    }

    static void Label(StringBuilder sb, string text, ref bool first)
    {
        sb.Append(@"\pard\ri").Append(RightPad).Append(@"\sb").Append(first ? 120 : 360).Append(@"\sa200\keepn\f1\fs17\cf4\expndtw24 ")
          .Append(Esc(text.ToUpperInvariant())).Append(@"\expndtw0\par");
        first = false;
    }

    static void Summary(StringBuilder sb, string md, ref bool first, bool autoLabel = true)
    {
        bool hadLabel = false;
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || Regex.IsMatch(line, @"^\s*(---+|\*\*\*+)\s*$")) continue;
            var h = Heading.Match(line);
            if (h.Success) { Label(sb, Plain(h.Groups[1].Value), ref first); hadLabel = true; continue; }
            if (!hadLabel && autoLabel) { Label(sb, "Summary", ref first); hadLabel = true; }
            var b = Bullet.Match(line);
            if (b.Success)
            {
                string mark = b.Groups[1].Success ? b.Groups[1].Value + "." : "\u2022";
                sb.Append(@"\pard\ri").Append(RightPad).Append(@"\li340\fi-340\tx340\sl336\slmult1\sa140\f0\fs22\cf2 ")
                  .Append(@"{\cf4 ").Append(Esc(mark)).Append(@"}\tab ").Append(Rich(Tidy(b.Groups[2].Value), Text2)).Append(@"\par");
            }
            else
                sb.Append(@"\pard\ri").Append(RightPad).Append(@"\sl336\slmult1\sa160\f0\fs22\cf2 ").Append(Rich(Tidy(line), Text2)).Append(@"\par");
        }
    }

    static void Transcript(StringBuilder sb, string md, ref bool first)
    {
        Label(sb, "Transcript", ref first);
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("# ")) continue;
            var m = Turn.Match(line);
            if (!m.Success)
            {
                sb.Append(@"\pard\ri").Append(RightPad).Append(@"\sl336\slmult1\sa160\f0\fs22\cf3\i ").Append(Rich(Plain(line), Soft)).Append(@"\i0\par");
                continue;
            }
            string text = m.Groups[3].Value.Trim();
            if (text.Length == 0) continue;
            bool you = m.Groups[2].Value == "You";
            sb.Append(@"\pard\ri").Append(RightPad).Append(@"\li").Append(TextCol).Append(@"\fi-").Append(TextCol)
              .Append(@"\tx").Append(SpeakerCol).Append(@"\tx").Append(TextCol).Append(@"\sl336\slmult1\sa200 ")
              .Append(@"{\f3\fs17\cf4 ").Append(Esc(m.Groups[1].Value)).Append(@"}\tab ")
              .Append(@"{\f1\fs20\cf").Append(you ? Ice : Soft).Append(' ').Append(m.Groups[2].Value).Append(@"}\tab ")
              .Append(@"{\f0\fs22\cf").Append(you ? Ink : Text2).Append(' ').Append(Rich(text, you ? Ink : Text2)).Append(@"}\par");
        }
    }

    // **bold**, *italic*, `code` inside a line. `baseColour` is restored after each span.
    static string Rich(string text, int baseColour)
    {
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in Inline.Matches(text))
        {
            sb.Append(Esc(text.Substring(pos, m.Index - pos)));
            if (m.Groups[1].Success) sb.Append(@"{\f1\cf").Append(Ink).Append(' ').Append(Esc(m.Groups[1].Value)).Append('}');
            else if (m.Groups[2].Success) sb.Append(@"{\f3\fs20\cf").Append(Soft).Append(' ').Append(Esc(m.Groups[2].Value)).Append('}');
            else sb.Append(@"{\i\cf").Append(Faint).Append(' ').Append(Esc(m.Groups[3].Value)).Append('}');
            pos = m.Index + m.Length;
        }
        sb.Append(Esc(text.Substring(pos)));
        return sb.ToString();
    }

    static string Plain(string s) => Inline.Replace(s, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);

    static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
            else if (c < 32) { if (c == '\t') sb.Append(' '); }
            else if (c > 126) sb.Append(@"\u").Append((short)c).Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // Codex sometimes writes LaTeX (\(\tfrac{3}{4}g\sin\theta\)); make it readable plain text.
    static readonly Dictionary<string, string> Latex = new()
    {
        ["theta"] = "θ", ["phi"] = "φ", ["mu"] = "μ", ["omega"] = "ω", ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
        ["Delta"] = "Δ", ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["lambda"] = "λ", ["cdot"] = "·", ["times"] = "×",
        ["leq"] = "≤", ["geq"] = "≥", ["neq"] = "≠", ["approx"] = "≈", ["rightarrow"] = "→", ["to"] = "→", ["sqrt"] = "√", ["pm"] = "±",
    };

    static string Tidy(string s)
    {
        s = Regex.Replace(s, @"\\\((.+?)\\\)|\\\[(.+?)\\\]|(?<![\w$])\$([^$\n]+?)\$(?![\w$])", m =>
        {
            string t = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            t = Regex.Replace(t, @"\\[td]?frac\s*\{([^{}]*)\}\s*\{([^{}]*)\}", "$1/$2");
            t = Regex.Replace(t, @"\\(?:text|mathrm|mathbf|operatorname)\s*\{([^{}]*)\}", "$1");
            t = Regex.Replace(t, @"\\([A-Za-z]+)", x => " " + (Latex.TryGetValue(x.Groups[1].Value, out var g) ? g : x.Groups[1].Value) + " ");
            t = Regex.Replace(t, @"[_^]\{([^{}]*)\}", "$1").Replace("{", "").Replace("}", "").Replace("_", "");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return Regex.Replace(t, @"\s+([)\],.;:])", "$1");
        });
        return s;
    }
}
