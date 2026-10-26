namespace CrewTerm.Terminal.Emulation;

/// <summary>
/// <see cref="TerminalScreen"/> 在某一时刻的不可变快照：可见单元格、滚动历史、光标以及影响输入编码的终端模式。
/// </summary>
/// <remarks>
/// daemon 把它序列化后经命名管道发给 UI；UI 据此绘制终端，并按其中的模式决定按键、粘贴与鼠标事件的编码。
/// 带默认值的参数是后来新增的字段，旧版本 daemon 的响应缺少它们时取与旧行为一致的默认值。
/// </remarks>
/// <param name="Columns">屏幕列数（单元格）。</param>
/// <param name="Rows">屏幕行数（单元格）。</param>
/// <param name="CursorColumn">光标所在列（从 0 开始）。</param>
/// <param name="CursorRow">光标所在行（从 0 开始）。</param>
/// <param name="CursorVisible">光标是否可见（DECTCEM，<c>CSI ? 25 h/l</c>）。</param>
/// <param name="Cells">活动缓冲区（主屏幕或备用屏幕）的单元格，按 <c>[行][列]</c> 索引，共 <see cref="Rows"/> 行、每行 <see cref="Columns"/> 个。</param>
/// <param name="History">主屏幕滚动历史的行文本（已去掉行尾空白，不含颜色），最旧的在前，最多 2,000 行。</param>
/// <param name="AlternateScreen">是否处于备用屏幕（全屏程序通常通过 <c>CSI ? 1049 h</c> 进入）。</param>
/// <param name="BracketedPaste">是否启用括号粘贴（<c>CSI ? 2004 h</c>）；启用时粘贴内容应包在 <c>CSI 200 ~</c> 与 <c>CSI 201 ~</c> 之间。</param>
/// <param name="ApplicationCursorKeys">是否启用应用光标键模式（DECCKM，<c>CSI ? 1 h</c>）；启用时方向键发送 <c>ESC O</c> 前缀而非 <c>CSI</c>。</param>
/// <param name="MouseTracking">鼠标跟踪模式：0 为关闭，1000 仅报告按键，1002 另报告按下时的拖动，1003 报告所有移动。</param>
/// <param name="SgrMouse">是否使用 SGR 鼠标编码（<c>CSI ? 1006 h</c>，<c>CSI &lt; b;x;y M/m</c>）。</param>
/// <param name="Utf8Mouse">是否使用 UTF-8 鼠标编码（<c>CSI ? 1005 h</c>），坐标可超过传统编码的 223 上限。</param>
public sealed record TerminalSnapshot(
    int Columns, int Rows, int CursorColumn, int CursorRow, bool CursorVisible,
    TerminalCell[][] Cells, string[] History, bool AlternateScreen,
    bool BracketedPaste = false, bool ApplicationCursorKeys = false, int MouseTracking = 0, bool SgrMouse = false,
    bool Utf8Mouse = false);
