# CrewTerm 0.2 terminal and mouse verification

Date: 2026-10-26. Host: Windows, .NET SDK 10.0.302, Terminal.Gui 2.5.0.

Scope: `CrewTerm.Terminal`, `UI/TerminalPane.cs`, `UI/TerminalMouse.cs`, and their tests. Subsequently authorized integration fixes change `WorkspaceWindow`'s terminal-layout selection callback and the `ChooseSecondary` method, as recorded below. Server protocol, daemon lifecycle, Program, Window clipboard/context-menu wiring and whole-product acceptance belong to the root integration task.

## Implemented behavior

- `ConPtySession.Start` preserves its existing arguments and adds optional `IReadOnlyDictionary<string,string?>? environment = null`. The child inherits the parent environment, receives `TERM=xterm-256color` and `COLORTERM=truecolor` to describe its actual virtual terminal, and then receives explicit overrides. Null removes a variable. The Unicode environment block is child-only, rejects NUL/equal-sign injection, and is freed after process creation. Parent environment variables are never changed. Existing job kill-on-close semantics and output-drain/query-lock ordering remain intact.
- Incremental VT styles include bold, faint, italic, underline, blink, conceal and strike-through; individual resets, semicolon/colon indexed and RGB color forms; colon underline variants render as ordinary underline. Added origin-relative addressing within scrolling margins, insert/newline modes, DEC G0/G1 line drawing with shift-in/out, terminal reset, and DECSC/DECRC rendition restoration.
- Combining marks, split surrogate pairs, common emoji ZWJ sequences, skin-tone modifiers, regional-indicator flags and VS16 presentation remain in a single cell grapheme with continuation width. A late VS16 at the right margin moves the whole glyph to the next line instead of leaving a wide glyph in one cell.
- Mouse reports support 1000 click, 1002 button-motion, 1003 all-motion, SGR 1006, legacy reports and UTF-8 1005. Left/middle/right press and release, wheel up/down, horizontal wheel, modifiers, drag and hover are encoded. Synthetic clicks are suppressed to avoid duplicate input. Application drags grab the real Terminal.Gui mouse and clamp an out-of-pane release to the last visible cell, so the child receives the release.
- Local text selection uses real press/move/release events, includes whole wide/combining glyphs, supports reverse and multiline selection, highlights cells, and freezes the viewed snapshot while selected. Incoming output still updates the live snapshot. Shift at gesture start overrides application tracking. Wheel without tracking (or Shift-wheel) browses history without changing keyboard focus. Typing or `ScrollToBottom` resumes the live view. History is clipped at its beginning after a resize instead of replaying through a narrow one-row screen and losing the first columns.
- `TerminalPane` exposes `SelectedText`, `HasSelection`, `ScrollOffset`, `CopyOnSelect` (default true), `CopyRequested: Action<string>`, `ContextMenuRequested: Action<System.Drawing.Point>` (screen coordinates), `CopySelection`, `ClearSelection`, `ScrollHistory(int)` (positive = older), and `ScrollToBottom`. Right-button press requests the local menu when untracked or Shift is held. Changing `SessionId` clears the previous selection/scroll view and releases a mouse grab.

## RED → GREEN evidence

Every testable production change was preceded by an executed failing test. New public APIs initially produced expected missing-member compilation failures, as allowed by the implementation plan; subsequent behavior failures were then observed and corrected. Commands below ran from `D:\代码\CrewTerm`.

| Area | RED command and observed failure | GREEN result |
| --- | --- | --- |
| Child environment | `dotnet test tests/CrewTerm.Terminal.Tests --filter "SessionEnvironmentIsChildLocal\|NullEnvironmentOverride" --no-restore --verbosity minimal` | Missing six-argument `Start` overload → 2 real-process tests passed; two concurrent sessions emitted different Chinese values and parent variables stayed unchanged. |
| Environment validation | `dotnet test tests/CrewTerm.Terminal.Tests --filter FullyQualifiedName~InvalidEnvironmentEntries --no-restore --verbosity minimal` | 4 failures: invalid entries were accepted or reached Win32 with invalid data → 4 passed after validating the environment block input. |
| Headless-host capabilities | `dotnet test tests/CrewTerm.Terminal.Tests --filter "FullyQualifiedName~NativeSessionsAdvertise\|FullyQualifiedName~ExplicitEnvironmentOverrides" --no-restore --verbosity minimal` | Actual output was `terminal=dumb color=%COLORTERM%` → 2 passed with terminal defaults and explicit override/null precedence. |
| Mouse protocols | `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter FullyQualifiedName~TerminalMouseTests --verbosity minimal` | 10 new failures for missing legacy/motion and incorrect horizontal encoding → 42 passed. Additional UTF-8 coordinate test failed returning null → passed after mode-aware bounds. |
| Selection/history/context | `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter FullyQualifiedName~TerminalPaneSelectionTests --verbosity minimal` | Expected missing selection APIs; after implementation one behavioral failure showed real `View.NewMouseEvent` dropped hover → enabled `MousePositionTracking`, 11 new selection cases passed. |
| Session switch / grab lifecycle | Focused `ChangingSessionClears...` and `ChangingSessionReleases...` filters | Stale selection and an actual `Application.Mouse.IsGrabbed` assertion failed → cleared frozen state and released application grab; 2 real application grab cases passed. |
| Drag outside viewport | Focused `ApplicationDragRelease\|LocalDragRelease` filters | Child received only its press when release occurred outside viewport → press/motion/release now delivered, 2 passed. |
| Narrowed history | Focused `HistoryFromAWider` filter | Expected `abcde`, actual `fghij` → kept original history width before clipping; passed. |
| VT styles/modes/Unicode | `dotnet test tests/CrewTerm.Terminal.Tests --filter FullyQualifiedName~ExtendedVtTests --no-restore --verbosity minimal` | Missing style properties, then 9 behavioral failures in origin, insert/newline, charset, reset, colon colors and emoji → 10 passed. Later `DecSavedCursor\|EmojiVariationSelectorAtRight` produced two expected failures → both passed. |
| Raw input boundary | `dotnet test tests/CrewTerm.Terminal.Tests --filter FullyQualifiedName~TerminalInputEncodingTests --no-restore --verbosity minimal` | Missing `EncodeInput` → 8 passed, asserting literal byte arrays and unchanged UTF-8 for ordinary or malformed packet text. |
| Rendered styles | Focused `TerminalPaneStyleTests` | Missing attribute mapping, then conceal foreground differed from background → style mapping and conceal cases passed. |

Two obsolete v0.1 expectations were intentionally updated for the requested v0.2 behavior: non-SGR tracking is now supported, so it is no longer expected to return null; Terminal.Gui defines `WheeledLeft/Right` as aliases for Ctrl+vertical-wheel, so those events now map to horizontal codes 66/67 rather than Ctrl+vertical codes 80/81. Duplicate theory data caused by that alias was removed; unrelated cases were retained.

A new environment test initially waited only for process exit and raced the independent output reader. Its wait condition was corrected to require exit **and** the final output marker, preserving the content assertions. This changes test synchronization, not product exit/drain behavior.

## Real ConPTY and installed provider checks

`NativeInputTests` uses an actual PowerShell-hosted C# fixture with native console modes and `Console.OpenStandardInput`. It does not mock ConPTY. Three cases passed:

1. Chinese plus `é` input reaches an input-code-page-65001 reader as the expected UTF-8 bytes.
2. A negotiated legacy mouse packet with column coordinate byte `0xB7` reaches a code-page-1252 reader as exactly `1B-5B-4D-20-B7-27`.
3. The same packet reaches a code-page-65001 reader as `1B-5B-4D-20-C2-B7-27`, preserving the coordinate character through Windows' input-code-page conversion. An initial probe incorrectly expected a single byte from the UTF-8 reader; the fixture now explicitly tests both code pages. The actual outgoing six-byte packet is independently asserted by `TerminalInputEncodingTests`.

The raw-byte exception is narrowly bounded: active 1000/1002/1003 tracking, no SGR or UTF-8 mouse mode, exactly six characters, `ESC [ M` prefix, valid button and coordinate ranges. All other input remains UTF-8. SGR packets contain ASCII decimal coordinates and avoid this legacy code-page boundary.

A disposable .NET console probe using the current `ConPtySession` ran these installed commands inside a fresh temporary directory:

| Command | Observed result |
| --- | --- |
| `claude --help` | Exit 0; usage/options rendered and 147 history rows captured. |
| `codex --help` | Exit 0; usage/options rendered and 98 history rows captured. |
| `claude` | Rendered the actual workspace trust screen; bracketed paste enabled. No trust confirmation or model prompt was sent. |
| `codex` | Before TERM fix: stopped at `TERM is set to dumb`. After fix: rendered the actual update-selection UI, alternate screen enabled, mouse tracking 1003 and bracketed paste enabled. No update choice or model prompt was sent. |

The probe disposed every session, exercising job-tree cleanup. It did not install/update providers, approve permissions, or make paid model requests. Interactive model streaming and every provider-specific screen are therefore outside this verification claim.

## Final module verification

- `dotnet test tests/CrewTerm.Terminal.Tests -c Release --no-restore --verbosity minimal` — **62 passed, 0 failed, 0 skipped**. Includes actual process output/input/resize, `.cmd` shims, environment isolation, descendant termination, query locking and the new VT/input tests.
- `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter "FullyQualifiedName~TerminalPane\|FullyQualifiedName~TerminalMouse\|FullyQualifiedName~TerminalInput" --verbosity minimal` — **66 passed, 0 failed, 0 skipped**.
- `dotnet build src/CrewTerm.Terminal -c Release --no-restore --verbosity minimal` — **0 warnings, 0 errors**.
- Scoped `git diff --check` — exit 0, no whitespace errors; Git reported only its existing LF→CRLF conversion notices.

App module tests use the separate `TerminalTests` configuration to avoid touching the user's running Debug daemon or the root agent's Release UI test processes. Whole-solution Release tests and published-UI verification are performed by the integration task.

## Practical compatibility boundaries

This is a concrete Windows terminal compatibility subset, not universal xterm conformance. Mouse encoding follows the [xterm control-sequence reference](https://www.invisible-island.net/xterm/ctlseqs/ctlseqs.html). Legacy coordinates are limited to 223 cells per axis; SGR is preferred. The application screen remains bounded to 500 columns, 300 rows and 2,000 plain-text history rows. History does not retain original colors or reflow old lines. Selection trims trailing blank columns and does not include automatic scroll-on-drag beyond the viewport; wheel first to select historical text.

Terminal.Gui 2.5 cannot distinguish Ctrl+vertical-wheel from its horizontal-wheel aliases. Underline variants use a single underline; visible blink/italic support depends on the enclosing terminal/font. Emoji handling covers the tested common combinations, not complete Unicode grapheme segmentation, Indic shaping or bidi layout. Image protocols, pixel mouse coordinates, Kitty keyboard extensions, active OSC hyperlinks, xterm focus-report events, arbitrary charset sets and full DEC saved-mode semantics are not claimed. Existing primary/alternate buffer and resize behavior is retained, but the implementation does not emulate every xterm mode or reflow screen content.

## Authorized mouse-tab refresh follow-up

The read-only layout review found that keyboard tab switching invalidated the pending screen-refresh generation, but clicking a tab did not. The Window's `SelectionChanged` callback only updated `LastSessionId`, allowing a previously captured refresh to update a disposed old pane. Root authorized a regression plus this single callback change.

`MouseTabRefreshRegressionTests.MouseTabSwitchRejectsThePreviousPanesDelayedRefresh` constructs the real `WorkspaceWindow` and `TerminalLayoutView`, captures that Window's actual `SessionRefreshGate` generation, and clicks a tab through `View.NewMouseEvent`. It then attempts the old pane's delayed snapshot update and proves that a fresh generation can still update the current pane. The test reads the private gate with reflection; it introduces no production test API, replaced callback, fake daemon or timing-dependent network fixture.

- RED: `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter FullyQualifiedName~MouseTabRefreshRegressionTests --verbosity minimal` — failed at `Assert.False(applied)` because the previous generation was still accepted.
- Minimal change: call `refreshGate.SelectionChanged()` inside the existing terminal-layout `SelectionChanged` callback before recording `LastSessionId`.
- GREEN: identical command — 1 passed.
- Broader verification: `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter "FullyQualifiedName~MouseTabRefreshRegressionTests\|FullyQualifiedName~SessionRefreshGateTests\|FullyQualifiedName~TerminalLayoutViewTests\|FullyQualifiedName~TerminalPane\|FullyQualifiedName~TerminalMouse\|FullyQualifiedName~TerminalInput" --verbosity minimal` — 75 passed, 0 failed, 0 skipped.
- `dotnet build src/CrewTerm.App -c TerminalTests --no-restore --verbosity minimal` — 0 warnings, 0 errors.

The separate Rebuild focus-callback crash and keyboard-return-to-live-view findings were reported to root, who owns their fixes and acceptance tests. They are not additional Window edits by this module.

## Empty-pane mouse follow-up

`TerminalPane.HandleMouse` originally returned before handling focus or context menus when no snapshot existed. This prevented choosing an empty split and opening its Agent-start menu.

- RED: `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter FullyQualifiedName~TerminalPaneContextTests --verbosity minimal` — 2 failures: left-click left focus on the other pane; right-click produced no callback.
- Minimal change: handle in-bounds press focus and eligible local right-click before requiring a terminal snapshot. Populated panes still honor negotiated application tracking and Shift override. Empty panes do not grab a text selection or emit PTY input.
- GREEN: identical focused command — 2 passed, including press/release/synthetic-click menu deduplication and exact screen-coordinate assertions.
- `dotnet test tests/CrewTerm.App.Tests -c TerminalTests --no-restore --filter "FullyQualifiedName~TerminalPane\|FullyQualifiedName~TerminalMouse\|FullyQualifiedName~TerminalInput\|FullyQualifiedName~MouseTabRefreshRegressionTests" --verbosity minimal` — 69 passed, 0 failed, 0 skipped.
- `dotnet test tests/CrewTerm.Terminal.Tests -c Release --no-restore --verbosity minimal` — 62 passed.
- `dotnet build src/CrewTerm.App -c TerminalTests --no-restore --verbosity minimal` — 0 warnings, 0 errors.

This follow-up changes only the terminal pane handler and its new `TerminalPaneContextTests.cs`; no Window code was changed.

## Second-pane chooser follow-up

Root authorized one additional integration correction in `WorkspaceWindow.Actions.cs`, restricted to `ChooseSecondary`. The legacy two-pane chooser excluded only the primary session. With three visible panes it could replace the second pane with the third pane's session, leaving the same ConPTY bound to two differently sized views. Selecting a session from another project also left the source project/file workspace active.

New `SecondPaneSelectionTuiTests.cs` runs the real Release UI inside ConPTY, starts actual `cmd.exe` sessions through an isolated named-pipe daemon, invokes the existing `Agent: Choose second split terminal` command, types into the selected shell, detaches, and checks the persisted layout. Each test uses its own GUID state directory and stops only that test daemon; the user's existing daemon is untouched.

- RED command: `dotnet test tests/CrewTerm.App.Tests -c Release --no-restore --filter FullyQualifiedName~SecondPaneSelectionTuiTests --verbosity minimal` — **2 failed, 1 passed**. The three-pane case persisted `[A, C, C]` instead of `[A, B, C]`; the cross-project case persisted project A instead of project B. Replacing an undisplayed session in the second pane already passed.
- Minimal fix: after choosing the session, call `BindSessionProject` and invalidate old refreshes; focus a currently visible pane already bound to that session and return. Otherwise retain the existing split/second-pane assignment behavior.
- GREEN: identical command — **3 passed, 0 failed, 0 skipped**. The third-pane case retains all original bindings and the third pane's active identity; the undisplayed case replaces only the second pane; the cross-project case selects project B while preserving project A's original layout. Typed markers reach only the intended real child process.
- `dotnet build src/CrewTerm.App -c Release --no-restore --verbosity minimal` — **0 warnings, 0 errors**.

Only this focused Release test class was run for the follow-up; root performs the subsequent whole-solution acceptance run. No other Window methods were edited by this correction.
