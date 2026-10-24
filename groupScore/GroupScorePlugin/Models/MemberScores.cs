namespace GroupScorePlugin.Models;

/// <summary>
/// 成员维度积分的全部算术。
///
/// 刻意不引用任何 Avalonia / ClassIsland 类型 —— 理由和第 2 步的条件判定、
/// 第 3 步的位置算术完全一样：抽出来之后自检台能**直接调用真代码**跑断言，
/// 而不是在测试文件里把同样的循环再抄一遍（那种测试测的是抄写，不是逻辑）。
///
/// 这个文件里出现一个 Avalonia 类型，整组成员断言就编不进自检台、直接消失。
/// </summary>
public static class MemberScores
{
    /// <summary>某个成员个人的净得分。只统计指名到这个人的记录。</summary>
    public static int TotalOf(IEnumerable<ScoreRecord> records, string memberId) =>
        records.Where(r => r.MemberId == memberId).Sum(r => r.Delta);

    /// <summary>
    /// 一个小组里「记到小组一级、没有落到具体人头上的分」。
    ///
    /// 包含两种情况，都归到这一类：
    ///   1. MemberId 为空 —— 老师本来就是按组加的（如「全组作业优秀」）；
    ///   2. MemberId 指向一个已经不存在的成员 —— 成员被删掉后的残留引用。
    /// 第 2 种之所以也算「未指派」而不是直接丢掉，是为了让下面
    /// <see cref="Balances"/> 那个恒等式永远成立；
    /// 恒等式一旦不成立，就说明有人绕过 ScoreService 直接改了 CachedTotal。
    /// </summary>
    public static int UnassignedTotal(IEnumerable<ScoreRecord> records,
                                      IReadOnlyCollection<string> knownMemberIds)
    {
        var known = new HashSet<string>(knownMemberIds);
        return records
            .Where(r => r.MemberId == null || !known.Contains(r.MemberId))
            .Sum(r => r.Delta);
    }

    /// <summary>
    /// 把成员聚合成带名次的列表。
    ///
    /// <paramref name="membersInDisplayOrder"/> 必须是**已经排好显示顺序**的成员
    /// （调用方按 SortOrder 排）。这里用的是稳定排序，所以同分成员会保持传入顺序，
    /// 不会出现「每次刷新名次顺序都不一样」的抖动。
    /// </summary>
    public static List<MemberScoreItem> Summarize(
        IEnumerable<Member> membersInDisplayOrder,
        IEnumerable<ScoreRecord> records)
    {
        // 先物化一次，避免在下面的循环里对同一个序列反复枚举
        var recordList = records as IList<ScoreRecord> ?? records.ToList();

        var items = membersInDisplayOrder
            .Select(m => new MemberScoreItem
            {
                MemberId = m.Id,
                GroupId = m.GroupId,
                Name = m.Name,
                Total = TotalOf(recordList, m.Id)
            })
            .OrderByDescending(i => i.Total)
            .ToList();

        // 标准竞赛排名：同分沿用上一名的名次（1、2、2、4），与小组排行榜同一个规则
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Rank = i > 0 && items[i].Total == items[i - 1].Total
                ? items[i - 1].Rank
                : i + 1;
        }

        return items;
    }

    /// <summary>
    /// 删除成员时把它的记录「孤儿化」：只把 MemberId 清空，**记录本身留着**。
    ///
    /// 为什么不连带删记录：
    ///   1. 删掉一个学生不等于他挣的那些分没发生过，小组总分不该因此变化；
    ///   2. 真要删记录，就得同时回退小组的 CachedTotal 缓存，
    ///      于是「删个人」变成「改全组分数」，风险和语义都更重。
    /// 这跟 DeleteGroup 不同 —— 删小组时归属对象整个不存在了，记录只能一起删。
    ///
    /// 返回被孤儿化的记录条数。
    /// </summary>
    public static int Orphanize(List<ScoreRecord> records, string memberId)
    {
        var count = 0;
        foreach (var r in records)
        {
            if (r.MemberId != memberId) continue;
            r.MemberId = null;
            count++;
        }
        return count;
    }

    /// <summary>
    /// 把一个小组的成员顺序归一化成 0..n-1。
    ///
    /// 先按现有 SortOrder 稳定排序再重新编号，于是：
    ///   - 正常数据：顺序不变，只是编号收紧（删掉中间某个成员后不留空洞）；
    ///   - 加字段之前的老数据（全是 0）：退化成「按文件里的顺序」，
    ///     而不是所有人并列 0 导致排序结果不确定。
    /// </summary>
    public static void NormalizeOrder(List<Member> members)
    {
        var ordered = members.OrderBy(m => m.SortOrder).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;
    }

    /// <summary>
    /// 恒等式：个人分之和 + 未指派分 == 小组总分。
    ///
    /// 这不是「业务规则」而是**数据完整性断言**：分是怎么来的只有两种可能 ——
    /// 落到某人头上，或落在小组上。所以只要这个等式不成立，
    /// 就一定有人绕过了 ScoreService 的「写记录 + 改缓存」双写。
    /// </summary>
    public static bool Balances(int groupTotal,
                                IEnumerable<MemberScoreItem> memberItems,
                                int unassignedTotal) =>
        memberItems.Sum(i => i.Total) + unassignedTotal == groupTotal;
}
