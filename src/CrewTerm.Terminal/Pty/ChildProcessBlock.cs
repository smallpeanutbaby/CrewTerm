using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CrewTerm.Terminal.Native;

namespace CrewTerm.Terminal.Pty;

/// <summary>
/// fork 之前准备好的子进程启动数据：全部以 NUL 结尾的 UTF-8 字符串与以空指针结尾的指针数组，位于非托管内存。
/// </summary>
/// <remarks>
/// 子进程只读取这些预先分配的内存，不需要在 fork 之后分配或封送任何数据。父进程在 fork 返回后释放它们。
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class ChildProcessBlock : IDisposable
{
    /// <summary>
    /// 本对象分配的全部非托管内存，释放时逐一归还。
    /// </summary>
    private readonly List<IntPtr> allocations = [];

    /// <summary>
    /// 程序路径（<c>argv[0]</c> 同一份字符串）。
    /// </summary>
    public IntPtr Program { get; }

    /// <summary>
    /// 参数指针数组。
    /// </summary>
    public IntPtr Arguments { get; }

    /// <summary>
    /// 环境变量指针数组。
    /// </summary>
    public IntPtr Environment { get; }

    /// <summary>
    /// 工作目录。
    /// </summary>
    public IntPtr Directory { get; }

    /// <summary>
    /// 清零的信号集，用于解除子进程继承的信号屏蔽。
    /// </summary>
    public IntPtr EmptySignalSet { get; }

    /// <summary>
    /// fork 期间屏蔽的信号集：除同步故障信号（<c>SIGILL</c>、<c>SIGTRAP</c>、<c>SIGBUS</c>、<c>SIGFPE</c>、<c>SIGSEGV</c>，以及与之编号重叠的
    /// 7、10）之外的全部信号。
    /// </summary>
    public IntPtr BlockedSignalSet { get; }

    /// <summary>
    /// 保存 fork 之前调用线程原有信号屏蔽字的缓冲区。
    /// </summary>
    public IntPtr SavedSignalSet { get; }

    /// <summary>
    /// 分配并填充全部启动数据。
    /// </summary>
    /// <param name="argv">参数列表，第一个元素是程序路径。</param>
    /// <param name="environment"><c>KEY=VALUE</c> 环境变量列表。</param>
    /// <param name="directory">工作目录。</param>
    public ChildProcessBlock(IReadOnlyList<string> argv, IReadOnlyList<string> environment, string directory)
    {
        Arguments = Array(argv);
        Program = Marshal.ReadIntPtr(Arguments);
        Environment = Array(environment);
        Directory = Text(directory);
        EmptySignalSet = SignalSet(0x00);
        BlockedSignalSet = SignalSet(0xFF);
        // 信号 n 对应第 n-1 位；Linux 与 macOS 在小端序下对前 31 个信号的布局相同。
        foreach (var signal in new[] { 4, 5, 7, 8, 10, 11 })
        {
            var offset = (signal - 1) / 8;
            Marshal.WriteByte(BlockedSignalSet, offset, (byte)(Marshal.ReadByte(BlockedSignalSet, offset) & ~(1 << ((signal - 1) % 8))));
        }
        SavedSignalSet = SignalSet(0x00);
    }

    /// <summary>
    /// 分配一个 <see cref="UnixPtyNative.SignalSetBytes"/> 字节、每个字节都填充为指定值的信号集。
    /// </summary>
    /// <param name="fill">填充值：0x00 为空集，0xFF 为全集。</param>
    /// <returns>信号集首地址。</returns>
    private IntPtr SignalSet(byte fill)
    {
        var set = Allocate(UnixPtyNative.SignalSetBytes);
        for (var offset = 0; offset < UnixPtyNative.SignalSetBytes; offset++) Marshal.WriteByte(set, offset, fill);
        return set;
    }

    /// <summary>
    /// 分配一个以空指针结尾的字符串指针数组。
    /// </summary>
    /// <param name="values">字符串。</param>
    /// <returns>数组首地址。</returns>
    private IntPtr Array(IReadOnlyList<string> values)
    {
        var array = Allocate((values.Count + 1) * IntPtr.Size);
        for (var index = 0; index < values.Count; index++) Marshal.WriteIntPtr(array, index * IntPtr.Size, Text(values[index]));
        Marshal.WriteIntPtr(array, values.Count * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    /// <summary>
    /// 分配一个以 NUL 结尾的 UTF-8 字符串。
    /// </summary>
    /// <param name="value">字符串。</param>
    /// <returns>字符串首地址。</returns>
    private IntPtr Text(string value)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        allocations.Add(pointer);
        return pointer;
    }

    /// <summary>
    /// 分配指定字节数的非托管内存。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>内存首地址。</returns>
    private IntPtr Allocate(int bytes)
    {
        var pointer = Marshal.AllocCoTaskMem(bytes);
        allocations.Add(pointer);
        return pointer;
    }

    /// <summary>
    /// 归还全部非托管内存。
    /// </summary>
    public void Dispose()
    {
        foreach (var pointer in allocations) Marshal.FreeCoTaskMem(pointer);
        allocations.Clear();
    }
}
