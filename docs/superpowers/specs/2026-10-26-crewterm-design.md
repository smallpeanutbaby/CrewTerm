# CrewTerm design

User-approved direction: an independent C# terminal application resembling Herdr, with multiple real agent terminals, project file browsing, editing and Git review. On 2026-10-26 the user selected the name CrewTerm and explicitly requested sustained implementation in `D:\代码\CrewTerm`. That instruction authorizes execution of the design discussed in chat without another approval round.

## Product and platform

- Product: CrewTerm; command: `crewterm`; Chinese description: 多 Agent 协作终端.
- Primary verified platform: native Windows x64, .NET 10, Windows Terminal/PowerShell. Use Terminal.Gui 2 stable for the TUI, Windows ConPTY for terminal sessions, and Git CLI for repository operations.
- Use real local agent CLIs already installed by the user; never simulate an agent response or invent a successful test, usage, or cost result.
- The UI remains a terminal application. No browser or desktop window replaces it.
- UI and a user-scoped local daemon are separate processes. Detaching keeps sessions alive. Explicit stop terminates them. A machine or daemon restart can restore saved configuration and layout, not previous process memory.

## Modules

1. Core: project/profile settings, safe JSON persistence, navigation and command state.
2. Terminal: ConPTY lifecycle and input/resize; incremental VT screen, colors, cursor, alternate screen and bounded scrollback. Real process integration tests are mandatory.
3. Workspace: rooted file browser and search; editing, undo/redo, atomic saves preserving encoding, external-change conflicts; Git status/diff/stage and worktrees; reviewable checkpoints.
4. Automation: installed agent profile discovery, explicit/observed agent state, cancellable command execution, persisted test/task history, sequential pipelines with failure stops and a concurrency limit.
5. App: Terminal.Gui workspace UI, daemon protocol and CLI, command palette, project/session selectors, file/editor/diff/tasks views, keyboard and mouse actions.

## User workflows and acceptance

- Open/add a project; project list persists across restarts.
- Start a shell or configured agent command in the selected project. List, focus, resize, send input to and close a real session. Support at least a two-pane terminal view and single-pane focus.
- Launch a second UI/client and reconnect to existing daemon sessions. Closing the UI must leave the daemon running.
- Browse directories, open files with line numbers, edit and save, search text, undo/redo and jump to a line. Warn on unsaved buffers and external modifications before replacing them.
- View repository changes and selected file diffs; stage selected paths; list/create/open/remove worktrees with dirty-worktree protection.
- Parse a file path/line from terminal/test output, open it in the editor, and send a selected or current code context to a chosen session.
- Run a command/build/test from the project, stream output, show its actual exit code and elapsed time, cancel its process tree, and reopen saved run history.
- Configure a sequence of command tasks, run it in dependency order, and stop on failure. Existing agent CLIs may be called from these commands. Model-generated plans are not fabricated by the host application.
- Reusable agent profiles and project command templates; configurable parallel command limit. Usage is displayed as unavailable unless a provider actually supplies it.
- Local file checkpoints preserve existing edits and restore only after displaying differences and explicit user choice.
- SSH may be used through a configured real `ssh` terminal command; remote files are not mislabeled as local files, and remote daemon federation is not claimed.

## Error and safety behavior

Each terminal binds to its launch project/worktree, including in the editor header. Current shell directory is not guessed. Invalid/missing folders produce actionable errors. Writes check original bytes before atomic replacement; no silent overwrites. Git is invoked with argument arrays, never concatenated filenames. The daemon accepts only the current Windows user and uses bounded messages. No auto-approve of agent prompts, automatic pushes, or destructive resets. User input and agent output remain data.

## Delivery and verification

TDD for all testable behavior: retain RED commands and expected failures, implement the smallest change, rerun focused GREEN tests, then refactor. Each module has its own xUnit project. Final validation includes the whole solution tests, Release build, self-contained Windows publish, CLI/daemon/ConPTY integration checks and a real TUI smoke run. Deliver README, shortcuts, test evidence and a runnable package. Known limitations must be stated rather than hidden behind a success count.
