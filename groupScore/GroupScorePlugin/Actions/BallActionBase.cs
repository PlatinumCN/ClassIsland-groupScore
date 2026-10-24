using ClassIsland.Core.Abstractions.Automation;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 悬浮球相关「自动化行动」的公共基类。
///
/// 为什么要抽这一层：
///   1. 四个行动都要拿 FloatingBallHost，抽出来只写一次注入；
///   2. 把「撤销必须无状态」这条约束写在所有人都会看到的地方（见下）。
///
/// ⚠️ 关于撤销（OnRevert）——这是本模块最容易踩的坑：
/// ClassIsland 每次运行行动都会重新从 DI 取一个行动实例
/// （ActionBase.GetInstance 内部就是 Services.GetKeyedService，注册时是瞬态），
/// 也就是说「执行」和之后的「撤销」大概率**不是同一个对象**。
/// 所以绝对不要在行动里用字段保存「上一次的可见性」之类的东西 ——
/// 撤销时那个字段会是默认值，撤销行为就变成了随机的。
/// 本插件所有行动的撤销都只依赖「当前状态」，是无状态的、可预测的。
/// </summary>
public abstract class BallActionBase : ActionBase
{
    protected BallActionBase(FloatingBallHost host)
    {
        Host = host;
    }

    /// <summary>
    /// 悬浮球宿主。由依赖注入提供，是单例 ——
    /// 和主界面组件、设置页拿到的是同一个对象，共享同一个窗口实例。
    /// </summary>
    protected FloatingBallHost Host { get; }
}
