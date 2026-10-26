namespace CrewTerm.App.UI.Input;

/// <summary>
/// <see cref="PrefixKeyState.Process"/> 对一次按键的判断。
/// </summary>
public enum PrefixKeyOutcome
{
    /// <summary>
    /// 与前缀无关，按普通按键处理。
    /// </summary>
    NotPrefix,

    /// <summary>
    /// 按下了前缀键，开始等待下一个键。
    /// </summary>
    Started,

    /// <summary>
    /// 等待期间的修饰键、重复或释放事件，继续等待。
    /// </summary>
    Ignored,

    /// <summary>
    /// 前缀之后按了 Esc，取消。
    /// </summary>
    Cancelled,

    /// <summary>
    /// 前缀之后再次按前缀键：把前缀键本身发给终端。
    /// </summary>
    SendPrefix,

    /// <summary>
    /// 前缀之后按了一个字符键，由调用方按字符执行命令。
    /// </summary>
    Command,

    /// <summary>
    /// 前缀之后按了不对应任何字符的键（例如功能键），前缀被消耗但没有操作。
    /// </summary>
    Unknown
}
