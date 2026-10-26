namespace CrewTerm.Automation.Providers;

/// <summary>
/// 命令行词法分析得到的一个参数。
/// </summary>
/// <param name="Value">去掉引号与转义后的参数值。</param>
/// <param name="Raw">原始拼写（含引号），重组命令时原样保留，避免改变用户的引用方式。</param>
/// <param name="Start">参数在原命令行中的起始位置。</param>
internal sealed record CommandToken(string Value, string Raw, int Start);
