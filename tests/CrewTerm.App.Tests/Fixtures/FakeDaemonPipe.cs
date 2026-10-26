using System.IO.Pipes;
using CrewTerm.App.Protocol;

namespace CrewTerm.App.Tests.Fixtures;

/// <summary>
/// 模拟旧版 daemon 的测试服务器使用的管道实例。
/// </summary>
/// <remarks>
/// 模拟服务器与真正的 daemon 一样，先创建下一个监听实例再处理当前连接：Linux / macOS 上同名实例共用一个监听套接字，
/// 最后一个实例释放时套接字被关闭，排队中的客户端连接会被重置。
/// </remarks>
internal static class FakeDaemonPipe
{
    /// <summary>
    /// 创建一个等待连接的管道实例：与真正的 daemon 相同的管道名与访问限制，最多两个实例（处理中与监听中）。
    /// </summary>
    /// <param name="state">状态目录。</param>
    /// <returns>新的管道实例。</returns>
    internal static NamedPipeServerStream Listen(string state)
        => new(PipeProtocol.PipeName(state), PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
