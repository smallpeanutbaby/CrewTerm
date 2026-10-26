namespace CrewTerm.App.UI.Controls;

/// <summary>
/// 侧栏、面板标题中表示一个 Agent（或一个空间的汇总）状态的外观：符号、颜色与状态单词。
/// </summary>
/// <param name="Glyph">状态符号，例如 <c>◉</c>、<c>●</c>、<c>○</c>。</param>
/// <param name="Color">符号颜色（24 位 RGB）。</param>
/// <param name="Word">状态单词，例如 <c>blocked</c>、<c>working</c>；空间没有任何 Agent 时为空字符串。</param>
/// <param name="Rank">汇总时的优先级：数值越大越需要关注，空间显示其中优先级最高的 Agent 状态。</param>
public readonly record struct AgentBadge(string Glyph, int Color, string Word, int Rank);
