using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VowenLite;

// The ArtCraft suite (EffectCraft = motion graphics, PhotoCraft = image editing, VectorCraft = vector art, FigCraft = UI design,
// PdfCraft = PDFs) through each app's own command-line: Luna's Codex runs `node vpc.js craft <app> <cli args>` and the host runs
// <app>-cli.exe as the user (GPU, fonts and the user's files all work there; the sandbox account has none of that).
// Work lands in Documents\ArtCraft. Server / plugin / benchmark subcommands are not passed through.
static class Craft
{
    static readonly (string Key, string Folder, string Exe, string What)[] Apps =
    {
        ("effect", "EffectCraft", "effectcraft-cli.exe", "motion graphics and video (After Effects-style comps, layers, keyframes, 300+ effects; renders mp4/gif/png)"),
        ("photo", "PhotoCraft", "photocraft-cli.exe", "image editing (Photoshop-style layers, adjustments, filters; reads/writes psd, png, jpg, webp, tif)"),
        ("vector", "VectorCraft", "vectorcraft-cli.exe", "vector art (Illustrator-style: logos, icons, posters; reads/writes svg, pdf, ai, png)"),
        ("fig", "FigCraft", "figcraft-cli.exe", "UI and layout design (Figma-style frames and components; exports png, svg, pdf)"),
        ("pdf", "PdfCraft", "pdfcraft-cli.exe", "PDFs (text, render pages, combine, split, extract, rotate, delete pages, OCR, optimise, forms)"),
    };

    static string Root(Settings s) => !string.IsNullOrWhiteSpace(s.ArtCraftDir) && Directory.Exists(s.ArtCraftDir) ? s.ArtCraftDir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ArtCraft");

    static string Cli(string key, Settings s)
    {
        foreach (var a in Apps)
            if (a.Key == key) { string p = Path.Combine(Root(s), a.Folder, a.Exe); return File.Exists(p) ? p : null; }
        return null;
    }

    static string KeyFor(string name)
    {
        string n = Regex.Replace(name.ToLowerInvariant(), @"[\s-]|craft|-cli|\.exe", "");
        return n switch { "effect" or "effects" or "affect" or "video" or "motion" => "effect", "photo" or "image" => "photo", "vector" or "svg" => "vector", "fig" or "figma" or "design" => "fig", "pdf" => "pdf", _ => null };
    }

    public static string WorkDir => Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArtCraft")).FullName;

    // An app named out loud ("effect craft" is often heard as "affect craft"), or a job that is plainly one of theirs.
    static readonly Regex AskRx = new(
        @"\bart[\s-]?craft\b|\b(?:(?:e|a)ffects?|photo|vector|fig|pdf)[\s-]?craft\b|\bmotion\s+graphics?\b|\blower\s+third\b|\bafter\s+effects\b|\b(?:title|intro|outro)\s+(?:card|animation|sequence)\b" +
        @"|\b(?:make|design|create|draw|build)\b.{0,40}\b(?:logo|icon|poster|banner|thumbnail|flyer|svg|vector|mock-?up|wireframe)\b" +
        @"|\b(?:merge|combine|split|extract|rotate|compress|optimi[sz]e|ocr|convert)\b.{0,40}\bpdfs?\b|\bpdfs?\b.{0,40}\b(?:merge|combine|split|extract|rotate|compress|ocr|pages?)\b" +
        @"|\b(?:edit|resize|crop|retouch|convert|remove\s+the\s+background|background\s+remov\w+)\b.{0,40}\b(?:photo|image|picture|png|jpe?g|psd)s?\b|\.(?:ecproj|pcraft|figcraft|psd)\b",
        RegexOptions.IgnoreCase);
    public static bool Ask(string request, Settings s) => s.ArtCraft && AskRx.IsMatch(request) && Apps.Any(a => Cli(a.Key, s) != null);

    public static string Help(Settings s)
    {
        var have = Apps.Where(a => Cli(a.Key, s) != null).ToList();
        if (have.Count == 0) return "";
        return "ARTCRAFT SUITE: the user has these creative apps and you can drive each one headlessly through the host with `node vpc.js craft <app> <args>` (it runs the app's own command-line as the user and prints the real output):\n" +
            string.Join("", have.Select(a => "  craft " + a.Key + " ...   " + a.Folder + ": " + a.What + "\n")) +
            "Every one documents itself: start with `node vpc.js craft <app> --help`, then `<subcommand> --help`, and use its `commands` listing (EffectCraft: `commands --filter TEXT`) to find command ids and parameters instead of guessing. " +
            "Typical shapes: effect `run <id> <json> ... --empty --save-as F.ecproj`, `script --eval \"CODE\" --project F.ecproj --save` (After Effects object model), `render-frame --project F --time 1.5 --max-side 900 --out check.png`, `render --project F --out name.mp4 --format h264`; " +
            "photo `run`/`convert`/`batch`; vector `run [--in FILE] --cmd ID --params JSON ... --export out.svg` and `convert IN OUT`; fig `run`, `export`, `render`; pdf `text`, `combine a.pdf b.pdf --out c.pdf`, `extract`, `split`, `edit`, `render --page N --out p.png`, `batch --steps ocr,optimize`.\n" +
            "The working folder is " + WorkDir + " (relative output paths land there; a file you wrote in your own working folder can be named by its bare file name). Files elsewhere need their full path. " +
            "Work like a designer: build, export a small PNG check image, LOOK at it with your image viewing tool (the host prints a `copy for viewing` path), fix what looks wrong, then make the final export. " +
            "Never overwrite the user's originals: write results as new files. When done put `OPEN: <full path of the result>` in your reply so the host opens it, and say in one sentence what you made.\n\n";
    }

    static readonly string[] Blocked = { "mcp", "serve", "plugin", "connect", "bench", "perf", "droplet" };

    public static (int, string) Run(List<string> args, string cwd)
    {
        var s = Settings.Load();
        string key = args.Count > 0 ? KeyFor(args[0]) : null;
        if (key == null) return (2, "usage: craft <effect|photo|vector|fig|pdf> <args>   (e.g. craft pdf --help)");
        string cli = Cli(key, s);
        if (cli == null) return (127, $"that ArtCraft app ({key}) isn't installed on this PC.");
        args = args.Skip(1).ToList();
        if (args.Count == 0) args.Add("--help");
        if (Blocked.Contains(args[0].ToLowerInvariant())) return (126, $"`{args[0]}` isn't available through the host (project, export and render commands are).");
        foreach (var a in args) if (a.StartsWith("--automation", StringComparison.OrdinalIgnoreCase) || a.Equals("--bridge", StringComparison.OrdinalIgnoreCase) || a.Equals("--connect", StringComparison.OrdinalIgnoreCase)) return (126, "that option isn't available through the host.");
        string work = WorkDir;
        var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in args)
        {
            // a file Codex wrote in its own working folder (script.jsx, steps.json) is given by its full path
            string v = a;
            try { if (!Path.IsPathRooted(a) && a.IndexOfAny(Path.GetInvalidPathChars()) < 0 && a.Length < 200 && cwd != null && File.Exists(Path.Combine(cwd, a))) v = Path.Combine(cwd, a); } catch { }
            psi.ArgumentList.Add(v);
        }
        try
        {
            using var p = Process.Start(psi);
            Jobs.Add(p);
            var outT = p.StandardOutput.ReadToEndAsync(); var errT = p.StandardError.ReadToEndAsync();
            bool slow = args[0].ToLowerInvariant() is "render" or "batch" or "convert";
            if (!p.WaitForExit(slow ? 20 * 60_000 : 3 * 60_000)) { try { p.Kill(true); } catch { } return (124, "the command ran too long and was stopped."); }
            string o = (outT.Result + (errT.Result.Length > 0 ? "\n" + errT.Result : "")).Trim();
            if (o.Length > 9000) o = o.Substring(0, 6000) + "\n…[output trimmed; narrow it with a filter or a more specific command]…\n" + o.Substring(o.Length - 2500);
            // An image result is copied next to Codex so its image viewer can open it (the sandbox can always read its own folder).
            string copied = "";
            try
            {
                if (p.ExitCode == 0 && cwd != null)
                    foreach (var flag in new[] { "--out", "--export" })
                    {
                        int oi = args.FindIndex(a => a == flag);
                        if (oi < 0 || oi + 1 >= args.Count) continue;
                        string src = Path.IsPathRooted(args[oi + 1]) ? args[oi + 1] : Path.Combine(work, args[oi + 1]);
                        if (!Regex.IsMatch(src, @"\.(png|jpe?g|webp)$", RegexOptions.IgnoreCase) || !File.Exists(src) || new FileInfo(src).Length > 12_000_000) continue;
                        string dst = Path.Combine(cwd, Path.GetFileName(src));
                        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) { File.Copy(src, dst, true); copied = "\n[copy for viewing: " + dst + "]"; }
                    }
            }
            catch { }
            Luna.Log($"craft {key}: {string.Join(' ', args.Take(4))} -> exit {p.ExitCode}");
            return (p.ExitCode, o + copied);
        }
        catch (Exception ex) { return (125, "the ArtCraft command-line didn't start: " + ex.Message); }
    }
}
