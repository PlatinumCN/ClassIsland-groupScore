using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 自动化行动：隐藏积分球。
///
/// 典型用法：考试、自习等不想让积分球出现的时段，
/// 用触发器在这段时间开始/结束时各配一条行动（一条隐藏、一条显示）。
///
/// 撤销语义：反向执行，即显示积分球。
/// </summary>
[ActionInfo(
    "bojinxia.groupScore.ball.hide",
    "隐藏积分球",
    "\ue817",                     // FluentIconKind.EyeOffRegular
    addDefaultToMenu: true,
    defaultGroupToMenu: "小组积分")]
public class HideBallAction : BallActionBase
{
    public HideBallAction(FloatingBallHost host) : base(host)
    {
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Host.Hide();
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        Host.Show();
    }
}
