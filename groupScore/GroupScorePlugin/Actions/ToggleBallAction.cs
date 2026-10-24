using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 自动化行动：切换积分球的显示 / 隐藏。
///
/// 典型用法：接到一个外部触发器（比如快捷键、某条规则）时，
/// 老师自己决定什么时候让球出现、什么时候收走。
///
/// 撤销语义：再次切换（本操作自逆），所以撤销后看到的和没执行过是一样的。
/// </summary>
[ActionInfo(
    "bojinxia.groupScore.ball.toggle",
    "切换积分球显示",
    "\ue161",                     // FluentIconKind.ArrowSyncRegular
    addDefaultToMenu: true,
    defaultGroupToMenu: "小组积分")]
public class ToggleBallAction : BallActionBase
{
    public ToggleBallAction(FloatingBallHost host) : base(host)
    {
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Host.Toggle();
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        Host.Toggle();
    }
}
