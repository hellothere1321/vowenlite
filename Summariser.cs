using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VowenLite;

// Summaries come from the Codex app's own CLI (`codex exec`, your ChatGPT login), never from
// this app. The transcript goes in on stdin; the reply is written to summary.md.
static class Summariser
{
    public static string FindCodex(Settings s)
    {
        if (File.Exists(s.CodexPath)) return s.CodexPath;
        // The Codex app keeps its CLI under %LOCALAPPDATA%\OpenAI\Codex\bin\<build hash>\codex.exe.
        string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bin))
        {
            var newest = Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (newest != null) return newest;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), "codex.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    const string Context =
        "\"You\" is me (my mic); \"Others\" is everyone else (computer audio). ";

    public static string Prompt(string transcript) =>
        "Write detailed, thorough meeting notes from this transcript as markdown. Sections: ## Summary (a full paragraph or two: " +
        "what the meeting was about, who said what, how it went), ## Discussion (a detailed section per topic discussed, in order, " +
        "with the specifics: numbers, names, dates, reasoning, objections, examples, open questions), ## Key points, " +
        "## Decisions, ## Action items (owner and due date when stated). Err on the side of too much detail rather than too little; " +
        "do not compress away specifics, and attribute points to You / Others where it matters. " + Context +
        "Leave out empty sections. Reply with only the markdown, no preamble.\n\n" + transcript;

    static string CleanPrompt(string transcript, string vocabulary, string known = "", bool marked = false) =>
        (known.Length > 0 ? "Names and terms this user's meetings really contain (prefer these spellings when a garbled word sounds like one): " + known + ". " : "") +
        (marked ? "Words wrapped in ⟦ ⟧ are ones the speech-to-text model was unsure about: check those first, fix them if wrong, and always remove the ⟦ ⟧ marks from your output. " : "") +
        "Below is a meeting transcript from a small speech-to-text model. Fix OBVIOUS transcription errors only: wrong homophones, " +
        "mangled names/products/games/tech terms, broken words, missing or wrong punctuation and capitalisation, and obvious " +
        "hallucinations (repeated lines, stray 'Thank you.' / 'Thanks for watching' on silence). Use context to decide what was " +
        "actually said. Do NOT summarise, reword, merge, reorder or add content; if unsure, leave the text as is. Keep every " +
        "timestamp and speaker label exactly as given, one entry per original line, and keep the '# Meeting' heading. " + Context +
        "Reply with only the corrected transcript, no preamble. Then, as the very last line, add `VOCAB: term1, term2, ...` " +
        "listing only capitalised proper nouns (people, places, brands, products, games, shows) you had to correct that the " +
        "speech-to-text will likely get wrong again (spelled correctly, at most 10; no ordinary words or phrases, subject terms, " +
        "equations or symbols; skip ones already in: " + vocabulary + "). Omit that line if there are none.\n\n" + transcript;

    // Pulls the trailing "VOCAB:" line out of the reply and merges its terms into Settings.Vocabulary.
    // Whisper's prompt only holds ~224 tokens, so keep the newest ~600 characters.
    static string TakeVocab(string text, Settings s)
    {
        var lines = text.TrimEnd().Split('\n').ToList();
        int i = lines.FindLastIndex(l => l.TrimStart('`', ' ', '*').StartsWith("VOCAB:", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return text;
        var terms = lines[i].Trim('`', ' ', '*', '\r').Substring(lines[i].IndexOf(':') + 1)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('`', '*', ' ', '.', '\r')).Where(Settings.GoodTerm).ToList();
        lines.RemoveAt(i);
        var all = s.Vocabulary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var t in terms)
            if (!all.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) all.Add(t);
        while (string.Join(", ", all).Length > 400 && all.Count > 1) all.RemoveAt(0);
        string joined = string.Join(", ", all);
        if (joined != s.Vocabulary) { s.Vocabulary = joined; s.Save(); }
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    public static async Task RunAsync(string folder, Settings s)
    {
        string codex = FindCodex(s) ?? throw new FileNotFoundException("Codex CLI not found. Is the Codex app installed?");
        string transcriptPath = Path.Combine(folder, "transcript.md"), raw = Path.Combine(folder, "transcript.raw.md");
        // The first run keeps the untouched whisper output; later runs always correct from that original.
        if (!File.Exists(raw)) File.Copy(transcriptPath, raw);
        string original = File.ReadAllText(raw);

        // transcript.flags.md = the same transcript with the words whisper was unsure about marked ⟦like this⟧ (Transcriber).
        string flagsPath = Path.Combine(folder, "transcript.flags.md");
        bool marked = File.Exists(flagsPath);
        string input = marked ? File.ReadAllText(flagsPath) : original;

        string transcript = original;
        try
        {
            string fixedText = TakeVocab(await Codex(codex, folder, s, CleanPrompt(input, s.Vocabulary, Lexicon.KnownList(s), marked), false), s);
            fixedText = fixedText.Replace("⟦", "").Replace("⟧", "");
            // Sanity check: a correction pass must not lose lines.
            if (Regex.Matches(fixedText, @"(?m)^\[").Count >= Regex.Matches(original, @"(?m)^\[").Count * 0.9)
            {
                transcript = fixedText;
                File.WriteAllText(transcriptPath, fixedText);
                // Every name Codex had to fix teaches the free fixer (learned.json): used by dictation and the next meetings.
                try { Lexicon.Learn(original, fixedText); } catch { }
            }
        }
        catch { } // keep the raw transcript and still summarise

        File.WriteAllText(Path.Combine(folder, "summary.md.tmp"), await Codex(codex, folder, s, Prompt(transcript), true));
        File.Move(Path.Combine(folder, "summary.md.tmp"), Path.Combine(folder, "summary.md"), true);
    }

    // "Ask AI" in the meeting window: answers a question from transcript.md (+ summary.md) of one meeting.
    // `history` = the earlier Q&A of this window session so follow-ups work.
    // `all` = answer from the notes of the recent meetings instead of one transcript ("when did we last talk about the budget?").
    public static async Task<string> AskAsync(string folder, Settings s, string question, string history, bool all = false, CancellationToken ct = default)
    {
        string codex = FindCodex(s) ?? throw new FileNotFoundException("Codex CLI not found. Is the Codex app installed?");
        string tp = Path.Combine(folder, "transcript.md"), sp = Path.Combine(folder, "summary.md");
        if (!all && !File.Exists(tp)) throw new FileNotFoundException("This meeting has no transcript yet.");
        var sb = new StringBuilder();
        sb.AppendLine(all
            ? "You answer questions about the user's recorded meetings, from the notes of their most recent ones below (newest first, each headed by its date and folder name). Use only these notes; do not run commands or browse. Say which meeting (date) each point comes from."
            : "You answer questions about ONE meeting the user recorded. Use only the transcript and notes below; do not run commands or browse.");
        if (!all)
        {
            sb.AppendLine("Lines in the transcript are '[mm:ss] You/Others: text' (You = the user's mic, Others = everyone else on the call). The transcript may contain speech-recognition errors; use judgement.");
            sb.AppendLine("The meeting may still be in progress, in which case the transcript is only what was said so far; say so if the answer could depend on what comes later.");
        }
        sb.AppendLine("Format: the answer is shown in a panel that renders markdown. Lead with the direct answer in one sentence, then the supporting detail as short bullets (a table when comparing, **bold** for names and numbers). " +
            "Cite the [mm:ss] timestamp of the moment each point comes from. Action items: one bullet each, starting with the task itself, then owner and due date when stated. " +
            "When asked to draft something (a message, an email), write it ready to send. No LaTeX, no preamble, no closing offer. If the meeting does not say, say so instead of guessing.");
        sb.AppendLine();
        if (all)
        {
            int budget = 60_000;
            foreach (var dir in Directory.GetDirectories(Paths.MeetingsDir).OrderByDescending(d => Directory.GetCreationTime(d)).Take(25))
            {
                string notes = Path.Combine(dir, "summary.md"), tr = Path.Combine(dir, "transcript.md");
                string body = File.Exists(notes) ? File.ReadAllText(notes).Trim() : File.Exists(tr) ? File.ReadAllText(tr).Trim() : null;
                if (body == null) continue;
                if (body.Length > 6000) body = body.Substring(0, 6000) + "\n[cut]";
                if ((budget -= body.Length) < 0) break;
                sb.AppendLine($"=== MEETING {Directory.GetCreationTime(dir):yyyy-MM-dd HH:mm} ({Path.GetFileName(dir)}){(string.Equals(dir, folder, StringComparison.OrdinalIgnoreCase) ? " [the one open now]" : "")} ===");
                sb.AppendLine(body); sb.AppendLine();
            }
        }
        else
        {
            if (File.Exists(sp)) { sb.AppendLine("=== NOTES (earlier summary) ==="); sb.AppendLine(File.ReadAllText(sp).Trim()); sb.AppendLine(); }
            sb.AppendLine("=== TRANSCRIPT ===");
            sb.AppendLine(File.ReadAllText(tp).Trim());
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(history)) { sb.AppendLine("=== EARLIER QUESTIONS IN THIS CONVERSATION ==="); sb.AppendLine(history.Trim()); sb.AppendLine(); }
        sb.AppendLine("=== QUESTION ===");
        sb.AppendLine(question.Trim());
        return (await Codex(codex, folder, s, sb.ToString(), false, "low", ct, light: true)).Trim();
    }

    static async Task<string> Codex(string codex, string folder, Settings s, string prompt, bool keepChat, string effort = null, CancellationToken ct = default, bool light = false)
    {
        string tmp = Path.Combine(folder, "codex-out-" + Guid.NewGuid().ToString("N") + ".tmp");
        var psi = new ProcessStartInfo(codex)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = folder,
        };
        // The summary pass is saved as a normal Codex chat (so it shows in the app); the transcript-fix pass is hidden.
        var args = new List<string> { "exec", "--skip-git-repo-check" };
        if (!keepChat) args.Add("--ephemeral");
        if (effort != null) args.AddRange(new[] { "-c", "model_reasoning_effort=\"" + effort + "\"" });
        // light (Ask AI): answer from the prompt alone, without the Codex app's plugins/rules or shell tools: faster and ~5k fewer tokens per question
        if (light && s.LunaLight) args.AddRange(new[] { "--ignore-user-config", "--ignore-rules", "-c", "windows.sandbox=\"elevated\"", "--disable", "shell_tool", "--disable", "unified_exec" });
        args.AddRange(new[] { "-s", "read-only", "-m", s.CodexModel, "-C", folder, "-o", tmp, "-" });
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var log = new StringBuilder();
        DataReceivedEventHandler keep = (_, e) => { if (e.Data != null) lock (log) { log.AppendLine(e.Data); if (log.Length > 8000) log.Remove(0, 4000); } };
        p.OutputDataReceived += keep;
        p.ErrorDataReceived += keep;
        p.Start();
        Jobs.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(prompt);
        p.StandardInput.Close();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))   // Stop in the Ask panel
            await p.WaitForExitAsync();

        try
        {
            ct.ThrowIfCancellationRequested();
            if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                string tail = log.ToString().Trim();
                if (tail.Length > 1500) tail = "..." + tail.Substring(tail.Length - 1500);
                throw new Exception($"Codex failed (exit {p.ExitCode}):\n{tail}");
            }
            return File.ReadAllText(tmp);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
