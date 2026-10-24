namespace GroupScorePlugin.Services;

/// <summary>写 SCORE_RECORDS 表时该采取哪种策略。</summary>
public enum RecordWriteMode
{
    /// <summary>库里的记录和内存完全一致，一条都不用写。</summary>
    None,

    /// <summary>只在尾部追加新记录（库中已有的前缀原样保留）。</summary>
    Tail,

    /// <summary>整张表重写。**任何判断不准的情况都退到这里**，宁可贵一点也不能漏写。</summary>
    Full
}

/// <summary>一次落盘该怎么做。<see cref="KeepCount"/> 只对 <see cref="RecordWriteMode.Tail"/> 有意义。</summary>
public readonly record struct RecordWritePlan(RecordWriteMode Mode, int KeepCount);

/// <summary>
/// 「这次落盘要不要重写整张记录表」的判定。
///
/// **为什么值得单独抽出来**：这是增量落盘唯一可能出错的地方，而这个错法是**静默**的 ——
/// 判断错了不会抛异常，只会让某些记录悄悄写不进去，等老师发现分数不对时已经过去好几节课。
/// 所以判定逻辑被特意写成不依赖任何 IO 的纯函数，好让自检台把每种组合都真跑一遍，
/// 而不是在测试里把同样的 if 抄一遍再自测（那种测试等于没测）。
///
/// 数据形态的前提（由 ScoreService 与 ScoreStore 共同保证）：
/// - 记录只会在**尾部追加**（加一次分 = 追加一条）；
/// - 撤销 = **删掉最后一条**；
/// - 列表顺序即时间顺序，不是按 CreatedAt 排序推出来的。
///
/// 于是「内存里的记录」永远是「库里那些记录」的前缀 + 若干条新记录，
/// 或者干脆是本库的后缀被截短（撤销过）。判定就是在确认这个前缀关系还成不成立。
/// </summary>
public static class RecordWriteDecision
{
    /// <param name="storedCount">库里现在有多少条记录。</param>
    /// <param name="storedLastId">库里**最后一条**记录的 Id；库里没记录时传 null。</param>
    /// <param name="memoryIds">内存里的记录 Id，按列表顺序（即时间顺序）。</param>
    public static RecordWritePlan Decide(int storedCount, string? storedLastId, IReadOnlyList<string> memoryIds)
    {
        if (memoryIds is null) return new RecordWritePlan(RecordWriteMode.Full, 0);

        var memoryCount = memoryIds.Count;

        // 内存里一条都没有：库里要么也是空的（什么都不用干），要么得清干净。
        if (memoryCount == 0)
            return storedCount == 0
                ? new RecordWritePlan(RecordWriteMode.None, 0)
                : new RecordWritePlan(RecordWriteMode.Full, 0);

        // 内存比库里还少 ⇒ 中间发生过撤销（尾部被截掉）。
        // 截断本来也能做，但撤销不是高频操作，为了少一条能出错的路，直接全量重写。
        if (memoryCount < storedCount)
            return new RecordWritePlan(RecordWriteMode.Full, 0);

        // 数量没变：确认最后一条还是同一条记录。
        // 一致 ⇒ 这次落盘只有小组名/颜色之类的改动，记录表一个字都不用写。
        if (memoryCount == storedCount)
        {
            return storedCount > 0 && string.Equals(memoryIds[memoryCount - 1], storedLastId, StringComparison.Ordinal)
                ? new RecordWritePlan(RecordWriteMode.None, 0)
                : new RecordWritePlan(RecordWriteMode.Full, 0);
        }

        // 内存比库多 —— 真正的追加场景，也是这条优化存在的意义。
        // 库里本来就空 ⇒ 内存里全部都是新的。
        if (storedCount == 0)
            return new RecordWritePlan(RecordWriteMode.Tail, 0);

        // 关键校验：内存里「对应库里最后一条」的那条必须真的是同一条记录。
        // 对不上说明记录的中间部分被动过（正常的增删路径到不了这里），
        // 此时只追加尾部就会漏掉中间的变化 ⇒ 退到全量重写。
        // storedLastId 为 null 时 Equals 必然为 false，同样安全地退到全量重写。
        return string.Equals(memoryIds[storedCount - 1], storedLastId, StringComparison.Ordinal)
            ? new RecordWritePlan(RecordWriteMode.Tail, storedCount)
            : new RecordWritePlan(RecordWriteMode.Full, 0);
    }
}
