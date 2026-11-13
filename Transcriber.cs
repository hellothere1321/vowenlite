using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VowenLite;

// Runs whisper-cli once per track (child process, so the model's memory goes away with it),
// then merges both tracks by timestamp into transcript.md.
static class Transcriber
{
    static readonly (string File, string Speaker)[] Tracks = { ("mic", "You"), ("system", "Others") };

    public record Segment(long FromMs, long ToMs, string Speaker, string Text, string Marked = null);

    sealed class SnapState
    {
        public Dictionary<string, long> Bytes { get; set; } = new();
        public List<Segment> Segs { get; set; } = new();
    }

    // stillLive != null = mid-meeting snapshot: transcribes only the audio added since the last snapshot (copied out
    // of the still-growing WAVs, recording untouched, WAVs kept) and appends it to transcript.md; it writes only if
    // stillLive() is still true when it finishes (a late snapshot can never overwrite the final transcript).
    // Returns null if it skipped writing. The final (non-partial) run redoes everything and deletes snap-state.json.
    public static async Task<string> RunAsync(string folder, Settings s, IProgress<string> progress, Func<bool> stillLive = null)
    {
        foreach (var path in new[] { s.WhisperPath, s.ModelPath })
            if (!File.Exists(path)) throw new FileNotFoundException("Missing " + path);

        bool partial = stillLive != null;
        var items = new List<(Segment Seg, List<Lexicon.W> Words)>();
        // Snapshots are incremental: snap-state.json keeps the earlier segments and how many PCM bytes of each
        // track are already transcribed; only the new audio goes through whisper (16 kHz mono 16-bit = 32000 B/s).
        string statePath = Path.Combine(folder, "snap-state.json");
        var state = new SnapState();
        if (partial) { try { state = JsonSerializer.Deserialize<SnapState>(File.ReadAllText(statePath)) ?? new SnapState(); } catch { } }
        var newBytes = new Dictionary<string, long>(state.Bytes);
        int i = 0;
        foreach (var (file, speaker) in Tracks)
        {
            i++;
            string wav = Path.Combine(folder, file + ".wav");
            if (!File.Exists(wav) || new FileInfo(wav).Length <= 44) continue;
            string label = $"Transcribing {speaker.ToLowerInvariant()} ({i}/{Tracks.Length})";
            string input = wav, outBase = Path.Combine(folder, file), tmp = null;
            long offsetMs = 0;
            if (partial)
            {
                long from = state.Bytes.GetValueOrDefault(file), len;
                byte[] chunk;
                using (var src = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    len = src.Length - 44; len -= len % 2;
                    if (len - from < 32000 * 4) continue;   // under 4 s of new audio: wait for more
                    src.Position = 44 + from;
                    chunk = new byte[len - from];
                    int got = 0;
                    while (got < chunk.Length) { int n = await src.ReadAsync(chunk.AsMemory(got)); if (n <= 0) break; got += n; }
                    if (got < chunk.Length) Array.Resize(ref chunk, got - got % 2);
                }
                tmp = Path.Combine(folder, "snap-" + file + ".wav");
                using (var w = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    var h = new byte[44];
                    void P(int at, string t) { for (int k = 0; k < t.Length; k++) h[at + k] = (byte)t[k]; }
                    P(0, "RIFF"); BitConverter.GetBytes(36 + chunk.Length).CopyTo(h, 4); P(8, "WAVEfmt ");
                    BitConverter.GetBytes(16).CopyTo(h, 16); BitConverter.GetBytes((short)1).CopyTo(h, 20); BitConverter.GetBytes((short)1).CopyTo(h, 22);
                    BitConverter.GetBytes(16000).CopyTo(h, 24); BitConverter.GetBytes(32000).CopyTo(h, 28);
                    BitConverter.GetBytes((short)2).CopyTo(h, 32); BitConverter.GetBytes((short)16).CopyTo(h, 34);
                    P(36, "data"); BitConverter.GetBytes(chunk.Length).CopyTo(h, 40);
                    await w.WriteAsync(h); await w.WriteAsync(chunk);
                }
                input = tmp; outBase = Path.Combine(folder, "snap-" + file); offsetMs = from / 32;
                newBytes[file] = from + chunk.Length;
            }
            progress.Report(label + (partial ? " (new audio only)..." : "..."));
            string json;
            try { json = await Whisper(input, outBase, s, pct => progress.Report($"{label}: {pct}%")); }
            finally { if (tmp != null) try { File.Delete(tmp); } catch { } }
            foreach (var it in ParseWords(File.ReadAllText(json), speaker, s))
                items.Add((it.Seg with { FromMs = it.Seg.FromMs + offsetMs, ToMs = it.Seg.ToMs + offsetMs }, it.Words));
            File.Delete(json);
        }

        // Spellings of one name are made consistent across the whole meeting, then the same mis-hearing fixes as
        // dictation (names, product spellings) run. Words whisper was unsure about are kept for Codex's correction pass.
        var lists = items.Select(x => x.Words).ToList();
        Lexicon.Canonicalize(lists, s);
        var flagged = Lexicon.Flagged(lists, lists.Sum(l => l.Count));
        var segs = items.Select(x => x.Seg with
        {
            Text = Vocab.Replace(Lexicon.Join(x.Words), s),
            Marked = Vocab.Replace(Lexicon.JoinMarked(x.Words, flagged), s),
        }).ToList();

        string md = Path.Combine(folder, "transcript.md");
        if (partial)
        {
            if (!stillLive()) return null;
            var all = new List<Segment>(state.Segs);
            all.AddRange(segs.Select(g => g with { Marked = null }));
            File.WriteAllText(statePath, JsonSerializer.Serialize(new SnapState { Bytes = newBytes, Segs = all }));
            File.WriteAllText(md, Format(folder, new List<Segment>(all)));
            progress.Report("Transcript so far saved");
            return md;
        }
        try { File.Delete(statePath); } catch { }
        File.WriteAllText(md, Format(folder, segs));
        // A fresh transcript invalidates Codex's earlier correction input (transcript.raw.md) and the flagged copy.
        string flags = Path.Combine(folder, "transcript.flags.md"), rawCopy = Path.Combine(folder, "transcript.raw.md");
        try { File.Delete(rawCopy); } catch { }
        try { if (flagged.Count > 0) File.WriteAllText(flags, Format(folder, segs, true)); else File.Delete(flags); } catch { }
        if (!s.KeepWavs)
            foreach (var (file, _) in Tracks) File.Delete(Path.Combine(folder, file + ".wav"));
        progress.Report("Transcript saved");
        return md;
    }

    static async Task<string> Whisper(string wav, string outBase, Settings s, Action<int> onPct)
    {
        var psi = new ProcessStartInfo(s.WhisperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(s.WhisperPath),
        };
        foreach (var a in new[] { "-m", s.MeetingModel(), "-f", wav, "-l", s.Language, "-t", s.Threads.ToString(), "-ojf", "-of", outBase, "-np", "-pp", "-bs", "5" })
            psi.ArgumentList.Add(a);
        foreach (var a in s.PromptArgs()) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--carry-initial-prompt");   // re-apply the glossary to every 30 s window (default: first window only)
        if (File.Exists(s.VadModelPath))
            foreach (var a in new[] { "--vad", "-vm", s.VadModelPath }) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var err = new StringBuilder();
        var pctRx = new Regex(@"progress\s*=\s*(\d+)%");
        DataReceivedEventHandler onLine = (_, e) =>
        {
            if (e.Data == null) return;
            var m = pctRx.Match(e.Data);
            if (m.Success) onPct(int.Parse(m.Groups[1].Value));
            else lock (err) { if (err.Length < 4000) err.AppendLine(e.Data); }
        };
        p.OutputDataReceived += onLine;
        p.ErrorDataReceived += onLine;
        p.Start();
        Jobs.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();

        string json = outBase + ".json";
        if (p.ExitCode != 0 || !File.Exists(json))
            throw new Exception($"whisper-cli failed (exit {p.ExitCode}):\n{err.ToString().Trim()}");
        return json;
    }

    static readonly Regex Noise = new(@"^\s*[\[\(][^\]\)]*[\]\)]\s*$"); // [BLANK_AUDIO], (music), ...

    // Like Parse, but keeps whisper's per-word confidence and snaps unsure words to known names (Lexicon).
    static List<(Segment Seg, List<Lexicon.W> Words)> ParseWords(string json, string speaker, Settings s)
    {
        var list = new List<(Segment, List<Lexicon.W>)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transcription", out var arr)) return list;
        foreach (var t in arr.EnumerateArray())
        {
            string text = t.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length == 0 || Noise.IsMatch(text)) continue;
            var words = Lexicon.FromSegment(t);
            Lexicon.Snap(words, s);
            if (words.Count == 0) continue;
            var off = t.GetProperty("offsets");
            list.Add((new Segment(off.GetProperty("from").GetInt64(), off.GetProperty("to").GetInt64(), speaker, ""), words));
        }
        return list;
    }

    public static List<Segment> Parse(string json, string speaker)
    {
        var list = new List<Segment>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transcription", out var arr)) return list;
        foreach (var t in arr.EnumerateArray())
        {
            string text = t.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length == 0 || Noise.IsMatch(text)) continue;
            var off = t.GetProperty("offsets");
            list.Add(new Segment(off.GetProperty("from").GetInt64(), off.GetProperty("to").GetInt64(), speaker, text));
        }
        return list;
    }

    // Sorted by start; consecutive segments from one speaker are joined into a line
    // until a 2 s pause or 30 s of speech.
    public static string Format(string folder, List<Segment> segs, bool marked = false)
    {
        var sb = new StringBuilder();
        string title = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (DateTime.TryParseExact(title, "yyyy-MM-dd_HH-mm", null, System.Globalization.DateTimeStyles.None, out var when))
            title = when.ToString("yyyy-MM-dd HH:mm");
        sb.Append("# Meeting ").AppendLine(title).AppendLine();
        if (segs.Count == 0) { sb.AppendLine("_No speech found._"); return sb.ToString(); }

        segs.Sort((a, b) => a.FromMs.CompareTo(b.FromMs));
        Segment start = null, last = null;
        var line = new StringBuilder();
        void Flush()
        {
            if (start == null) return;
            sb.Append('[').Append(Stamp(start.FromMs)).Append("] ").Append(start.Speaker).Append(": ").AppendLine(line.ToString()).AppendLine();
            line.Clear();
        }
        foreach (var s in segs)
        {
            bool join = last != null && s.Speaker == last.Speaker && s.FromMs - last.ToMs < 2000 && s.FromMs - start.FromMs < 30000;
            if (!join) { Flush(); start = s; }
            else line.Append(' ');
            line.Append(marked ? s.Marked ?? s.Text : s.Text);
            last = s;
        }
        Flush();
        return sb.ToString();
    }

    public static string Stamp(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
