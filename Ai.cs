using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace VowenLite;

// One-shot model calls for the assistant modules: a prompt (and optionally a picture) in, text out.
// Read-only, ephemeral, light Codex (no plugins, no shell): for extracting and writing, not for doing things on the PC
// (anything that needs the browser or the shell goes through Luna.Ask-style runs instead).
static class Ai
{
    // Tests: when set, Ask returns Fake(prompt) instead of running Codex.
    public static Func<string, string> Fake;

    public static async Task<string> Ask(string prompt, string image = null, string effort = "low", CancellationToken ct = default)
    {
        if (Fake != null) return Fake(prompt);
        var s = Settings.Peek();
        string codex = Summariser.FindCodex(s) ?? throw new FileNotFoundException("Codex CLI not found. Is the Codex app installed?");
        string dir = Directory.CreateDirectory(Path.Combine(Paths.DataDir, "luna", "ai")).FullName;
        string tmp = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N") + ".tmp");
        var psi = new ProcessStartInfo(codex)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
        };
        var args = new List<string> { "exec", "--ephemeral", "--skip-git-repo-check", "-c", $"model_reasoning_effort=\"{effort}\"" };
        if (s.LunaLight) args.AddRange(new[] { "--ignore-user-config", "--ignore-rules", "-c", "windows.sandbox=\"elevated\"", "--disable", "shell_tool", "--disable", "unified_exec" });
        if (image != null && File.Exists(image)) { args.Add("-i"); args.Add(image); }
        args.AddRange(new[] { "-s", "read-only", "-m", s.CodexModel, "-C", dir, "-o", tmp, "-" });
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var log = new StringBuilder();
        DataReceivedEventHandler keep = (_, e) => { if (e.Data != null) lock (log) { log.AppendLine(e.Data); if (log.Length > 8000) log.Remove(0, 4000); } };
        p.OutputDataReceived += keep;
        p.ErrorDataReceived += keep;
        var sw = Stopwatch.StartNew();
        p.Start();
        Jobs.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                string tail = log.ToString().Trim();
                if (tail.Length > 800) tail = "..." + tail.Substring(tail.Length - 800);
                throw new Exception($"Codex failed (exit {p.ExitCode}): {tail}");
            }
            Luna.Log($"ai: {sw.Elapsed.TotalSeconds:0.0}s, prompt {prompt.Length} chars{(image != null ? " + picture" : "")}");
            return File.ReadAllText(tmp).Trim();
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // A picture of the window in front (PrintWindow, its own short-lived process). Null if it failed.
    public static string Screenshot()
    {
        string dir = Directory.CreateDirectory(Path.Combine(Paths.DataDir, "luna", "ai")).FullName;
        return Luna.TakeShot(dir);
    }

    // The first JSON object or array in a reply (models sometimes wrap it in ``` fences or a sentence). Null if there is none.
    public static JsonNode Json(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        for (int i = 0; i < reply.Length; i++)
        {
            char c = reply[i];
            if (c != '{' && c != '[') continue;
            int end = Match(reply, i);
            if (end < 0) continue;
            try { return JsonNode.Parse(reply.Substring(i, end - i + 1)); } catch { }
        }
        return null;
    }

    // Index of the bracket closing the one at `start`, skipping strings. -1 if unbalanced.
    static int Match(string s, int start)
    {
        int depth = 0; bool str = false, esc = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (str) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') str = false; continue; }
            if (c == '"') str = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    public static void SelfChecks(Action<bool, string> check)
    {
        check(Json("Sure:\n```json\n{\"a\": [1, \"}\"]}\n```")?["a"]?[1]?.ToString() == "}", "ai: json inside fences with a brace in a string");
        check(Json("[{\"t\":1}] trailing") is JsonArray { Count: 1 }, "ai: json array");
        check(Json("no json here") == null, "ai: no json");
    }
}
