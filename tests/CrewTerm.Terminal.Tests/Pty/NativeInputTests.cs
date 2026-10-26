using System.Text;
using System.Runtime.Versioning;
using CrewTerm.Terminal.Pty;

namespace CrewTerm.Terminal.Tests.Pty;

/// <summary>
/// 端到端验证写入 <see cref="ConPtySession"/> 的输入在真实控制台程序中读到的字节。
/// 测试通过真实 ConPTY 启动 PowerShell，用 <c>Add-Type</c> 即时编译一个探针程序：它按指定代码页把控制台切换到
/// 虚拟终端输入模式，逐字节读取标准输入直到回车，并把读到的字节以十六进制写入临时文件供断言。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeInputTests
{
    /// <summary>
    /// 等待探针的最长时间：Windows PowerShell 用 <c>Add-Type</c> 即时编译探针，在负载较高的持续集成机器上可能需要数十秒。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 验证：中文与带重音字符在 UTF-8（65001）代码页下以正确的 UTF-8 字节到达程序；探针输出 <c>CSI ?1000h</c> 开启鼠标跟踪后，
    /// 屏幕记录的鼠标跟踪模式为 1000，且坐标字节为 U+00B7 的传统鼠标报告在 1252 代码页下以单字节 <c>B7</c> 到达、
    /// 在 65001 代码页下以 <c>C2-B7</c> 到达。
    /// </summary>
    /// <param name="modes">探针在输出 <c>input-ready</c> 前写到终端的模式设置序列；为空表示不改变模式。</param>
    /// <param name="inputText">测试写入会话的输入文本（随后再写入一个回车结束读取）。</param>
    /// <param name="codePage">探针设置的控制台输入代码页。</param>
    /// <param name="expected">期望探针读到的字节，格式为 <see cref="BitConverter.ToString(byte[])"/> 的连字符分隔十六进制。</param>
    [WindowsTheory]
    [InlineData("", "中文é", 65001, "E4-B8-AD-E6-96-87-C3-A9")]
    [InlineData("\u001b[?1000h", "\u001b[M \u00b7'", 1252, "1B-5B-4D-20-B7-27")]
    [InlineData("\u001b[?1000h", "\u001b[M \u00b7'", 65001, "1B-5B-4D-20-C2-B7-27")]
    public async Task InputSurvivesARealVirtualTerminalInputReader(string modes, string inputText, int codePage, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "CrewTerm native input " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "input.txt");
            var script = """
                Add-Type -TypeDefinition @'
                using System;
                using System.IO;
                using System.Runtime.InteropServices;
                public static class CrewTermInputProbe {
                    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
                    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint m);
                    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint m);
                    [DllImport("kernel32.dll")] static extern bool SetConsoleCP(uint cp);
                    public static void Run(string path) {
                        var handle = GetStdHandle(-10);
                        uint mode;
                        GetConsoleMode(handle, out mode);
                        SetConsoleCP(uint.Parse(Environment.GetEnvironmentVariable("CREWTERM_PROBE_CP")));
                        SetConsoleMode(handle, (mode | 0x200u | 0x80u) & ~0x46u);
                        Console.Write(Environment.GetEnvironmentVariable("CREWTERM_PROBE_MODES") + "input-ready\r\n");
                        var bytes = new byte[64];
                        var input = Console.OpenStandardInput();
                        int size = 0;
                        for (;;) {
                            int value = input.ReadByte();
                            if (value < 0 || value == 13) break;
                            bytes[size++] = (byte)value;
                        }
                        File.WriteAllText(path, BitConverter.ToString(bytes, 0, size));
                    }
                }
                '@
                [CrewTermInputProbe]::Run($env:CREWTERM_PROBE_FILE)
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var session = ConPtySession.Start("native-input", $"powershell.exe -NoProfile -NoLogo -EncodedCommand {encoded}",
                root, 200, 24, new Dictionary<string, string?> { ["CREWTERM_PROBE_FILE"] = file, ["CREWTERM_PROBE_MODES"] = modes,
                    ["CREWTERM_PROBE_CP"] = codePage.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await Poll.Until(() => session.Screen.GetText().Contains("input-ready"), ProbeTimeout);
            if (modes.Length > 0)
                Assert.True(session.Screen.Snapshot().MouseTracking == 1000,
                    $"tracking={session.Screen.Snapshot().MouseTracking}; sgr={session.Screen.Snapshot().SgrMouse}; utf8={session.Screen.Snapshot().Utf8Mouse}");
            session.Write(inputText);
            session.Write("\r");
            await Poll.Until(() => session.HasExited && File.Exists(file), ProbeTimeout);
            Assert.Equal(expected, await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
