namespace GroupScorePlugin.Models;

/// <summary>
/// 「现在能不能加分」需要的全部输入，一次性打包。
///
/// 做成纯数据（只含三个 bool）有两个好处：
/// ① <c>ScoreService</c> 不必知道 ClassIsland 的存在，自检台可以直接构造它来测拦截；
/// ② 取数据（读 ILessonsService）和判断规则（<see cref="LessonRules"/>）彻底分开，
///    前者在宿主里做、后者在自检台里做，两边对的是同一套规则。
/// </summary>
/// <param name="RestrictToClassTime">设置里有没有开「只在上课时加分」。</param>
/// <param name="ClassPlanUsable">课表功能是否可用（已加载且已启用）。</param>
/// <param name="IsOnClass">当前是否处于上课时段。</param>
public readonly record struct ScoreAccessSnapshot(bool RestrictToClassTime, bool ClassPlanUsable, bool IsOnClass)
{
    /// <summary>按这份快照，现在能不能加分。</summary>
    public bool AllowsScore => LessonRules.CanAddScore(RestrictToClassTime, ClassPlanUsable, IsOnClass);

    /// <summary>不能加分时的原因；能加时为 null。</summary>
    public string? BlockedReason => LessonRules.ScoreBlockedReason(RestrictToClassTime, ClassPlanUsable, IsOnClass);
}
