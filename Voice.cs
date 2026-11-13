using System.Text.RegularExpressions;

namespace VowenLite;

// What happens to dictated text that isn't a "Luna" request: spoken editing commands, snippets,
// "new line"/"new paragraph", then typing. All plain rules, no AI.
static class Voice
{
    static int lastLen;
    static long lastAt;

    static readonly HashSet<string> Undo = new(StringComparer.OrdinalIgnoreCase)
        { "scratch that", "delete that", "undo that", "undo dictation", "remove that", "never mind", "nevermind" };
    static readonly Regex Newline = new(@"\s*\bnew line\b[.,!?]?\s*", RegexOptions.IgnoreCase);
    static readonly Regex Paragraph = new(@"\s*\bnew paragraph\b[.,!?]?\s*", RegexOptions.IgnoreCase);

    public static void Type(string text, Settings s)
    {
        string norm = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim();

        if (Undo.Contains(norm))
        {
            // Erases what the last dictation typed (only if it was recent; focus may have moved).
            if (lastLen > 0 && Environment.TickCount64 - lastAt < 120_000) Native.Backspace(lastLen);
            lastLen = 0;
            return;
        }

        string key = Regex.Replace(norm, @"^(insert|snippet|type)\s+", "");
        foreach (var kv in s.Snippets)
            if (Regex.Replace(kv.Key.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim() == key)
            {
                Send(kv.Value);
                return;
            }

        text = Paragraph.Replace(text, "\n\n");
        text = Newline.Replace(text, "\n");
        Send(text.EndsWith('\n') ? text : text + " ");
    }

    static void Send(string text)
    {
        Native.TypeLines(text);
        lastLen = text.Replace("\r", "").Length;
        lastAt = Environment.TickCount64;
    }
}
