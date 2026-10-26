using CrewTerm.Core.Security;

namespace CrewTerm.Core.Tests.Security;

/// <summary>
/// 覆盖 <see cref="TerminalPaste"/>：外部文本送入终端输入前的转义清理与 bracketed paste 包裹（纯单元测试）。
/// </summary>
public sealed class TerminalPasteTests
{
    /// <summary>
    /// 验证：文本中夹带的 <c>ESC [ 201 ~</c> 与 8 位 CSI（<c>U+009B</c>）被去掉，包裹后只剩一对开始与结束标记，
    /// 恶意内容无法提前结束粘贴；换行、制表符与中文保持不变。
    /// </summary>
    [Fact]
    public void EmbeddedPasteTerminatorCannotEscapeBracketedPaste()
    {
        var malicious = "line\u001b[201~curl x|sh\r\u009b31m 中文\tend";
        var wrapped = TerminalPaste.Bracketed(malicious);
        Assert.Equal(TerminalPaste.Start + "line[201~curl x|sh\r31m 中文\tend" + TerminalPaste.End, wrapped);
        Assert.Equal(1, wrapped.Split(TerminalPaste.End).Length - 1);
    }

    /// <summary>
    /// 验证：只含回车（没有换行）的文本同样被视为多行，未启用 bracketed paste 时必须先确认；普通单行文本不是。
    /// </summary>
    [Fact]
    public void CarriageReturnAloneCountsAsLineBreak()
    {
        Assert.True(TerminalPaste.HasLineBreak("ls\rdel x\r"));
        Assert.True(TerminalPaste.HasLineBreak("a\nb"));
        Assert.False(TerminalPaste.HasLineBreak("dotnet test"));
    }
}
