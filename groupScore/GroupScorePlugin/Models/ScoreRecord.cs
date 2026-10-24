namespace GroupScorePlugin.Models;

/// <summary>
/// 一次积分变动记录（只记录增量）。对应数据表 SCORE_RECORDS。
///
/// 这是整个插件唯一的事实来源：总分是这些记录累加的结果，
/// 撤销、按时间段统计、按原因筛选审计，全都建立在它之上。
/// 永远不要直接改 Group.CachedTotal 而不写记录，否则两边会不一致。
/// </summary>
public class ScoreRecord
{
    /// <summary>主键。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>外键 → Group.Id。</summary>
    public string GroupId { get; set; } = "";

    /// <summary>外键 → Member.Id。为空表示这条记录只记到小组一级。</summary>
    public string? MemberId { get; set; }

    /// <summary>变动量。加分为正、扣分为负，绝不为 0。</summary>
    public int Delta { get; set; }

    /// <summary>变动原因，如「课堂回答正确」。</summary>
    public string Reason { get; set; } = "";

    /// <summary>外键 → ScorePreset.Id。为空表示是手动输入的自定义分值。</summary>
    public string? PresetId { get; set; }

    /// <summary>
    /// 发生时间。注意：它的职责是「展示与按时间段统计」，
    /// 不是确定先后顺序 —— 同一毫秒内的连续点击可能产生相同时间戳。
    /// 真实的先后顺序由记录在列表中的位置决定（后来的追加在后面）。
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
