namespace GroupScorePlugin.Models;

/// <summary>
/// 上课时段相关的两条规则：
/// ① 悬浮球该不该在这节课自动弹出（可以按科目白名单筛）；
/// ② 现在允不允许加分（可以限制成「只在上课时」）。
///
/// 跟 <c>BallPlacement</c> / <c>MemberScores</c> / <c>PeriodStats</c> 一样，
/// 这里**一个 ClassIsland / Avalonia 类型都不引**，只吃 bool 和字符串。
/// 原因不是洁癖：一旦引了宿主类型，自检台就编不进这个文件，
/// 于是「整组断言静默消失」—— 不是报错，是看起来全过。
///
/// 所以本文件只回答「什么算上课」这件事本身，「去宿主那儿取数据」交给调用方
/// （<c>FloatingBallHost</c> 负责取，然后把基本类型喂进来）。
/// </summary>
public static class LessonRules
{
    /// <summary>
    /// 悬浮球该不该自动弹出。
    /// </summary>
    /// <param name="autoShowOnClass">总开关：上课时自动弹出。</param>
    /// <param name="subjectFilterOn">是否启用科目白名单。</param>
    /// <param name="allowedSubjects">白名单里的科目名。</param>
    /// <param name="currentSubjectName">当前正在上的科目名；自习 / 课间这类没有科目的时段传 null。</param>
    public static bool ShouldAutoShow(bool autoShowOnClass,
                                      bool subjectFilterOn,
                                      IReadOnlyList<string>? allowedSubjects,
                                      string? currentSubjectName)
    {
        if (!autoShowOnClass) return false;
        if (!subjectFilterOn) return true;

        // 开着筛选却一个科目都没勾 —— 谁都不弹。
        // 这比「那还是都弹吧」更贴近老师的本意：勾这个选项的意思就是
        //「我只想在这几节课看到它」，而不是「暂时不想配」。
        if (allowedSubjects == null || allowedSubjects.Count == 0) return false;

        var name = currentSubjectName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;

        foreach (var s in allowedSubjects)
        {
            // 名字有两个来源（老师在设置里勾的、课表里读到的），
            // 首尾空格和大小写不该被当成不同科目。
            if (string.Equals(s?.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 现在允不允许加分。三重兜底，顺序就是优先级：
    ///
    /// 1. 没开「只在上课时加分」→ 一直允许（老行为，不受任何影响）；
    /// 2. **课表不可用 → 允许**。老师可能压根没在 ClassIsland 里配课表，
    ///    这时时段永远停在「放学后」，照着卡就等于插件变砖 —— 宁可放开；
    /// 3. 课表可用 → 看是不是正在上课。
    /// </summary>
    /// <param name="restrictToClassTime">设置里有没有开这个限制。</param>
    /// <param name="classPlanUsable">课表功能是否可用（已加载且已启用）。</param>
    /// <param name="isOnClass">当前是否处于上课时段。</param>
    public static bool CanAddScore(bool restrictToClassTime, bool classPlanUsable, bool isOnClass)
        => !restrictToClassTime || !classPlanUsable || isOnClass;

    /// <summary>加分被拒时给老师看的原因；允许时为 null。</summary>
    public static string? ScoreBlockedReason(bool restrictToClassTime, bool classPlanUsable, bool isOnClass)
        => CanAddScore(restrictToClassTime, classPlanUsable, isOnClass)
            ? null
            : "现在不在上课时间，已按设置禁止加分";

    /// <summary>
    /// 把加分限制的当前状态说成一句人话，给设置页显示。
    /// 也放在这里是为了和 <see cref="CanAddScore"/> 用同一套判断，不另立一份。
    /// </summary>
    public static string DescribeScoreAccess(bool restrictToClassTime, bool classPlanUsable, bool isOnClass)
    {
        if (!restrictToClassTime) return "加分时段限制：已关闭（任何时候都能加分）";
        if (!classPlanUsable) return "加分时段限制：已开启，但课表不可用，暂不限制";
        return isOnClass
            ? "加分时段限制：已开启，当前正在上课，可以加分"
            : "加分时段限制：已开启，当前不在上课时间，已禁止加分";
    }
}
