<div align="center">

# CrewTerm

**A cross-platform terminal workspace for running and coordinating multiple AI coding agents side by side**

[![CI](https://github.com/smallpeanutbaby/CrewTerm/actions/workflows/ci.yml/badge.svg)](https://github.com/smallpeanutbaby/CrewTerm/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/smallpeanutbaby/CrewTerm?include_prereleases&sort=semver)](https://github.com/smallpeanutbaby/CrewTerm/releases)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-informational)](#platform-support)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[简体中文](README.md) · English

</div>

CrewTerm is a terminal user interface (TUI) written in C#. It runs Claude Code, Codex, other agent CLIs or any shell side by side
in one window, next to your project files, a code editor, Git changes and background tasks — and lets agents message each other,
hand off work and wait for each other's state.

Every session is a real pseudoterminal process (ConPTY on Windows, `forkpty` on Linux and macOS) owned by a background service:
close the UI and your agents and tasks keep running; open CrewTerm again and you are back where you left off.

## Features

**Terminals and layout**

- Built-in profiles for Claude Code, Codex, OpenCode, Gemini CLI, GitHub Copilot CLI and your shell; add any CLI or `ssh` target.
- Named tabs and nested splits in four directions, saved per project. Closing a layout never kills processes.
- VT emulation: colors and text styles, alternate screen, CJK wide characters and common emoji, 2,000-line scrollback,
  SGR / legacy mouse protocols, local selection and copy, jump from `file:line` to code.

**Multi-agent coordination**

- Semantic agent state — working / waiting for input / done (unread) / idle / exited / unknown — with source and reason.
  Plain output is never mistaken for "done".
- Directed inbox, reply threading, prompt delivery, task handoff and state waits. An agent waiting for approval is never auto-answered.
- Lifecycle hooks for Claude Code and Codex apply to a single launch and never modify the provider's global configuration.
- Native conversation resume with the provider's exact session ID — resume, restart or forget, never "whatever was last".

**Project tools**

- File browser, syntax-highlighted editor and full-text search. Saves keep the original encoding and line endings and refuse to
  overwrite files an agent changed on disk.
- Send selected code with path and line numbers to a chosen agent via bracketed paste; you confirm before it is submitted.
- Git changes, diffs, per-file staging and worktree management (one isolated branch directory per agent).
- Background tasks and sequential pipelines, file checkpoints with preview-before-restore, and local plugins with timeouts and output limits.
- A complete CLI: terminals, tasks, state, messages, handoffs, native sessions and plugins are all scriptable.

## Platform support

| Platform | Architectures | Terminal backend | CI |
| --- | --- | --- | --- |
| Windows 10 1809+ / Windows 11 | x64, arm64 | ConPTY + job objects | `windows-latest` |
| Linux (glibc) | x64, arm64 | `forkpty` + process groups | `ubuntu-latest` |
| macOS | Apple Silicon, Intel | `forkpty` + process groups | `macos-latest` (Apple Silicon) |

Every push builds and runs the full test suite on all three systems. Platform-specific behavior (Windows `.cmd` launcher rules,
Unix execute permissions and signals) is covered by tests that run only on the matching platform.

## Installation

### Release packages

Download the archive for your platform from [Releases](https://github.com/smallpeanutbaby/CrewTerm/releases).
Packages are self-contained; no .NET installation is required.

| System | File | Run |
| --- | --- | --- |
| Windows | `crewterm-<version>-win-x64.zip` | Extract and run `Start CrewTerm.cmd` or `crewterm.exe` |
| Linux | `crewterm-<version>-linux-x64.tar.gz` | `mkdir -p ~/.local/crewterm && tar -xzf crewterm-*.tar.gz -C ~/.local/crewterm`, then run `~/.local/crewterm/crewterm` |
| macOS | `crewterm-<version>-osx-arm64.tar.gz` | As on Linux; run `xattr -dr com.apple.quarantine ~/.local/crewterm` before the first start |

Add the extracted directory to `PATH` to use `crewterm` anywhere. Packages are not code-signed: Windows may show a SmartScreen
prompt on first run, and macOS needs the quarantine attribute removed as shown above.

### From source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```sh
git clone https://github.com/smallpeanutbaby/CrewTerm.git
cd CrewTerm
dotnet run --project src/CrewTerm.App -c Release -- --project /path/to/your/project
```

### Prerequisites

- A modern true-color terminal (Windows Terminal, iTerm2, GNOME Terminal, WezTerm, …), at least 120 × 35, with a monospace font
  that covers the characters you use.
- Git, for the Git features.
- Agent CLIs are installed and signed in by you. CrewTerm never installs them, signs in, or stores model API keys.

## Quick start

```sh
crewterm --project ~/work/my-app
```

1. **F2** adds more local projects.
2. **F3** starts an installed agent (the list shows the resolved program path) or a custom command such as `bash`, `pwsh` or `ssh host`.
3. **F9** opens the command palette: `Terminal: Split right` and friends split in four directions, `Tab: New` adds a tab; drag dividers to resize.
4. **Alt+4** browses files (the file pane is hidden by default; **F4** toggles it), **Alt+3** edits, **F5** saves; **F6** shows Git changes; **F7** runs tests or pipelines.
5. Use `Agent: Notifications`, `Agent: Inbox` and `Agent: Handoff prompt` from the palette to coordinate agents.
6. **F10** detaches; sessions and tasks keep running. Run `crewterm` again to return to the same layout.

Agents working in the same directory see each other's edits. To isolate them, create a worktree per agent with **F6 → Worktrees: create isolated branch**.

## Keyboard shortcuts

| Key | Action |
| --- | --- |
| F1 | Help |
| F2 / F3 | Add project / new agent or terminal |
| F4 / F5 | Toggle file pane / save file |
| F6 / F7 | Git / background tasks and pipelines |
| F8 / F9 | Quick split switch / searchable command palette |
| F10 | Detach, keeping sessions and tasks running |
| Alt+1 … Alt+5 | Focus first / second terminal pane, editor, file list, sidebar `agents` |
| Ctrl+b, then a key | herdr-style prefix keys: `N` new space, `c` new tab, `v` / `-` split, `x` close pane, `q` detach, `?` all keys |
| Ctrl+Shift+P | Command palette |
| Ctrl+Shift+V / Ctrl+Shift+C | Paste into the terminal / copy selection (or the visible screen) |
| Shift + mouse, wheel | Local selection, menu or scrollback even when the program tracks the mouse |

Every picker filters as you type. The terminal owns ordinary keys, Tab, Ctrl+C and Esc; F1–F10 are reserved by the workspace —
use `Terminal: Send reserved key` to send them to the program.

> **macOS:** enable "Use Option as Meta key" in Terminal.app (iTerm2: "Left Option key: Esc+") for the Alt shortcuts;
> function keys may need Fn.

## Command line

All subcommands talk to the same background service and print JSON (`read` prints screen text). They exit with 0 on success,
or print the reason to standard error and exit with 1.

```sh
crewterm sessions                                     # list running terminals
crewterm new --command "claude" --project . --name "Reviewer"
crewterm send <ID> --text $'npm test\r'              # raw input (\r is Enter)
crewterm read <ID>                                    # screen text
crewterm run --command "dotnet test" --project .      # background task; tasks / task <ID> / cancel <ID>
crewterm agent-state <ID>                             # semantic state and evidence
crewterm agent-wait <ID> --state done,blocked --timeout-ms 30000
crewterm message-send --from <A> --to <B> --text "Please review this change"
crewterm plugin-install --path ./my-plugin            # disabled until plugin-enable
crewterm stop                                         # stop the service and all its terminals and tasks
```

See the [CLI guide](docs/cli-guide.md) (Chinese) for every command, option and exit code.

## Data and configuration

The state directory holds projects, layouts and settings, agent profiles, command and pipeline templates, the native session catalog,
agent state and notifications, inboxes, plugin registrations, task history and file checkpoints.

| System | Default state directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\CrewTerm` |
| Linux | `$XDG_DATA_HOME/CrewTerm`, or `~/.local/share/CrewTerm` |
| macOS | `~/Library/Application Support/CrewTerm` |

`--state-dir PATH` gives you a fully independent workspace and background service. State files are written atomically
(temporary file → flush → replace); corrupt files are preserved rather than overwritten, and concurrent saves report a conflict.

## Security model

- **Only the current user can reach the service.** Both Windows named pipes and Unix domain sockets are restricted to the current user,
  and the client verifies the server's identity. On Linux and macOS, a newly created state directory is `0700`.
- **No hijacking from the project directory.** A bare program name at the start of a terminal command is looked up only in absolute
  `PATH` entries, never in the working directory; the Git that CrewTerm runs is resolved the same way; and on Windows, cmd in terminal
  sessions, background tasks, command templates and pipelines does not search the current directory either. An untrusted repository
  therefore cannot shadow `git`, `node`, `ssh` or `dotnet`. Run project scripts as `./build.sh` or `.\build.cmd`.
- **Input cannot escape.** Pasted text, code context and prompts are stripped of `ESC` and other control characters before they reach
  a terminal, so they cannot end a bracketed paste early or inject keystrokes.
- **Display cannot be injected.** Control characters in session names, messages and plugin names are rejected at the service boundary;
  command and plugin output is sanitized before display.
- **Plugins are not sandboxed.** Local plugins run as you and are disabled after installation; install only plugins you trust.
- CrewTerm never stores model API keys, estimates tokens or costs, or bypasses any agent's approvals.

Please report vulnerabilities privately as described in the [security policy](SECURITY.md).

## Architecture

```
CrewTerm.App ──► CrewTerm.Terminal     pseudoterminals (ConPTY / forkpty) and the VT screen
     │      ──► CrewTerm.Workspace    files, editing, Git, checkpoints ────────────┐
     │      ──► CrewTerm.Automation   tasks, agent state and messaging, plugins ───┤
     └──────► CrewTerm.Core           paths, atomic I/O, state files, models ◄─────┘
```

Dependencies point downward only: Core depends on nothing, Terminal, Workspace and Automation are independent of each other,
and only App composes them. Inside App, `UI` and `Cli` → `Client` → `Protocol` ← `Server`; the service never references UI code.
See the [architecture document](docs/architecture.md) (Chinese) for the process and threading model, persistence, and the cross-platform design.

## Building from source

```sh
dotnet build CrewTerm.slnx -c Release       # warnings are errors
dotnet test  CrewTerm.slnx -c Release       # full test suite
./scripts/publish.sh                        # Linux / macOS: artifacts/<RID>, self-contained
./scripts/publish.ps1 -Runtime win-x64      # Windows (or PowerShell 7 on any platform)
```

The build enforces the engineering rules: missing XML documentation, broken doc references, unused `using` directives and
namespaces that do not match folders all fail the build. Tests start real pseudoterminals, shells, Git repositories, the named-pipe
service and the full UI process. Set `CREWTERM_TEST_EXE` to a published executable to run the UI and CLI tests against a release build.
Run `./scripts/verify.sh` (or `./scripts/verify.ps1`) before submitting changes.

## Known limitations

- The terminal is a tested VT subset: no image protocols, pixel mouse reporting or Kitty keyboard enhancements; scrollback is plain text.
- Multi-line prompts are delivered only to agents with bracketed paste enabled. Coordination is explicit: no automatic task splitting or implicit broadcast.
- Edited files are limited to 4 MiB, search to 500 results, checkpoints to 4,000 files / 64 MiB; restores are per file, not one atomic transaction.
- Up to 32 terminal sessions per service; tasks keep the last 256K characters of output and the latest 100 history entries.
- Ending a session ends its whole process tree (Windows job object / Unix process group). On Unix, descendants that start their own session (for example with `setsid`) are outside that group.
- No plugin marketplace, multi-device sync or remote machine management; you can run `ssh` in a terminal, but the file pane and service are local.

## Documentation

The full documentation is written in Chinese and starts at the [documentation home](docs/README.md).

| Area | Documents |
| --- | --- |
| Getting started | [Quick start](docs/getting-started.md) · [User guide](docs/user-guide.md) · [Keys and commands](docs/commands.md) · [FAQ](docs/faq.md) · [Glossary](docs/glossary.md) |
| Agents and automation | [Agent integration](docs/agent-integration.md) · [Multi-agent workflows](docs/workflows.md) · [CLI guide](docs/cli-guide.md) · [Native sessions](docs/native-sessions.md) |
| Extending and integrating | [Local plugins](docs/local-plugins.md) · [Plugin example](examples/plugins/workspace-notes/README.md) · [Service protocol](docs/protocol.md) · [Configuration and data](docs/configuration.md) |
| Development | [Developer guide](docs/development.md) · [Architecture](docs/architecture.md) · [Contributing](CONTRIBUTING.md) · [Code of conduct](CODE_OF_CONDUCT.md) |
| Project | [Changelog](CHANGELOG.md) · [Security policy](SECURITY.md) · [Verification records](docs/verification/README.md) |

## Contributing

Issues and pull requests are welcome. Please read the [contributing guide](CONTRIBUTING.md) and the
[developer guide](docs/development.md) first. By participating you agree to follow the [code of conduct](CODE_OF_CONDUCT.md).

## License

CrewTerm is released under the [MIT License](LICENSE).
