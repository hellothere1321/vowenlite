# Planner: tasks + calendar, repeating, synced with your phone

VowenLite keeps tasks and calendar events in `%LOCALAPPDATA%\VowenLite\planner.json` and syncs them two ways with
**Google Tasks** (every task list) and **Google Calendar** (primary). Google Assistant / Gemini, the Google Calendar app
and the Google Tasks app on your phone use the same data, so that is the whole phone link: no IP address, no pairing,
works at home, at school, on mobile data. Free (Google's Tasks and Calendar APIs cost nothing at personal volume).
Without Google it still works as a local planner (voice + Plan window + alerts); sync just stays off.

(Not Google Drive: Drive is for files. Calendar and Tasks are what the phone apps and Assistant actually read, so they are the right sync targets.)

## The Plan window
Say **"planner"** (hold Right Shift, no Luna needed), or say "open planner" to Luna, or press **Plan** in the VowenLite window.
`VowenLite.exe --plan [today|week|month|day|tasks|upcoming]`. One window at a time: opening it again brings it forward.
- **Today**: greeting, the next 7 days as a strip, your schedule with a "now" line, what is overdue / due.
- **Calendar**: Day / Week / Month. Click a slot or drag to make an event, drag an event to move it, drag its bottom edge to resize, click to edit. Tasks due appear too (toggle "Tasks").
- **Tasks**: grouped Overdue / Today / Tomorrow / This week / Later / No date, filter by list in the sidebar, tick the circle to complete.
- **Upcoming**: the next three weeks as an agenda.
- **Quick add** (top bar, press `/` or `A`): type it the way you say it, a live preview shows what it understood. "lab report friday #school !", "dentist every other friday 3pm for 1 hour", "standup every weekday 9am", "pay rent monthly on the 1st". Auto / Task / Event chips override the guess (a time of day means event).
- **New** (`N`) opens the full editor: date, time, all-day, repeat (daily, weekdays, weekly, every 2 weeks, monthly, yearly, custom days and end date or count), alert, list, priority, notes. Keys: `T` today, `1-4` views, `D/W/M` day/week/month, `←/→` previous/next, `Esc` closes the editor.
- **Lists** (School, Work…) are colour-coded categories. Add `#school` or "in my school list". Tasks in a list sync to a Google Tasks list of the same name (created if missing); lists you make on the phone show up here.
- **Repeating**: tick off a repeating task and it comes back on its next date. Repeating events sync to Google as real recurring events (RRULE). Delete/move one occurrence ("this event only") or the whole series.

## Voice (free, local; hold Right Shift and say "Planner, …", or Right Ctrl/Alt, or "Luna, …")
- Open: "planner", "open planner", "show my week", "month view", "open my tasks", "agenda"
- Add: "add task email Sam friday", "add event dentist tomorrow at 3pm for an hour", "remind me tomorrow at 5 to call mom", "schedule gym every monday, wednesday and friday at 6pm", "add weekly event standup monday 9am", "add task lab report friday in my school list high priority", "add milk to my shopping list". After "Planner," plain text also works: "Planner, call mom sunday at 5" (event), "Planner, buy stamps" (task)
- Repeats understood: every day / weekday / weekend / week / other week / month / year, "every monday and thursday", "every 3 days", "biweekly", "monthly on the 15th", with "until dec 15", "for 6 weeks" or "10 times"
- Ask: "what's my day", "what's on tomorrow", "what's on my calendar this week", "what are my tasks", "what's on my school list", "what's next", "when is my dentist appointment", "am I free tomorrow at 3"
- Change: "mark lab report done", "move dentist to saturday at 4pm", "push the essay to tomorrow", "postpone the meeting by an hour", "rename X to Y", "make the essay high priority", "put X in my school list", "make piano repeat every week", "stop repeating piano", "skip standup tomorrow", "delete event dentist", "clear completed tasks", "sync my calendar"
- Luna's Codex model can also write to the planner through the `PLAN:` hook (a planner sentence like the ones above, with real dates), and sees your next 14 days when the request is about plans/tasks/schedule. Say "Luna, plan my study week around my classes" and it can add the sessions.
- `Settings.PlannerWord` (default true) turns the bare "planner" trigger off. It only fires at the start of an utterance, followed by a pause or a planner word ("Planner is a nice word" is still typed normally).

## One-time Google setup (about 5 minutes)
1. https://console.cloud.google.com → create a project (any name). **APIs & Services → Library**: enable **Google Tasks API** and **Google Calendar API**.
2. **APIs & Services → OAuth consent screen**: User type *External*, app name VowenLite, your email. Add yourself under *Test users*. Then press **Publish app** (status "In production"). Do this, otherwise Google expires the sign-in every 7 days. Google shows an "unverified app" warning once; it's your own app, choose Advanced → continue.
3. **Credentials → Create credentials → OAuth client ID → Desktop app**. Download the JSON.
4. In the Plan window press **Connect Google**, pick the downloaded JSON, sign in in the browser. (CLI alternative: `& "$env:LOCALAPPDATA\VowenLite\VowenLite.exe" --gauth "C:\path\to\client_secret_xxx.json"`.) The refresh token is stored in `google-token.json` and keeps you signed in. Run from a normal PowerShell, not a Claude desktop shell (its AppData writes are sandboxed, see AGENTS.md).

## Using it on the phone
- **Voice** (Google Assistant or Gemini): "Hey Google, add buy milk to my tasks", "Hey Google, add dentist tomorrow at 3pm to my calendar". Timers are built into the phone and don't sync.
- **Manual**: Google Calendar and Google Tasks apps (and their widgets).
- Sync runs every 5 minutes in the background, 2 s after any change made on the PC, every minute while the Plan window is open.
- The PC rings a card before an event (default 10 min; "remind me" reminders ring at the time, repeating ones every time). The phone notifies through the Calendar app's own alerts.

## Limits
- Google Tasks only stores a due **date** (no time). A task you give a time keeps it in the title ("Call mom (5:00 PM)"). Use "remind me ..." / an event when you need a timed alert on both devices. Priority and notes of tasks stay on the PC (Tasks has no field for priority); events keep list + priority in hidden Google fields.
- Only the primary calendar and events from yesterday to 90 days ahead are synced. Repeating events made on the phone appear as individual occurrences (read as separate events); repeating events made on the PC are real recurring events on both sides. Unusual rules ("2nd Tuesday") can't be created here. A single occurrence moved (not deleted) on the phone stays at its old time here.
- Timers/stopwatch/focus sessions stay on each device.
- Gemini replaces Assistant on some phones; it writes to the same Google Calendar/Tasks (needs the Google Workspace/Tasks extension on in Gemini settings).

## Tests
`out\VowenLite.exe --plantest | Out-Host`: date parsing, repeat rules, voice commands, the "planner" trigger and the full sync against an in-process fake Google (temp data dir; never touches real data). Not covered: the real Google endpoints and the browser sign-in.
`out\VowenLite.exe --planshot <png> [view] [w h]` opens the Plan window on demo data and saves a screenshot (set `VOWENLITE_DATA` to a folder you can write to, and `PLANSHOT_JS` to run a script first, e.g. open the editor).
