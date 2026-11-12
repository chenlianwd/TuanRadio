namespace AIRadio.Desktop.Models;

/// <summary>
/// 电台场景模式枚举：控制全局广播氛围、AI DJ 语境软化、推荐流派倾斜与串场静默节奏。
/// </summary>
public enum RadioSceneMode
{
    /// <summary>
    /// 标准日常电台：全天候自然电台体验。
    /// </summary>
    Default = 0,

    /// <summary>
    /// 伴眠电台：深夜/睡前轻声伴眠体验。DJ 轻柔耳语、推荐舒缓宁静、临界完全静默留白。
    /// </summary>
    Sleep = 1,

    /// <summary>
    /// 专注模式（预留）：免打扰、器乐优先。
    /// </summary>
    Focus = 2
}
