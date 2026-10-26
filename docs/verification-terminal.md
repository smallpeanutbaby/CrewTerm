# Terminal module verification

Verified on 2026-10-26 with .NET SDK 10.0.302 / runtime 10.0.10, Windows x64 10.0.26200.

## Delivered API

- `ConPtySession.Start(id, commandLine, workingDirectory, columns, rows)` starts a real process attached to Windows ConPTY.
- Sessions expose `Id`, `CommandLine`, `WorkingDirectory`, `ProcessId`, `Screen`, `HasExited`, `ExitCode`, `OutputReceived`; `Write`, `Resize` and idempotent `Dispose` control the owned session.
- A kill-on-close Windows Job Object owns the launched process and descendants. The process starts suspended, is assigned to the job, then resumes. Explicit disposal terminates the complete owned tree. UI detachment is implemented by the separate application daemon and must not dispose the session.
- `TerminalScreen.Feed`, `Resize`, `GetText` and `Snapshot` provide synchronized terminal state. Snapshots contain independent row arrays and immutable cells. Colors use RGB integers; wide-character continuation cells have width zero.
- Snapshots expose cursor visibility, primary/alternate screen state, bracketed-paste and application-cursor modes, mouse tracking and SGR mouse mode, plus the newest 2,000 history lines.
- `ResponseRequested` supplies cursor-position, device-status, device-attribute and basic Kitty-keyboard query responses. Callbacks run outside the screen-state lock.

## RED → GREEN evidence

1. `dotnet test tests/CrewTerm.Terminal.Tests --filter TerminalScreenTests --no-restore --verbosity minimal`
   - RED: CS0246, `TerminalScreen` did not exist.
   - GREEN: 4/4 passing after cursor-addressing, partial escape, line erase and detached-snapshot implementation.

2. `dotnet test tests/CrewTerm.Terminal.Tests --filter VtBehaviorTests --no-restore --verbosity minimal`
   - RED: 13/13 behavioral failures, including incorrect autowrap, empty scrollback, absent alternate screen and incorrect Chinese display width.
   - GREEN: 17 total tests passing after the VT implementation.
   - Cases cover SGR ANSI/indexed/RGB colors, cursor movement, erase/insert/delete, scrolling regions, saved cursor, reverse index, alternate-screen restore, bounded scrollback, CJK, split UTF-16 surrogate pairs, combining marks, replacing a wide glyph, and OSC/DCS suppression.

3. `dotnet test tests/CrewTerm.Terminal.Tests --filter "ConPtySessionTests|ResizeAndQueryTests" --no-restore --verbosity minimal`
   - RED: missing `ConPtySession`, `Resize` and `ResponseRequested` APIs.
   - First native implementation exposed a behavioral failure: two real command/input tests timed out; the process exited with its correct code but output bypassed ConPTY and reached the redirected test host.
   - Root cause: inherited redirected standard handles. Setting `STARTF_USESTDHANDLES` with null standard handles, matching Windows Terminal's startup pattern, made the child use ConPTY console handles.
   - GREEN: all 5 initial native tests passing. They verify actual command output and exit code 7, interactive input, resizing, idempotent disposal, missing working directory and invalid executable handling.

4. `dotnet test tests/CrewTerm.Terminal.Tests --filter "CmdShimWithSpaces|BareCommandFinds" --no-restore --verbosity minimal`
   - RED: the bare `.cmd` shim in the configured working directory failed with file-not-found. The explicit spaced shim path already worked.
   - GREEN: 2/2 passing after command resolution and explicit `cmd.exe /d /s /c` wrapping of batch launchers. Full suite then passed 27/27.

5. `dotnet test tests/CrewTerm.Terminal.Tests --filter WindowsCmdShimTakesPriorityOverExtensionlessUnixShim --no-restore --verbosity minimal`
   - RED: both bare and absolute extensionless npm-style commands failed with Win32 error 193, “%1 is not a valid Win32 application”, when a Unix shell shim and `.cmd` sibling coexisted.
   - GREEN: 2/2 passing after prioritizing `.exe`, `.com`, `.cmd`, `.bat` before extensionless files. Both cases use a real `.cmd` process and assert its emitted marker and exit code 9.

6. `dotnet test tests/CrewTerm.Terminal.Tests --filter "QueryReplyCallbacksDoNotHoldTheScreenLock|DisposeTerminatesDescendantProcesses" --no-restore --verbosity minimal`
   - RED: callback-lock regression failed because another thread could not take a snapshot while the reply callback ran. This exposed a lock-order inversion between `Feed`/native input and native resize/screen state.
   - The descendant-cleanup integration test passed before modification: a real PowerShell process starts a hidden child process, then disposal terminates that child through the job.
   - GREEN: 2/2 passing after collecting reply messages under the screen lock and dispatching them afterward. Production process-tree cleanup remains unchanged.

## Final module verification

```powershell
dotnet test tests/CrewTerm.Terminal.Tests -c Release --no-restore --verbosity minimal
# Passed: 31; failed: 0; skipped: 0.

dotnet build src/CrewTerm.Terminal -c Release --no-restore --verbosity minimal
# Succeeded; 0 warnings; 0 errors.
```

The integration tests launch only operating-system shells and generated temporary scripts. They do not invoke installed AI agents or consume model credits.

## Compatibility boundaries

- The native session backend is Windows ConPTY. Other platforms receive an explicit unsupported-platform error.
- Screen dimensions are bounded at 1–500 columns and 1–300 rows. Resizing preserves visible content around the cursor; it does not reflow old text into a new width.
- This is a tested subset of VT behavior for common terminal applications, not a claim of complete xterm conformance. Native image protocols, hyperlink activation, full emoji grapheme shaping, underline/italic rendering, terminal origin mode and every DEC extension are not implemented.
- Titles, hyperlink metadata and other OSC/DCS payloads are consumed without becoming visible text; they are not executed.
- Raw input is UTF-8. The application UI is responsible for encoding keys, paste and mouse events according to the mode flags exposed in each snapshot.
- User terminal listeners must not perform long blocking work on `OutputReceived`; screen parsing occurs before that callback and listener exceptions cannot stop terminal output draining.
- Killing an outer test harness job intentionally kills all processes inherited by that job, including a daemon launched inside it. This is distinct from a UI exiting normally; the application smoke test validates reconnect while preserving the outer harness until verification ends.

Reference used for the redirected-handle correction: [Windows Terminal ConPTY connection](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalConnection/ConptyConnection.cpp).
