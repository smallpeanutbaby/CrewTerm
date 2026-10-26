namespace CrewTerm.Core.Layouts;

/// <summary>
/// 分割窗格时，新窗格相对于被分割窗格的位置。
/// </summary>
public enum SplitDirection
{
    /// <summary>
    /// 新窗格放在左侧（水平分割，新窗格为第一个子节点）。
    /// </summary>
    Left,
    /// <summary>
    /// 新窗格放在右侧（水平分割，新窗格为第二个子节点）。
    /// </summary>
    Right,
    /// <summary>
    /// 新窗格放在上方（垂直分割，新窗格为第一个子节点）。
    /// </summary>
    Up,
    /// <summary>
    /// 新窗格放在下方（垂直分割，新窗格为第二个子节点）。
    /// </summary>
    Down
}
