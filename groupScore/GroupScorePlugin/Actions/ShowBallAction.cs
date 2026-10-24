using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 自动化行动：显示积分球（以收起的圆球状态出现）。
///
/// 典型用法：触发器「上课」→ 本行动，并勾选「可恢复」，
/// 这样下课时 ClassIsland 会自动执行反向操作把球收回去。
///
/// 撤销语义：反向执行，即隐藏积分球。
/// </summary>
[ActionInfo(
    "bojinxia.groupScore.ball.show",
    "显示积分球",
    "\ue813",                     // ClassIsland.Core.Icons.FluentIconKind.EyeRegular
    addDefaultToMenu: true,
    defaultGroupToMenu: "小组积分")]
public class ShowBallAction : BallActionBase
{
    public ShowBallAction(FloatingBallHost host) : base(host)
    {
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Host.Show();
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        Host.Hide();
    }
}
