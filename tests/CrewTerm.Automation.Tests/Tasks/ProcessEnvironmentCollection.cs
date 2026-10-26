namespace CrewTerm.Automation.Tests.Tasks;

/// <summary>
/// 需要临时修改本测试进程环境变量的测试集合：集合内的测试逐个运行，且不与本程序集的其他测试并行。
/// </summary>
/// <remarks>
/// 后台任务继承测试进程的环境；修改环境变量期间如果还有其他测试在启动进程，它们会看到被改动的环境。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    /// <summary>
    /// 集合名称，供测试类的 <see cref="CollectionAttribute"/> 引用。
    /// </summary>
    public const string Name = "Process environment";
}
