namespace GroupScorePlugin.Models;

/// <summary>统计周期的种类。</summary>
public enum PeriodKind
{
    /// <summary>自然周。起止取决于设置里的「一周从周几开始」。</summary>
    Week,

    /// <summary>自然月。</summary>
    Month
}

/// <summary>
/// 一个左闭右开的统计区间 [Start, EndExclusive)。
///
/// 右端为什么是「开」而不是「闭」：
/// 相邻两期的边界上必然同时存在两条记录（周日晚 23:59:59 和周一 00:00:00），
/// 用闭区间的话，边界那一瞬间的记录要么被两边各算一次、要么被两边都漏掉，
/// 取决于怎么拼。半开区间天然严丝合缝：上一期的 EndExclusive 就是这一期的 Start，
/// 不重不漏。这条是整个历史对比的地基，改它等于把账算错。
/// </summary>
public sealed class ScorePeriod
{
    /// <summary>空区间，只在模型默认值里用得上（真实的周期一定由 PeriodStats 算出来）。</summary>
    public static ScorePeriod Empty { get; } = new();

    public PeriodKind Kind { get; init; }

    /// <summary>区间起点（含）。</summary>
    public DateTime Start { get; init; }

    /// <summary>区间终点（不含）。显示给老师看之前必须退回一天，见 PeriodStats.FormatRange。</summary>
    public DateTime EndExclusive { get; init; }

    /// <summary>某个时刻是否落在这个周期里。</summary>
    public bool Contains(DateTime moment) => moment >= Start && moment < EndExclusive;

    /// <summary>区间长度。整周是 7 天，整月是 28~31 天。</summary>
    public TimeSpan Length => EndExclusive - Start;
}

/// <summary>
/// 对比表里的一行：某个小组在这两个周期里的表现。
///
/// 它直接持有 <see cref="Models.Group"/> 的引用而不是抄一份名字和颜色 ——
/// 名字是活的（老师随时可能改名），抄一份就会出现「表里还是旧名字」。
/// </summary>
public sealed class PeriodGroupRow
{
    /// <summary>对应的小组。名字、颜色、画刷都从它上面取，保证和别处一致。</summary>
    public Group Group { get; init; } = new();

    /// <summary>本期得分名次。同分并列，规则与排行榜一致（1、2、2、4）。</summary>
    public int PeriodRank { get; init; }

    /// <summary>本期净增（本期所有记录的 Delta 之和）。</summary>
    public int CurrentDelta { get; init; }

    /// <summary>上期净增。</summary>
    public int PreviousDelta { get; init; }

    /// <summary>期末累计总分：截至本期结束时刻的全部记录之和。</summary>
    public int CumulativeNow { get; init; }

    /// <summary>上期末累计总分。</summary>
    public int CumulativeBefore { get; init; }

    /// <summary>按期末累计总分排的名次（本期）。</summary>
    public int CurrentCumulativeRank { get; init; }

    /// <summary>按上期末累计总分排的名次。</summary>
    public int PreviousCumulativeRank { get; init; }

    // ---------- 下面是给界面用的派生值 ----------
    //
    // 符号约定在这里定死，别处不要另立一套：
    //   DeltaChange > 0 → 本期比上期多得了分（好事）
    //   RankChange  > 0 → 名次前进（数字变小，也是好事）
    // 两个字段的「正数都是好事」，界面才不会把箭头画反。

    /// <summary>本期净增 − 上期净增。</summary>
    public int DeltaChange => CurrentDelta - PreviousDelta;

    /// <summary>上期累计名次 − 本期累计名次。正数 = 前进。</summary>
    public int RankChange => PreviousCumulativeRank - CurrentCumulativeRank;

    public string CurrentText => FormatSigned(CurrentDelta);

    public string PreviousText => FormatSigned(PreviousDelta);

    public string DeltaChangeText => DeltaChange == 0 ? "—" : FormatSigned(DeltaChange);

    public string CumulativeText => CumulativeNow.ToString();

    /// <summary>箭头朝上表示名次前进。注意取绝对值，否则「↓-2」这种反话会跑出来。</summary>
    public string RankChangeText => RankChange switch
    {
        > 0 => $"↑{RankChange}",
        < 0 => $"↓{-RankChange}",
        _ => "—"
    };

    /// <summary>带符号格式化，供本类与界面共用（+3 / -2 / 0 显示成 +0）。</summary>
    public static string FormatSigned(int delta) => delta >= 0 ? $"+{delta}" : delta.ToString();
}

/// <summary>
/// 「本期 vs 上期」的完整结果。界面拿到它就能直接把表画出来，不需要再算任何东西。
/// </summary>
public sealed class PeriodComparison
{
    public ScorePeriod Current { get; init; } = ScorePeriod.Empty;

    public ScorePeriod Previous { get; init; } = ScorePeriod.Empty;

    /// <summary>按本期得分降序排好的行。</summary>
    public List<PeriodGroupRow> Rows { get; init; } = new();

    /// <summary>全班本期净增合计。</summary>
    public int CurrentTotal { get; init; }

    public int PreviousTotal { get; init; }

    /// <summary>本期产生了多少条记录。给「本周只有 3 条记录」这种判断用。</summary>
    public int CurrentRecordCount { get; init; }

    public int PreviousRecordCount { get; init; }

    /// <summary>本期比上期多了多少分（班级整体）。</summary>
    public int TotalChange => CurrentTotal - PreviousTotal;

    public string TotalChangeText => PeriodGroupRow.FormatSigned(TotalChange);
}
