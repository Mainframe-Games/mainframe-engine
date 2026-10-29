# ADR 0221 — Native crash capture: in-house (sentinel + runtime dumps + supervisor), no new dependency

- **Date:** 2026-10-28
- **Status:** accepted
- **Docs:** docs/design/crash-reporting.md#native-crashes-on-desktop

## Context

G15 (crash reporting) needs to notice native faults, stack overflows, `Environment.FailFast` and kills, and ideally
leave a dump. The proposal listed five options (A sentinel + previous log, B .NET `createdump` variables, C a
supervisor relaunch, D a signal handler in a native shim, E `sentry-native` or Crashpad) and three open questions:

1. Do `runtimeconfig.json` `configProperties` carry `DbgEnableMiniDump` and its siblings, or are they environment-only?
2. In-house path, `sentry-native`, or Crashpad?
5. Is Steamworks' minidump upload worth wiring?

The spike (G15.6), on macOS arm64 with .NET 10: a console app with `DbgEnableMiniDump=1`, `DbgMiniDumpType=1` and
`DbgMiniDumpName` as `RuntimeHostConfigurationOption`s (they appear in `runtimeconfig.json`) calling
`Environment.FailFast` wrote **no dump**; the same app with `DOTNET_DbgEnableMiniDump=1` etc. in the environment wrote a
22 MB mini dump in 3.1 s (`createdump`, exit 134). The coreclr PAL reads these knobs with `CLRConfigNoCache`
(environment only).

## Decision

- **Q1: environment-only**, so dumps need the variables before the runtime starts. **A always** (the session sentinel
  and the previous log's tail, every platform), **B for editor-launched games** (the editor sets the variables on each
  game it launches, `NativeDumpMode.Mini`, into the game's `{user data}/crashes/dump-%p.dmp`; `DOTNET_EnableCrashReport`
  adds the runtime's `.crashreport.json` on Linux and macOS), and **B + C for shipped desktop games as an opt-in**,
  `crash.nativeDumps` (`off` default, `triage`, `mini`, `heap`): `GameHost.Run` relaunches the game as its own child with
  the variables set and waits on it (`CrashSupervisor`). C is skipped under a debugger, when the editor launched the
  game, headless, when the variables are already set, and on mobile and consoles (no processes there).
- **Q2: in-house (A + B + C), no new native dependency.** No native shim, no `natives.yml` leg: nothing in `Native/`
  changes, so no RID needs a CI build for G15. Option D stays rejected (chaining in front of the runtime's own
  SIGSEGV handler); E (`sentry-native`/Crashpad) remains possible later behind `ICrashSink`, and M13 keeps its Sentry
  native SDKs on mobile.
- **Q5: no Steamworks error reporting.** It is Windows-only and Valve-hosted; the game's own endpoint
  (`crash.upload`) covers every platform.
- **The supervisor reports at once.** When the child exits abnormally with its sentinel left behind, the supervisor
  writes the `UncleanExit` (or `NativeDump`, with `dump.dmp` and `crashreport.json` moved into the bundle) report with
  the decoded exit code; the next start's prompt offers it. A dump found for a dead session on a normal start goes into
  its bundle the same way.

## Consequences

- No new NuGet package and no native code; `Native/natives.lock` is unchanged.
- Dumps are large (a mini dump of the fixture game is ~20 MB, written in ~3 s while the process is frozen); triage dumps
  hold stacks only. Uploads send them only with `crash.upload.includeDumps`.
- With `crash.nativeDumps` on, a shipped game shows two processes and starts ~100 ms later; the Steam overlay's
  interplay with a relaunched child is not verified (it stays opt-in until it is).
- The sentinel's liveness test is a lock file (`session-<pid>.lock`, held with no sharing), not a process query: the
  managed layer starts no process outside the desktop supervisor (consoles checklist).
