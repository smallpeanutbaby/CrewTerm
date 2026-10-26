using System.Text.Json.Serialization;

namespace CrewTerm.Automation.Activity;

/// <summary>
/// Agent 的语义活动状态，与其终端进程是否存在是两回事。
/// </summary>
/// <remarks>
/// 在 JSON 中以枚举名字符串（如 <c>"Working"</c>）序列化，而不是整数，
/// 便于状态文件、daemon 协议和插件负载在成员顺序变化时保持兼容。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AgentActivityState>))]
public enum AgentActivityState
{
    /// <summary>
    /// 没有可靠的活动证据：会话刚登记、终端刚启动，或尚未收到任何显式事件。
    /// </summary>
    Unknown,
    /// <summary>
    /// 有显式证据表明一个任务或一轮对话正在进行（例如 Claude 的 <c>UserPromptSubmit</c> hook）。
    /// </summary>
    Working,
    /// <summary>
    /// Agent 需要用户输入或决定（权限请求、已识别的审批提示、Provider 报告失败等）。
    /// </summary>
    Blocked,
    /// <summary>
    /// 收到了显式的完成事件，但对应的完成通知尚未被确认。
    /// </summary>
    Done,
    /// <summary>
    /// 完成已被确认，或收到了显式的空闲事件（例如会话开始/结束、空闲提示）。
    /// </summary>
    Idle,
    /// <summary>
    /// 终端进程已经退出；这只描述进程，不代表任务语义上已完成。
    /// </summary>
    Exited
}
