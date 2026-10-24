using ClassIsland.Core.Attributes;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 自动化行动：直接展开积分球的操作面板（跳过「点一下圆球」这一步）。
///
/// 典型用法：搭配一个你自己习惯的触发时机，
/// 需要的时候直接把加减分面板送到讲台上，省掉一次点击。
///
/// 撤销语义：收起为圆球（不是隐藏）。因为本行动只是「展开」，
/// 反向就是把展开的面板收回去，球本身还在。
/// </summary>
[ActionInfo(
    "bojinxia.groupScore.ball.expand",
    "展开积分球面板",
    "\ue0e1",                     // FluentIconKind.ArrowExpandRegular
    addDefaultToMenu: true,
    defaultGroupToMenu: "小组积分")]
public class ExpandBallAction : BallActionBase
{
    public ExpandBallAction(FloatingBallHost host) : base(host)
    {
    }

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        Host.ShowExpanded();
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        Host.Collapse();
    }
}
