# AgentReaper

A Windows tray utility that reclaims the child processes AI coding agents leave behind.

[日本語版 README](README.ja.md) · [AGENTS.md](AGENTS.md)

---

## The problem

If you run Claude Code, Codex, Cursor or similar all day, MCP servers and hook scripts get
spawned constantly. Most exit cleanly. Some don't — when a session dies abnormally, its
children are orphaned. They sit there idle, holding memory and handles, forever.

Measured on the author's workstation:

- 55 stalled hook processes, every one orphaned, every one with **0 seconds of CPU time**,
  the oldest alive for **6.5 days**, together holding roughly **2.6 GB**
- 23 orphaned MCP server processes under one agent client, unnoticed for 21 hours

They don't show up as a CPU problem. They show up as the machine slowly getting worse over
a week, and as free memory pages quietly disappearing.

## What this does

Finds that specific shape — orphaned, idle, aged, repeated — and terminates the process
tree. Then reports what changed.

It also reads the memory lists, because the symptom people actually notice (the mouse
freezing for a second) tracks free-and-zeroed pages, not the number Task Manager labels
"Available".

**This is not a general PC optimizer.** It targets one failure mode.

## What this is not

This repository ships **the mechanism, without the configuration**. `signatures.conf` is
empty when you clone it, and that is deliberate — what is leaking on your machine depends
on which agents and MCP servers you run, and only a measurement of your machine can say.

The intended workflow is that you hand [AGENTS.md](AGENTS.md) to the coding agent you
already use, run the diagnose command, and let it fill in the configuration for your setup.
You approve what it proposes. That is the product: a safe skeleton plus a briefing document
your agent can act on.

If you would rather do it by hand, everything is a plain text file and the process is in
AGENTS.md too.

---

## Quick start

One command at a time. These examples install to `C:\dev\AgentReaper` — substitute your own path.

**1. Get it**

```powershell
git clone https://github.com/shoutaitou3-creator/AgentReaper.git C:\dev\AgentReaper
```

**2. Build it**

```powershell
powershell -ExecutionPolicy Bypass -File C:\dev\AgentReaper\build.ps1
```

**3. Measure** (changes nothing)

```powershell
C:\dev\AgentReaper\dist\AgentReaper.exe --diagnose --json
```

No .NET SDK, no npm, no downloads. `build.ps1` uses the C# compiler that already ships with
Windows (`.NET Framework 4.x`). The whole thing is about 3,000 lines of C# you can read.

**4. Let an agent configure it**

A freshly started agent has no idea which folder you mean. "This repo" does not resolve.
**Give it absolute paths.**

> Read all of `C:\dev\AgentReaper\AGENTS.md`, then run `C:\dev\AgentReaper\dist\AgentReaper.exe --diagnose --json` and propose a `signatures.conf` and `settings.conf` for this machine. Do not terminate any process. Do not run `--approve`.

Nothing is terminated until you have seen a dry run and approved it.

---

## What `--diagnose` measures

It changes nothing. One scan, one JSON document, ordered the way you should read it.

| Field | What it tells you |
|---|---|
| `remoteAccess` | Detects AnyDesk / TeamViewer / RDP and similar. **If this is not empty, perceived speed is capped by this machine's uplink, not by the machine.** Reading the local numbers without knowing that will mislead you |
| `memory` | `freeAndZeroGb` is what correlates with stalls. `availableGb` is free + standby — that is the number Task Manager calls "Available" |
| `physicalMemory` | Slots populated, and channel width. One module halves memory bandwidth |
| `graphics` | Dedicated VRAM, plus `displays` — count, resolution, refresh, and a lower bound on composition bandwidth. Driver-added virtual screens are composed exactly like physical ones and cost bandwidth that appears in no utilization percentage |
| `reapCandidates` | The reaping targets. **If empty, an empty `signatures.conf` is the correct outcome** |
| `topGroups` | The largest process groups. Context only — never write a signature from this |
| `signatures` / `settings` / `install` | Current configuration, and anything that was rejected, with the reason |
| `warnings` | The above, assembled into human-readable findings. Start here |

## When nothing is leaking and it is still slow

`reapCandidates` is empty, every utilization figure has headroom, and the machine is still
slow. This is the common case. AgentReaper cannot fix it alone, but **the order in which to
suspect things** is written up as a procedure in [AGENTS.md](AGENTS.md).

- **Record this machine's own idle baseline first.** Never compare against another machine's number
- **Measure while it is actually heavy.** An idle measurement describes the state where nothing is wrong, so it neither confirms nor clears anything
- **Change one thing at a time, and collect both the number and how it feels**
- **A null result is a result** — stop tuning the knob that did nothing
- **Work in cost order, not suspicion order** (link → orphaned processes → free pages → screens → VRAM → buying memory)
- **If a second machine of the same build does not have the problem, diff the two.** Every field that matches is eliminated with no experiment at all

---

## Safety

The interesting design constraint: this is configured by a language model, on a stranger's
machine, to terminate processes. Asking politely in documentation is not a safety mechanism.
So the limits are enforced in code, and **none of them can be turned off in a config file**.

**Approval gate.** Until you have looked at a dry run and run `--approve`, nothing is
terminated — even with `dryRun = false`. Approval is bound to a hash of `signatures.conf`,
so changing one character revokes it. The check lives inside `Reaper.Execute`, so the tray,
the CLI and the background scan all pass through it.

**Patterns are rejected at load** when they are shorter than 4 characters, exactly match a
generic term (`node`, `python`, `chrome`, `server.js`, …), or would hit a protected process.

**Patterns are disabled during a scan** when they turn out to match more than 6 distinct
executables, more than 15% of all running processes (checked only above 50 processes, where
a ratio is meaningful), or any process that has used more than 60 seconds of CPU. A leaked
child uses ~0 CPU; anything busy is doing real work.

**Per-tree rules, on top of the above.** A tree is kept if *any* of these hold:

- a running agent client is among its ancestors
- it is one of the N newest instances for that client
- it is younger than the grace period (minimum 5 minutes)
- its CPU time increased in any of the last N scans (minimum 2 consecutive idle scans)
- it contains a protected process, or an excluded PID

Editors, terminals, agent clients and OS core processes are on a hard-coded protected list
that configuration cannot override.

**Thresholds scale with installed RAM.** `autoLightenFreeGb` and `warnFreeGb` are derived
from how much memory the machine actually has, because Windows keeps free memory low and
standby high regardless of RAM size. A threshold copied from a 128GB workstation would sit
permanently above a 16GB laptop's normal free level and fire forever, throwing away the file
cache on every cooldown — slower, while appearing to help. Values above 25% of installed RAM
are clamped in code.

**Rejections are never silent.** Anything refused appears in `--diagnose` output and in the
log with the reason. A signature that quietly stops working is worse than one that fails.

There is a small test suite in `tests/`.

### Antivirus

Windows Defender and other AV may flag this. It enumerates every process, terminates process
trees, and calls `NtSetSystemInformation` — that is the behavioural profile of something
unpleasant, and a self-compiled unsigned binary has no reputation to draw on.

That is a fair assessment of the observable behaviour, and you should not take a
stranger's word that it is fine. The mitigations available to you: the source is small
enough to read, you compile it yourself, there is no network code anywhere in it, and you
can watch what it does with `--dry-run` before allowing it to do anything.

Configuring AV exclusions is your decision. This project does not ask you to make one.

---

## Commands

| Command | Effect |
|---|---|
| `AgentReaper.exe` | Run in the tray (default) |
| `--diagnose` | Measure and report. Changes nothing |
| `--diagnose --json` | Same, as JSON on stdout and in `dist\diagnose.json` |
| `--dry-run` | Scan once, show what would be reaped |
| `--approve` | Record that you have reviewed a dry run. Required before reaping |
| `--reap-once` | Scan once and reap (needs approval) |
| `--lighten` | Reclaim memory lists now (needs elevation) |
| `--pool-tags` | Break down kernel pool usage by tag |
| `--pool-tags --attribute` | Also resolve which driver owns each tag |
| `--help` | Usage |

Add `--nogui` to suppress windows; output still goes to `last-report.txt` and stdout.

## Files

| File | |
|---|---|
| `signatures.conf` | What to reap. Empty by default — you or your agent write this |
| `settings.conf` | Thresholds and behaviour. Ships observation-only |
| `approved.conf` | Written by `--approve`. Do not edit by hand |
| `diagnose.json` | Last `--diagnose` result |
| `agent-reaper.log` | What it did, and what it refused to do |

## Optional installation

```powershell
.\install-autostart.ps1        # Startup shortcut. No admin rights
.\install-elevated-task.ps1    # One UAC prompt, registers an on-demand elevated task
```

The second registers a task with a fixed command line (`--memory-commands`) so that memory
operations don't prompt for UAC each time. That mode takes no caller arguments and never
terminates a process — read `RunMemoryCommands` in `src/Reaper.cs` before you approve it.

Both are optional.

## Requirements

Windows 10/11 x64. .NET Framework 4.x (already present). Nothing else.

## A note on language

This was written in Japanese. Source comments, the tray UI, log messages and the config file
comments are all Japanese. JSON keys are English, and so are this README and AGENTS.md.

If you want an English UI, the strings are plain literals in `src/*.cs`. Translating them is
a normal edit — and given how this repo is meant to be used, your agent can do it.

## License

MIT. See [LICENSE](LICENSE).

Use at your own risk. This terminates processes on your machine. Read the dry run.
