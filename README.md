# ClaudeBlink

Shows Claude Code session state on a blink(1) mk2.

| Status (default look) | Meaning |
| --- | --- |
| Long wait (fast-blinking red) | a session has needed input for over 5 minutes |
| Needs input (blinking orange) | a session is waiting for you (permission prompt, question, plan approval) |
| Just finished (blinking blue) | a session completed in the last 10 seconds |
| Compacting (pulsing purple) | a session is compacting its context |
| Subagents (pulsing cyan) | a session has subagents running, including background ones after the session stops |
| Running (pulsing green) | a session is working |
| Done (solid blue) | sessions are finished and nothing needs you |
| Idle (off) | no sessions |

Statuses are listed from highest to lowest priority, and the light shows the highest one across all sessions. Long wait, Just finished, Compacting and Subagents can each be switched off in Settings. A switched-off status falls back to the one it's based on: Long wait to Needs input, Just finished to Done, and Compacting and Subagents to Running.

## How it works

- **`ClaudeBlinkHook.exe`** is registered as a Claude Code hook in `~/.claude/settings.json`. Each event writes one small file per session to `%USERPROFILE%\.claude-blink\sessions\`. Per-tool events run `async`, so they never slow Claude down.
- **`ClaudeBlink.exe`** is a tray app. It polls those files and drives the LED over raw HID, with no driver or blink1-tool needed. Right-click the tray dot to see sessions, forget stale ones, or edit settings.

| Hook event | Recorded state |
| --- | --- |
| UserPromptSubmit, PostToolUse, PostToolUseFailure, PermissionDenied | working |
| PermissionRequest, Notification (permission / elicitation / agent_needs_input), PreToolUse (AskUserQuestion / ExitPlanMode) | waiting |
| PreCompact | compacting |
| PostCompact, SessionStart (compact) | back to the state before compacting |
| Stop, StopFailure | done |
| SubagentStart / SubagentStop | adds or removes a subagent marker |
| SessionEnd | removed |

A tool call made inside a subagent keeps that subagent's marker alive. It doesn't flip a finished session back to running.

`ClaudeBlink.exe status` prints each session's recorded state and the status it currently shows.

Esc interrupts don't fire a hook, so the tray app spots them in the transcript. A running session (or subagent) with no activity for 15 minutes counts as done (for example, a killed terminal). `done` sessions drop off after 12 hours.

## Data folder

Session files, settings (`config.json`), `error.log` and `tray-state.json` live in `%USERPROFILE%\.claude-blink`. This is deliberately **not** AppData. The Claude desktop app is an MSIX package, and Windows silently redirects AppData writes from its Code sessions and their hooks into a private per-package folder, which the tray app would never see.

For the same reason, start the tray app yourself (double-click `ClaudeBlink.exe`) rather than from inside a Claude session. The app registers itself to run at sign-in, so a registry write made from inside Claude can't be redirected either.

## Install

Download `ClaudeBlink-win-Setup.exe` from the latest GitHub release and run it. No admin rights are needed. Setup installs the .NET 10 desktop runtime if it's missing, installs to `%LocalAppData%\ClaudeBlink`, adds the Claude Code hooks, turns on run-at-sign-in and starts the tray app. Uninstalling from **Settings → Apps** removes the hooks and the sign-in entry. Your settings in `%USERPROFILE%\.claude-blink` are left alone.

## Updates

The tray app checks GitHub Releases 30 seconds after it starts and then every 6 hours, and downloads any new version in the background. It installs the update and restarts itself once no session is active, meaning the light is Idle or Done and Settings is closed. To check right away, open the tray menu and choose **Check for updates**. To install a downloaded update immediately, choose **Restart to update**. Each update also re-registers the hooks, so new hook events reach existing installs.

Builds that weren't installed by Setup (for example from `dotnet publish`) never update themselves.

## Releasing

Push a tag: `git tag v0.2.0 && git push origin v0.2.0`.

[release.yml](.github/workflows/release.yml) builds both exes with that version and packs them using [Velopack](https://velopack.io). It then publishes a GitHub release containing `Setup.exe`, a portable zip and full/delta update packages. The exes aren't code-signed, so Windows SmartScreen warns on first run until you add signing (`vpk pack --signParams`).

## Build from source

```bash
dotnet publish ClaudeBlink/ClaudeBlink.csproj -c Release -o "$LOCALAPPDATA/Programs/ClaudeBlink"
dotnet publish ClaudeBlink.Hook/ClaudeBlink.Hook.csproj -c Release -o "$LOCALAPPDATA/Programs/ClaudeBlink"
```

Before you republish, run `ClaudeBlink.exe exit`, which closes the tray app cleanly. Otherwise it locks the exe.

```powershell
& "$env:LOCALAPPDATA\Programs\ClaudeBlink\ClaudeBlink.exe" install | Out-String   # hooks + run at login (--no-startup to skip)
& "$env:LOCALAPPDATA\Programs\ClaudeBlink\ClaudeBlink.exe" uninstall | Out-String # removes both
& "$env:LOCALAPPDATA\Programs\ClaudeBlink\ClaudeBlink.exe" test | Out-String      # cycles the three colors
```

`install` backs up the previous settings to `settings.json.claudeblink.bak`.

## Settings

To open Settings, right-click the tray dot and choose **Settings…**, double-click the dot, or launch `ClaudeBlink.exe` again.

- Each status has its own color, brightness and effect. The effects are Solid; Blink at slow (750 ms), normal (500 ms) or fast (100 ms) on/off times; Pulse with slow (2.5 s), normal (1.5 s) or fast (0.5 s) cycles; and Off.
- Any change shows on the LED right away for a few seconds. Nothing is kept until you click Save.
- You can also set the Long wait and Just finished timings, the stale/expiry timeouts, and whether the app starts at sign-in.
