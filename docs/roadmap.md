# Roadmap: a personal assistant built around the planner

## The idea
The planner is the brain. Everything else either **feeds it** (captures what you have to do) or **acts on it** (schedules, starts, nudges, prepares).
What makes it new is that VowenLite sits on the PC itself, so it can do things a phone assistant or a chat app can't:
- **It's the input layer.** Every dictated sentence passes through it, so it can catch promises you make ("I'll send it Friday").
- **It sees the screen.** One tap turns whatever you're looking at (a syllabus, an email, a Discord message) into planner items.
- **It knows which window is in front.** That gives real time tracking without timers.
- **It's always on.** It can prepare things overnight and have them waiting in the morning.
- **It can set up your workspace.** "Start" opens the right page, file and timer, not just a reminder.

## Rules for everything below
- The planner (`planner.json`, synced to Google) is the single source of truth. Nothing keeps its own to-do list.
- Every proactive thing is one card with one tap (accept / later / never), and has an off switch in settings.
- Speed budget: local commands under 1 s, the fast lane under 5 s, Codex only for real multi-step tasks.
- Local first: dictation text, window titles and time tracking never leave the PC.
- The line on school work: no answers to live quizzes or tests. Practice, review and explanations are fine.

## Phase 0: make it trustworthy (first; everything else depends on it)
Real logs show most failures were plumbing: about 15 of 21 school requests failed on sign-in, lost context or slowness.
- **0.1 Fast lane.** Browser tab ops ("close tabs") become free local commands. Short questions go to a quick model at low effort. Files: `Brain.cs` routing, `Router.cs`.
- **0.2 "done" always resumes.** "done", "I'm signed in" and "go on" continue the pending request in the thread. Check what commit "Resume Luna after sign-in" already covers. Files: `Luna.cs`, `Threads.cs`.
- **0.3 Sign in once a week, not per request.** Find out why the background browser's session got logged out between requests (cookies dropped when `--webhost` exits?). Files: `WebHost.cs`.
- **0.4 No pointless questions.** Never ask "may I control the browser?" when it already can. Every failure ends with one concrete next step (a button where possible). Files: the Luna prompt in `Luna.cs`.
- Done when: replaying the non-quiz school requests from `threads.json` mostly succeeds. Only the user can check this (it needs a live sign-in).

## Phase 1: the planner fills itself (capture)
- **1.1 Calendar feeds.** Paste any `.ics` link once (Brightspace: Calendar → Subscribe, if the school has it turned on). Due dates become tasks in a "School" list, deduplicated by the feed's UID and refreshed hourly. They sync to the phone through the existing Google sync. No sign-in needed. New `Feeds.cs`; `Item` gets `Source` and `Link`.
- **1.2 "Plan this."** Double-tap Right Ctrl on anything on screen and say "plan this". It pulls every date and task out, then shows a review card with checkboxes and one "add all". A syllabus becomes the whole semester in one go. Files: `TapDetector.cs`, the Luna screen path, `Cards.cs`.
- **1.3 Promise catcher.** A local pattern check on dictated text spots commitments ("I'll send it by Friday", "let's meet Tuesday"). A small card asks: add "Send Sam the notes" for Friday? Opt-in; nothing is stored unless accepted. Files: `Dictation.cs` → `Planner.Parse`.
- **1.4 Lecture to tasks.** A recorded lecture's summary also proposes tasks ("read ch. 14 by Monday") as a review card. Files: `Summariser.cs`.
- **1.5 Course weights.** "Plan this" on a syllabus also saves the grading weights (quizzes 20%, final 30%) per course. That feeds priority (2.3) and the grade calculator: "can I drop to a B?" from a pasted or screenshotted gradebook.

## Phase 2: the planner acts (it does the scheduling)
- **2.1 Estimates.** Every task gets a time estimate. It's guessed from the task's type, and you can correct it by voice ("that's 2 hours"). `Item.Est` in minutes.
- **2.2 Self-healing time blocks.** It finds free slots before each due date and puts work blocks on the calendar, marked as VowenLite blocks. If a real event lands on a block, or you skip one, it moves the block and says so ("moved to Wed 7 pm, 1.5 h left").
- **2.3 The "Now" card.** One pinned card always shows the single next thing to do, ranked by due time × course weight × time left × free time today. Buttons: Start / Later / Done.
- **2.4 Start sets up the work.** Start opens the task's `Link` (the quiz page, the doc, the repo in VS Code). It also starts a focus timer for the block and minimizes the apps you've marked as distractions. If one of those comes to the front mid-focus, a quiet card says "12 min left on lab report".
- **2.5 Evening rollover (one 30 s card at night).** Done today, what's slipping, and what it moved, with one tap to accept. The morning brief picks up from it.
- **2.6 Escalation.** Due tonight and not started means nudges get louder: a card in the evening, then a spoken reminder, then a phone alert through a Google event.

## Phase 3: works while you're away
- **3.1 Overnight queue.** "Luna, tonight: summarize the readings for my art class and make a practice quiz." It runs when the PC is idle and plugged in, at lower effort, and leaves the result as a morning card. Files: `Jobs` in `Program.cs`.
- **3.2 Study packs.** For every quiz or test in the planner, it builds a pack overnight: reading summaries, about 15 flashcards and a scored practice quiz in the answer window. This is the legitimate fix for "I never see my score".
- **3.3 After-submit review.** When a quiz is marked done, it offers to go over the topics you weren't sure of. It works from what you remember, or from the submission page if the school shows it.
- **3.4 Morning brief v2.** Due today, today's blocks, the "Now" pick, and what the overnight jobs produced. It can be spoken.

## Phase 4: learns you
- **4.1 Automatic time tracking.** Front window plus the active task gives the real time spent per task. Only daily totals are kept.
- **4.2 Estimate vs actual.** "Essays take you 3.5 h, not 2." Future estimates and blocks get sized from that.
- **4.3 Your real hours.** It learns when you actually work (say 9 pm to 1 am) and puts blocks there.
- **4.4 Sunday review card.** Hours by course, what's coming next week, and the grade outlook from the weights.

## Data model changes (`Planner.cs` `Item`)
`Est`, `Spent` (minutes), `Source` (`feed:<id>` | `screen` | `dictation` | `lecture` | `you`), `Link` (URL or path), `BlockFor` (on a block event: the task id it serves).
Google sync: blocks are ordinary events on the phone. `Source`, `Link` and `BlockFor` stay local; Google only gets the title and times.

## Build order
1. Phase 0.1 + 0.4 (small, and every request gets better).
2. Phase 1.1 feeds (needs the user's feed link).
3. Phase 2.3 + 2.4, the "Now" card with Start.
4. Then 2.2 blocks, 1.2 "plan this" and 3.1 overnight queue, in whatever order turns out most useful.
Hold new miscellaneous commands until Phase 2 ships.

## Open questions for the user
- Does your Brightspace calendar have Subscribe / a feed link?
- Quiet hours: when should nudges never fire?
- Which apps count as distractions for focus blocks?
- Promise catcher: OK for it to scan dictated text for this, locally? (opt-in either way)
