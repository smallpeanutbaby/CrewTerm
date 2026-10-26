# App verification and regression evidence

Environment: Windows x64, .NET SDK 10.0.302, Terminal.Gui 2.5.0 and Terminal.Gui.Editor 2.5.7. All terminal integration tests launch actual Windows ConPTY processes; tests do not send model prompts.

## Initial RED / GREEN

- `dotnet run --project src/CrewTerm.App --no-restore -- --help`: RED because OutputType was Library and no executable existed.
- `dotnet test tests/CrewTerm.App.Tests --no-restore --filter FullyQualifiedName~TuiSmokeTests`: RED with Win32 file-not-found for crewterm.exe. After the CLI entry point, daemon launcher and initial workspace UI were added, opening the UI, F1 help and F10 detach passed in a real ConPTY.
- Editor binding tests preceded the integration: real byte-preserving saves, external-agent conflicts, unsaved navigation, line/context operations. A missing Close API was observed RED before explicit-discard close was implemented.

## Behavior regressions discovered during integration

| Focused filter | Observed RED | Result after fix |
| --- | --- | --- |
| `TerminalInputTests` | Alt+B encoded null instead of ESC+b | Printable rune resolved after removing Alt; 3 passed |
| `PersistenceFailure` | Failed session metadata save left a live orphan session | Creation rolled back and process disposed; regression passed |
| `ConnectionSaturation` | 16 clients occupied all pipes and crashed the accept loop | Capacity reserved before creating another pipe; daemon tests passed |
| `FrameworkQuit` | File list had no initial selection, so Enter opened nothing; after that fix, Esc from file list discarded a dirty buffer | Select first file, central IsRunningChanging guard, explicit Save/Discard/Cancel accelerators; full edit/save/cancel/save-and-detach passed |
| `SelectingSession` | Selecting project B's live session persisted project A's file root | Session/project binding updates both settings; detach and reconnect passed |
| `SelectingSession` (extended) | Explicit `--project A` still attached saved session from B | Explicit project chooses its own matching live session; no accidental input into B |
| `SessionRefreshGateTests` | New coordinator API absent | Delayed old results cannot overwrite newer user selection; focused test passed |
| `TaskMenuRuns` | Enter cancelled the command prompt; subsequent UI pipe call froze on the UI synchronization context | Explicit prompt Enter handling and background pipe call; actual command output/exit 0 displayed, persisted task asserted |
| `CommandPaletteFilters` | Typing searched the list but did not filter commands | Filter gets initial focus; Enter selects the filtered result; new terminal wizard starts a real shell |
| `ConcurrentLayout` | A stale client's F10 encountered a save conflict and could not detach | Explicit choice to detach without overwriting the other client's layout; actual two-client test passed |

Each focused command uses `dotnet test tests/CrewTerm.App.Tests --no-restore --filter FullyQualifiedName~FILTER`. For the daemon tests, compound filters were used where independent regressions were introduced together. Additional daemon/task/CLI/resize and editor/mouse/input-dispatch evidence is recorded in the Automation and Workspace verification documents.

## End-to-end coverage

- Open workspace, F1, Esc, clean F10 detach.
- Two embedded shells, separate input routes, split view, live sessions after UI exit, final queued command before detach.
- File browser → editor → actual file save; dirty Esc and F10 guards; cancelling exit preserves the buffer.
- Cross-project session selection → saved project/session identity → reconnect; explicit project overrides saved binding.
- F7 command prompt → actual background command → streamed output and exit code.
- F9 immediate filtering → checkpoint menu; F3 custom terminal/name wizard → interactive shell.
- Script-facing CLI tests use redirected stdout/stderr and verify EOF, JSON, live terminal input/output, tasks, errors, close and stop.

## Test-harness corrections

The following test changes corrected the stimulus rather than weakening product assertions:

- Alt+number and subsequent typing are sent as separate physical key events. Concatenating an ambiguous ESC+digit+text stream caused the terminal input decoder to consume an escape sequence. The independent-routing assertions remain.
- Terminal.Gui MessageBox defaults to its last button (Cancel). Save is tested with its explicit Alt+S accelerator; the test still asserts the complete saved file contents and process exit.
- The outer ConPTY test object owns a kill-on-close job. Disposing that test container intentionally kills its descendants. Reconnect tests keep the container until verification ends while still asserting that the actual UI process has exited. Production process-tree cleanup was not weakened.

## Final verification

Executed on 2026-10-26:

```powershell
dotnet test CrewTerm.slnx -c Release --no-restore --logger 'trx;LogFilePrefix=crewterm-final'
# Core 16 + Terminal 31 + Workspace 28 + Automation 39 + App 98
# Total: 212 passed, 0 failed, 0 skipped.

dotnet build CrewTerm.slnx -c Release --no-restore
# 0 warnings, 0 errors.

./scripts/publish.ps1
# Self-contained win-x64 output generated successfully.

$env:CREWTERM_TEST_EXE = (Resolve-Path artifacts/win-x64/crewterm.exe)
dotnet test tests/CrewTerm.App.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~TuiSmokeTests --logger 'trx;LogFilePrefix=crewterm-published'
# 7 passed, 0 failed, 0 skipped, using the published executable and its daemon.
Remove-Item Env:CREWTERM_TEST_EXE

./artifacts/win-x64/crewterm.exe --version
# CrewTerm 0.1.0
./artifacts/win-x64/crewterm.exe doctor --state-dir work/published-doctor
# Installed Claude/Codex resolved to their Windows .cmd launchers.
```

Actual installed CLI checks through new/read ConPTY sessions returned Claude Code 2.1.310 and codex-cli 0.163.0, both exit 0. No model prompt was sent. Detailed CLI RED/GREEN evidence is in `verification-automation.md`.

Final read-only integration review found no remaining reproducible P1 in detached process handles, input draining or resize coordination. The delivered version retains the documented platform and VT compatibility boundaries.
