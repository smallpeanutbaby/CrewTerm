using System.Reflection;
using CrewTerm.App.Protocol;

namespace CrewTerm.App;

/// <summary>
/// 发行版本标识，以及使用 Agent 功能所需的最低 daemon 协议版本。
/// </summary>
/// <remarks>
/// 客户端连接到已在运行的 daemon 时用 <c>ping</c> 握手：daemon 协议过旧时生成警告，
/// 此后只允许旧协议也支持的基础操作，已有终端继续运行。
/// </remarks>
public static class CrewTermVersion
{
    /// <summary>
    /// 应用项目定义的版本号：程序集 <see cref="AssemblyInformationalVersionAttribute"/> 中 <c>+</c> 之前的部分（去掉源码提交哈希等构建元数据）。
    /// </summary>
    public static string Current { get; } = typeof(CrewTermVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    /// <summary>
    /// 当前协议版本，支持语义化 Agent 状态、原生会话与插件；daemon 在 <c>ping</c> 响应中返回它，旧版 daemon 不返回。
    /// </summary>
    public const int ProtocolVersion = 2;

    /// <summary>
    /// 根据握手（<c>ping</c>）响应判断 daemon 是否足够新。
    /// </summary>
    /// <param name="response">daemon 对 <c>ping</c> 的响应。</param>
    /// <returns>
    /// 协议版本不低于 <see cref="ProtocolVersion"/> 时为 <see langword="null"/>；否则为英文警告，
    /// 说明需要在保存现有终端的工作后显式运行 <c>crewterm stop</c> 并重新打开 CrewTerm。
    /// </returns>
    internal static string? ServerWarning(DaemonResponse response) => response.ProtocolVersion >= ProtocolVersion
        ? null : $"Connected daemon is {response.Version ?? response.Text ?? "an older release"}; CrewTerm {Current} Agent features require a daemon restart. Existing terminals remain running. Finish or save their work before explicitly running 'crewterm stop', then reopen CrewTerm.";

    /// <summary>
    /// 判断请求是否需要当前协议版本的 daemon。
    /// </summary>
    /// <remarks>
    /// 旧协议也支持的基础操作（<c>ping</c>、<c>list</c>、<c>input</c>、<c>screen</c>、<c>resize</c>、<c>close</c>、<c>stop</c> 与后台任务操作）不需要；
    /// <c>create</c> 只有带 Provider、原生会话标识或恢复命令时才需要；其他操作（Agent、消息、会话目录、插件等）一律需要。
    /// </remarks>
    /// <param name="request">要发送的请求。</param>
    /// <returns>需要当前协议时为 <see langword="true"/>。</returns>
    internal static bool RequiresCurrentProtocol(DaemonRequest request) => request.Operation switch
    {
        "ping" or "list" or "input" or "screen" or "resize" or "close" or "stop" or
            "task-start" or "pipeline-start" or "task-list" or "task-get" or "task-cancel" => false,
        "create" => request.Provider is not null || request.NativeSessionId is not null || request.ResumeCommand is not null,
        _ => true
    };
}
