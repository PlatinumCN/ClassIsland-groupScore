using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Shared;
using GroupScorePlugin.Models;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Views;

/// <summary>
/// 悬浮球窗口：一个无边框、置顶、背景透明的自绘小窗口。
///
/// 两种状态复用同一个窗口（而不是开两个窗口）：
///   收起 = 64×64 的圆球，点一下展开
///   展开 = 340 宽的积分操作面板
/// 窗口用 SizeToContent 自动跟随内容尺寸，所以切换状态时不用手写宽高，
/// 只需要在尺寸变化后重新贴回屏幕角落。
/// </summary>
public partial class FloatingBallWindow : Window
{
    private readonly ScoreService _score;
    private readonly SettingsService _settings;

    private bool _expanded;
    private string _groupSignature = "";

    // ---- 成员下拉 ----
    /// <summary>成员下拉每一条对应的成员 Id；索引 0 固定是 null（「不指名」）。</summary>
    private List<string?> _memberChoiceIds = new();
    private string _memberSignature = "";

    // ---- 拖动状态 ----
    // 只在「按下 → 移动 → 抬起」这一小段时间内有效。
    private bool _dragging;
    private bool _dragMoved;
    private (int X, int Y) _dragOriginScreen;   // 按下时指针的**屏幕物理坐标**
    private PixelPoint _dragStartPosition;      // 按下时窗口的左上角
    private bool _applyingDragResult;   // 正在把拖动结果写进设置（用来抑制自己触发的回读）

    /// <summary>
    /// 展开那一刻球的左上角。
    ///
    /// 面板比球宽得多。要是展开时还按「设置里的角落 / 拖动记忆」重算一遍位置，
    /// 球会先跳回屏幕角落、面板再从那儿冒出来 —— 看起来像被谁挪了一下。
    /// 记住球在哪儿、让面板从那儿长出来，展开动作才连得上。
    ///
    /// 拖动面板之后会同步更新它，否则之后每次尺寸变化（列表变长让面板长高）
    /// 都会按展开时那个旧锚点把面板拽回去 —— 表现是「刚拖好，一操作又跳回去」。
    /// </summary>
    private PixelPoint? _expandAnchor;

    /// <summary>
    /// 公共无参构造。
    /// 一方面让 Avalonia 的 XAML 运行时加载器认得这个窗口（否则会报 AVLN3001 警告），
    /// 另一方面它也是「从任意位置拿到一个悬浮球窗口」的兜底入口。
    /// 正式路径走下面那个带参数的构造，依赖注入更清晰。
    /// </summary>
    public FloatingBallWindow()
        : this(IAppHost.GetService<ScoreService>(), IAppHost.GetService<SettingsService>())
    {
    }

    /// <summary>
    /// 构造函数注入依赖，而不是在 View 里到处写 IAppHost.GetService：
    /// 依赖从哪来一目了然，也方便将来写单元测试。
    /// </summary>
    public FloatingBallWindow(ScoreService score, SettingsService settings)
    {
        _score = score;
        _settings = settings;

        InitializeComponent();
        SetupWindow();
        BindGroupCombo();

        _score.ScoresChanged += OnScoresChanged;
        _score.ScoreBlocked += OnScoreBlocked;
        _settings.Settings.PropertyChanged += OnSettingsChanged;

        // 换组就必须换成员名单，所以这条订阅要在 RefreshGroups 之前挂上，
        // 否则首次填充小组下拉时那次「选中第一组」不会连带把成员列表填好。
        GroupCombo.SelectionChanged += (_, _) => RefreshMembers();

        RefreshGroups();
        RefreshMembers();
        RefreshRank();
    }

    /// <summary>
    /// 窗口关掉时一定要退订。
    /// 这两条订阅的发布者（ScoreService / PluginSettings）活得比窗口长，
    /// 不退订的话被关掉的窗口会一直被它们引用着 —— 泄漏，而且旧窗口还会跟着刷新。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _score.ScoresChanged -= OnScoresChanged;
        _score.ScoreBlocked -= OnScoreBlocked;
        _settings.Settings.PropertyChanged -= OnSettingsChanged;
    }

    /// <summary>
    /// 设置页改了配置，悬浮球这边跟着动。
    /// 位置类改动（角落 / 拖动记忆）立刻归位，不透明度改动立刻生效 ——
    /// 否则老师会以为「改了没反应」。
    /// </summary>
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PluginSettings.BallOpacity):
                ApplyBallOpacity();
                break;

            case nameof(PluginSettings.Corner):
                if (!_dragging) ApplyPosition();
                break;

            case nameof(PluginSettings.BallX):
            case nameof(PluginSettings.BallY):
                // 拖动结果是我们自己写的，位置本来就是对的；
                // 而且 X、Y 是分两次赋值，中途会短暂出现「只有 X 没有 Y」的半个坐标，
                // 那一刻按角落算会把球闪回角落一下。所以自己写的那两次直接跳过。
                if (!_dragging && !_applyingDragResult) ApplyPosition();
                break;

            case nameof(PluginSettings.ScorePanelStyle):
                // 在设置页里换了样式，悬浮球这边要立刻跟着换，否则老师会以为没生效
                ApplyPanelStyle();
                break;
        }
    }

    // ==================== 窗口外观 ====================

    private void SetupWindow()
    {
        // 无边框 + 置顶 + 不进任务栏 + 不可缩放：悬浮球该有的样子
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;

        // 不要抢走讲台上的键盘焦点
        ShowActivated = false;

        // 让窗口背景真正透明，圆球才不会带一个方形底板
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // 内容尺寸一变（球 ⇄ 面板）就重新吸回屏幕角落
        SizeChanged += (_, _) => ApplyPosition();
        ApplyBallOpacity();
    }

    private void ApplyBallOpacity()
    {
        BallRoot.Opacity = _settings.Settings.BallOpacity;
    }

    /// <summary>
    /// 把窗口摆到该在的地方：拖过就用记忆的位置，没拖过就按设置里的角落停靠。
    /// 尺寸一变（球 ⇄ 面板）会重新走一遍 —— 面板宽得多，很可能需要重新钳进屏幕。
    /// 所有算术都在 <see cref="BallPlacement"/> 里，这里只负责取屏幕信息、塞 Position。
    /// </summary>
    private void ApplyPosition()
    {
        // 拖动中不重算：这会儿位置由指针说了算，插一脚会让窗口和指针错开
        if (_dragging) return;

        if (!TryGetWorkArea(out var area)) return;

        var (w, h) = PhysicalWindowSize();
        if (w <= 1 || h <= 1) return;

        // 展开态：从球所在的位置往右下「长」出来。贴到屏幕边时 Clamp 会把整块面板收进去，
        // 效果就是「在右下角的球，面板向左上展开」。
        if (_expanded && _expandAnchor is { } anchor)
        {
            var (ax, ay) = BallPlacement.Clamp(anchor.X, anchor.Y, w, h, area);
            Position = new PixelPoint(ax, ay);
            return;
        }

        var s = _settings.Settings;
        var (x, y) = BallPlacement.Resolve(s.BallX, s.BallY, s.Corner, w, h, area);
        Position = new PixelPoint(x, y);
    }

    /// <summary>让悬浮球立刻回到设置指定的位置。设置页「恢复默认位置」会调到这儿。</summary>
    public void SnapToConfiguredPosition() => ApplyPosition();

    /// <summary>
    /// 窗口当前的**物理像素**尺寸。
    ///
    /// `ClientSize` 是逻辑单位（`GetClientRect` 的结果除以了 `RenderScaling`），
    /// 而工作区和窗口位置都是物理像素 —— 不换算的话，150% 缩放下面板会被当成 340 宽，
    /// 实际却占 510，于是贴边时留出一大截在屏幕外。
    /// </summary>
    private (int Width, int Height) PhysicalWindowSize()
        => BallPlacement.ToPhysicalSize(ClientSize.Width, ClientSize.Height, RenderScaling);

    /// <summary>
    /// 取主屏工作区（已排除任务栏）。
    /// 多屏切换 / 分辨率变化 / 远程桌面断开时都可能抛，取不到就返回 false，等下次尺寸变化重试。
    /// </summary>
    private bool TryGetWorkArea(out WorkArea area)
    {
        area = default;
        try
        {
            var screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null) return false;

            var r = screen.WorkingArea;
            area = new WorkArea(r.X, r.Y, r.Width, r.Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyPosition();
    }

    // ==================== 状态切换 ====================

    /// <summary>以收起（圆球）状态显示。</summary>
    public void ShowBall()
    {
        SetExpanded(false);
        if (!IsVisible) Show();
    }

    /// <summary>以展开（操作面板）状态显示。</summary>
    public void ShowExpanded()
    {
        SetExpanded(true);
        if (!IsVisible) Show();
    }

    private void SetExpanded(bool expanded)
    {
        // 锚点要在改 IsVisible 之前记下来 —— IsVisible 一变，布局就会重算并触发
        // SizeChanged（进而 ApplyPosition），那时候再取位置就已经不是球的位置了。
        if (expanded && !_expanded) _expandAnchor = Position;
        else if (!expanded) _expandAnchor = null;

        _expanded = expanded;
        BallRoot.IsVisible = !expanded;
        PanelRoot.IsVisible = expanded;

        if (expanded)
        {
            ApplyPanelStyle();
            BuildPresets();
            RefreshGroups();
            RefreshMembers();
            RefreshRank();
            StatusText.Text = "";
        }
    }

    // ==================== 加分面板样式 ====================

    /// <summary>
    /// 按设置切换面板形态：
    ///   逐个选择 = 下拉框挑小组 + 预设按钮（小组多、要指到具体成员时用）
    ///   全部展开 = 每个小组一行，行尾直接带加减分按钮（课上最高频的用法）
    /// </summary>
    private void ApplyPanelStyle()
    {
        var grid = ScorePanelStyles.IsGrid(_settings.Settings.ScorePanelStyle);

        PickerArea.IsVisible = !grid;
        GridArea.IsVisible = grid;
        // 全部展开样式里每行本身就带分数，再挂一个排行榜是重复信息
        RankArea.IsVisible = !grid;
        // 按钮上写的是「点了会切到哪个样式」，不是当前样式
        StyleToggleButton.Content = grid ? "逐个选择" : "全部展开";

        if (grid) BuildGridRows();
    }

    private void OnToggleStyleClick(object? sender, RoutedEventArgs e)
    {
        var s = _settings.Settings;
        // 写完设置会触发 OnSettingsChanged → ApplyPanelStyle，这里不用再手动刷一遍
        s.ScorePanelStyle = ScorePanelStyles.IsGrid(s.ScorePanelStyle)
            ? ScorePanelStyles.Picker
            : ScorePanelStyles.Grid;
    }

    // ==================== 事件处理 ====================

    // ---- 拖动手势 ----
    // 收起态和展开态共用这一套逻辑。两条入口（球 / 面板）必须都走 BeginDrag ——
    // 各写一份的话改一处漏一处，而漏掉的那一处表现是「拖着不走」，
    // 通常要等老师用到那个状态才发现。
    //
    // 球很小（64×64），老师的手一定会抖。所以「点击」和「拖动」不能靠事件类型区分，
    // 只能靠位移阈值：松开时位移没超过阈值就算点击，照样展开面板。

    private void OnBallPointerPressed(object? sender, PointerPressedEventArgs e)
        => BeginDrag(e, BallRoot);

    /// <summary>
    /// 面板上的按下。面板里全是按钮和输入框，直接照搬球的逻辑会把控件的点击吃掉，
    /// 所以先判断这一下是不是落在交互控件上 —— 是的话让给控件自己处理。
    /// </summary>
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractiveSource(e.Source)) return;
        BeginDrag(e, PanelRoot);
    }

    private void BeginDrag(PointerPressedEventArgs e, Control source)
    {
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;

        _dragging = true;
        _dragMoved = false;
        _dragOriginScreen = CursorScreen(e);
        _dragStartPosition = Position;

        // 必须捕获指针：不捕获的话，指针一滑出被按住的那一块（球只有 64 像素，
        // 面板也就在键盘旁边那一小片），Moved 事件就全丢了，表现是「跟到半路停住」。
        // 捕获之后松开还会还回去。
        e.Pointer.Capture(source);
    }

    /// <summary>
    /// 指针此刻的屏幕物理坐标。拖动位移一律在这个坐标系里算。
    ///
    /// 事件的 `GetPosition` 给的是「相对窗口客户区」的逻辑坐标 —— 直接在两次事件间相减是错的：
    /// 窗口在拖动过程中自己也在动，同一个物理位置在客户区坐标系里的读数跟着窗口一起变，
    /// 相减出来的位移会被窗口自身的位移抵消掉一部分。
    /// 鼠标动得越快（单位时间内位移越大），被抵消掉的比例越高，球就越跟不上手；
    /// 贴到屏幕边被钳住之后抵消比例达到 100%，球就「粘」在那儿不动了。
    ///
    /// 加回窗口的屏幕位置换算成绝对坐标，两次相减才是指针真正走过的距离。
    /// `Position` 的 getter 是 `GetWindowRect`，读的就是此刻窗口的真实位置，不存在缓存滞后。
    /// </summary>
    private (int X, int Y) CursorScreen(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        return BallPlacement.PointerOnScreen(Position.X, Position.Y, p.X, p.Y, RenderScaling);
    }

    /// <summary>
    /// 指针是不是落在「需要自己收点击的控件」上（或它的内部）。
    /// 从命中元素往上找，撞到窗口为止。
    /// </summary>
    private static bool IsInteractiveSource(object? source)
    {
        var v = source as Visual;
        while (v is not null && v is not Window)
        {
            // 覆盖现有面板里所有会「吃掉一次点击」的控件：
            // 按钮、下拉框、文本输入、数字输入（它内部的上下箭头是 RepeatButton）、滚动区。
            if (v is Button or ToggleButton or ComboBox or TextBox or NumericUpDown or ScrollViewer)
                return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void OnBallPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;

        var now = CursorScreen(e);
        var dx = now.X - _dragOriginScreen.X;
        var dy = now.Y - _dragOriginScreen.Y;

        // 还在阈值内：当作手抖，不搬窗口，也先不标记为拖动。
        // 阈值跟着系统缩放走（"4 像素"指的是 4 个逻辑像素），否则高缩放屏上轻轻一抖就变成拖动。
        if (!_dragMoved && !BallPlacement.ExceedsDragThreshold(dx, dy, BallPlacement.DragThreshold * RenderScaling))
            return;
        _dragMoved = true;

        if (!TryGetWorkArea(out var area)) return;
        var (w, h) = PhysicalWindowSize();

        // 始终以「按下时的窗口位置 + 累计位移」计算，而不是在当前位置上累加，
        // 这样即使中途被钳制到边界，指针往回走也能立刻跟回来，不会粘在边上。
        var (x, y) = BallPlacement.DragTo(_dragStartPosition.X, _dragStartPosition.Y, dx, dy,
            w, h, area);
        Position = new PixelPoint(x, y);
    }

    private void OnBallPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);

        // 没真的拖动 → 这次是点击，交给 OnBallTapped 去展开面板
        if (!_dragMoved) return;

        // 拖完把锚点挪到当前位置。不挪的话，之后任何一次尺寸变化
        // （展开后数据变多、面板长高）都会按展开时那个旧锚点把面板拽回去。
        _expandAnchor = Position;

        // 到这一步才写设置。写设置会连带触发一次落盘（SettingsService 订阅了 PropertyChanged），
        // 要是放在 PointerMoved 里写，拖一次就得写几十遍磁盘。
        _applyingDragResult = true;
        try
        {
            _settings.Settings.BallX = Position.X;
            _settings.Settings.BallY = Position.Y;
        }
        finally
        {
            _applyingDragResult = false;
        }
    }

    private void OnBallTapped(object? sender, TappedEventArgs e)
    {
        // 拖动完抬手指，Avalonia 一般不会再报 Tapped；
        // 但万一报了，也不能让「拖一下」变成「顺手把面板也展开了」。
        if (_dragMoved)
        {
            _dragMoved = false;
            return;
        }

        SetExpanded(true);
    }

    private void OnCollapseClick(object? sender, RoutedEventArgs e) => SetExpanded(false);

    /// <summary>把「这笔分记给谁」写成一句话：光是组名，或「组名·成员名」。</summary>
    private string DescribeTarget(Group group)
    {
        var memberId = SelectedMemberId;
        if (memberId == null) return group.Name;

        var member = _score.FindMember(memberId);
        return member == null ? group.Name : $"{group.Name}·{member.Name}";
    }

    private void OnApplyCustomClick(object? sender, RoutedEventArgs e)
    {
        var group = GroupCombo.SelectedItem as Group;
        if (group == null)
        {
            StatusText.Text = "请先选择小组";
            return;
        }

        var delta = (int)(DeltaBox.Value ?? 0m);
        if (delta == 0)
        {
            StatusText.Text = "分值不能为 0";
            return;
        }

        var who = DescribeTarget(group);

        // 返回值必须看。被「只在上课时加分」挡下来时会返回 false，
        // 这时原因已经由 ScoreBlocked 事件写进状态栏了 —— 直接 return，别覆盖它。
        // （早先这里无视返回值、照样显示「已记：…」，是最糟的那种误报：
        //  老师以为加上了，其实一分没进。）
        if (!_score.AddScore(group.Id, delta, ReasonBox.Text ?? "", null, SelectedMemberId)) return;

        ReasonBox.Text = "";
        StatusText.Text = $"已记：{who} {(delta > 0 ? "+" : "")}{delta}";
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (_score.UndoLast(out var description))
            StatusText.Text = $"已撤销：{description}";
        else
            StatusText.Text = "没有可撤销的记录";
    }

    private void OnScoresChanged(object? sender, EventArgs e)
    {
        // 数据可能由别的界面（设置页）改动，这里统一在 UI 线程刷新
        if (Dispatcher.UIThread.CheckAccess()) RefreshAll();
        else Dispatcher.UIThread.Post(RefreshAll);
    }

    private void RefreshAll()
    {
        RefreshGroups();
        RefreshMembers();
        RefreshRank();

        // 全部展开样式的行里写着每组的当前分，数据一变就得重建，
        // 否则会出现「刚加完分，那一行的数字还是旧的」。
        if (ScorePanelStyles.IsGrid(_settings.Settings.ScorePanelStyle)) BuildGridRows();
    }

    /// <summary>
    /// 重建「全部展开」样式的每一行：色块 + 组名 + 当前分 + 行尾快捷按钮。
    ///
    /// 行是代码生成的，没走数据绑定：每行按钮要同时捕获「哪一组」和「加多少分」两样东西，
    /// 走模板得先给 Group 挂一个按钮集合（污染数据模型），还要在嵌套模板里取父级数据；
    /// 代码生成反而更短也更不容易错（BuildPresets 也是同一个路子）。
    /// </summary>
    private void BuildGridRows()
    {
        GridScorePanel.Children.Clear();

        var groups = _score.Groups;
        var buttons = QuickRows.BuildButtons(_score.Presets);

        GridHintText.Text = _score.Presets.Count > 0
            ? "点行尾按钮就给该组记分，预设里的「原因」会自动带上。"
            : "还没有预设项，先用 ±1 兜底。去设置页加几条预设，这里就会变成你自己的按钮。";

        if (groups.Count == 0)
        {
            GridScorePanel.Children.Add(new TextBlock
            {
                Text = "还没有小组，去设置页添加",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#6E7686"))
            });
            return;
        }

        foreach (var group in groups)
            GridScorePanel.Children.Add(BuildGridRow(group, buttons));
    }

    private Control BuildGridRow(Group group, IReadOnlyList<QuickButton> buttons)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("6,*,Auto,Auto"),
            Margin = new Thickness(0, 1)
        };

        var stripe = new Border
        {
            Width = 6,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = group.Brush
        };
        Grid.SetColumn(stripe, 0);

        var name = new TextBlock
        {
            Text = group.Name,
            Margin = new Thickness(8, 0, 8, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#D6DAE2")),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);

        var total = new TextBlock
        {
            Text = group.CachedTotal.ToString(),
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 28,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#EDEDED")),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(total, 2);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(buttonRow, 3);

        foreach (var quick in buttons)
        {
            var captured = quick;   // 避免闭包捕获循环变量
            var button = new Button
            {
                Content = captured.Text,
                FontSize = 11,
                MinWidth = 32,
                Padding = new Thickness(7, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                // 国内习惯：加分用红、扣分用绿
                Foreground = new SolidColorBrush(Color.Parse(captured.IsIncrease ? "#E24B4A" : "#1D9E75"))
            };
            ToolTip.SetTip(button, captured.Tooltip);
            button.Click += (_, _) => ApplyQuickScore(group, captured);
            buttonRow.Children.Add(button);
        }

        row.Children.Add(stripe);
        row.Children.Add(name);
        row.Children.Add(total);
        row.Children.Add(buttonRow);
        return row;
    }

    /// <summary>
    /// 行尾按钮的落点：直接给这一组记分，不需要先在下拉框里选它 ——
    /// 「全部展开」样式存在的意义就是省掉那一步。
    /// </summary>
    private void ApplyQuickScore(Group group, QuickButton button)
    {
        // 被「只在上课时加分」挡下时会返回 false，原因已由 OnScoreBlocked 写进状态栏，别覆盖它
        if (!_score.AddScore(group.Id, button.Delta, button.Reason)) return;

        StatusText.Text = $"已记：{group.Name} {button.Text}";
    }

    /// <summary>
    /// 加分被「只在上课时加分」挡下来了，把原因写在面板状态栏上。
    ///
    /// 这条反馈不能省：老师按下按钮却什么都没发生，只会认定插件坏了。
    /// 状态栏就在按钮下方，是目前这个面板唯一的提示位置。
    /// </summary>
    private void OnScoreBlocked(object? sender, string reason)
    {
        void Set() => StatusText.Text = reason;
        if (Dispatcher.UIThread.CheckAccess()) Set();
        else Dispatcher.UIThread.Post(Set);
    }

    // ==================== 界面刷新 ====================

    private void BindGroupCombo()
    {
        // 用 Group 对象本身作为条目，显示文本走 DisplayText（它实现了 INPC，
        // 所以小组被改名或分数变化时下拉框里的文字会自己更新）
        GroupCombo.ItemTemplate = new FuncDataTemplate<Group>((group, _) =>
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(Group.DisplayText)));
            return text;
        }, true);
    }

    /// <summary>
    /// 重建小组下拉列表。
    /// 这里用「Id 序列签名」做短路：加分只会改变分数、不改变小组构成，
    /// 此时直接返回，避免每次加分都把下拉框重建一遍把老师的选择清掉。
    /// </summary>
    private void RefreshGroups()
    {
        var groups = _score.Groups.ToList();
        var signature = string.Join("|", groups.Select(g => g.Id));
        if (signature == _groupSignature && GroupCombo.ItemCount > 0) return;

        _groupSignature = signature;

        var previousId = (GroupCombo.SelectedItem as Group)?.Id;
        GroupCombo.ItemsSource = groups;
        GroupCombo.SelectedItem =
            groups.FirstOrDefault(g => g.Id == previousId) ?? groups.FirstOrDefault();
    }

    /// <summary>
    /// 重建成员下拉。索引 0 固定是「不指名」（只记到小组一级）。
    ///
    /// 这里用一个与条目等长的 Id 数组做索引映射，而不是把 <see cref="Member"/> 对象塞进
    /// ItemsSource —— 那样「不指名」这一项就得伪造一个假成员，反而更容易出错。
    /// </summary>
    private void RefreshMembers()
    {
        var group = GroupCombo.SelectedItem as Group;
        var members = group == null
            ? new List<Member>()
            : _score.MembersOf(group.Id).ToList();

        var signature = (group?.Id ?? "") + "|" + string.Join(",", members.Select(m => m.Id));
        if (signature == _memberSignature && MemberCombo.ItemCount > 0) return;
        _memberSignature = signature;

        // 重建前先记下选中的是谁（按 Id 而不是索引，索引会因为有人被删而错位）
        var previousMemberId = SelectedMemberId;

        _memberChoiceIds = new List<string?> { null };
        _memberChoiceIds.AddRange(members.Select(m => (string?)m.Id));

        var labels = new List<string> { "（不指名，只记小组）" };
        labels.AddRange(members.Select(m => m.Name));

        MemberCombo.ItemsSource = labels;

        var index = previousMemberId == null
            ? 0
            : Math.Max(0, _memberChoiceIds.IndexOf(previousMemberId));
        MemberCombo.SelectedIndex = index;
    }

    /// <summary>当前选中的成员 Id；选的是「不指名」时返回 null。</summary>
    private string? SelectedMemberId
    {
        get
        {
            var i = MemberCombo.SelectedIndex;
            return i >= 0 && i < _memberChoiceIds.Count ? _memberChoiceIds[i] : null;
        }
    }

    private void RefreshRank()
    {
        MiniRankList.ItemsSource = _score.GetRanking(_settings.Settings.BoardTopN);
    }

    /// <summary>按当前预设项动态生成快捷按钮。用代码生成是因为按钮数量会随设置变化。</summary>
    private void BuildPresets()
    {
        PresetPanel.Children.Clear();

        foreach (var preset in _score.Presets)
        {
            var button = new Button
            {
                Content = preset.DisplayText,
                FontSize = 12,
                Padding = new Thickness(10, 5),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var captured = preset;   // 避免闭包捕获循环变量
            button.Click += (_, _) => ApplyPreset(captured);
            PresetPanel.Children.Add(button);
        }

        if (PresetPanel.Children.Count == 0)
        {
            PresetPanel.Children.Add(new TextBlock
            {
                Text = "还没有预设项，去设置页添加",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#6E7686"))
            });
        }
    }

    private void ApplyPreset(ScorePreset preset)
    {
        var group = GroupCombo.SelectedItem as Group;
        if (group == null)
        {
            StatusText.Text = "请先选择小组";
            return;
        }

        var who = DescribeTarget(group);
        if (!_score.AddScore(group.Id, preset.Delta, preset.Label, preset.Id, SelectedMemberId)) return;
        StatusText.Text = $"已记：{who} {preset.DisplayText}";
    }
}
