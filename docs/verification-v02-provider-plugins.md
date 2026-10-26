# v0.2 Provider / Local Plugin verification

Date: 2026-10-26. Workspace: `D:\代码\CrewTerm`. Tests use xUnit and real Windows child processes; no paid model prompts, global authentication changes, global provider settings changes, or PowerShell execution-policy changes were made.

The approved v0.2 spec/plan were followed with observed RED → GREEN. The plan's executing-plans/subagent-driven-development skill packages were unavailable, so the existing specialist-agent split and manual test-first workflow were used. Implementation uses the repository's current Core `AtomicJsonStore`, preserving unrelated concurrent refactors.

## Native catalog and exact resume

Initial RED:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter FullyQualifiedName~NativeSessionTests --no-restore
```

Expected compile failure: `NativeSessionRecord`, `NativeSessionStore`, and `ProviderSessionService` did not exist. The same command after implementation passed **24/24**.

Coverage: multiple independently opened stores preserve dormant records; bind/forget persist; close is distinct from removing metadata at the daemon boundary; Claude launch preallocates an exact UUID; known provider selectors use exact IDs; missing IDs never become `--last`; custom resume differs from fresh restart; real temporary `codex.cmd` receives the same UUID after reloading the catalog.

Provider evidence from nonbilling commands:

```powershell
codex --version
codex --help
codex resume --help
claude --version
claude --help
```

Installed versions: `codex-cli 0.163.0`, `Claude Code 2.1.310`. Gemini/OpenCode/Copilot were not installed; their selectors were checked against official documentation and construction tests, not a local CLI invocation. Official links and exact limitations are in [native-sessions.md](native-sessions.md).

## Fork identity and failed stdin regressions

Before production changes:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter 'FullyQualifiedName~ForkedOrNonPersistent|FullyQualifiedName~BrokenStdin' --no-restore
```

Observed **4/4 failures**: three fork/nonpersistent launches incorrectly claimed a source conversation ID; one real plugin closed its stdin while sleeping and the runner left that process alive after the pipe write failed. The test records its own child PID and cleans up only that child even on assertion failure.

After the minimal fixes:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter 'FullyQualifiedName~NativeSessionTests|FullyQualifiedName~LocalPluginTests' --no-restore
```

Observed **40/40 passed**. Fork/nonpersistent commands retain their original command without inventing identity. Runner pipe failures kill/reap the still-running process and become failure results.

## Local registry and real execution

Initial RED:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter FullyQualifiedName~LocalPluginTests --no-restore
```

Expected compile failures: local plugin manifest/catalog/runner types were absent. The first process run then exposed a fixture issue: **5 failed, 7 passed** because Windows PowerShell 5's default Restricted policy blocked test scripts before stdin handling. Investigation confirmed the already-installed PowerShell 7 runtime had RemoteSigned policy. Fixtures now resolve `pwsh` through `ExecutableLocator`; no policy bypass or global setting change was introduced. Focused result: **12/12 passed**, later **13/13** including the broken-stdin regression above.

Real process checks cover Unicode JSON stdin, quoted text, literal `&` in argv, plugin-relative working directory, disabled no-launch, install/enable/disable/remove persistence, independent registry instances, manifest validation, combined output retention, nonzero exit, timeout, cancellation, and independent good/bad event handlers. Source files survive removal. A real process that sleeps beyond its deadline is stopped, and caller cancellation is distinguished from timeout.

## Working shipped example

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter FullyQualifiedName~LocalPluginSampleTests --no-restore
```

RED: the example-directory assertion failed before example production files existed. GREEN: **1/1** after adding the manifest and PowerShell script. The test copies the actual example to a temporary directory, registers it, starts real processes, summarizes real files, and verifies the exact event JSON written by its hook.

A second regression added the real UI `projectRoot` payload. RED: **1 failed, 1 passed**, with `payload.workingDirectory is required`. The script now accepts either field. GREEN: **2/2**. See [Workspace Notes](../examples/plugins/workspace-notes/README.md). Its script requires PowerShell 7; the general plugin runner can execute any declared native program.

## Preserve provider configuration during resume

Review found that constructing only executable + resume ID discarded profile/model/settings/cwd arguments. Tests were written before the fix:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter FullyQualifiedName~NativeSessionTests --no-restore
```

RED: **15 failed, 26 passed**. Failures covered dropped Codex `--profile`, `-c`, `--cd`, `--model`; dropped Claude `--settings`, model and system prompt; invalid automatic recovery of wrappers/unknown options; compound-launch false identity; initial `--` swallowing an appended Claude session flag; and restart dropping profile. The original test assertion that expected the model option to disappear was corrected because it encoded this confirmed defect, not weakened to hide it.

The new limited `ProviderCommandLine` grammar uses option arities observed in installed help. It preserves raw option/value spelling, removes only known selectors and a single initial prompt, and rejects unsupported/ambiguous reconstruction with a specific reason and explicit-resume alternative. It is deliberately not a general shell parser. Variable-length options, wrappers, unrecognized options and compound commands require a registered resume command. Explicit registered commands take priority.

GREEN: **41/41** before the additional real argv fixture. That fixture sends the generated command through a temporary CLI process and verifies each received argument; it uncovered the separate TaskRunner boundary below. The automation-services worker also verified the actual daemon/ConPTY resume route using a `codex.cmd` fixture in a directory with spaces, reporting **11/11 native protocol tests**. That integration result belongs to the daemon worker's evidence, not a paid provider conversation check.

## TaskRunner nested-shell quoting regression

The real argv fixture failed on `--cd "...project & folder"` because the existing UTF-8 launcher embedded the user command directly in two cmd parsers. An independent single-layer cmd probe received every argument correctly, isolating the defect to TaskRunner. Root authorized a minimal `CreateStartInfo` change and the independent regression:

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter 'FullyQualifiedName~QuotedMetacharacters' --no-restore
```

RED: **1/1 failed**, with `literal!` interpreted as a command from the quoted argument `中文 & literal! 100%`.

The outer cmd still changes the code page before starting the inner cmd, preserving the existing encoding fix. A child-only, randomly named environment variable transports the original command through delayed expansion so the outer parser does not reinterpret its metacharacters. The inner shell retains normal shell operators; no parent environment mutation occurs.

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj --filter 'FullyQualifiedName~TaskRunnerTests|FullyQualifiedName~NativeSessionTests' --no-restore
```

GREEN: **55/55 passed**, including quoted spaces/ampersands, literal exclamation and percent characters, intentional `&&`, Unicode output, actual native argument receipt, nonzero exits, cancellation and concurrency.

## Final verification for this slice

```powershell
dotnet test tests/CrewTerm.Automation.Tests/CrewTerm.Automation.Tests.csproj -c Release --no-restore
dotnet build src/CrewTerm.Automation/CrewTerm.Automation.csproj -c Release --no-restore
git diff --check -- src/CrewTerm.Automation/TaskRunner.cs tests/CrewTerm.Automation.Tests/TaskRunnerTests.cs
```

Observed: **111/111 Automation tests passed, 0 skipped**; Automation Release build succeeded with **0 warnings, 0 errors**. Diff whitespace check succeeded; Git printed only its existing LF→CRLF normalization notices. Full solution, packaged executable, UI/ConPTY and provider hook integration are outside this slice and require their owners' evidence and the root release run. No claim is made that a paid model conversation was created and resumed.
