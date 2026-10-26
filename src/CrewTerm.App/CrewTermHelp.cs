namespace CrewTerm.App;

/// <summary>
/// <c>crewterm --help</c> 与工作台 F1 帮助共用的说明文本。
/// </summary>
internal static class CrewTermHelp
{
    /// <summary>
    /// 命令行用法、快捷键与会话生命周期说明。
    /// </summary>
    internal static readonly string Text = $"""
        CrewTerm {CrewTermVersion.Current} — multi-agent terminal workspace (Windows / C#)

        crewterm [--project PATH] [--state-dir PATH]     Open workspace
        crewterm doctor                                Discover installed agent CLIs
        crewterm sessions                              List live terminals (JSON)
        crewterm new --command "claude" --project PATH  Start an embedded terminal
        crewterm send ID --text "..."                   Send literal input
        crewterm read ID                               Read terminal screen
        crewterm close ID                              Terminate a terminal
        crewterm run --command "dotnet test" --project PATH
        crewterm tasks | task ID | cancel ID            Background jobs
        crewterm agent-state [ID]                       Semantic state and evidence
        crewterm agent-report ID --state STATE --reason TEXT
        crewterm agent-hook --provider claude --id ID    Passive provider callback (stdin JSON; Codex uses JSON argv)
        crewterm agent-prompt ID --text TEXT            Deliver a prompt to a live, unblocked Agent
        crewterm agent-wait ID --state done,blocked --timeout-ms 10000
        crewterm notifications [ID]                    List notifications without marking read
        crewterm notification-read NOTIFICATION_ID     Explicit acknowledgement
        crewterm message-send --from ID --to ID --text TEXT
        crewterm message-list ID | message-ack ID --message MESSAGE_ID
        crewterm agent-handoff --from ID --to ID --text TEXT
        crewterm sessions --all true                    Include dormant saved sessions
        crewterm session-bind ID --provider codex --native-id UUID
        crewterm session-resume ID                      Resume the recorded native conversation
        crewterm session-restart ID | session-forget ID  Fresh start / forget closed record
        crewterm plugins | plugin-install --path PATH   Register local plugins (disabled by default)
        crewterm plugin-enable ID --enabled true        Enable local commands and hooks
        crewterm plugin-run ID --command NAME --json JSON
        crewterm plugin-event --event NAME --json JSON
        crewterm plugin-history | plugin-remove ID
        crewterm stop                                  Stop daemon, terminals and jobs

        F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git
        F7 Tasks  F8 Split  F9 Commands (directional splits and tabs)  F10 Detach
        Ctrl+Shift+P Commands | Ctrl+Shift+V Paste | Ctrl+Shift+C Copy selection/screen
        Alt+1/2 Terminal focus | Alt+3 Editor | Alt+4 Files | Alt+5 Agents
        Mouse: click tabs, drag dividers, right-click actions.
        Hold Shift to override application mouse tracking for local selection.

        Detaching keeps sessions/tasks alive. Explicit Close/Stop terminates them.
        Agent authentication and billing belong to each installed CLI.
        """;
}
