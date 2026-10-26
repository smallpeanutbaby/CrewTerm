using CrewTerm.Core.IO;

namespace CrewTerm.App;

/// <summary>
/// 命令行解析结果：子命令、<c>--名称 值</c> 形式的选项和位置参数。
/// </summary>
/// <remarks>
/// 规则：第一个非选项参数是子命令（默认 <c>ui</c>），其余非选项参数是位置参数；
/// <c>--help</c>/<c>-h</c>/<c>--version</c>/<c>--server</c> 是无值开关，直接作为子命令；
/// 其他 <c>--选项</c> 必须带一个值，且值不能以 <c>--</c> 开头。选项名不区分大小写。
/// </remarks>
public sealed class CrewTermCommandLine
{
    /// <summary>
    /// <c>--名称 值</c> 形式的选项，键为不带 <c>--</c> 前缀的选项名（不区分大小写）；同名选项出现多次时后者覆盖前者。
    /// </summary>
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 子命令（已转为小写），例如 <c>ui</c>、<c>sessions</c>、<c>run</c>、<c>help</c>、<c>server</c>。
    /// </summary>
    public string Command { get; private set; } = "ui";

    /// <summary>
    /// 启动握手发现已运行的 daemon 版本过旧、无法提供 Agent 功能时写入的警告文本；兼容时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 非空时，CLI 与 UI 会拒绝发送需要当前协议的请求（见 <see cref="CrewTermVersion.RequiresCurrentProtocol"/>），
    /// 并以该文本作为错误消息。
    /// </remarks>
    public string? ServerCompatibilityWarning { get; internal set; }

    /// <summary>
    /// 子命令之后的位置参数，例如会话或任务标识。
    /// </summary>
    public List<string> Positionals { get; } = [];

    /// <summary>
    /// <c>--state-dir</c> 指定的状态目录；未指定时为 <see cref="StateFiles.DefaultDirectory"/>。
    /// </summary>
    public string StateDirectory => Get("state-dir") ?? StateFiles.DefaultDirectory;

    /// <summary>
    /// 读取选项值。
    /// </summary>
    /// <param name="name">不带 <c>--</c> 前缀的选项名。</param>
    /// <returns>选项值；未提供时为 <see langword="null"/>。</returns>
    public string? Get(string name) => values.GetValueOrDefault(name);

    /// <summary>
    /// 解析命令行参数。
    /// </summary>
    /// <param name="args">进程参数。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentException">选项缺少值。</exception>
    public static CrewTermCommandLine Parse(string[] args)
    {
        var options = new CrewTermCommandLine();
        var commandSet = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "--version" or "--server")
            { options.Command = arg.TrimStart('-'); commandSet = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Option {arg} requires a value");
                options.values[arg[2..]] = args[++i];
            }
            else if (!commandSet) { options.Command = arg.ToLowerInvariant(); commandSet = true; }
            else options.Positionals.Add(arg);
        }
        return options;
    }
}
