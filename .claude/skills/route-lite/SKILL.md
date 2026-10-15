---
name: route-lite
description: Small build loop for Cookie Jar with no Opus - one Sonnet lead plans, splits and reviews, up to five Haiku 5.5 subagents do the scoped work. Trigger ONLY when the user types /route-lite or asks for "the small route workflow". Not for ordinary single-agent edits.
---

# route-lite: Sonnet lead + Haiku workers

Cheap, small, and deliberately simple. **No Opus, no Codex/Sol.** The session model is the lead; every worker is a `haiku` subagent (Haiku 5.5).

## Roles

- **Lead (you, Sonnet 5.5).** Interview, plan, split, route, review, integrate, decide. If the session model is Opus, stop and tell the user to switch with `/model sonnet` first. Workers never make design decisions.
- **Workers (up to 5, `Agent` with `model: "haiku"`).** One narrow task each, run in parallel when their files do not overlap. Use `subagent_type: "general-purpose"` for edits and `"Explore"` for read-only searches.

## Loop

1. **Scope.** Restate the goal in 2-3 lines. Ask the user only if the answer changes what you build. Read the files you need yourself; do not delegate understanding.
2. **Plan.** Write a task table (task, files owned, done-when). Rules:
   - Max **5** workers at once, **8** total per run.
   - **One owner per file.** If two tasks need the same file, merge them or run them in order.
   - Anything touching security (DPAPI/token handling), process killing, input simulation, or more than ~150 changed lines stays with the lead.
3. **Dispatch.** Send all independent workers in one message. Each prompt must contain: the exact files they may edit, the exact behaviour, the repo rules below, and "report back: files changed + anything you were unsure about, max 150 words". Workers get the minimum context, not the whole conversation.
4. **Review (mandatory).** Read every worker's diff yourself. Reject and re-dispatch (max 2 retries per task) or fix it yourself. Never accept "done" on a worker's word.
5. **Verify.** Run the checks below. A failure goes back to the owner of that file, or to you.
6. **Report.** Per task: done / redone / taken over by lead. List what was not verified. Then stop; do not commit or push unless asked.

## Repo rules (paste into every worker prompt)

- Project: Cookie Jar, WPF, .NET Framework, **C# 5** (no `?.`, no string interpolation, no `nameof`, no expression-bodied members). Match the surrounding style and comment density.
- Edit only the listed files. No new dependencies, no drive-by refactors, no formatting-only changes. Keep each file's existing line endings.
- Never run `TeleportTests.exe input|service|all` or anything that moves the real mouse or sends keys, and never run `ScratchHost.exe` without the lead's say-so (it takes window focus).
- Do not touch `bin/`, `bin_dev/`, `tests/out/`, or any user data.

## Verify

Build to a scratch folder, not `bin/`:

```powershell
.\build.ps1 -OutDir $env:TEMP\cj_build
.\tests\build_tests.ps1 -OutDir $env:TEMP\cj_tests
foreach ($m in 'detector','cashout','invite','clicker','order') { & "$env:TEMP\cj_tests\TeleportTests.exe" $m | Select-String RESULT }
```

All must build clean and report `0 failed`. Anything interactive (live Roblox, input tests, screenshots) is listed as **not verified** unless the user explicitly allowed it this session.
