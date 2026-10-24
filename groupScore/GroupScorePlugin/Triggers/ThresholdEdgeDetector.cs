namespace GroupScorePlugin.Triggers;

/// <summary>一次「喂数据」造成的状态迁移。</summary>
public enum ThresholdEdge
{
    /// <summary>状态没变，什么都不用做。</summary>
    None = 0,

    /// <summary>从不满足变成满足 —— 该触发了。</summary>
    Raised = 1,

    /// <summary>从满足变成不满足 —— 该恢复了。</summary>
    Fallen = 2
}

/// <summary>
/// 阈值边沿检测器：把「分数一直在变」这件事，翻译成「刚刚跨过去了」那一个瞬间。
///
/// <para>
/// 为什么必须有它：ClassIsland 的触发器是<b>电平</b>式的 ——
/// 只要调 <c>Trigger()</c> 就会去跑一遍行动集。而分数是个连续量，
/// 「第 1 组已经 20 分了」这个事实在老师之后的每一次加分里依然成立。
/// 如果每次都调 <c>Trigger()</c>，老师每加一分，工作流就再跑一遍，
/// 庆祝音效会响到老师把插件删掉为止。
/// </para>
///
/// <para>
/// 所以这里做<b>边沿触发</b>：只在 false→true 的那一刻给 <see cref="ThresholdEdge.Raised"/>，
/// 在 true→false 的那一刻给 <see cref="ThresholdEdge.Fallen"/>，
/// 其余时候一律 <see cref="ThresholdEdge.None"/>。
/// </para>
///
/// <para>
/// 额外的好处：它自带<b>重入保护</b>。如果行动本身又改了分数（比如触发器配的是
/// 「第 1 组 ≥10 分」，行动配的是「给全部小组 +5 分」），回调会再次进来 ——
/// 但此时状态早就是 true 了，第二次 <see cref="Feed"/> 返回 None，不会自激。
/// </para>
///
/// <para>
/// 这个类不依赖 ClassIsland，所以自检台能把它编进去，直接验上面这些性质。
/// </para>
/// </summary>
public sealed class ThresholdEdgeDetector
{
    private bool _satisfied;

    /// <summary>当前是否处于「满足」状态。</summary>
    public bool IsSatisfied => _satisfied;

    /// <summary>
    /// 装填初始状态，<b>不产生任何事件</b>。
    ///
    /// 用在 <c>Loaded()</c> 里：工作流刚被加载时，某个组可能早就超过阈值了。
    /// 那时候不该立刻触发一遍 —— 老师会看到一堆莫名其妙的庆祝音效，
    /// 而这些分是上一节课加的。只有「加载之后发生的变化」才算数。
    /// </summary>
    public void Prime(bool satisfied) => _satisfied = satisfied;

    /// <summary>
    /// 喂入当前状态，返回本次的状态迁移。
    /// </summary>
    public ThresholdEdge Feed(bool satisfied)
    {
        if (satisfied == _satisfied) return ThresholdEdge.None;

        _satisfied = satisfied;
        return satisfied ? ThresholdEdge.Raised : ThresholdEdge.Fallen;
    }
}
