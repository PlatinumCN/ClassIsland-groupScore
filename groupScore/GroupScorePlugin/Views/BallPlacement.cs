namespace GroupScorePlugin.Views;

/// <summary>屏幕工作区（已排除任务栏），用原始整数表示，刻意不依赖 Avalonia。</summary>
public readonly record struct WorkArea(int X, int Y, int Width, int Height);

/// <summary>
/// 悬浮球「该摆在哪」的全部计算。
///
/// 为什么单独抽一个文件、而且一个 Avalonia 类型都不用：
/// 窗口定位最容易出的错（球跑到屏幕外、插上副屏后找不到、面板展开顶出边界）
/// 全都是**纯算术**，跟窗口系统无关。抽出来之后自检台能直接调用真代码跑断言，
/// 而不是在测试里把同样的 if 再抄一遍 —— 抄一遍的测试，测的是抄写，不是逻辑。
///
/// 窗口那边只负责三件事：拿到工作区尺寸、把这里的返回值塞进 Position、把结果落盘。
/// </summary>
public static class BallPlacement
{
    /// <summary>离屏幕边缘留出的空隙，免得球贴着任务栏不好点。</summary>
    public const int DefaultMargin = 12;

    /// <summary>超过这个像素位移才算「拖动」，否则当成点击展开。</summary>
    public const double DragThreshold = 4.0;

    /// <summary>
    /// 合法角落的**唯一来源**。
    /// 设置页的下拉框顺序直接引用这个数组，避免「设置页写一份、窗口判断写一份」两边慢慢漂移。
    /// 第 0 项同时也是新装插件的默认值。
    /// </summary>
    public static readonly string[] CornerIds = { "BottomRight", "TopRight", "BottomLeft", "TopLeft" };

    /// <summary>默认停靠角落。</summary>
    public static string DefaultCorner => CornerIds[0];

    /// <summary>这个角落字符串认不认识。存的是字符串（要落 JSON），所以得防手改配置写错。</summary>
    public static bool IsKnownCorner(string? corner) => Array.IndexOf(CornerIds, corner) >= 0;

    /// <summary>
    /// 把窗口左上角钳进工作区，保证整窗可见。
    ///
    /// 特例：窗口比工作区还大（展开面板 340 宽，遇到竖屏或高分屏缩放的极端情形）。
    /// 这时「右下上限」会跑到「左上下限」的左边，Clamp 的语义就崩了。
    /// 这里的选择是**保左上角**：宁可右边被切一点，也要保证球本身（在左上/左下）看得见、点得到。
    /// </summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height,
        WorkArea area, int margin = DefaultMargin)
    {
        var minX = area.X + margin;
        var minY = area.Y + margin;
        var maxX = area.X + area.Width - width - margin;
        var maxY = area.Y + area.Height - height - margin;

        var cx = maxX < minX ? minX : Math.Clamp(x, minX, maxX);
        var cy = maxY < minY ? minY : Math.Clamp(y, minY, maxY);
        return (cx, cy);
    }

    /// <summary>
    /// 按角落算默认位置（结果同样会被钳制，工作区太小时也有兜底）。
    ///
    /// 认不出的角落字符串（手改配置写了个别的词）**归一化成默认角落**，而不是
    /// 「两个判断都落空 → 左上角」。理由：设置页的下拉框遇到认不出的值时也是回落到第 0 项，
    /// 两边必须指向同一个地方，否则会出现「设置页写着右下角、球却停在左上角」。
    /// </summary>
    public static (int X, int Y) FromCorner(string? corner, int width, int height,
        WorkArea area, int margin = DefaultMargin)
    {
        if (!IsKnownCorner(corner)) corner = DefaultCorner;

        var alignRight = corner is "TopRight" or "BottomRight";
        var alignBottom = corner is "BottomLeft" or "BottomRight";

        var x = alignRight ? area.X + area.Width - width - margin : area.X + margin;
        var y = alignBottom ? area.Y + area.Height - height - margin : area.Y + margin;
        return Clamp(x, y, width, height, area, margin);
    }

    /// <summary>
    /// 最终决策：老师拖过 → 用记忆的位置（再钳一次）；没拖过 → 按角落停靠。
    ///
    /// 记忆坐标**两个都要有**才算数。只有半个（手改配置、或将来写入中断）
    /// 就当作没拖过 —— 半个坐标配上默认的 0，会让球跑到左上角甚至屏幕外。
    /// </summary>
    public static (int X, int Y) Resolve(int? memoryX, int? memoryY, string? corner,
        int width, int height, WorkArea area, int margin = DefaultMargin)
    {
        if (memoryX.HasValue && memoryY.HasValue)
            return Clamp(memoryX.Value, memoryY.Value, width, height, area, margin);

        return FromCorner(corner, width, height, area, margin);
    }

    /// <summary>位移够不够算一次拖动。小于阈值的手抖仍然按「点击」处理，否则老师点不开面板。</summary>
    public static bool ExceedsDragThreshold(double dx, double dy, double threshold = DragThreshold)
        => Math.Abs(dx) >= threshold || Math.Abs(dy) >= threshold;

    /// <summary>一次拖动中的落点：起点 + 位移，然后钳进工作区。拖动过程中每一帧都走这里。</summary>
    /// <remarks>
    /// <paramref name="dx"/> / <paramref name="dy"/> 必须是**指针在屏幕上的物理像素位移**，
    /// 不能是「两次窗口客户区坐标相减」—— 后者在窗口自己移动时会把窗口的位移也算进去，
    /// 结果是球跟不上手（贴到屏幕边之后还会粘住不动）。换算见 <see cref="PointerOnScreen"/>。
    /// </remarks>
    public static (int X, int Y) DragTo(int startX, int startY, double dx, double dy,
        int width, int height, WorkArea area, int margin = DefaultMargin)
        => Clamp(startX + (int)Math.Round(dx), startY + (int)Math.Round(dy),
                 width, height, area, margin);

    /// <summary>
    /// 逻辑尺寸（Avalonia 的布局单位 / DIP）换算成屏幕上的物理像素。
    ///
    /// 为什么必须换算：`TopLevel.ClientSize` / `Bounds` 是**逻辑单位**
    /// （Win32 后端里它就是 `GetClientRect` 的结果除以 `RenderScaling`），
    /// 而 `Window.Position`、`Screens.WorkingArea`、指针的屏幕坐标全是**物理像素**。
    /// 两边混着算，在 125% / 150% 缩放的机器上直接错 ——
    /// 表现是「球贴不到屏幕边、露出一截在外面」，100% 缩放时又完全看不出来。
    /// </summary>
    public static (int Width, int Height) ToPhysicalSize(double logicalWidth, double logicalHeight, double scaling)
        => ((int)Math.Ceiling(logicalWidth * scaling), (int)Math.Ceiling(logicalHeight * scaling));

    /// <summary>
    /// 指针此刻在**屏幕**上的物理像素坐标 = 窗口左上角的屏幕坐标 + 指针在窗口内的偏移。
    ///
    /// <paramref name="clientX"/> / <paramref name="clientY"/> 来自指针事件，是相对窗口客户区的
    /// **逻辑**坐标，所以要乘上 <paramref name="scaling"/>。
    ///
    /// 拖动时**只能**用这个坐标系做差。原因：窗口在拖动过程中自己也在动，
    /// 同一个物理位置在「窗口客户区坐标系」里的读数会随窗口一起变，
    /// 两次事件相减出来的位移会被窗口自身的位移抵消掉一部分 ——
    /// 鼠标越快、位移越大，误差越大；贴着屏幕边被钳住时误差达到满值，
    /// 表现就是「球越拖越跟不上手，最后粘在边上」。
    /// 换成绝对屏幕坐标之后，两次相减就是指针真正走过的距离。
    /// </summary>
    public static (int X, int Y) PointerOnScreen(int windowX, int windowY,
        double clientX, double clientY, double scaling)
        => (windowX + (int)Math.Round(clientX * scaling),
            windowY + (int)Math.Round(clientY * scaling));
}
