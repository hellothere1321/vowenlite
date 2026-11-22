using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VowenLite;

// Roadmap 3.1-3.3: works while you're away (overnight.json).
//   Queue: "tonight: summarize my art readings" runs when the PC is idle and plugged in at night (Settings.Overnight), lower effort;
//          the result waits as a morning Nudge (click = open it).
//   Study packs (Settings.StudyPacks): for each quiz/test in the planner within 3 days: summaries, ~15 flashcards and a scored practice quiz.
//   After-submit review: when a quiz/test task is ticked done, a Nudge offers to go over the topics.
//   Never answers a live quiz or test: practice and review only.
static class Overnight
{
    sealed class Job { public string Id { get; set; } = ""; public string Text { get; set; } = ""; public DateTime Queued { get; set; } public int Tries { get; set; } }
    sealed class Result { public string Id { get; set; } = ""; public string Job { get; set; } = ""; public string File { get; set; } = ""; public DateTime Finished { get; set; } public bool Seen { get; set; } }
    sealed class State
    {
        public List<Job> Jobs { get; set; } = new();
        public List<Result> Results { get; set; } = new();
        public Dictionary<string, string> Packs { get; set; } = new();       // task id -> study pack html file
        public Dictionary<string, int> PackTries { get; set; } = new();
        public List<string> SeenDone { get; set; } = new();                  // quiz/exam task ids already seen as done
        public bool Baselined { get; set; }
        public DateTime LastPack { get; set; }
        public string LastMorning { get; set; } = "";
    }

    static readonly object mx = new();
    static State st;
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    static string StateFile => Path.Combine(Paths.DataDir, "overnight.json");
    static string ResultDir => Directory.CreateDirectory(Path.Combine(Paths.DataDir, "overnight")).FullName;

    static State Load()
    {
        lock (mx)
        {
            if (st != null && st.Jobs != null) return st;
            try { if (File.Exists(StateFile)) st = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)); } catch { }
            return st ??= new State();
        }
    }
    static void Save() { lock (mx) { try { File.WriteAllText(StateFile, JsonSerializer.Serialize(Load(), Opts)); } catch (Exception ex) { Luna.Log("overnight: save failed: " + ex.Message); } } }
    // Tests switch data folders between runs.
    internal static void Reset() { lock (mx) st = null; }

    // ---- test hooks
    internal static Func<string, Settings, Task<string>> Runner = (job, s) => Luna.RunQuiet(job, s);
    internal static Func<(double idleMin, bool ac)> Probe = RealProbe;
    internal static Action<string, string> Opener = (file, title) => Luna.OpenWidget(file, title);
    internal static Func<string, string, string> CourseTextFn = CourseText;
    internal static Func<string> PackDir = () => Luna.WidgetsDir;
    internal static Task Inflight = Task.CompletedTask;
    static int busyJob, busyPack;

    // ---- commands

    static readonly Regex QueueRx = new(@"^(?:(?:tonight|overnight)\s*[,:]\s*|overnight\s+|while i(?:'m| am)?\s+(?:sleep|sleeping|asleep|away|out)\s*[,:]?\s*)(.{3,})$", RegexOptions.IgnoreCase);
    static readonly Regex NotJob = new(@"^(?:remind|add|set|start|call|text|put|schedule|create|i |we )", RegexOptions.IgnoreCase);
    static readonly Regex ListRx = new(@"^what(?:['’]s| is| are)?\s+(?:queued|in the (?:overnight )?queue|lined up)(?:\s+(?:for tonight|tonight|overnight))?$", RegexOptions.IgnoreCase);
    static readonly Regex CancelRx = new(@"^(?:cancel|clear|drop|forget)\s+(?:all\s+)?(?:tonight['’]?s|the overnight|overnight|the night)\s+(?:jobs?|queue|tasks?)$", RegexOptions.IgnoreCase);
    static readonly Regex QuizRx = new(@"^(?:quiz me|test me|give me a (?:practice )?quiz|practice quiz)\s+(?:on|about|for)\s+(.{2,})$", RegexOptions.IgnoreCase);

    public static string TryLocal(string l, Settings s)     // "tonight, <job>", "what's queued", "cancel tonight's jobs", "quiz me on <topic>"
    {
        l = (l ?? "").Trim();
        var m = QueueRx.Match(l);
        if (m.Success && !NotJob.IsMatch(m.Groups[1].Value.Trim()))
        {
            string job = m.Groups[1].Value.Trim();
            if (Luna.DryRun) return "DRY RUN: queue for tonight: " + job;
            Queue(job);
            return "✓ Queued for tonight: " + job;
        }
        if (ListRx.IsMatch(l))
        {
            if (Luna.DryRun) return "DRY RUN: list the overnight queue";
            List<Job> jobs; lock (mx) jobs = Load().Jobs.ToList();
            if (jobs.Count == 0) return "✓ Nothing queued for tonight";
            return "Queued for tonight:\n\n" + string.Join("\n", jobs.Select((j, i) => $"{i + 1}. {j.Text}"));
        }
        if (CancelRx.IsMatch(l))
        {
            if (Luna.DryRun) return "DRY RUN: cancel the overnight queue";
            int n; lock (mx) { n = Load().Jobs.Count; Load().Jobs.Clear(); }
            Save();
            return n == 0 ? "✓ Nothing was queued" : $"✓ Cancelled {n} job{(n == 1 ? "" : "s")} for tonight";
        }
        m = QuizRx.Match(l);
        if (m.Success)
        {
            string topic = m.Groups[1].Value.Trim();
            if (Luna.DryRun) return "DRY RUN: build a practice quiz on " + topic;
            int w = Cards.BeginWork("Practice quiz: " + topic);
            Inflight = Task.Run(async () =>
            {
                try
                {
                    string file = await BuildPack(topic, "", "", "", "low", "quiz-" + topic);
                    if (file == null) { Cards.EndWork(w, "Couldn't build the quiz", true, "The model didn't return a usable quiz. Try again."); return; }
                    Cards.EndWork(w, "Practice quiz ready");
                    Opener(file, "Practice quiz: " + topic);
                }
                catch (Exception ex) { Luna.Log("overnight: quiz failed: " + ex.Message); Cards.EndWork(w, "Couldn't build the quiz", true, ex.Message); }
            });
            return "✓ Building a practice quiz on " + topic + "…";
        }
        return null;
    }

    public static void Queue(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return;
        lock (mx) Load().Jobs.Add(new Job { Id = Guid.NewGuid().ToString("N").Substring(0, 8), Text = request.Trim(), Queued = DateTime.Now });
        Save();
    }

    public static List<string> ReadyToday() => ReadyToday(DateTime.Now);     // one line per result finished since yesterday evening (Coach's morning brief)

    internal static List<string> ReadyToday(DateTime now)
    {
        var since = now.Date.AddDays(-1).AddHours(18);
        lock (mx) return Load().Results.Where(r => r.Finished >= since).OrderBy(r => r.Finished).Select(r => "Ready: " + r.Job).ToList();
    }

    // ---- when the queue may run

    // Pure: night (00:00-06:30 or quiet hours), the user away for 20 minutes, on mains power.
    internal static bool CanRun(DateTime now, string quietHours, double idleMin, bool ac)
    {
        bool night = now.TimeOfDay < new TimeSpan(6, 30, 0) || Nudge.InWindow(quietHours, now);
        return night && idleMin >= 20 && ac;
    }

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS p);

    static (double, bool) RealProbe()
    {
        double idle = 0; bool ac = true;
        try
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref li)) idle = unchecked((uint)Environment.TickCount - li.dwTime) / 60000.0;
        }
        catch { }
        try { if (GetSystemPowerStatus(out var ps)) ac = ps.ACLineStatus != 0; } catch { }   // 255 (unknown, e.g. a desktop) counts as plugged in
        return (idle, ac);
    }

    // ---- the minute timer

    public static void Tick(Settings s, DateTime now)
    {
        var (idle, ac) = Probe();
        if (s.Overnight)
        {
            Morning(s, now, idle);
            if (CanRun(now, s.QuietHours, idle, ac) && Load().Jobs.Count > 0 && Interlocked.CompareExchange(ref busyJob, 1, 0) == 0)
                Inflight = Task.Run(async () => { try { await RunNext(s); } finally { Interlocked.Exchange(ref busyJob, 0); } });
        }
        if (s.StudyPacks)
        {
            Review(s);
            bool night = now.TimeOfDay < new TimeSpan(6, 30, 0) || Nudge.InWindow(s.QuietHours, now);
            if ((idle >= 10 || night) && now - Load().LastPack >= TimeSpan.FromHours(1) && Interlocked.CompareExchange(ref busyPack, 1, 0) == 0)
            {
                var task = NextPackTask(now);
                if (task == null) Interlocked.Exchange(ref busyPack, 0);
                else
                {
                    lock (mx) Load().LastPack = now;
                    Save();
                    Inflight = Task.Run(async () => { try { await MakeStudyPack(task); } catch (Exception ex) { Luna.Log("overnight: study pack failed: " + ex.Message); } finally { Interlocked.Exchange(ref busyPack, 0); } });
                }
            }
        }
    }

    // Runs the first queued job and files the answer. A failed run (null) is retried on later nights, three times at most.
    internal static async Task RunNext(Settings s)
    {
        Job job;
        lock (mx) job = Load().Jobs.FirstOrDefault();
        if (job == null) return;
        string answer = null;
        try { answer = await Runner(job.Text, s); } catch (Exception ex) { Luna.Log("overnight: job failed: " + ex.Message); }
        lock (mx)
        {
            var q = Load();
            if (string.IsNullOrWhiteSpace(answer))
            {
                var j = q.Jobs.FirstOrDefault(x => x.Id == job.Id);
                if (j != null && ++j.Tries >= 3) q.Jobs.Remove(j);
            }
            else
            {
                q.Jobs.RemoveAll(x => x.Id == job.Id);
                string file = Path.Combine(ResultDir, DateTime.Now.ToString("yyyy-MM-dd") + "-" + Slug(job.Text) + ".md");
                for (int n = 2; File.Exists(file); n++) file = Path.Combine(ResultDir, DateTime.Now.ToString("yyyy-MM-dd") + "-" + Slug(job.Text) + "-" + n + ".md");
                File.WriteAllText(file, "# " + job.Text + "\n\n" + answer.Trim() + "\n", new UTF8Encoding(false));
                q.Results.Add(new Result { Id = job.Id, Job = job.Text, File = file, Finished = DateTime.Now });
                Luna.Log("overnight: finished " + job.Text);
            }
        }
        Save();
    }

    // First tick after 07:00 with the user at the PC: one nudge per finished result.
    static void Morning(Settings s, DateTime now, double idle)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (now.Hour < 7 || idle >= 5 || Nudge.Quiet(s, now)) return;
        List<Result> fresh;
        lock (mx) { var q = Load(); if (q.LastMorning == today) return; fresh = q.Results.Where(r => !r.Seen).ToList(); }
        foreach (var r in fresh)
        {
            var rr = r;
            Nudge.Show("overnight:" + r.Id, "Ready: " + r.Job, "Finished overnight", () => OpenResult(rr), null, null, TimeSpan.FromHours(12));
            lock (mx) r.Seen = true;
        }
        lock (mx) Load().LastMorning = today;
        Save();
    }

    static void OpenResult(Result r)
    {
        string text;
        try { text = File.ReadAllText(r.File); } catch { text = "The saved result is missing."; }
        text = Regex.Replace(text, @"^# .*\r?\n\r?\n", "");
        Luna.Show(r.Job, text);
    }

    // ---- study packs

    static Item NextPackTask(DateTime now)
    {
        var tasks = Planner.OpenTasks().Where(t => t.Start != null && Habits.Kind(t.Title) is "quiz" or "exam"
            && t.Start.Value.Date >= now.Date && t.Start.Value.Date <= now.Date.AddDays(3)).OrderBy(t => t.Start).ToList();
        lock (mx)
        {
            var q = Load();
            foreach (var t in tasks)
            {
                if (q.Packs.TryGetValue(t.Id, out var f) && File.Exists(f)) continue;
                if (q.PackTries.GetValueOrDefault(t.Id) >= 3) continue;
                return t;
            }
        }
        return null;
    }

    static async Task MakeStudyPack(Item t)
    {
        lock (mx) { var q = Load(); q.PackTries[t.Id] = q.PackTries.GetValueOrDefault(t.Id) + 1; }
        Save();
        string file = await BuildPack(t.Title, t.Cat ?? "", t.Notes ?? "", CourseTextFn(t.Cat ?? "", t.Title), "medium", "study-" + t.Title);
        if (file == null) return;
        lock (mx) Load().Packs[t.Id] = file;
        Save();
        string title = t.Title;
        Nudge.Show("overnight:pack:" + t.Id, "Study pack ready: " + title, "Summary, flashcards and a practice quiz", () => Opener(file, "Study pack: " + title), null, null, TimeSpan.FromHours(24));
        Luna.Log("overnight: study pack for " + title);
    }

    // Asks the model for summary + flashcards + quiz and writes the page; returns its path (null if the reply was unusable).
    static async Task<string> BuildPack(string topic, string course, string notes, string material, string effort, string fileName)
    {
        string prompt =
            "You are making PRACTICE and REVIEW material for a student's own studying. This is not a live quiz or test: never answer, look up or solve a quiz or test that is in progress; only prepare study material.\n" +
            $"Topic / task: {topic}\n" + (course.Length > 0 ? $"Course: {course}\n" : "") + (notes.Trim().Length > 0 ? $"Task notes: {notes.Trim()}\n" : "") +
            (material.Trim().Length > 0 ? "Material the student saved for this course (use it where relevant):\n" + material.Trim() + "\n" : "") +
            "Reply with ONLY one JSON object, no prose, in this exact shape:\n" +
            "{\"summary\": \"markdown study summary, at most 300 words\", \"flashcards\": [{\"q\": \"...\", \"a\": \"...\"}] (about 15), " +
            "\"quiz\": [{\"q\": \"...\", \"choices\": [\"..\",\"..\",\"..\",\"..\"], \"answer\": 0, \"why\": \"one-sentence explanation\"}] (about 10; answer = index 0-3 of the correct choice)}";
        string reply = await Ai.Ask(prompt, null, effort);
        var pack = ParsePack(reply);
        if (pack == null) return null;
        string html = BuildHtml(topic, pack.Value.summary, pack.Value.cards, pack.Value.quiz);
        string dir = PackDir();
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Slug(fileName) + ".html");
        File.WriteAllText(file, html, new UTF8Encoding(false));
        return file;
    }

    internal sealed class QuizQ { public string Q = "", Why = ""; public string[] Choices = new string[4]; public int Answer; }

    internal static (string summary, List<(string q, string a)> cards, List<QuizQ> quiz)? ParsePack(string reply)
    {
        if (Ai.Json(reply) is not JsonObject o) return null;
        string summary = o["summary"]?.ToString() ?? "";
        var cards = new List<(string, string)>();
        if (o["flashcards"] is JsonArray fa)
            foreach (var c in fa)
            {
                string q = c?["q"]?.ToString() ?? "", a = c?["a"]?.ToString() ?? "";
                if (q.Length > 0 && a.Length > 0) cards.Add((q, a));
            }
        var quiz = new List<QuizQ>();
        if (o["quiz"] is JsonArray qa)
            foreach (var c in qa)
            {
                if (c?["choices"] is not JsonArray ch || ch.Count != 4) continue;
                int ans = -1;
                try { ans = (int)c["answer"]!.AsValue().GetValue<JsonElement>().GetDouble(); } catch { int.TryParse(c["answer"]?.ToString(), out ans); }
                string q = c["q"]?.ToString() ?? "";
                if (q.Length == 0 || ans < 0 || ans > 3) continue;
                quiz.Add(new QuizQ { Q = q, Choices = ch.Select(x => x?.ToString() ?? "").ToArray(), Answer = ans, Why = c["why"]?.ToString() ?? "" });
            }
        if (cards.Count == 0 && quiz.Count == 0 && summary.Length == 0) return null;
        return (summary, cards, quiz);
    }

    // One self-contained page (no external anything): Summary, Flashcards (click to flip) and a Practice quiz scored on Submit.
    internal static string BuildHtml(string title, string summary, List<(string q, string a)> cards, List<QuizQ> quiz)
    {
        var data = new JsonObject
        {
            ["summary"] = summary,
            ["cards"] = new JsonArray(cards.Select(c => (JsonNode)new JsonObject { ["q"] = c.q, ["a"] = c.a }).ToArray()),
            ["quiz"] = new JsonArray(quiz.Select(q => (JsonNode)new JsonObject
            {
                ["q"] = q.Q, ["choices"] = new JsonArray(q.Choices.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()), ["answer"] = q.Answer, ["why"] = q.Why,
            }).ToArray()),
        };
        // JSON inside a script tag: keep "<" out so no text can close the tag.
        string json = data.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }).Replace("<", "\\u003c");
        string t = WebUtility.HtmlEncode(title ?? "");
        return @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Study: " + t + @"</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#000;color:#f3f4f6;font:16px/1.5 'Segoe UI',sans-serif}
header{padding:18px 22px 0}h1{margin:0 0 12px;font-size:20px}
.tabs{display:flex;gap:8px;padding:0 22px 12px;border-bottom:1px solid #24272e}
.tab{background:#15171b;color:#9a9fa9;border:1px solid #24272e;border-radius:10px;padding:9px 16px;font:inherit;cursor:pointer}
.tab.on{color:#000;background:#a9d8ff;border-color:#a9d8ff}
main{padding:20px 22px;max-width:820px;margin:0 auto}section{display:none}section.on{display:block}
.card{background:#15171b;border:1px solid #24272e;border-radius:14px;padding:20px;margin-bottom:14px}
.muted{color:#9a9fa9}h3{margin:18px 0 6px}ul{padding-left:22px}
#fc{min-height:170px;display:flex;align-items:center;justify-content:center;text-align:center;font-size:20px;cursor:pointer;user-select:none}
.row{display:flex;gap:10px;align-items:center;justify-content:center;margin-top:10px}
button.b{background:#15171b;color:#f3f4f6;border:1px solid #24272e;border-radius:10px;padding:10px 18px;font:inherit;cursor:pointer}
button.b:hover,.tab:hover{border-color:#a9d8ff}
.ch{display:block;width:100%;text-align:left;margin:6px 0;background:#0c0d10;color:#f3f4f6;border:1px solid #24272e;border-radius:10px;padding:10px 14px;font:inherit;cursor:pointer}
.ch.sel{border-color:#a9d8ff;background:#14202b}.ch.ok{border-color:#4ade80;background:#10261a}.ch.bad{border-color:#f87171;background:#2a1414}
.why{margin-top:8px;color:#9a9fa9}#score{font-size:28px;color:#a9d8ff;text-align:center;margin:10px 0}
</style></head><body>
<header><h1>" + t + @"</h1></header>
<div class=""tabs""><button class=""tab on"" data-t=""sum"">Summary</button><button class=""tab"" data-t=""fl"">Flashcards</button><button class=""tab"" data-t=""qz"">Practice quiz</button></div>
<main>
<section id=""sum"" class=""on""><div class=""card"" id=""sumbody""></div></section>
<section id=""fl""><div class=""card"" id=""fc""></div><div class=""row""><button class=""b"" id=""prev"">Previous</button><span class=""muted"" id=""pos""></span><button class=""b"" id=""next"">Next</button></div><p class=""muted"" style=""text-align:center"">Click the card to flip it.</p></section>
<section id=""qz""><div id=""qs""></div><div class=""row""><button class=""b"" id=""submit"">Submit</button><button class=""b"" id=""retry"" style=""display:none"">Try again</button></div><div id=""score""></div></section>
</main>
<script>
const D=" + json + @";
function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;')}
function md(s){let h='',inl=false;for(const raw of String(s).split(/\r?\n/)){let l=esc(raw).replace(/\*\*(.+?)\*\*/g,'<b>$1</b>').replace(/\*(.+?)\*/g,'<i>$1</i>');
 if(/^\s*[-*] /.test(raw)){if(!inl){h+='<ul>';inl=true}h+='<li>'+l.replace(/^\s*[-*] /,'')+'</li>';continue}
 if(inl){h+='</ul>';inl=false}
 if(/^#+ /.test(raw))h+='<h3>'+l.replace(/^#+ /,'')+'</h3>';else if(raw.trim())h+='<p>'+l+'</p>'}
 return h+(inl?'</ul>':'')}
document.getElementById('sumbody').innerHTML=D.summary?md(D.summary):'<span class=""muted"">No summary.</span>';
document.querySelectorAll('.tab').forEach(b=>b.onclick=()=>{document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('on',x===b));
 document.querySelectorAll('section').forEach(s=>s.classList.toggle('on',s.id===b.dataset.t))});
let ci=0,flipped=false;const fc=document.getElementById('fc');
function showCard(){if(!D.cards.length){fc.textContent='No flashcards.';document.getElementById('pos').textContent='';return}
 const c=D.cards[ci];fc.textContent=flipped?c.a:c.q;fc.style.color=flipped?'#a9d8ff':'#f3f4f6';document.getElementById('pos').textContent=(ci+1)+' / '+D.cards.length}
fc.onclick=()=>{flipped=!flipped;showCard()};
document.getElementById('prev').onclick=()=>{if(D.cards.length){ci=(ci-1+D.cards.length)%D.cards.length;flipped=false;showCard()}};
document.getElementById('next').onclick=()=>{if(D.cards.length){ci=(ci+1)%D.cards.length;flipped=false;showCard()}};
showCard();
const picked=[];let done=false;const qs=document.getElementById('qs');
function drawQuiz(){qs.innerHTML='';D.quiz.forEach((q,i)=>{const d=document.createElement('div');d.className='card';
 const h=document.createElement('div');h.textContent=(i+1)+'. '+q.q;d.appendChild(h);
 q.choices.forEach((c,j)=>{const b=document.createElement('button');b.className='ch';b.textContent=String.fromCharCode(65+j)+'. '+c;
  b.onclick=()=>{if(done)return;picked[i]=j;d.querySelectorAll('.ch').forEach((x,k)=>x.classList.toggle('sel',k===j))};d.appendChild(b)});
 const w=document.createElement('div');w.className='why';w.id='why'+i;d.appendChild(w);qs.appendChild(d)})}
function submitQuiz(){if(done||!D.quiz.length)return;done=true;let correct=0;
 D.quiz.forEach((q,i)=>{const d=qs.children[i],bs=d.querySelectorAll('.ch');bs.forEach(x=>x.classList.remove('sel'));
  bs[q.answer].classList.add('ok');
  if(picked[i]===q.answer)correct++;else{if(picked[i]!==undefined)bs[picked[i]].classList.add('bad');
   document.getElementById('why'+i).textContent=(picked[i]===undefined?'Skipped. ':'Not quite. ')+'Answer: '+String.fromCharCode(65+q.answer)+'. '+q.why}});
 document.getElementById('score').textContent=correct+'/'+D.quiz.length;
 document.getElementById('submit').style.display='none';document.getElementById('retry').style.display=''}
document.getElementById('submit').onclick=submitQuiz;
document.getElementById('retry').onclick=()=>{done=false;picked.length=0;document.getElementById('score').textContent='';
 document.getElementById('submit').style.display='';document.getElementById('retry').style.display='none';drawQuiz()};
if(!D.quiz.length){document.getElementById('submit').style.display='none';qs.innerHTML='<span class=""muted"">No quiz questions.</span>'}else drawQuiz();
</script></body></html>";
    }

    // Saved text for a course: summaries of recent meetings that mention its list name or title words.
    static string CourseText(string cat, string title)
    {
        try
        {
            var words = new List<string>();
            if (cat.Trim().Length >= 3) words.Add(cat.Trim());
            words.AddRange(Regex.Matches(title ?? "", @"[A-Za-z][A-Za-z-]{4,}").Select(x => x.Value)
                .Where(w => !Regex.IsMatch(w, @"^(?:quiz|quizzes|exam|midterm|final|test|chapter|review|study)$", RegexOptions.IgnoreCase)));
            if (words.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var dir in Directory.GetDirectories(Paths.MeetingsDir).OrderByDescending(d => d, StringComparer.Ordinal).Take(40))
            {
                string f = Path.Combine(dir, "summary.md");
                if (!File.Exists(f)) continue;
                string text = File.ReadAllText(f);
                if (!words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                sb.AppendLine(text.Length > 1500 ? text.Substring(0, 1500) : text).AppendLine();
                if (sb.Length > 5000) break;
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    // ---- after-submit review

    static void Review(Settings s)
    {
        var done = Planner.Live().Where(t => t.Kind == "task" && t.Done && Habits.Kind(t.Title) is "quiz" or "exam").ToList();
        List<Item> fresh; bool changed;
        lock (mx)
        {
            var q = Load();
            fresh = q.Baselined ? done.Where(t => !q.SeenDone.Contains(t.Id)).ToList() : new();   // the first run only learns what was already done
            changed = !q.Baselined || fresh.Count > 0;
            q.Baselined = true;
            foreach (var t in done) if (!q.SeenDone.Contains(t.Id)) q.SeenDone.Add(t.Id);
        }
        if (changed) Save();
        foreach (var t in fresh)
        {
            string title = t.Title;
            Nudge.Show("overnight:review:" + t.Id, "Review " + title + "?", "Go over the key ideas", () => ReviewNow(title), null, null, TimeSpan.FromHours(8));
        }
    }

    static void ReviewNow(string title)
    {
        int w = Cards.BeginWork("Review: " + title);
        Task.Run(async () =>
        {
            try
            {
                string text = await Ai.Ask(
                    $"A student has just handed in \"{title}\". Help them REVIEW it afterwards (this is after the fact: do not look up or answer any live quiz or test). " +
                    "Write short markdown with: 1) the key ideas worth checking, 2) the 5 most commonly missed points on this kind of quiz or exam, " +
                    "3) a closing invitation to tell you which questions they weren't sure about so you can go over them together.", null, "low");
                Cards.EndWork(w, "Review ready");
                Luna.Show("Review: " + title, text);
            }
            catch (Exception ex) { Luna.Log("overnight: review failed: " + ex.Message); Cards.EndWork(w, "Couldn't build the review", true, ex.Message); }
        });
    }

    static string Slug(string text)
    {
        string s = Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 40) s = s.Substring(0, 40).Trim('-');
        return s.Length == 0 ? "job" : s;
    }

    // ---- tests

    public static void SelfChecks(Action<bool, string> check)
    {
        var t = (int h, int m) => new DateTime(2026, 1, 15, h, m, 0);
        check(CanRun(t(2, 0), "", 30, true), "overnight: runs at 02:00 when idle and plugged in");
        check(!CanRun(t(2, 0), "", 10, true), "overnight: not while the user was active 10 min ago");
        check(!CanRun(t(2, 0), "", 30, false), "overnight: not on battery");
        check(!CanRun(t(12, 0), "", 60, true), "overnight: not at noon");
        check(!CanRun(t(6, 31), "", 60, true), "overnight: not after 06:30");
        check(CanRun(t(23, 0), "22:30-07:00", 25, true), "overnight: quiet hours open the window");
        check(CanRun(t(6, 29), "", 20, true), "overnight: 06:29 and exactly 20 min idle is ok");
        string h = BuildHtml("Art <script>alert(1)</script> quiz", "Sum </script><b>x</b>", new() { ("Q?", "A") },
            new() { new QuizQ { Q = "2+2?", Choices = new[] { "1", "2", "3", "4" }, Answer = 3, Why = "math" } });
        check(h.StartsWith("<!doctype html>") && h.Contains("function submitQuiz") && h.Contains("</html>"), "overnight: pack page is a full html document with score logic");
        check(!h.Contains("<script>alert(1)") && h.Contains("&lt;script&gt;alert(1)"), "overnight: pack title is escaped");
        check(h.Split("</script>").Length == 2, "overnight: pack data cannot close the script tag");
        check(!Regex.IsMatch(h, @"<script[^>]+src=|https?://"), "overnight: pack page has no external resources");
        var p = ParsePack("{\"summary\":\"s\",\"flashcards\":[{\"q\":\"a\",\"a\":\"b\"},{\"q\":\"\",\"a\":\"x\"}],\"quiz\":[{\"q\":\"q\",\"choices\":[\"1\",\"2\",\"3\",\"4\"],\"answer\":2,\"why\":\"w\"},{\"q\":\"bad\",\"choices\":[\"1\"],\"answer\":0}]}");
        check(p != null && p.Value.cards.Count == 1 && p.Value.quiz.Count == 1 && p.Value.quiz[0].Answer == 2, "overnight: pack json keeps only valid cards and questions");
        check(Slug("Summarize my Art readings!") == "summarize-my-art-readings", "overnight: slug");
        check(QueueRx.Match("tonight: summarize my readings").Success && QueueRx.Match("while I sleep, check prices").Success && !QueueRx.IsMatch("tonight is fun"), "overnight: queue phrases");
    }

    public static int Test(string[] args)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }
        var s = new Settings { Overnight = true, StudyPacks = true, QuietHours = "" };
        string packs = Path.Combine(Paths.DataDir, "packs");
        string opened = null;
        Reset();
        PackDir = () => packs; Opener = (f, ti) => opened = f; CourseTextFn = (c, ti) => "";
        Probe = () => (60, true);

        // queue / list / cancel through the local commands
        Check(Luna.DryRun || TryLocal("tonight, summarize my art readings", s) == "✓ Queued for tonight: summarize my art readings", "queue via tonight,");
        Check(TryLocal("while I sleep check the bus times", s)?.StartsWith("✓ Queued") == true, "queue via while I sleep");
        Check(TryLocal("what's queued for tonight", s)?.Contains("2. check the bus times") == true, "list queue");
        Check(TryLocal("cancel tonight's jobs", s) == "✓ Cancelled 2 jobs for tonight", "cancel");
        Check(TryLocal("tonight: remind me to call Sam", s) == null && TryLocal("what's my day", s) == null, "does not steal other commands");

        // one queued job runs through a fake runner; result file + ReadyToday + morning nudge
        string ran = null;
        Runner = (job, st2) => { ran = job; return Task.FromResult("Readings summary.\n\n- point one"); };
        TryLocal("overnight summarize my art readings", s);
        var night = DateTime.Today.AddHours(2);
        Tick(s, night); Inflight.GetAwaiter().GetResult();
        Check(ran == "summarize my art readings", "runner got the job");
        string f = Directory.GetFiles(Path.Combine(Paths.DataDir, "overnight"), "*.md").FirstOrDefault();
        Check(f != null && File.ReadAllText(f).Contains("point one"), "result file saved");
        Check(ReadyToday(DateTime.Today.AddHours(8)).Count == 1 && ReadyToday(DateTime.Today.AddHours(8))[0] == "Ready: summarize my art readings", "ReadyToday line");
        Check(Load().Jobs.Count == 0, "job removed from the queue");
        Probe = () => (1, true);
        Tick(s, DateTime.Today.AddHours(7.5));
        string key = "overnight:" + Load().Results[0].Id;
        Check(Nudge.Has(key), "morning nudge shown");
        Nudge.Remove(key);

        // study pack for a quiz due tomorrow
        var task = Planner.AddTask("Art 101 quiz 3", DateTime.Today.AddDays(1), "chapters 4-6", null, "Art 101");
        string prompt = null;
        Ai.Fake = p =>
        {
            prompt = p;
            return "```json\n{\"summary\":\"## Colour\\n- warm vs cool\",\"flashcards\":[{\"q\":\"Primary colours?\",\"a\":\"Red, yellow, blue\"}],\"quiz\":[{\"q\":\"Which is warm?\",\"choices\":[\"Blue\",\"Red\",\"Green\",\"Violet\"],\"answer\":1,\"why\":\"Red is warm.\"}]}\n```";
        };
        Probe = () => (30, true);
        var noon = DateTime.Today.AddHours(12);
        Tick(s, noon); Inflight.GetAwaiter().GetResult();
        string page = Directory.Exists(packs) ? Directory.GetFiles(packs, "*.html").FirstOrDefault() : null;
        Check(page != null, "study pack file exists");
        string body = page == null ? "" : File.ReadAllText(page);
        Check(body.Contains("function submitQuiz") && body.Contains("correct+'/'+D.quiz.length"), "pack has the score logic");
        Check(prompt != null && prompt.Contains("Art 101 quiz 3") && prompt.Contains("Art 101") && prompt.Contains("chapters 4-6") && prompt.Contains("never answer"), "prompt has task, course, notes and the practice-only rule");
        Check(Nudge.Has("overnight:pack:" + task.Id), "study pack nudge shown");
        Tick(s, noon.AddMinutes(10)); Inflight.GetAwaiter().GetResult();
        Check(Directory.GetFiles(packs, "*.html").Length == 1, "no second pack for the same task");

        // "quiz me on" builds and opens one at once
        Check(TryLocal("quiz me on the renaissance", s)?.StartsWith("✓ Building") == true, "quiz me on reply");
        Inflight.GetAwaiter().GetResult();
        Check(opened != null && opened.Contains("quiz-the-renaissance"), "quiz me on opens the page");

        // after-submit review: baseline first, then a quiz ticked done
        Tick(s, noon.AddHours(2));
        Planner.SetDone(task.Id, true);
        Tick(s, noon.AddHours(2).AddMinutes(1));
        Check(Nudge.Has("overnight:review:" + task.Id), "review nudge after the quiz is done");
        Nudge.Remove("overnight:review:" + task.Id); Nudge.Remove("overnight:pack:" + task.Id);

        Ai.Fake = null;
        if (fails == 0) Console.WriteLine("nighttest ok");
        return fails == 0 ? 0 : 1;
    }
}
