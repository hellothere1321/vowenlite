# Luna: free local commands

Say these after "Luna," (hold Right Shift), or just hold **Right Ctrl or Right Alt** and say them (no "Luna" needed).
Everything here runs on your PC with no model and no tokens. Anything not matched goes to Codex (Luna medium).
Test any phrase without side effects: `out\VowenLite.exe --opentest "phrase"` (dry run).
Lead-ins like "can you please", "hey", "for me", "real quick" are ignored.

## Open things
- **Apps** (anything in the Start menu): "open spotify", "launch discord", "fire up steam", "pull up obsidian", "open codex" (the ChatGPT app)
- **Programs without a Start entry** (Chrome etc.): "open chrome"
- **Websites**: "open youtube", "go to github", "open reddit", "open gmail", "go to youtube.com", "open github dot com" (list in `Sites` in settings.json)
- **Windows settings / tools**: "open wifi", "bluetooth settings", "display settings", "sound", "battery", "windows update", "task manager", "control panel", "device manager", "recycle bin", "snipping tool", "this pc"
- **Folders** (fuzzy, forgiving of mis-hearings): "open downloads", "open better verilog", "show vowenlite in explorer"
- **In VS Code**: "open better verilog in vs code"
- **Switch to an open window**: "switch to chrome", "go to spotify", "focus on discord"
- **Saved widgets**: "open my widgets", "open the last widget"

## Searching and the web
- "search for X", "google X", "look up X", "search up X"
- "search youtube for X", "search X on youtube", on amazon / github / wikipedia / reddit / maps / spotify / stack overflow / images / imdb
- "play X on spotify", "play X on youtube", "play some jazz" (YouTube search)
- "directions to X", "navigate to X", "X near me", "weather", "weather in tokyo", "define X"

## Sound and media
- "volume up / down", "turn the volume down a lot", "raise the volume a bit", "set volume to 30", "max volume", "mute", "unmute"
- "pause", "play", "resume", "stop the music", "next song", "skip", "previous song", "play the next song"

## Screen and windows
- "take a screenshot", "lock my pc", "snap left / right", "maximize", "minimize", "restore window", "show desktop", "switch window"
- "move this window to the other monitor", "task view", "new desktop", "next / previous desktop"
- "brightness up / down", "dim the screen", "set brightness to 40" (laptop screens)
- "dark mode", "light mode", "toggle theme"
- "close this window", "full screen"

## Keyboard actions in the current app
"close this tab", "new tab", "reopen tab", "next tab", "previous tab", "refresh", "go back", "go forward", "copy", "cut", "paste", "select all", "undo", "redo", "save",
"zoom in / out", "reset zoom", "scroll up / down", "scroll to the top / bottom", "new window", "select the address bar", "press enter", "press escape"

## Windows shortcuts
"clipboard history" (Win+V), "emoji", "voice typing", "notifications", "quick settings", "widgets", "start menu", "windows search"

## Killing things (cards are clickable)
- **Click the × on a card** (it appears on hover) or **right-click the card** to dismiss it: cancels a timer, stops a running Luna request, removes a note, clears the stopwatch. A small click sound confirms it.
- **Click the stopwatch card** to pause/resume it.
- **A finished timer rings** (bell card with an ice border, beeps every 7 s) until you click it or say **"stop"** / "dismiss" / "silence" (a bare "stop" silences the alarm instead of pausing music while one is ringing).
- **Voice**: "stop timer", "cancel the timers", "kill the alarm", "turn off the timer", "clear my reminders", "end the pomodoro" (also the mangled "stocktimer").
- A new Luna request **cancels** a Codex request that is still running; single mis-heard words ("stocktimer") no longer go to Codex, you just get a "Didn't catch" pill.

## Overlay cards (bottom-right stack)
Everything below appears as a card above the taskbar, stacked: Luna working, timers, stopwatch, pinned notes.
- **Luna working**: any request that goes to Codex (or builds a widget) shows a spinning card with the elapsed time and what it's doing ("Building tip calculator"), then "Done" / "Opened Tip Calculator" / "Answer ready".
- **Stopwatch**: "start a stopwatch", "lap", "stop the stopwatch", "reset stopwatch"
- **Pinned notes** (survive restarts): "pin a note buy milk", "note to self call grandma", "remember that the test is on friday", "what are my notes", "remove the last note", "clear notes"
- **Focus / pomodoro**: "start a pomodoro" (25 min then a 5 min break, automatic), "focus for 45 minutes", "start a 50 minute focus session"
- **Recurring reminders**: "remind me every 30 minutes to stand up", "every hour remind me to drink water" (shown with a ↻; "cancel timers" stops them)

## Timers and reminders (with feedback)
- "timer for 10 minutes", "set a timer for an hour and thirty minutes", "timer twenty five minutes"
- "remind me in 10 minutes to call mom", "remind me to stretch in 30 minutes"
- "how long is left", "what timers are running", "cancel timers"
- Feedback: a chime and a check-mark pill when set, a live countdown ring chip (bottom-right) while running, a repeating alarm and a popup when done.
  (Timers live in memory: restarting the background app clears them.)

## Quick answers (shown in the pill or a small window)
- "what time is it", "what's the date", "what day is it", "how many days until christmas / june 5"
- "how much battery do I have", "how much storage do I have", "how long has my pc been on"
- "flip a coin", "roll a d20", "roll a die", "pick a number between 5 and 50"
- Calculator: "what is 3+3", "17 times 23", "15 percent of 240", "100 divided by 8", "square root of 144", "7 squared", "2 to the power of 10"

## Typing
"type my name" (snippets from settings.json `Snippets`), "type hello world"

## Dictation (Right Shift, no Luna)
"scratch that" / "delete that" (erases the last dictation), "new line", "new paragraph", spoken snippets ("my name"), mis-hearing fixes (`Replacements`), "um/uh" removed.

## Things that go to Luna (Codex, medium effort)
Questions, explanations, writing, anything not matched above. **Interactive widgets**: ask for a calculator, chart, quiz, game, planner, visualization, simulation...
("make me an interactive tip calculator") and it opens as a window; widgets are saved in `Documents\VowenLite\Widgets`. Widgets are sandboxed (no network, no files).
Follow-ups work for a few minutes ("and in kilometres?").

## Assistant hooks (Codex can ask the daemon to do things its sandbox can't)
A Codex reply may contain these lines; the daemon runs them and hides them (`Assistant.cs`). Say things like:
- "remind me tomorrow at 3 to email my teacher", "set a reminder for Friday at 4 pm to ..." → `REMIND:` (saved in `reminders.json`, **survives restarts**, rings like a timer)
- "write a reply saying I'll be late" / "type out a thank-you note" → `TYPE:` types it into the window you're in; "copy that to my clipboard" → `COPY:`
- "read that out loud", "say good morning" → `SAY:` (Windows speech)
- "pin that" → `PIN:` (a pinned note card)
- Memory: tell Luna lasting things ("I'm in AP Physics", "call me Sam", "keep answers short") and it saves them (`REMEMBER:`, file `memory.txt`, shown to Luna on every request). Free commands: "what do you remember about me", "forget everything", "forget about my school".
- Test: `out\VowenLite.exe --hooktest "REMIND: in 5 minutes | x\nPIN: y"` (dry run), `--hooklive` really runs them.
- Luna's PC tasks can also write to Downloads, Desktop, Documents, Le code, Spicetify and Spotify folders (`LunaWritableDirs`).

## More free commands (no model, no tokens)
- **Briefing**: "good morning", "daily briefing", "what do I have today" → date/time, reminders in the next 24 h, timers, pinned notes, battery
- **Reminders list / clear**: "what reminders do I have", "cancel my reminders" (dismissing a reminder card also removes it for good)
- **Clipboard**: "read my clipboard", "read my clipboard out loud", "clear my clipboard", "paste as plain text"
- **Last answer**: "copy that answer", "read that answer out loud", "read it again", "stop talking"
- **Unit conversion**: "convert 5 miles to kilometers", "72 f in c", "ten pounds to kg", "how many cups in a litre" (length, weight, volume, speed, temperature, data, time)
- **Le code**: "open le code" opens the folder in Explorer (mis-hearings like lay code / la code / le coat resolve too)

(There is no terminal command any more: it was removed on 2026-11-04 because voice phrasing was too inconsistent. "open terminal" still launches the Windows Terminal app.)

## Projects and screen (Luna memory that only shows up when relevant)
- **Projects**: folders under `Le code` are your projects. Name one in a request ("fix the my-app relay", "how does my-app sync work") and Luna gets a quick local look at it for free: folder, top-level files, recently changed files, the README/AGENTS head, notes it saved earlier, and what you asked about it before. If your last project talk was within 30 minutes and you name no project, the project is attached marked "use only if relevant" (the model decides; no extra call).
- Luna saves lasting facts with `PROJECT: name | fact` lines. A project tracked for the first time is seeded from earlier answers in `daemon.log`. File: `projects.json`.
- Say: "what projects are you tracking", "we're working on my-app" / "switch to project latent clone" (sets the active one), "forget project my-app".
- **Screen**: ask about what you see ("what's this error on my screen", "what am I looking at", "explain this page", "read my screen") and a picture of the window you're in (max 1800 px) is attached to that one request (`--shot`). Never captured otherwise.
- **Pinned notes**: hover a long note for the full text, click it to open it in a window.

## Light mode (default)
Luna runs Codex with `--ignore-user-config --ignore-rules` (setting `LunaLight`, default on): it no longer loads the Codex app's MCP servers, 12 plugins and rules, so each request starts fewer processes, uses ~28% fewer prompt tokens (14.5k vs 20.1k for a trivial question) and answers faster (about 2.8 s vs 4.6 s). The Windows sandbox setting is passed explicitly so PC tasks still write files. Set `"LunaLight": false` in settings.json to get the full Codex setup back. The ChatGPT/Codex desktop app is not needed for Luna; only its `codex.exe` is used.

## Internet and git (setting `LunaInternet`, default on)
- **Web search**: Luna has Codex's live web search ("search the web for...", "what's the latest...", "who won last night"), also for plain questions.
- **PC tasks have network access** (downloads, public git clones, APIs). Inside Codex's sandbox `curl.exe` / `Invoke-WebRequest` fail (no Windows TLS login) so Luna is told to use `node` for HTTP; git inside the sandbox uses OpenSSL and never prompts.
- **Git on your own repos runs in the daemon** (your GitHub login, outside the sandbox), free and instant: "git pull my-app", "pull the my-app repo", "fetch my-app", "git status of my-app", "git log for my-app". Pull is fast-forward only (never merges or overwrites); push is not offered. Codex can do the same with a `GIT: <repo> | pull` hook line.

## Tasks and calendar (synced with Google, see PLANNER.md)
- **Tasks**: "add task email Sam friday", "add milk to my to do list", "what are my tasks" (+ "today / tomorrow / this week / overdue"), "mark email Sam as done", "delete the task milk"
- **Calendar**: "add event dentist tomorrow at 3pm for an hour", "schedule lunch with Dana friday at noon", "add standup to my calendar next monday 9am", "what's on my calendar tomorrow / this week", "what's next"
- **Timed reminders** (rings on the PC, notifies on the phone): "remind me tomorrow at 5 to call mom" ("in 10 minutes" still uses the in-memory timers)
- "open planner" / "open my tasks" opens the Plan window; "sync my calendar" syncs now. The morning briefing now lists today's events and due tasks.
- **Say just "planner"** (Right Shift, no Luna): "Planner" opens the Plan window; "Planner, call mom sunday at 5" adds an event, "Planner, buy stamps" a task, "Planner, what's on tomorrow" answers. Ordinary sentences starting with "planner" are still typed.
- **Repeating**: "schedule gym every monday, wednesday and friday at 6pm", "add task water plants every sunday", "remind me every weekday at 9 to ...", "every other week", "monthly on the 15th", "until dec 15", "for 6 weeks". Change them: "make piano repeat every week", "stop repeating piano", "skip standup tomorrow".
- **Lists + priority**: "add task lab report friday in my school list high priority", "add milk to my shopping list", "what's on my school list", "put X in my school list", "make X high priority"
- **Ask / change**: "what's my day", "when is my dentist appointment", "am I free tomorrow at 3", "move dentist to saturday at 4pm", "push the essay to tomorrow", "postpone the meeting by an hour", "rename X to Y", "clear completed tasks"
- Luna's Codex model can write to the planner itself (`PLAN:` hook) and sees your next 14 days when the request is about plans, tasks or the schedule.

## Jarvis mode (added 2026-11-10)

Say "Luna, ..." or "Jarvis, ..." with Right Shift, or hold Right Ctrl / Right Alt and just talk.

**Answers** open in a box at the bottom right of the main monitor once they are complete (it never takes focus): formatted text, tables, code and diagrams. Copy / Expand / close (or Esc after clicking it); drag it by its header; a newer answer replaces it. Short answers are also said out loud: "be quiet" turns that off, "talk to me" turns it on (`SpeakReplies`).

**Which model** (when you don't name one): quick questions run on Luna at low effort; hard thinking, real writing, code work, screen control and ArtCraft design go to Terra, at most `SmartRunsPerDay` (15) times a day, then everything stays on Luna. "Sol, ..." / "Terra, ..." / "swap to Terra" still win. Say "usage" to see today's runs and tokens (free).

**Screen control** (needs a model run; Esc stops it at once): "click the ...", "scroll down", "fill in this form", "finish this doc", "continue this essay", "take control and ...". Luna reads the whole document, looks at the screen, clicks, presses keys and types. It will not type into sign-in or payment pages and will not press a final Send / Submit / Buy / Delete: it gets things ready and tells you.

**Writing for you** follows the natural-style rules (matches your document's voice, no AI filler). "Humanize the clipboard" tidies copied text for free.

**ArtCraft suite**: "in EffectCraft make a lower third that says ...", "make a logo for ...", "merge these PDFs in my downloads", "remove the background from this image", "export that design as PNG". Results land in Documents\ArtCraft and open when done.

**Planner**: "plan my day" lays today's due / high-priority tasks into the free time around your calendar (free); "block it in" books them as Focus events. In the meeting window's Ask AI panel, "Add to planner" turns an answer's bullets into tasks.

**Ask AI (meeting window)**: starter buttons, This meeting / All meetings switch, answers kept per meeting (`ask.json`), Copy / Ask again / Clear / Hide, Stop while it thinks, Up arrow recalls your last question.
