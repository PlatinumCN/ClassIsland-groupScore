using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 自动化行动：给小组加分或扣分。
///
/// 有了它，老师不用点积分球也能记分。典型配法：
/// <code>
/// 触发器「上课」 ──> 行动「小组加减分」（+1 分，全部小组）✔ 可恢复
/// </code>
/// 下课铃响时 ClassIsland 自动执行恢复，把刚加的 1 分扣回去。
///
/// <para>
/// ⚠️ 本类必须**直接**继承 <see cref="ActionBase{TSettings}"/>，
/// 中间不要再插一层自定义基类（比如像那四条悬浮球行动共用的 <c>BallActionBase</c>）。
/// 原因是 ClassIsland 这样取设置类型：
/// <code>provider.GetType().BaseType?.GetGenericArguments().FirstOrDefault()</code>
/// 隔了一层，<c>BaseType</c> 就变成你的自定义基类，泛型参数取不到，
/// 行动设置永远是 null —— <b>而且不报错，只是静默失效</b>。
/// 自检台里有一条检查专门盯这个。
/// </para>
/// </summary>
[ActionInfo(
    "bojinxia.groupScore.addScore",
    "小组加减分",
    "\ue017",                     // ClassIsland.Core.Icons.FluentIconKind.AddSubtractCircleRegular
    addDefaultToMenu: true,
    defaultGroupToMenu: "小组积分")]
public class AddScoreAction : ActionBase<AddScoreActionSettings>
{
    private readonly ScoreService _score;

    public AddScoreAction(ScoreService score) => _score = score;

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Apply(1);
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        Apply(-1);
    }

    /// <summary>
    /// <paramref name="sign"/> 为 +1 表示执行、-1 表示恢复。
    /// 恢复不是去删掉原来那条记录，而是**记一条反向量**。
    /// 理由：执行和恢复之间，老师很可能自己又手动加过分，
    /// 「删掉最后一条」会删错人；补一条反向量则是可审计的、绝不会连坐。
    /// </summary>
    private void Apply(int sign)
    {
        var delta = Settings.Delta * sign;
        if (delta == 0) return;

        var baseReason = string.IsNullOrWhiteSpace(Settings.Reason) ? "自动化" : Settings.Reason.Trim();
        var reason = sign > 0 ? baseReason : $"撤销：{baseReason}";

        foreach (var groupId in ResolveTargets(sign))
        {
            // 返回值要看：被「只在上课时加分」挡下来时会返回 false，
            // 而时段是全局判定 —— 挡下一个就等于这次执行整个不成立，直接收工。
            // （行动没有反馈通道，所以这里不提示；老师会在悬浮球 / 设置页上看到原因。）
            if (!_score.AddScore(groupId, delta, reason)) return;
        }
    }

    /// <summary>
    /// 算出这次要动哪些小组。
    ///
    /// 加分时顺手把名单记进设置（<see cref="AddScoreActionSettings.AffectedGroupIds"/>），
    /// 恢复时优先用这份名单 —— 这样「全部小组」模式也精确：
    /// 执行时班里有 4 个组、恢复前老师新建了第 5 个组，恢复只会扣那 4 个。
    /// </summary>
    private List<string> ResolveTargets(int sign)
    {
        // Groups 返回的是快照（内部 ToList 过），遍历时不会被 AddScore 的写入影响
        var current = _score.Groups.Select(g => g.Id).ToList();
        var configured = string.IsNullOrEmpty(Settings.GroupId)
            ? new List<string>()
            : new List<string> { Settings.GroupId };

        if (sign > 0)
        {
            var targets = Settings.ApplyToAllGroups ? current : configured;
            Settings.AffectedGroupIds = targets;
            return targets;
        }

        // 恢复：优先用当初记下的名单。
        // 名单为空只可能出现在「旧版本配置」或「有人手工改过配置文件」的情况，
        // 那就退回按当前配置推一份，保证仍然有确定行为。
        var recorded = Settings.AffectedGroupIds;
        if (recorded is { Count: > 0 }) return recorded;
        return Settings.ApplyToAllGroups ? current : configured;
    }
}
