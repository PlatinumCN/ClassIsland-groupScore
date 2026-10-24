namespace GroupScorePlugin.Triggers;

/// <summary>
/// 「小组积分达到阈值」的条件判定。
///
/// <para>
/// 特意做成<b>不依赖 ClassIsland</b> 的静态纯函数：触发器本体必须继承 TriggerBase，
/// 没法编进自检台；但这条判定是触发器里唯一有分支的逻辑，
/// 抽出来之后自检台就能拿真实数据把它跑个遍 ——
/// 而不是「在测试里把同样的 if 再写一遍」，那种测试等于没测。
/// </para>
/// </summary>
public static class GroupScoreThresholdRule
{
    /// <summary>
    /// 在给定的一组分数里，看有没有满足条件的。
    /// </summary>
    /// <param name="groups">候选小组的 (Id, 总分)。</param>
    /// <param name="onlyGroupId">只看这个小组；null 或空字符串表示「任意小组都算」。</param>
    /// <param name="threshold">阈值分数线。</param>
    /// <param name="comparison">比较方向。</param>
    /// <returns>只要有一个小组够线就返回 true。</returns>
    public static bool IsSatisfied(IEnumerable<(string Id, int Total)> groups,
                                   string? onlyGroupId,
                                   int threshold,
                                   ThresholdComparison comparison)
    {
        var anyGroup = string.IsNullOrEmpty(onlyGroupId);

        foreach (var (id, total) in groups)
        {
            if (!anyGroup && id != onlyGroupId) continue;

            // 用 switch 而不是 ==：配置文件里万一出现旧版本不认识的枚举值，
            // 走默认分支（按「达到或超过」）比走成「掉到以下」安全得多。
            var hit = comparison switch
            {
                ThresholdComparison.AtMost => total <= threshold,
                _ => total >= threshold
            };

            if (hit) return true;
        }

        return false;
    }
}
