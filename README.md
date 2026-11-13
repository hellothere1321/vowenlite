# VowenLite

Offline meeting notes and push-to-talk dictation, using whisper.cpp on the CPU. No accounts, no cloud.

- **Ctrl+`** opens the window. Press it again to close the app completely.
- **Hold Right Shift** and speak, then let go: the text is typed wherever your cursor is (about half a second later).
  A pill at the top of the screen shows Listening (red) then Transcribing (amber).
  A quick Shift press or Shift+letter (capitals) never records. Doesn't type into apps running as admin.
- **Record / Stop** in the window records your mic ("You") and the PC's sound ("Others"), then
  writes `transcript.md` to `Documents\VowenLite\Meetings\<date>\`.
- After transcribing, the meeting is summarised by **Codex** (the Codex app's CLI, your ChatGPT login, model `gpt-6-luna`)
  into `summary.md`, shown above the transcript. **Summarise (Codex)** re-runs it.

Whisper isn't kept loaded. It starts for each transcription and exits (~1 s start-up, ~85 MB while it runs).
The background part (hotkeys) uses about 5-10 MB, plus a ~5 MB watcher that restarts it instantly if it dies.
Both start at login from the Task Scheduler task "VowenLite".

## Install / remove
```
powershell -File install.ps1
powershell -File uninstall.ps1
```
Settings: `%LOCALAPPDATA%\VowenLite\settings.json` (mic, model path, threads, `Dictation` on/off,
`KeepWavs`, `CodexModel`, `AutoSummarise`). Swap `ModelPath` to `ggml-small.en.bin` for better accuracy (uses more RAM only while transcribing).

whisper.cpp binaries and models (MIT) were taken from the Vowen 0.6.1 install; no Vowen code is used.
