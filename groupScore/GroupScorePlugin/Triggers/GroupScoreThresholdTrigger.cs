using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Triggers;

/// <summary>
/// 自动化触发器：某个小组的积分达到（或跌破）阈值。
///
/// <para>
/// 配上第 1 步做的「小组加减分」行动，就是一个完整的闭环。典型配法：
/// </para>
/// <code>
/// 触发器「小组积分达到阈值」（第 3 小组 ≥ 20 分） ✔
///    └─> 行动「小组加减分」(+2 分，第 3 小组，原因「小组夺冠」)
/// </code>
/// 第 3 小组冲到 20 分的那一刻自动再奖 2 分，老师只管点分数，不用盯着看。
///
/// <para>
/// ⚠️ 本类必须<b>直接</b>继承 <see cref="TriggerBase{TSettings}"/>，
/// 中间不要再插一层自定义基类。框架取设置类型的写法是：
/// <code>info.SettingsControlType.BaseType?.GetGenericArguments().First()</code>
/// 隔了一层，<c>BaseType</c> 就变成你的自定义基类，泛型参数取不到 ——
/// 于是框架认为这个触发器「没有设置」，<b>根本不会给它注入 SettingsInternal</b>，
/// 而这个类的 <c>Settings</c> 会永远是 null。<b>不报错，只是静默失效。</b>
/// 自检台里有一条检查专门盯这个。
/// </para>
/// </summary>
[TriggerInfo(
    "bojinxia.groupScore.trigger.threshold",
    "小组积分达到阈值",
    "\ue171")]   // ClassIsland.Core.Icons.FluentIconKind.ArrowTrendingLinesRegular
public class GroupScoreThresholdTrigger : TriggerBase<GroupScoreThresholdTriggerSettings>
{
    private readonly ScoreService _score;

    /// <summary>
    /// 边沿检测器。这里可以直接用字段记状态，跟行动那边的处境不一样 ——
    /// 行动的实例是「执行一次取一个」，字段存不住东西；
    /// 触发器实例则被框架用 <c>TriggerSettings.TriggerInstance</c> 一直握着，
    /// 从 <see cref="Loaded"/> 到 <see cref="UnLoaded"/> 都是同一个对象。
    /// 官方那个「托盘菜单触发器」也是这么用字段记 <c>MenuItem</c> 的。
    /// </summary>
    private readonly ThresholdEdgeDetector _detector = new();

    public GroupScoreThresholdTrigger(ScoreService score) => _score = score;

    /// <summary>
    /// 挂到工作流上时调用。
    ///
    /// 可以安全地在<b>这里</b>读 <c>Settings</c>：框架是先注入设置对象
    /// （<c>trigger.SettingsInternal = ...</c>）再调 <see cref="Loaded"/> 的，
    /// 官方自带的触发器也在 Loaded 里直接读设置。
    /// </summary>
    public override void Loaded()
    {
        // 先取一次当前状态当基线，不产生事件。
        // 工作流刚加载时可能已经有组超过阈值了，那是上一节课的功劳，
        // 不该在这一刻补放一遍庆祝音效。
        _detector.Prime(IsSatisfied());

        _score.ScoresChanged += OnScoresChanged;
    }

    /// <summary>
    /// 从工作流上卸下时调用。必须在这里退订，
    /// 否则这个对象会被 ScoreService 的事件一直牵着，退不掉 ——
    /// 既漏内存，又会在工作流已经不存在之后继续触发。
    /// </summary>
    public override void UnLoaded()
    {
        _score.ScoresChanged -= OnScoresChanged;
    }

    private void OnScoresChanged(object? sender, EventArgs e)
    {
        try
        {
            switch (_detector.Feed(IsSatisfied()))
            {
                case ThresholdEdge.Raised:
                    Trigger();          // 跨过阈值 → 跑行动集
                    break;

                case ThresholdEdge.Fallen:
                    // 又掉回去了 → 请框架恢复。
                    // 只有老师在行动集上勾了「可恢复」时这一下才真的会恢复，
                    // 没勾就是空操作，不会出错。
                    TriggerRevert();
                    break;
            }
        }
        catch
        {
            // 这里绝不能把异常抛回去：调用栈的上游是老师正在点的那个加/减分按钮，
            // 自动化配置有问题不该让课堂上的记分操作跟着崩。
            // 宁可这一次不触发，下一次分数变化还有机会。
        }
    }

    /// <summary>
    /// 当前是否满足触发条件。
    ///
    /// 判定逻辑本身在 <see cref="GroupScoreThresholdRule"/> 里（不依赖 ClassIsland，
    /// 所以自检台能拿真实数据把它跑个遍）；这里只负责把分数喂给它。
    /// <c>Settings.GroupId</c> 为空表示「任意小组」，有一个组够线就算满足。
    /// </summary>
    private bool IsSatisfied()
    {
        var settings = Settings;

        // 防御：理论上框架一定注入了设置对象，但真为 null 时
        // 也不能让每次加分都抛 NullReferenceException。
        if (settings is null) return false;

        // Groups 内部已经 ToList() 过，是快照，遍历期间不会被写入影响
        return GroupScoreThresholdRule.IsSatisfied(
            _score.Groups.Select(g => (g.Id, g.CachedTotal)),
            settings.GroupId,
            settings.Threshold,
            settings.Comparison);
    }
}
