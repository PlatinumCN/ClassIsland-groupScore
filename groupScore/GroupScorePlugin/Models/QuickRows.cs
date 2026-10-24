namespace GroupScorePlugin.Models;

/// <summary>
/// 「全部展开」样式里，某个小组行尾的一个按钮。
/// <see cref="Text"/> 是按钮上那两个字，<see cref="Reason"/> 是真正写进记录的原因。
/// </summary>
/// <param name="Text">按钮上的短标签，如「+2」「-1」。</param>
/// <param name="Delta">点一下加多少分。</param>
/// <param name="Reason">写进记录的原因，如「回答正确」。</param>
/// <param name="IsIncrease">是不是加分。界面据此上色（国内习惯：加分红、扣分绿）。</param>
public sealed record QuickButton(string Text, int Delta, string Reason, bool IsIncrease)
{
    /// <summary>鼠标悬停时的完整说明，如「回答正确（+2）」。短标签只有数字，光看它不知道扣的什么分。</summary>
    public string Tooltip => $"{Reason}（{Text}）";
}

/// <summary>
/// 「全部展开」样式里每一行行尾该有哪些按钮。
///
/// 抽成纯函数是为了能在自检台里直接断言：这里一旦算错，
/// 界面上表现成「一排按钮点了没反应」或「按钮和分值对不上」，
/// 而不是编译错误 —— 必须跑起来点才知道，代价太高。
/// </summary>
public static class QuickRows
{
    /// <summary>一个预设项都没有时用的兜底分值。老师第一次用插件、还没配预设时就是这个。</summary>
    public static readonly int[] FallbackDeltas = { 1, -1 };

    /// <summary>分值在按钮上的短标签：正数带 +，负数自带 -，零是 0。</summary>
    public static string ShortLabel(int delta) => delta >= 0 ? $"+{delta}" : delta.ToString();

    /// <summary>
    /// 生成行尾按钮清单。
    ///
    /// 有预设项就用预设项 —— 预设承载着「原因」，是这套插件的核心概念
    /// （「张三答对了」和「张三 +1」在期末复盘时完全是两回事）。
    /// 一个预设都没有才退化成 ±1：老师刚装好插件、还没配预设时，
    /// 至少要让这一行是有用的，而不是一排空行。
    /// </summary>
    public static List<QuickButton> BuildButtons(IEnumerable<ScorePreset>? presets)
    {
        var list = new List<QuickButton>();

        if (presets != null)
        {
            foreach (var p in presets)
                list.Add(new QuickButton(ShortLabel(p.Delta), p.Delta, p.Label, p.Delta >= 0));
        }

        if (list.Count > 0) return list;

        foreach (var delta in FallbackDeltas)
        {
            list.Add(new QuickButton(
                ShortLabel(delta),
                delta,
                delta > 0 ? "加 1 分" : "扣 1 分",
                delta >= 0));
        }
        return list;
    }
}
