using System.Text;
using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// 业务层：积分的一切读写都必须经过这里。
///
/// 它守两条规矩：
/// 1. 改总分的同时一定写一条记录（双写），保证「记录 = 事实来源」不被绕过。
/// 2. 任何改动完成后触发 ScoresChanged，界面订阅它自动刷新，谁也不用去轮询。
/// </summary>
public class ScoreService
{
    private readonly ScoreStore _store;
    private Func<ScoreAccessSnapshot>? _accessProvider;

    /// <summary>数据发生变化时触发。组件和窗口订阅它来自动刷新界面。</summary>
    public event EventHandler? ScoresChanged;

    /// <summary>
    /// 加分被上课时段拦下时触发，参数是给老师看的原因。
    /// 界面订阅它来回答「为什么我点了没反应」—— 缺了这个反馈，
    /// 老师只会以为插件坏了。
    /// </summary>
    public event EventHandler<string>? ScoreBlocked;

    public ScoreService(ScoreStore store)
    {
        _store = store;
        SeedIfEmpty();
    }

    // ==================== 上课时段守门 ====================

    /// <summary>
    /// 注入「现在是什么时段」的提供者。由 <c>FloatingBallHost</c> 在拿到 ILessonsService 之后调用。
    /// **没注入 = 完全不做时段限制** —— 自检台、以及「宿主里取不到课程服务」都走这条路。
    /// </summary>
    public void SetAccessProvider(Func<ScoreAccessSnapshot> provider) => _accessProvider = provider;

    /// <summary>当前时段快照；没有提供者时返回 null（含义是「不限制」）。</summary>
    public ScoreAccessSnapshot? Access => SafeAccess();

    /// <summary>现在能不能加分。界面用它提前禁用按钮，而不是等老师点了才告诉他不行。</summary>
    public bool CanAddScoreNow => SafeAccess()?.AllowsScore ?? true;

    /// <summary>最近一次加分被时段拦下的原因；成功加分后清空。</summary>
    public string? LastBlockedReason { get; private set; }

    /// <summary>
    /// 取快照并把异常吞掉。
    /// 宿主服务读崩了（课表被换掉、服务被替换）时当作「限制不生效」：
    /// 宁可放开也不能让加分整体瘫痪 —— 这是本插件所有宿主交互的统一态度。
    /// </summary>
    private ScoreAccessSnapshot? SafeAccess()
    {
        if (_accessProvider == null) return null;
        try { return _accessProvider(); }
        catch { return null; }
    }

    /// <summary>四张表的直接入口（只读用途）。写操作请走本类的方法。</summary>
    public ScoreDatabase Data => _store.Data;

    /// <summary>按显示顺序排列的小组。</summary>
    public IReadOnlyList<Group> Groups => Data.OrderedGroups.ToList();

    /// <summary>按显示顺序排列的预设项。</summary>
    public IReadOnlyList<ScorePreset> Presets =>
        Data.Presets.OrderBy(p => p.SortOrder).ToList();

    // ==================== 首次运行播种 ====================

    /// <summary>
    /// 第一次运行时给几个占位小组和常用预设，方便直接上手试。
    /// 只在完全空库时执行，用户把小组删光后重启不会再冒出来（因为 SchemaVersion 已写入）。
    /// </summary>
    private void SeedIfEmpty()
    {
        var d = Data;
        if (d.Seeded) return;

        var colors = new[] { "#D85A30", "#378ADD", "#1D9E75", "#BA7517", "#D4537E", "#7F77DD" };
        for (var i = 0; i < 4; i++)
        {
            d.Groups.Add(new Group
            {
                Name = $"第 {i + 1} 小组",
                Color = colors[i % colors.Length],
                SortOrder = i
            });
        }

        var presets = new (string Label, int Delta)[]
        {
            ("回答正确", 2),
            ("作业优秀", 3),
            ("主动发言", 1),
            ("纪律扣分", -1),
            ("迟到", -2)
        };
        for (var i = 0; i < presets.Length; i++)
        {
            d.Presets.Add(new ScorePreset
            {
                Label = presets[i].Label,
                Delta = presets[i].Delta,
                SortOrder = i
            });
        }

        d.Seeded = true;
        _store.MarkDirty();
    }

    // ==================== 核心：加 / 减分 ====================

    /// <summary>
    /// 给某个小组加分或扣分。这是整个插件最核心的一个方法。
    /// </summary>
    /// <param name="groupId">目标小组。</param>
    /// <param name="delta">变动量，正数加分、负数扣分。为 0 时直接忽略。</param>
    /// <param name="reason">变动原因，会显示在记录里。</param>
    /// <param name="presetId">来自哪个预设项，手动输入则为 null。</param>
    /// <param name="memberId">可选，追溯到个人。</param>
    public bool AddScore(string groupId, int delta, string reason,
                         string? presetId = null, string? memberId = null)
    {
        // ★ 时段守门必须放在这个唯一入口上。
        // 加分目前有 7 个调用点（行动 1 处、悬浮球面板 2 处、设置页 4 处），
        // 在这一句里拦掉就全覆盖了，将来再加调用点也不会漏
        // —— 这是本项目「积分增删只有 ScoreService 一个入口」那条规矩换来的红利。
        var access = SafeAccess();
        if (access is { } snapshot && !snapshot.AllowsScore)
        {
            LastBlockedReason = snapshot.BlockedReason;
            ScoreBlocked?.Invoke(this, LastBlockedReason ?? "现在不能加分");
            return false;
        }
        LastBlockedReason = null;

        if (delta == 0) return false;

        var group = Data.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return false;

        // 校验归属：memberId 指向的成员必须真实存在、而且就在这个组里。
        // 不符合就降级成「只记到小组一级」，而不是拒绝整笔操作 ——
        // 老师按下按钮就期待分数进去了，宁可归属粗略，也不能让分凭空丢掉。
        // （界面上的下拉都是按组过滤过的，真出现跨组只可能是传参写错。）
        if (!string.IsNullOrEmpty(memberId))
        {
            var member = Data.Members.FirstOrDefault(m => m.Id == memberId);
            if (member == null || member.GroupId != groupId) memberId = null;
        }

        // 双写：先追加记录，再更新缓存总分。两件事必须在同一个方法里完成。
        Data.ScoreRecords.Add(new ScoreRecord
        {
            GroupId = groupId,
            MemberId = memberId,
            Delta = delta,
            Reason = string.IsNullOrWhiteSpace(reason) ? "手动调整" : reason.Trim(),
            PresetId = presetId
        });
        group.CachedTotal += delta;

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 撤销最近一次加分/扣分。
    /// 实现方式是「删掉最后一条记录并反向修正总分」，而不是维护额外的撤销栈 ——
    /// 记录表本身就是栈，不必再存一份状态。
    /// 连点多次就是连续撤销。
    /// </summary>
    public bool UndoLast(out string description)
    {
        description = "";
        if (Data.ScoreRecords.Count == 0) return false;

        // 列表顺序即时间顺序，最后一条就是最近一次操作
        var last = Data.ScoreRecords[^1];
        Data.ScoreRecords.RemoveAt(Data.ScoreRecords.Count - 1);

        var group = Data.Groups.FirstOrDefault(g => g.Id == last.GroupId);
        if (group != null) group.CachedTotal -= last.Delta;

        description = $"{group?.Name ?? "未知小组"} {FormatDelta(last.Delta)}（{last.Reason}）";

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>撤销上一步，不关心描述。</summary>
    public bool UndoLast() => UndoLast(out _);

    // ==================== 排行榜 ====================

    /// <summary>
    /// 生成排行榜。名次并列规则是标准竞赛排名（1、2、2、4），
    /// 而不是稠密排名（1、2、2、3）—— 同分的小组必须拿到同一个名次。
    /// </summary>
    /// <param name="topN">只取前几名，-1 表示全部。</param>
    public List<RankItem> GetRanking(int topN = -1)
    {
        var sorted = Data.Groups
            .OrderByDescending(g => g.CachedTotal)
            .ThenBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .ToList();

        // 一次遍历算出各组今日得分，避免在下面的循环里对每条记录反复求和
        var todayStart = DateTime.Today;
        var todayByGroup = Data.ScoreRecords
            .Where(r => r.CreatedAt >= todayStart)
            .GroupBy(r => r.GroupId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Delta));

        var result = new List<RankItem>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            // 与上一名同分则沿用上一名的名次
            var rank = i > 0 && sorted[i].CachedTotal == sorted[i - 1].CachedTotal
                ? result[i - 1].Rank
                : i + 1;

            result.Add(new RankItem
            {
                Rank = rank,
                GroupId = sorted[i].Id,
                Name = sorted[i].Name,
                Total = sorted[i].CachedTotal,
                ColorHex = sorted[i].Color,
                TodayDelta = todayByGroup.TryGetValue(sorted[i].Id, out var t) ? t : 0
            });
        }

        return topN > 0 ? result.Take(topN).ToList() : result;
    }

    /// <summary>某个小组在某个时间点之后累计得了多少分。</summary>
    public int GetTotalSince(string groupId, DateTime since) =>
        Data.ScoreRecords
            .Where(r => r.GroupId == groupId && r.CreatedAt >= since)
            .Sum(r => r.Delta);

    /// <summary>最近的若干条记录，最新的在最前面。</summary>
    public List<ScoreRecord> GetRecentRecords(int count = 20) =>
        Data.ScoreRecords
            .AsEnumerable()
            .Reverse()
            .Take(count)
            .ToList();

    /// <summary>把一条记录渲染成人能读的一行字。</summary>
    public string DescribeRecord(ScoreRecord record)
    {
        var groupName = Data.Groups.FirstOrDefault(g => g.Id == record.GroupId)?.Name ?? "已删除小组";

        // 指名到人的记录写成「第一小组·张三」，让老师一眼看出这条是给谁加的
        var memberName = record.MemberId == null
            ? null
            : Data.Members.FirstOrDefault(m => m.Id == record.MemberId)?.Name;
        var who = memberName == null ? groupName : $"{groupName}·{memberName}";

        return $"{record.CreatedAt:MM-dd HH:mm}　{who}　{FormatDelta(record.Delta)}　{record.Reason}";
    }

    private static string FormatDelta(int delta) => delta >= 0 ? $"+{delta}" : delta.ToString();

    // ==================== 历史对比 ====================

    /// <summary>
    /// 生成「本期 vs 上期」的对比（本周 vs 上周，或本月 vs 上月）。
    ///
    /// 这里只负责把真数据喂给 <see cref="PeriodStats"/>，一行日期算术都不写。
    /// 理由：周期边界差一天是看不出来的，账却已经错了，
    /// 所以切分与聚合必须留在自检台能**直接调用**的地方。
    /// </summary>
    /// <param name="kind">按周还是按月。</param>
    /// <param name="weekStartsOn">一周从周几开始（来自设置），只有按周时才用得上。</param>
    /// <param name="now">观测时刻，默认取本机当前时间；参数化只为测试方便。</param>
    public PeriodComparison GetPeriodComparison(
        PeriodKind kind, int weekStartsOn, DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;

        return PeriodStats.Compare(
            Data.OrderedGroups.ToList(),
            Data.ScoreRecords,
            PeriodStats.Of(moment, kind, weekStartsOn),
            PeriodStats.PreviousOf(moment, kind, weekStartsOn));
    }

    // ==================== 小组管理 ====================

    public Group AddGroup(string name, string? color = null)
    {
        var nextOrder = Data.Groups.Count == 0 ? 0 : Data.Groups.Max(g => g.SortOrder) + 1;
        var palette = new[] { "#D85A30", "#378ADD", "#1D9E75", "#BA7517", "#D4537E", "#7F77DD" };
        var g = new Group
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"第 {nextOrder + 1} 小组" : name.Trim(),
            Color = color ?? palette[Data.Groups.Count % palette.Length],
            SortOrder = nextOrder
        };
        Data.Groups.Add(g);
        _store.MarkDirty();
        RaiseChanged();
        return g;
    }

    public bool RenameGroup(string groupId, string newName)
    {
        var g = Data.Groups.FirstOrDefault(x => x.Id == groupId);
        if (g == null) return false;
        g.Name = newName.Trim();
        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>删除小组，并级联清掉它名下的成员和积分记录。</summary>
    public bool DeleteGroup(string groupId)
    {
        var g = Data.Groups.FirstOrDefault(x => x.Id == groupId);
        if (g == null) return false;

        Data.Groups.Remove(g);
        Data.Members.RemoveAll(m => m.GroupId == groupId);
        Data.ScoreRecords.RemoveAll(r => r.GroupId == groupId);

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>在显示顺序中上移或下移一位。direction 为 -1 上移、+1 下移。</summary>
    public bool MoveGroup(string groupId, int direction)
    {
        var ordered = Data.OrderedGroups.ToList();
        var index = ordered.FindIndex(g => g.Id == groupId);
        if (index < 0) return false;

        var target = index + direction;
        if (target < 0 || target >= ordered.Count) return false;

        // 交换双方的位置，并重排其余项，避免出现相同的 SortOrder
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 批量导入小组：一行一个名字，空行忽略。
    /// 开学时一次建完七八个组，比一个个点快得多。
    /// </summary>
    public int ImportGroups(IEnumerable<string> lines)
    {
        var added = 0;
        foreach (var raw in lines)
        {
            var name = raw.Trim().TrimStart('-', '*', '　', ' ');
            if (string.IsNullOrWhiteSpace(name)) continue;
            AddGroup(name);
            added++;
        }
        return added;
    }

    // ==================== 成员管理 ====================

    /// <summary>
    /// 某个小组的成员，按显示顺序排列。
    /// 顺序先比 SortOrder、再比姓名，所以「顺序字段没写对」时至少还有个确定的兜底。
    /// </summary>
    public IReadOnlyList<Member> MembersOf(string groupId) =>
        Data.Members
            .Where(m => m.GroupId == groupId)
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.Name)
            .ToList();

    /// <summary>全部成员（跨小组）。转组、查找时用。</summary>
    public IReadOnlyList<Member> AllMembers => Data.Members.ToList();

    public Member? FindMember(string? memberId) =>
        string.IsNullOrEmpty(memberId)
            ? null
            : Data.Members.FirstOrDefault(m => m.Id == memberId);

    public Member AddMember(string groupId, string name)
    {
        // 组内顺序取「当前最大值 + 1」。组成员被删光后回到 0，编号不会一直涨。
        var siblings = Data.Members.Where(m => m.GroupId == groupId).ToList();
        var nextOrder = siblings.Count == 0 ? 0 : siblings.Max(m => m.SortOrder) + 1;

        var m = new Member
        {
            GroupId = groupId,
            Name = string.IsNullOrWhiteSpace(name) ? "新成员" : name.Trim(),
            SortOrder = nextOrder
        };
        Data.Members.Add(m);
        _store.MarkDirty();
        RaiseChanged();
        return m;
    }

    public bool RenameMember(string memberId, string newName)
    {
        var m = Data.Members.FirstOrDefault(x => x.Id == memberId);
        if (m == null) return false;
        m.Name = newName.Trim();
        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>组内上移 / 下移一位。direction 为 -1 上移、+1 下移。</summary>
    public bool MoveMember(string memberId, int direction)
    {
        var m = Data.Members.FirstOrDefault(x => x.Id == memberId);
        if (m == null) return false;

        var ordered = MembersOf(m.GroupId).ToList();
        var index = ordered.FindIndex(x => x.Id == memberId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count) return false;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 删除成员。**记录不删，只把它们孤儿化**（MemberId 清空）。
    ///
    /// 所以删掉一个学生之后：小组总分不变，他原来那些分变成「未指派到个人的分」。
    /// 详细理由见 <see cref="MemberScores.Orphanize"/>。
    /// <paramref name="orphanedRecords"/> 是被孤儿化的记录条数，用来给老师一句提示。
    /// </summary>
    public bool DeleteMember(string memberId, out int orphanedRecords)
    {
        orphanedRecords = 0;
        var m = Data.Members.FirstOrDefault(x => x.Id == memberId);
        if (m == null) return false;

        orphanedRecords = MemberScores.Orphanize(Data.ScoreRecords, memberId);
        Data.Members.Remove(m);

        // 删完把顺序收紧，避免留下空洞（否则排序看起来还是对的，但编号已经乱了）
        MemberScores.NormalizeOrder(Data.Members.Where(x => x.GroupId == m.GroupId).ToList());

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 把成员转到另一个小组（学生换座位时会用到）。
    /// 他名下的记录也一并改归属 —— 记录里的 GroupId 才是总分归属的依据，
    /// 只改成员不改记录，他的分就会继续算在原组头上。
    /// </summary>
    public bool MoveMemberToGroup(string memberId, string targetGroupId)
    {
        var m = Data.Members.FirstOrDefault(x => x.Id == memberId);
        if (m == null || m.GroupId == targetGroupId) return false;

        var target = Data.Groups.FirstOrDefault(g => g.Id == targetGroupId);
        var source = Data.Groups.FirstOrDefault(g => g.Id == m.GroupId);
        if (target == null) return false;

        // 关键：记录改了归属，两边的缓存总分必须同步搬过去。
        // 少了这一步就又变成「绕过双写直接改数据」，CheckGroupBalance 立刻会不成立 ——
        // 分数不会消失，但会同时算在两个组头上（或者在哪个组都不算）。
        var moved = 0;
        foreach (var r in Data.ScoreRecords)
        {
            if (r.MemberId != memberId) continue;
            r.GroupId = targetGroupId;
            moved += r.Delta;
        }
        if (source != null) source.CachedTotal -= moved;
        target.CachedTotal += moved;

        m.GroupId = targetGroupId;
        MemberScores.NormalizeOrder(Data.Members.Where(x => x.GroupId == targetGroupId).ToList());

        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    // ---- 成员积分的读 ----

    /// <summary>组内成员积分榜（按个人分降序，同分并列）。</summary>
    public List<MemberScoreItem> GetMemberScores(string groupId)
    {
        var records = Data.ScoreRecords.Where(r => r.GroupId == groupId).ToList();
        return MemberScores.Summarize(MembersOf(groupId), records);
    }

    /// <summary>某个成员的个人净得分。</summary>
    public int GetMemberTotal(string memberId) =>
        MemberScores.TotalOf(Data.ScoreRecords, memberId);

    /// <summary>
    /// 某个小组里没落到具体人头上的分。
    /// 界面要把它显示出来 —— 否则老师会以为「成员分加起来怎么比组总分少，是不是算错了」。
    /// </summary>
    public int GetUnassignedTotal(string groupId)
    {
        var members = Data.Members.Where(m => m.GroupId == groupId).Select(m => m.Id).ToList();
        var records = Data.ScoreRecords.Where(r => r.GroupId == groupId);
        return MemberScores.UnassignedTotal(records, members);
    }

    /// <summary>
    /// 组总分与「成员分 + 未指派分」是否对得上。
    /// 正常永远是 true；只为在界面/自检里能主动发现「有人绕过双写」而存在。
    /// </summary>
    public bool CheckGroupBalance(string groupId)
    {
        var group = Data.Groups.FirstOrDefault(g => g.Id == groupId);
        if (group == null) return false;
        return MemberScores.Balances(group.CachedTotal, GetMemberScores(groupId), GetUnassignedTotal(groupId));
    }

    // ==================== 预设项管理 ====================

    public ScorePreset AddPreset(string label, int delta)
    {
        var next = Data.Presets.Count == 0 ? 0 : Data.Presets.Max(p => p.SortOrder) + 1;
        var p = new ScorePreset
        {
            Label = string.IsNullOrWhiteSpace(label) ? "未命名" : label.Trim(),
            Delta = delta,
            SortOrder = next
        };
        Data.Presets.Add(p);
        _store.MarkDirty();
        RaiseChanged();
        return p;
    }

    public bool DeletePreset(string presetId)
    {
        var p = Data.Presets.FirstOrDefault(x => x.Id == presetId);
        if (p == null) return false;
        Data.Presets.Remove(p);
        _store.MarkDirty();
        RaiseChanged();
        return true;
    }

    // ==================== 导入导出 ====================

    /// <summary>
    /// 导出积分记录为 CSV。用带 BOM 的 UTF-8，
    /// 否则 Excel 打开会把中文显示成乱码（这是很多人踩过的坑）。
    /// </summary>
    public string BuildCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间,小组,成员,变动,原因");

        foreach (var r in Data.ScoreRecords)
        {
            var group = Data.Groups.FirstOrDefault(g => g.Id == r.GroupId)?.Name ?? "已删除小组";
            var member = r.MemberId == null
                ? ""
                : Data.Members.FirstOrDefault(m => m.Id == r.MemberId)?.Name ?? "";
            sb.AppendLine($"{r.CreatedAt:yyyy-MM-dd HH:mm:ss},{Escape(group)},{Escape(member)},{r.Delta},{Escape(r.Reason)}");
        }

        sb.AppendLine();
        sb.AppendLine("小组,当前总分");
        foreach (var g in Data.OrderedGroups)
        {
            sb.AppendLine($"{Escape(g.Name)},{g.CachedTotal}");
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 立刻把数据落盘。
    /// 平时靠 ScoreStore 的防抖延迟写，但退出前必须强制刷一次，
    /// 否则最后一秒内的改动会丢在内存里。
    /// </summary>
    public void FlushToDisk() => _store.SaveNow();

    private void RaiseChanged() => ScoresChanged?.Invoke(this, EventArgs.Empty);
}
