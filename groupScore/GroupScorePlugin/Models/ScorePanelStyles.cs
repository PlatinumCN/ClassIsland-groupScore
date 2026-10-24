namespace GroupScorePlugin.Models;

/// <summary>
/// 加分面板的样式。
///
/// 两种形态对应两种真实场景：
///   <see cref="Picker"/> 逐个选择 —— 先在下拉框里挑小组、再点预设按钮。
///                        小组多、或者要指到某个学生头上时用这个。
///   <see cref="Grid"/>   全部展开 —— 每个小组一行，行尾直接就是加减分按钮。
///                        课上最高频的动作是「看见哪组表现好，马上给那组加一分」，
///                        这条路径上不该有「先选一次小组」这个中间步骤。
///
/// 存的是字符串（要落 JSON），所以必须有一个归一化入口：
/// 老师手改 settings.json 写了个别的词时，要退回默认样式，
/// 而不是让面板落进「两个区域都不显示」的空状态。
///
/// 跟 <see cref="BallPlacement"/> 一样，这里**一个 Avalonia / ClassIsland 类型都不引**，
/// 自检台才能把真代码编进去跑断言。
/// </summary>
public static class ScorePanelStyles
{
    /// <summary>逐个选择。</summary>
    public const string Picker = "Picker";

    /// <summary>全部展开。</summary>
    public const string Grid = "Grid";

    /// <summary>
    /// 合法取值的**唯一来源**。设置页的下拉框顺序直接引用它，
    /// 避免「设置页写一份、窗口判断写一份」两边慢慢漂移。
    /// 第 0 项同时也是新装插件的默认值。
    /// </summary>
    public static readonly string[] All = { Picker, Grid };

    /// <summary>默认样式。</summary>
    public static string Default => All[0];

    /// <summary>这个样式认不认识。比较时忽略大小写，手改配置写成 "grid" 也认。</summary>
    public static bool IsKnown(string? style)
        => Array.Exists(All, s => string.Equals(s, style, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 归一化：认不出的值（手改配置写错、旧版本留下的字段）一律回落成默认样式。
    /// 返回的是 <see cref="All"/> 里那个规范写法，不是调用方传进来的原文。
    /// </summary>
    public static string Normalize(string? style)
        => Array.Find(All, s => string.Equals(s, style, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>是不是「全部展开」样式。</summary>
    public static bool IsGrid(string? style) => Normalize(style) == Grid;
}
