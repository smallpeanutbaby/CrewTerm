# Automation, daemon, CLI and resize verification

Environment: Windows x64, .NET SDK 10.0.302. Commands are run from `D:\代码\CrewTerm`. Tests launch temporary shell commands, not installed AI agents.

## TDD evidence

| Scope | RED command / observed failure | GREEN evidence |
|---|---|---|
| Bounded process runner | `dotnet test tests/CrewTerm.Automation.Tests --filter TaskRunnerTests --no-restore --verbosity minimal`; missing `TaskRunner` before production implementation | Initial 10 tests passed: real stdout/stderr, exit 7, cwd with spaces, process cancellation, one-slot serialization, bounded output, pre-cancelled jobs, invalid configuration |
| History and pipelines | `dotnet test tests/CrewTerm.Automation.Tests --filter "TaskHistoryTests\|PipelineRunnerTests" --no-restore --verbosity minimal`; missing history/pipeline APIs | Initial 6 tests passed with real temporary files and processes; failed first step never executes the next side effect |
| Profiles and state | `dotnet test tests/CrewTerm.Automation.Tests --filter "AgentCatalogTests\|AgentStateTrackerTests" --no-restore --verbosity minimal`; missing profile/state APIs | 10 tests passed for `.cmd` discovery, profile persistence/update/removal, corruption preservation, explicit state, partial approval prompts and no invented completion |
| Windows quoted commands regression | `dotnet test tests/CrewTerm.Automation.Tests --filter QuotedExecutableAndArgumentWithSpacesRunUnchanged --no-restore --verbosity minimal`; exit 1 and output beginning with a literal backslash-quote before the executable | Same command passed after changing intentional shell commands from CRT `ArgumentList` escaping to cmd's own outer-quote grammar |
| Chinese output regression | `dotnet test tests/CrewTerm.Automation.Tests --filter ChineseShellOutputRemainsReadable --no-restore --verbosity minimal`; expected `你好终端`, got replacement characters | Raw-byte investigation showed CP936 bytes even after `chcp 65001` in the same cmd. A fresh nested cmd after setting UTF-8 fixed the regression. All 12 TaskRunner tests passed together |
| Structurally corrupt history regression | `dotnet test tests/CrewTerm.Automation.Tests --filter StructurallyInvalidHistoryIsReportedWithoutOverwriting --no-restore --verbosity minimal`; `[null]` caused `NullReferenceException`, `[{}]` was accepted | Both cases now raise `InvalidDataException` while preserving bytes |
| Invalid new history record regression | `dotnet test tests/CrewTerm.Automation.Tests --filter InvalidNewResultDoesNotOverwriteHistory --no-restore --verbosity minimal`; no exception for empty ID | New records are validated before writing; all 6 history tests passed |
| Reusable pipeline templates | `dotnet test tests/CrewTerm.Automation.Tests --filter PipelineTemplateStoreTests --no-restore --verbosity minimal`; missing store API | 6 tests passed including reloaded template execution against a real selected directory, updates/removal and corrupt JSON preservation |
| Daemon-owned commands | `dotnet test tests/CrewTerm.App.Tests --filter DaemonTaskServiceTests --no-restore --verbosity minimal`; missing daemon task service/info API | 6 tests passed: continued execution without caller ownership, live output, completed-history reload, cancellation, fail-fast pipeline, stopping queued jobs, bounded output and real missing-command exit code |
| Task protocol integration | `dotnet test tests/CrewTerm.App.Tests --filter TaskStartReturnsBackgroundTaskId --no-restore --verbosity minimal`; behavioral failure `Unknown operation: task-start` | Protocol tests passed for background IDs, a newly connected client, empty-output summaries, full output reads, cancellation, fail-fast pipelines, configured concurrency, invalid commands and stop persistence |
| Host shutdown regression | `dotnet test tests/CrewTerm.App.Tests -c Release --filter "DaemonTests\|DaemonTaskServiceTests\|DaemonTaskProtocolTests" --no-restore --verbosity minimal`; 20/21 passed, `DisposeSafelyDrainsConnectionsWaitingForRequests` raised `ObjectDisposedException` from handlers releasing `connectionSlots` after Host.Dispose | Move semaphore disposal to RunAsync after all request handlers drain; the same Release command then passed 21/21 |
| Windows npm shim discovery regression | `dotnet test tests/CrewTerm.Automation.Tests -c Release --filter LocatorPrefersNativeShimWhenExtensionlessScriptAlsoExists --no-restore --verbosity minimal`; both PATH and absolute-path cases expected `example.cmd` but returned the extensionless Unix script | Prefer PATHEXT candidates before the bare name only on Windows. All 8 AgentCatalog tests and all 39 Automation tests passed |
| CLI daemon output-handle inheritance | `dotnet test tests/CrewTerm.App.Tests -c Release --filter CliIntegrationTests --no-restore --verbosity minimal`; 1/6 passed, five first daemon-using CLI calls exceeded 25 seconds after the CLI process exited because stdout/stderr readers never saw EOF | The root agent changed daemon launch to a detached Windows process without inherited handles. The same command then passed all 6 tests in 9 seconds |
| Terminal resize coordination | `dotnet test tests/CrewTerm.App.Tests -c Release --filter TerminalResizeCoordinatorTests --no-restore --verbosity minimal`; the new coordinator type did not exist | 7 tests passed: delayed old resize precedes the newest queued size, intermediate dimensions coalesce, sessions proceed independently, confirmed repeats are skipped, failures can retry, and returning to an old size is not lost |

The first test for each newly created API necessarily produced a missing-type compilation failure. Later discovered defects had behavioral failing assertions before their fixes. The xUnit `Assert.Single` analyzer diagnostic was corrected without changing the assertion's behavior.

## Public integration surface

- `TaskRunner(maxConcurrency, maxOutputCharacters).RunAsync(CommandTask, onOutput, cancellationToken)` runs explicitly provided noninteractive shell commands. stdin is closed; use ConPTY sessions for interactive agents.
- `TaskHistory(stateDirectory)` writes `task-history.json`; `List` is newest first and `Add` upserts by stable run ID, retaining 100 entries by default.
- `PipelineRunner(runner).RunAsync(commands, onOutput, cancellationToken)` runs ordered commands and stops at the first failed/cancelled step.
- `AgentCatalog(stateDirectory)` stores custom profiles in `agent-profiles.json`; `Discover` checks available executables including Windows `.cmd` shims. Discovery does not launch providers or verify their credentials.
- `PipelineTemplateStore(stateDirectory)` stores `PipelineTemplate`/`PipelineStepTemplate` in `pipeline-templates.json`. `ForProject` supplies the selected directory for each step.
- `DaemonTaskService(stateDirectory, maximumConcurrency)` owns asynchronous jobs. `Start` and `StartPipeline` return stable IDs; `List`/`Get` return snapshots; `Cancel` requests process-tree cancellation; `StopAsync` cancels and waits for active/queued work; `Dispose` performs cleanup. Completed runs reload from history on restart.
- Daemon protocol operations: `task-start` uses Command/Name/WorkingDirectory; `pipeline-start` uses Name plus optional request `Steps: CommandTask[]`; both return task IDs in `SessionId`. `task-list` returns `Tasks` with empty Output fields. `task-get` uses SessionId and returns full `Task`. `task-cancel` uses SessionId. Missing tasks and invalid commands return an error response.
- `DaemonHost` reads `WorkspaceSettings.MaximumConcurrentTasks` at startup. Client disconnect does not stop background tasks. Explicit server stop or disposal cancels jobs, waits for them, and persists their final status. Connection-limit resources are released only after active handlers drain.
- `TerminalResizeCoordinator(Func<string, int, int, Task> resize).RequestAsync(string id, int columns, int rows)` serializes calls per terminal while retaining its newest pending dimensions. Its returned task completes when that batch settles. An obsolete failure does not discard a newer request; a failure of the latest request faults the task and leaves the size unconfirmed for a later retry. Different terminal IDs have independent queues.

## Installed CLI smoke checks

After the terminal module corrected its own Windows shim candidate ordering, the current Release `crewterm.exe` ran both installed CLIs through real daemon `new`/`sessions`/`read`/`close` operations in an isolated temporary state directory. `doctor` resolved `claude.cmd` and `codex.cmd`; `claude --version` printed `2.1.310 (Claude Code)` and `codex --version` printed `codex-cli 0.163.0`. Both sessions reported `HasExited: true` and `ExitCode: 0`. The daemon was stopped in `finally`. These were version queries only, with no AI prompts or login actions.

## Boundaries

- Observed Agent state is a conservative UI hint. Generic output, silence, or text saying tests passed does not imply successful task completion. No cost/token values are invented.
- Tasks deliberately execute shell syntax supplied by the user. Internal program paths and persisted profiles are distinct from untrusted terminal output.
- Task output is retained as a 262144-character tail by default. stdout/stderr chunks are serialized in arrival order, not guaranteed to reflect original inter-stream instruction ordering.
- A daemon restart loads completed history, not running process memory. Detaching a client does not call `StopAsync`; an explicit daemon stop does.
- `List` includes each task's output. The protocol should send compact list entries and use `Get` for full logs to avoid multiplying output across every polling response.
- JSON stores serialize concurrent access within the daemon process and write through same-directory temporary files before atomic replacement. Corrupt files are preserved and reported.

## Final verification

- `dotnet test tests/CrewTerm.App.Tests -c Release --filter DaemonTaskServiceTests --no-restore --verbosity minimal`: **6 passed, 0 failed, 0 skipped**.
- `dotnet test tests/CrewTerm.Automation.Tests -c Release --no-restore --verbosity minimal`: **37 passed, 0 failed, 0 skipped**.
- `dotnet build src/CrewTerm.Automation -c Release --no-restore --verbosity minimal`: **succeeded, 0 warnings, 0 errors**.
- `git diff --check -- src/CrewTerm.Automation tests/CrewTerm.Automation.Tests src/CrewTerm.App/Server/DaemonTaskService.cs tests/CrewTerm.App.Tests/DaemonTaskServiceTests.cs docs/verification-automation.md`: **exit 0**.
- After task protocol integration and shutdown-race fix: `dotnet test tests/CrewTerm.App.Tests -c Release --filter "DaemonTests\|DaemonTaskServiceTests\|DaemonTaskProtocolTests" --no-restore --verbosity minimal`: **21 passed, 0 failed, 0 skipped**.
- `dotnet build src/CrewTerm.App -c Release --no-restore --verbosity minimal`: **succeeded, 0 warnings, 0 errors** after the shutdown fix; targeted `git diff --check` also returned **exit 0**.
- After Windows shim discovery fix: `dotnet test tests/CrewTerm.Automation.Tests -c Release --no-restore --verbosity minimal`: **39 passed, 0 failed, 0 skipped**.
- After detached daemon launch fix: `dotnet test tests/CrewTerm.App.Tests -c Release --filter CliIntegrationTests --no-restore --verbosity minimal`: **6 passed, 0 failed, 0 skipped**. This executes the current build's `crewterm.exe` for help/version/doctor, create/send/read/list/close, run/tasks/task, failure exit-code reporting and invalid arguments. Each case owns a unique temporary state directory and stops its daemon in test teardown.
- Final combined check: `dotnet test tests/CrewTerm.App.Tests -c Release --filter "DaemonTests\|DaemonTaskServiceTests\|DaemonTaskProtocolTests\|CliIntegrationTests\|TerminalResizeCoordinatorTests" --no-restore --verbosity minimal`: **34 passed, 0 failed, 0 skipped**.
- Final `dotnet build src/CrewTerm.App -c Release --no-restore --verbosity minimal`: **succeeded, 0 warnings, 0 errors**. A subsequent full Automation Release run passed **39/39**. Targeted `git diff --check` returned **exit 0**, and process inspection found no remaining daemon processes belonging to the CLI tests or installed-agent version probes.
