using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace VowenLite;

static class Paths
{
    // Binaries and models sit next to the exe (bin\, models\); settings and logs in LocalAppData.
    public static readonly string AppDir = AppContext.BaseDirectory;
    // VOWENLITE_DATA redirects settings/planner/tokens (used by --plantest so tests never touch real data).
    public static readonly string DataDir = Ensure(Environment.GetEnvironmentVariable("VOWENLITE_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VowenLite"));
    public static string MeetingsDir => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VowenLite", "Meetings"));

    static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    // C1: the sandboxed Codex account (CodexSandboxUsers) inherits Modify on the whole profile tree, so by default it can read
    // vault.json, the browser profile (cookies = live sessions), the Google tokens and daemon.log. We clamp the sensitive files to
    // a non-inherited DACL of {current user, SYSTEM, Administrators} only. The luna\ subfolder is deliberately NOT clamped: the
    // sandbox legitimately works there. Idempotent and best-effort (a failure just leaves the inherited ACL).
    static readonly string[] SecretFiles = { "vault.json", "browser-sites.json", "google-token.json", "google-client.json", "daemon.log" };
    public static readonly string ProfileDir = Path.Combine(DataDir, "browser-profile");

    static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User;
    static readonly SecurityIdentifier Sys = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    static void AddOwners<T>(T rules) where T : FileSystemSecurity
    {
        InheritanceFlags inh = rules is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { Me, Sys, Admins })
            rules.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
    }

    // Lock one path (file or directory) to the three trusted principals, inheritance disabled. Creates an empty file first so the
    // DACL is in place before anything writes secrets into it; for the browser profile, call this BEFORE WebView2 populates it.
    public static void Secure(string path, bool dir)
    {
        try
        {
            if (dir) Directory.CreateDirectory(path);
            else if (!File.Exists(path)) File.WriteAllText(path, "");
            if (dir)
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);   // drop inherited rules
                AddOwners(sec);
                new DirectoryInfo(path).SetAccessControl(sec);
            }
            else
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                AddOwners(sec);
                new FileInfo(path).SetAccessControl(sec);
            }
        }
        catch { }
    }

    // Clamp every known secret that already exists (plus the browser profile). Safe to call repeatedly; call at daemon and web-host start.
    public static void SecureKnown()
    {
        foreach (var f in SecretFiles) { string p = Path.Combine(DataDir, f); if (File.Exists(p)) Secure(p, false); }
        if (Directory.Exists(ProfileDir)) Secure(ProfileDir, true);
    }
}

class Settings
{
    public string WhisperPath { get; set; } = Path.Combine(Paths.AppDir, "bin", "whisper-cli.exe");
    public string ModelPath { get; set; } = Path.Combine(Paths.AppDir, "models", "ggml-base.en.bin");
    // Bigger, slower model for meeting transcripts (speed doesn't matter there); ModelPath stays the fast dictation model.
    public string MeetingModelPath { get; set; } = Path.Combine(Paths.AppDir, "models", "ggml-small.en.bin");
    public string VadModelPath { get; set; } = Path.Combine(Paths.AppDir, "models", "ggml-silero-v5.1.2.bin");
    // Your own extra terms (comma separated); the summariser appends terms Codex says were misheard.
    public string Vocabulary { get; set; } = "";
    // Names whisper should always get right (comma separated): you, friends, teachers. Kept first when the prompt is trimmed.
    public string Names { get; set; } = "";
    // Mis-hearing -> fix, applied after transcribing (whole words, any case; spaces also match hyphens). See Vocab.cs.
    public Dictionary<string, string> Replacements { get; set; } = Vocab.DefaultReplacements();
    // Say the trigger on its own (optionally after "insert"/"snippet") and its text is typed instead.
    public Dictionary<string, string> Snippets { get; set; } = new();
    // Spoken name -> folder, for "open <name>" / "open <name> in vs code" when the name isn't a folder name.
    public Dictionary<string, string> FolderAliases { get; set; } = new()
    {
        ["verilog"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Le code", "Betterverilog"),
    };
    // Dictation model. "" = the fast base.en (~0.5 s per utterance). ggml-small.en.bin is more accurate but costs
    // ~1.5 s even on a 3 s clip (loading the model dominates) and a bit more with a long glossary.
    public string DictationModelPath { get; set; } = "";
    public int AudioKeepDays { get; set; } = 0;     // >0: delete mic/system WAVs of transcribed meetings older than this
    // Only proper-noun-like terms are worth biasing whisper with: ASCII, <= 3 words, capitalised.
    public static bool GoodTerm(string t) =>
        System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9.'\- ]{1,38}$") && t.Split(' ').Length <= 3 &&
        t.Any(char.IsUpper) && t.Any(char.IsLower);
    public string MeetingModel() => File.Exists(MeetingModelPath) ? MeetingModelPath : ModelPath;
    public string DictationModel()
    {
        return DictationModelPath.Length > 0 && File.Exists(DictationModelPath) ? DictationModelPath : ModelPath;
    }
    public string[] PromptArgs(int maxChars = 640) => new[] { "--prompt", Vocab.Prompt(this, maxChars) };
    public string Language { get; set; } = "en";
    public int Threads { get; set; } = Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));
    public bool KeepWavs { get; set; } = true;
    public string MicDeviceId { get; set; } = ""; // "" = Windows default mic
    public bool Dictation { get; set; } = true;   // hold Right Shift to dictate (daemon)
    public string CodexAppId { get; set; } = "OpenAI.Codex_2p2nqsd0c76g0!App"; // Store app, opened via shell:AppsFolder
    public string CodexPath { get; set; } = "";             // "" = newest codex.exe the Codex app installed
    public string CodexModel { get; set; } = "gpt-6-luna";
    public bool PlannerWord { get; set; } = true;           // say "Planner, ..." (no Luna needed) with Right Shift: "Planner" alone opens the Plan window, "Planner, add dentist friday at 3" adds it
    public bool Luna { get; set; } = true;                 // "Luna, <request>" with Right Shift runs the request instead of typing it
    // Where "open <name>" (folders, VS Code) looks for a project folder called <name> (two levels down).
    public List<string> TerminalRoots { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Le code"),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };
    // Hold this key and speak: everything you say is a Luna request (no need to say "Luna"). "RightCtrl", "RightAlt" or "" to turn off.
    public string LunaHoldKey { get; set; } = "RightCtrl";
    // A second hold-to-talk key that works the same way (Right Alt by default, so either key talks to Luna). "" to turn off.
    public string LunaHoldKey2 { get; set; } = "RightAlt";
    static int HoldVk(string name) => name?.Trim().ToLowerInvariant() switch { "rightctrl" or "rctrl" or "rightcontrol" => 0xA3, "rightalt" or "ralt" or "altgr" => 0xA5, _ => 0 };
    public int[] LunaHoldVks() => new[] { HoldVk(LunaHoldKey), HoldVk(LunaHoldKey2) }.Where(v => v != 0).Distinct().ToArray();
    // Double-tap Right Ctrl (two quick taps, no other key) opens the typing box, like the Copilot key.
    public bool AskDoubleTap { get; set; } = true;
    // "open <name>" opens these websites when <name> isn't an installed app. Add your own.
    public Dictionary<string, string> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com", ["gmail"] = "https://mail.google.com", ["google"] = "https://www.google.com",
        ["google drive"] = "https://drive.google.com", ["drive"] = "https://drive.google.com", ["google docs"] = "https://docs.google.com",
        ["docs"] = "https://docs.google.com", ["google sheets"] = "https://sheets.google.com", ["sheets"] = "https://sheets.google.com",
        ["google slides"] = "https://slides.google.com", ["slides"] = "https://slides.google.com", ["google calendar"] = "https://calendar.google.com",
        ["calendar"] = "https://calendar.google.com", ["google classroom"] = "https://classroom.google.com", ["classroom"] = "https://classroom.google.com",
        ["google maps"] = "https://maps.google.com", ["maps"] = "https://maps.google.com", ["github"] = "https://github.com", ["reddit"] = "https://www.reddit.com",
        ["twitter"] = "https://x.com", ["x"] = "https://x.com", ["instagram"] = "https://www.instagram.com", ["netflix"] = "https://www.netflix.com",
        ["amazon"] = "https://www.amazon.com", ["wikipedia"] = "https://www.wikipedia.org", ["chatgpt"] = "https://chatgpt.com", ["claude"] = "https://claude.ai",
        ["twitch"] = "https://www.twitch.tv", ["linkedin"] = "https://www.linkedin.com", ["pinterest"] = "https://www.pinterest.com", ["tiktok"] = "https://www.tiktok.com",
        ["whatsapp web"] = "https://web.whatsapp.com", ["canva"] = "https://www.canva.com", ["notion"] = "https://www.notion.so", ["figma"] = "https://www.figma.com",
    };
    // Extra folders Luna's PC tasks may write to (the sandbox otherwise only allows its own scratch folder). Missing ones are skipped.
    public List<string> LunaWritableDirs { get; set; } = new()
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Le code"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify"),
    };
    public bool LunaInternet { get; set; } = true;          // Luna can search the web and its PC tasks can use the network (downloads, public git clones). Git on YOUR repos goes through the daemon (GIT: hook / "git pull my-app").
    public bool LunaLight { get; set; } = true;             // run Codex without the Codex app's MCP servers/plugins/rules (faster, ~5k fewer tokens, fewer processes). Set false to use your full Codex setup.
    public string LunaModel { get; set; } = "gpt-6-luna";
    public string SolModel { get; set; } = "";               // "Sol, ..." by voice; "" = newest gpt-*-sol in the Codex model list
    public string TerraModel { get; set; } = "";             // "Terra, ..." by voice; "" = newest gpt-*-terra
    public string StickyModel { get; set; } = "";            // set by "swap to Terra" / "swap to Sol"; "" = Luna ("swap to Luna" clears it)
    public string LunaEffort { get; set; } = "medium";      // low | medium | high (low = ~2x faster, medium = smarter)
    // ---- Jarvis mode
    public bool AutoRoute { get; set; } = true;              // no model named: quick questions run cheap (Luna, low), hard thinking / writing / code / screen control / ArtCraft go to SmartModel
    public string SmartModel { get; set; } = "terra";        // family (terra | sol) or an exact model id used for those; "luna" = never escalate
    public int SmartRunsPerDay { get; set; } = 15;           // cap on escalated runs per day (after that everything stays on Luna). "usage" by voice shows the count
    public bool ScreenControl { get; set; } = true;          // Luna may look at the screen, click, press keys and type when the request asks for it (Esc stops it)
    public bool WebBrowser { get; set; } = true;             // Luna has its own invisible background browser (vweb.js, WebHost.cs) for websites, signing in from the password vault
    public bool Humanize { get; set; } = true;               // writing for you follows the natural-style rules, and typed text gets the free tidy-up pass
    public bool ArtCraft { get; set; } = true;               // Luna can drive EffectCraft / PhotoCraft / VectorCraft / FigCraft / PdfCraft through their command-lines
    public string ArtCraftDir { get; set; } = "";            // "" = %LOCALAPPDATA%\Programs\ArtCraft
    public string SpeakReplies { get; set; } = "short";      // off | short (answers up to ~2 sentences are also said out loud) | all. "be quiet" / "talk to me" by voice
    public string AssistantNames { get; set; } = "Luna, Jarvis";   // words that start a request with Right Shift ("Jarvis, what's on today")
    public int SettingsVersion { get; set; }              // bumps when a default is changed on purpose (see Load)
    public bool AutoSummarise { get; set; } = true;          // summarise with Codex right after transcribing

    static string FilePath => Path.Combine(Paths.DataDir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                // A path saved by another copy of the exe (e.g. the build folder) falls back to ours.
                var d = new Settings();
                if (!File.Exists(loaded.WhisperPath)) loaded.WhisperPath = d.WhisperPath;
                if (!File.Exists(loaded.ModelPath)) loaded.ModelPath = d.ModelPath;
                if (!File.Exists(loaded.VadModelPath)) loaded.VadModelPath = d.VadModelPath;
                if (loaded.SettingsVersion < 2) { loaded.LunaEffort = "medium"; loaded.SettingsVersion = 2; }   // asked for: Luna medium
                if (loaded.DictationModelPath.Contains(@"\out\models") || (loaded.DictationModelPath.Length > 0 && !File.Exists(loaded.DictationModelPath))) loaded.DictationModelPath = "";                if (loaded.Vocabulary == "Marvel Rivals, Rule 34") loaded.Vocabulary = ""; // old seed
                loaded.Vocabulary = string.Join(", ", loaded.Vocabulary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(GoodTerm));
                loaded.Save(); // writes out any options added since the file was created
                return loaded;
            }
        }
        catch { }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); } catch { }
    }
}
