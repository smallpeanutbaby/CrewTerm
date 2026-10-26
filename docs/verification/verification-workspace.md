# Workspace verification

Verified on native Windows x64 with .NET SDK 10.0.302. No AI provider was launched; Git tests use disposable real local repositories.

## TDD evidence

| Stage | Command | Observed result |
| --- | --- | --- |
| RED, new file APIs | `dotnet test tests/CrewTerm.Workspace.Tests --filter FileWorkspaceTests --no-restore --verbosity minimal` | Build failed with missing `FileWorkspace` and `FileConflictException` before implementation. |
| Behavioral RED, binary search | Same file test command after initial implementation | 11 passed, 1 failed: binary file raised `InvalidDataException` through the search filter. Reflection confirmed this exception derives from `SystemException`, not `IOException`. |
| Focused reproduction | `dotnet test tests/CrewTerm.Workspace.Tests --filter SearchFindsLiteralMatchesAndSkipsGeneratedAndBinaryFiles --no-restore --verbosity minimal` | 1 failed, expected binary search exception. |
| GREEN, file APIs | `dotnet test tests/CrewTerm.Workspace.Tests --filter FileWorkspaceTests --no-restore --verbosity minimal` | 12 passed after explicitly handling invalid text/binary files. |
| RED, Git APIs | `dotnet test tests/CrewTerm.Workspace.Tests --filter GitServiceTests --no-restore --verbosity minimal` | Missing `GitService` and `GitOperationException` before implementation. |
| GREEN, Git APIs | Same command | 7 passed using real Git status, diff, staging, rename and worktree operations. |
| RED, checkpoint APIs | `dotnet test tests/CrewTerm.Workspace.Tests --filter CheckpointStoreTests --no-restore --verbosity minimal` | Missing `CheckpointStore` before implementation. |
| GREEN, checkpoints | Same command | 6 passed, including refusing changes after preview. |
| Regression RED, worktree metadata | `dotnet test tests/CrewTerm.Workspace.Tests --filter 'FullyQualifiedName~LinkedWorktreeGitMetadataFile\|FullyQualifiedName~ProjectsInsideRepositories' --no-restore --verbosity minimal` | 2 failed and 1 passed. `.git` is a file in linked worktrees; the directory-only exclusion accidentally searched and checkpointed it. |
| Regression GREEN | Same command | 3 passed after excluding `.git` metadata files as well as directories. |

The `\|` in the table is Markdown escaping: the actual quoted filter uses a plain pipe (`|`).

## Final module verification

- `dotnet test tests/CrewTerm.Workspace.Tests -c Release --no-restore --verbosity minimal`: **28 passed, 0 failed, 0 skipped**.
- `dotnet build src/CrewTerm.Workspace -c Release --no-restore --verbosity minimal`: **0 warnings, 0 errors**.
- `git diff --check -- src/CrewTerm.Workspace tests/CrewTerm.Workspace.Tests`: exit 0.

## Verified behavior

- Project-relative browsing, directory-first ordering and reparse-point exclusion.
- Rejecting `..` traversal and symlink/junction traversal.
- UTF-8 with/without BOM and UTF-16 editing, newline preservation, undo/redo and dirty state.
- Saving through a same-directory durable temporary file followed by replacement; repeated original-content checks reject external edits and deletions.
- Literal case-insensitive source search, cancellation, generated-file and binary exclusion.
- Literal Git path arguments, spaces/Chinese/brackets in filenames, staged/unstaged/untracked diffs, renames and deleted-file staging.
- Projects rooted inside a repository get changes relative to that project and exclude unrelated sibling changes.
- Creating/listing/removing Git worktrees, retaining branches and refusing dirty-worktree removal.
- Checkpoints preserve pre-existing edits and exact binary bytes, preview changes and refuse any content/new-file changes after preview.
- A reviewed restore can recreate missing files and remove explicitly listed post-checkpoint files; generated directories and Git metadata are untouched.

## Boundaries

- Editable text is UTF-8, BOM-marked UTF-16 or UTF-32; legacy code pages are not guessed. Files larger than 4 MiB or containing NUL characters are rejected by the editor.
- Search is literal, capped at 500 matches, and skips `.git`, `.vs`, `bin`, `obj`, `node_modules`, `artifacts` and `.crewterm` directories. It skips inaccessible directories and non-text files.
- Checkpoints include eligible source files, including small binary files. They fail rather than silently truncate at 4000 files, 4 MiB per file or 64 MiB total. Checkpoint storage must be outside the project. Reparse points are never followed.
- Checkpoint restore requires the caller to show the preview and obtain an explicit restore action. It preflights all files and rechecks each file before mutation. Individual replacements are atomic; a multi-file restore is not a filesystem transaction, so a mid-restore I/O failure can leave a subset restored and returns an error. Obtain a fresh preview before retrying.
- File content checks prevent ordinary concurrent editing conflicts; an external process can still race the final check and atomic replacement. There is no cross-process filesystem compare-and-swap primitive in this implementation.
- Git mutations use `ProcessStartInfo.ArgumentList` and literal pathspecs. No automatic pushes, resets, branch deletion or forced worktree removal are performed.

## Editor safety during checkpoint restore

The restore completion callback previously called `editor.Close(true)` after the asynchronous disk operation. A user could edit the current document or open another file while restore was pending, and that callback would discard the newer buffer. It now captures the document identity before starting restore and refreshes only that same, unmodified document. A different or dirty document is preserved with a status message explaining that disk contents were restored. If restore removed the original clean file, its editor is safely closed.

| Stage | Command | Observed result |
| --- | --- | --- |
| RED, new reload API | `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~EditorBindingTests.RestoreCompletion --no-restore` | Missing `TryReloadUnchanged` before implementation; the concurrent `SessionRefreshGate` addition also temporarily prevented compilation. |
| Behavioral RED | Same command, after extracting the existing `Close(true)` completion behavior into the new method | 3 failed: newer edits and a newly opened document were discarded; the original clean document was cleared instead of reloaded. |
| GREEN | `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~EditorBindingTests --no-restore` | 8 passed after checking document identity and dirty state, then reopening only the original clean file. |
| Deleted-file RED | `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~EditorBindingTests.RestoreCompletionCloses --no-restore` | 1 failed with `FileNotFoundException` when restore removed the open file. |
| Final editor GREEN | `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~EditorBindingTests --no-restore` | 9 passed after safely closing the original clean buffer when its file was removed. |

Additional verification: Workspace tests in Release passed 28/28; App Release build completed with 0 warnings and 0 errors. The unchanged-buffer positive case also edits and saves after reloading, verifying that disk conflict tracking is rebound to the restored contents.

## Embedded terminal mouse input

The public Terminal.Gui `View.NewMouseEvent(Mouse)` entry point is exercised against two real `TerminalPane` controls. Tests use different screen and viewport coordinates to verify local coordinate forwarding and confirm that only the target pane sends input. Mouse-down focuses the clicked pane; wheel input does not change keyboard focus.

- Behavioral RED before production changes: `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~TerminalMouseTests --no-restore` produced **2 failed, 3 passed**. Click and wheel events emitted no child input.
- Encoder RED: the same command failed to compile because `TerminalMouse` did not exist before the encoding tests were added.
- GREEN: the same command passed **33/33** after implementation.
- Broader App verification: `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj -c Release --filter 'FullyQualifiedName!~TuiSmokeTests' --no-restore` passed **76/76**, with no skips. Full ConPTY UI smoke tests are verified separately by the application integration workflow.

Supported input is left/middle/right button press and release, vertical wheel events, and Shift/Alt/Ctrl modifiers. Forwarding requires both supported tracking mode 1000/1002/1003 and SGR mode 1006. Coordinates are checked against the pane viewport and child screen. Synthetic click events are ignored after the raw press/release pair. Mouse motion, horizontal wheel, legacy mouse encodings, selection copying and native scrollback wheel handling are outside this implementation; F9 exposes scrollback independently. SGR syntax and modifier values were checked against the [xterm control-sequence specification](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Extended-coordinates) and the installed Terminal.Gui 2.5.0 XML documentation.

## Session input draining

`SessionInputDispatcher` accepts at most 2048 queued items, preserves accepted cross-session input order with one sender, and stops accepting input before drain begins. A successful drain waits for pending text and Enter input. A timeout cancels the sender and returns false without waiting forever for a sender that ignores cancellation. Send failures are reported through the optional worker-thread callback; later accepted input is still attempted, and drain reports failure. `Dispose` is a cancellation fallback; normal detach must drain first.

- New-API RED: `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~SessionInputDispatcherTests --no-restore` failed to compile because the dispatcher did not exist.
- Behavioral RED: `dotnet test tests/CrewTerm.App.Tests/CrewTerm.App.Tests.csproj --filter FullyQualifiedName~SessionInputDispatcherTests.DrainWaits --no-restore` failed after the prior close-then-immediately-cancel order was extracted: drain had already completed while the first send was still blocked.
- GREEN: the full dispatcher filter passed **8/8** after cancellation was moved to the timeout path.
- Tests cover blocked cross-session ordering and Enter delivery, queue capacity without silently dropped accepted items, late-write rejection, cancellation on timeout/dispose, error reporting and callback isolation, and a sender that ignores cancellation.
