namespace GroupScorePlugin.Models;

/// <summary>
/// 历史对比的全部算术：时间切片、期内净增、期末累计、名次。
///
/// 又一次刻意不引用任何 Avalonia / ClassIsland 类型，理由和前面几步一样：
/// 抽出来之后自检台能**直接调用真代码**跑断言，而不是在测试文件里把同样的
/// 日期运算再抄一遍。日期这块尤其值得这么干 —— 周期边界错一天是看不出来的，
/// 老师的账本却已经在悄悄出错。
///
/// 这个文件里出现一个 Avalonia 类型，整组周期断言就编不进自检台、直接消失。
/// </summary>
public static class PeriodStats
{
    /// <summary>
    /// 默认一周从周一算起。用 DayOfWeek 的数值表示（Sunday = 0、Monday = 1…），
    /// 这样设置里存的值可以直接和 DateTime.DayOfWeek 比较，中间少一次换算就少一个坑。
    /// </summary>
    public const int DefaultWeekStartsOn = 1;

    /// <summary>
    /// 设置页下拉框里可选的周起始日。它是**唯一来源**，
    /// 设置页只负责给这些值配中文标签，静态体检会比对两边数量是否一致。
    /// </summary>
    public static readonly int[] WeekStartChoices = { 1, 0 };   // 周一、周日

    /// <summary>把可能被手改坏的值（-3 / 99 / 空）拉回合法范围。</summary>
    public static int NormalizeWeekStart(int weekStartsOn) =>
        weekStartsOn is >= 0 and <= 6 ? weekStartsOn : DefaultWeekStartsOn;

    /// <summary>
    /// 某个时刻所在的那一周的起点（当天 00:00，含时间部分会被砍掉）。
    /// 先归一到日期、再往回退几天，所以同一天的任何时刻算出来都一样。
    /// </summary>
    public static DateTime StartOfWeek(DateTime moment, int weekStartsOn)
    {
        var startsOn = NormalizeWeekStart(weekStartsOn);
        var day = moment.Date;
        var back = ((int)day.DayOfWeek - startsOn + 7) % 7;
        return day.AddDays(-back);
    }

    /// <summary>某个时刻所在月份的第一天。</summary>
    public static DateTime StartOfMonth(DateTime moment) => new(moment.Year, moment.Month, 1);

    /// <summary>某个时刻所在的当期区间（本周 / 本月）。</summary>
    public static ScorePeriod Of(DateTime now, PeriodKind kind, int weekStartsOn)
    {
        if (kind == PeriodKind.Month)
        {
            var monthStart = StartOfMonth(now);
            return new ScorePeriod
            {
                Kind = kind,
                Start = monthStart,
                EndExclusive = monthStart.AddMonths(1)
            };
        }

        var weekStart = StartOfWeek(now, weekStartsOn);
        return new ScorePeriod
        {
            Kind = kind,
            Start = weekStart,
            EndExclusive = weekStart.AddDays(7)
        };
    }

    /// <summary>
    /// 紧挨着当前期的上一个周期（上周 / 上月）。
    ///
    /// 注意是「上一个自然周期」而不是「往前推 7 天 / 30 天」：
    /// 老师说的是「这周比上周」，周三那天往回推 7 天得到的是上周三，
    /// 那个 7 天窗口横跨两个自然周，比出来的数谁也对不上。
    ///
    /// 上期末端直接取当前期起点，于是两段区间严丝合缝（这就是半开区间的好处）。
    /// </summary>
    public static ScorePeriod PreviousOf(DateTime now, PeriodKind kind, int weekStartsOn)
    {
        var current = Of(now, kind, weekStartsOn);

        DateTime start;
        if (kind == PeriodKind.Month)
        {
            // 必须从「月首」往回减一个月，不能拿当前日期直接减 ——
            // 3 月 31 日减一个月会被截断成 2 月 28 日，得到一个从月中开始的区间。
            // 月首减月永远不会溢出，所以这样是安全的。
            start = current.Start.AddMonths(-1);
        }
        else
        {
            // 当前期起点的前一天必然落在上一周里，取它的周起点即可，
            // 比直接减 7 天更稳妥（不依赖「一周正好 7 天」这个隐含假设）。
            start = StartOfWeek(current.Start.AddDays(-1), weekStartsOn);
        }

        return new ScorePeriod { Kind = kind, Start = start, EndExclusive = current.Start };
    }

    /// <summary>周期标题。中文文案放在这里，界面直接用，免得两边各写一份对不上。</summary>
    public static string TitleOf(PeriodKind kind, bool isCurrent) => kind switch
    {
        PeriodKind.Month => isCurrent ? "本月" : "上月",
        _ => isCurrent ? "本周" : "上周"
    };

    /// <summary>
    /// 把区间渲染成「10-12 ~ 10-18」这种老师一眼能懂的样子。
    ///
    /// 关键点：EndExclusive 是「结束时刻」，它是下一期的起点（比如 10-19 00:00）。
    /// 直接把它显示出来，看起来就像这个周期多算了一天。
    /// 所以显示时必须退回一天，取半开区间真正覆盖的最后一天。
    /// </summary>
    public static string FormatRange(ScorePeriod period)
    {
        if (period.Length <= TimeSpan.Zero) return "—";
        var lastDay = period.EndExclusive.AddDays(-1).Date;
        return $"{period.Start:MM-dd} ~ {lastDay:MM-dd}";
    }

    /// <summary>某个周期内各小组的净增分（没记录的小组不会出现在结果里）。</summary>
    public static Dictionary<string, int> TotalsByGroup(
        IEnumerable<ScoreRecord> records, ScorePeriod period)
    {
        var result = new Dictionary<string, int>();
        foreach (var r in records)
        {
            if (!period.Contains(r.CreatedAt)) continue;
            result.TryGetValue(r.GroupId, out var accumulated);
            result[r.GroupId] = accumulated + r.Delta;
        }
        return result;
    }

    /// <summary>某个周期内有多少条记录。</summary>
    public static int CountRecords(IEnumerable<ScoreRecord> records, ScorePeriod period) =>
        records.Count(r => period.Contains(r.CreatedAt));

    /// <summary>
    /// 截至某个时刻（不含）各小组的累计总分。
    ///
    /// 这是「期末累计」的来源，也是整个历史对比里唯一能回答
    /// 「上周这个时候谁领先」的东西 —— <see cref="Group.CachedTotal"/> 只有
    /// 「现在」这一个值，拿它减一减是算不出历史名次的。
    /// </summary>
    public static Dictionary<string, int> CumulativeByGroup(
        IEnumerable<ScoreRecord> records, DateTime asOfExclusive)
    {
        var result = new Dictionary<string, int>();
        foreach (var r in records)
        {
            if (r.CreatedAt >= asOfExclusive) continue;
            result.TryGetValue(r.GroupId, out var accumulated);
            result[r.GroupId] = accumulated + r.Delta;
        }
        return result;
    }

    /// <summary>
    /// 按给定的取值给小组排名，规则与排行榜完全一致：标准竞赛排名（1、2、2、4），
    /// 同分并列。平手时的先后由 SortOrder、Name 决定，所以结果稳定、不会每次刷新都跳。
    /// </summary>
    public static Dictionary<string, int> RankByValue(
        IReadOnlyList<Group> groupsInDisplayOrder, Func<Group, int> value)
    {
        var sorted = groupsInDisplayOrder
            .OrderByDescending(value)
            .ThenBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .ToList();

        var ranks = new Dictionary<string, int>(sorted.Count);
        var lastValue = 0;
        var lastRank = 0;

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = value(sorted[i]);
            // 与上一名同分则沿用上一名的名次
            var rank = i > 0 && current == lastValue ? lastRank : i + 1;
            ranks[sorted[i].Id] = rank;
            lastValue = current;
            lastRank = rank;
        }

        return ranks;
    }

    /// <summary>
    /// 组装「本期 vs 上期」的完整对比。
    ///
    /// 只统计 <paramref name="groupsInDisplayOrder"/> 里还存在的小组：
    /// 已经删掉的小组留下的记录在界面上无处安放，硬算进合计反而会让老师
    /// 「合计对不上表里各行之和」。
    /// </summary>
    public static PeriodComparison Compare(
        IReadOnlyList<Group> groupsInDisplayOrder,
        IReadOnlyList<ScoreRecord> records,
        ScorePeriod current,
        ScorePeriod previous)
    {
        var currentTotals = TotalsByGroup(records, current);
        var previousTotals = TotalsByGroup(records, previous);
        var cumulativeNow = CumulativeByGroup(records, current.EndExclusive);
        var cumulativeBefore = CumulativeByGroup(records, previous.EndExclusive);

        var currentRanks = RankByValue(groupsInDisplayOrder, g => ValueOf(currentTotals, g.Id));
        var currentCumulativeRanks = RankByValue(groupsInDisplayOrder, g => ValueOf(cumulativeNow, g.Id));
        var previousCumulativeRanks = RankByValue(groupsInDisplayOrder, g => ValueOf(cumulativeBefore, g.Id));

        // 主排序看「本期得分」：老师打开这一页最想知道的是「这一周谁最活跃」。
        var rows = groupsInDisplayOrder
            .Select(g => new
            {
                Row = new PeriodGroupRow
                {
                    Group = g,
                    PeriodRank = ValueOf(currentRanks, g.Id),
                    CurrentDelta = ValueOf(currentTotals, g.Id),
                    PreviousDelta = ValueOf(previousTotals, g.Id),
                    CumulativeNow = ValueOf(cumulativeNow, g.Id),
                    CumulativeBefore = ValueOf(cumulativeBefore, g.Id),
                    CurrentCumulativeRank = ValueOf(currentCumulativeRanks, g.Id),
                    PreviousCumulativeRank = ValueOf(previousCumulativeRanks, g.Id)
                },
                g.SortOrder,
                g.Name
            })
            .OrderByDescending(x => x.Row.CurrentDelta)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => x.Row)
            .ToList();

        var known = new HashSet<string>(groupsInDisplayOrder.Select(g => g.Id));

        return new PeriodComparison
        {
            Current = current,
            Previous = previous,
            Rows = rows,
            // 合计按「表里有行的小组」求和，保证「合计 == 各行之和」永远成立
            CurrentTotal = rows.Sum(r => r.CurrentDelta),
            PreviousTotal = rows.Sum(r => r.PreviousDelta),
            CurrentRecordCount = records.Count(r => current.Contains(r.CreatedAt) && known.Contains(r.GroupId)),
            PreviousRecordCount = records.Count(r => previous.Contains(r.CreatedAt) && known.Contains(r.GroupId))
        };
    }

    private static int ValueOf(Dictionary<string, int> map, string key) =>
        map.TryGetValue(key, out var value) ? value : 0;
}
