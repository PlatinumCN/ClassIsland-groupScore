namespace GroupScorePlugin.Models;

/// <summary>
/// 预设积分项（快捷按钮模板）。对应数据表 PRESETS。
/// 例如「回答正确 +2」「迟到 -1」，让老师上课时一键就能加分，不必每次输入数值。
/// </summary>
public class ScorePreset
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>按钮上显示的文字。</summary>
    public string Label { get; set; } = "";

    /// <summary>该预设对应的分值变动量。</summary>
    public int Delta { get; set; }

    /// <summary>显示顺序。</summary>
    public int SortOrder { get; set; }

    /// <summary>按钮上显示的完整文本，如「回答正确 +2」。</summary>
    public string DisplayText => Delta >= 0 ? $"{Label} +{Delta}" : $"{Label} {Delta}";
}
