namespace CrewTerm.App.Tests.Tui;

/// <summary>
/// 端到端界面测试的测试集合：集合内的测试逐个运行，且不与本程序集的其他测试并行。
/// </summary>
/// <remarks>
/// 每个界面测试都会同时启动完整的工作区进程、daemon 与若干 Shell，并按屏幕内容计时等待；
/// 与其他测试并行时，在核心数较少的机器（例如持续集成运行器）上会因调度延迟而无谓地超时。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TuiCollection
{
    /// <summary>
    /// 集合名称，供各界面测试类的 <see cref="CollectionAttribute"/> 引用。
    /// </summary>
    public const string Name = "End-to-end TUI";
}
