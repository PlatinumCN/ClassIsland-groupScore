using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Profile;
using GroupScorePlugin.Models;
using GroupScorePlugin.Views;

namespace GroupScorePlugin.Services;

/// <summary>
/// 悬浮球宿主：负责悬浮球的创建、显示/隐藏，以及「跟着课程时段自动启停」。
///
/// 这是本插件的技术核心之一，因为它填的是 ClassIsland 的一个空档：
/// ClassIsland 2.1 自带的是「主界面」那个大窗（置顶透明的课表面板），
/// 并没有提供悬浮球这种小控件，插件 SDK 里也没有任何 Float/Ball 相关的扩展点。
/// 所以悬浮球必须由插件自己 new 一个 Avalonia 无边框窗口，并自己管它的生命周期。
/// </summary>
public class FloatingBallHost
{
    private readonly ScoreService _score;
    private readonly SettingsService _settings;

    private FloatingBallWindow? _window;
    private ILessonsService? _lessons;
    private bool _initialized;
    private bool _disposed;

    public FloatingBallHost(ScoreService score, SettingsService settings)
    {
        _score = score;
        _settings = settings;
    }

    /// <summary>悬浮球当前是否可见。</summary>
    public bool IsVisible => _window?.IsVisible == true;

    /// <summary>
    /// 在应用启动完成后调用一次。订阅课程时段事件，实现「上课自动弹出」。
    /// </summary>
    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _initialized = true;

        // 整段包在 try 里：本方法是从 ClassIsland 的 AppStarted 事件回调过来的，
        // 一旦抛出异常就会冒泡进宿主的事件循环。
        // 积分球出问题最多是球不显示，但绝不能把课表软件搞崩。
        try
        {
            // 用服务定位器而不是构造函数注入：插件服务在 Initialize 阶段注册，
            // 而 ILessonsService 属于宿主，晚一点取更稳妥。
            _lessons = IAppHost.TryGetService<ILessonsService>();
            if (_lessons != null)
            {
                _lessons.OnClass += HandleOnClass;
                _lessons.OnBreakingTime += HandleOnBreakingTime;
                _lessons.OnAfterSchool += HandleOnAfterSchool;
            }

            // 把「现在是什么时段」的读法交给 ScoreService（它靠这个做「加分只在上课时」的守门）。
            // 即使 _lessons 是 null 也要注入：「没有提供者」和「提供者说课表不可用」
            // 是两件不同的事 —— 后者还能在界面上把原因讲清楚。
            _score.SetAccessProvider(BuildAccessSnapshot);

            AppBase.Current.AppStopping += HandleAppStopping;
        }
        catch
        {
            // 订阅失败也不影响主界面上那个积分榜组件继续工作
        }

        // 如果 ClassIsland 是在上课中途启动的，也要把球显示出来，
        // 否则老师得等到下一节课才能看到它。
        // 科目白名单同样要过一遍：课间启动时 CurrentSubject 可能已经是「下节课」的了，
        // 所以「正在上课」这个前置条件不能省。
        if (CurrentStateIsOnClass() && ShouldAutoShowNow())
        {
            Show();
        }
    }

    private bool CurrentStateIsOnClass()
    {
        try
        {
            return _lessons?.CurrentState == TimeState.OnClass;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 时段快照（喂给 ScoreService 的守门规则） ====================

    /// <summary>
    /// 把宿主里的时段读成一份纯数据快照。**这里只负责取数，判断规则在 LessonRules 里**
    /// —— 分开之后自检台才能对着同一套规则跑测试。
    /// </summary>
    private ScoreAccessSnapshot BuildAccessSnapshot()
    {
        var s = _settings.Settings;
        var usable = false;
        var onClass = false;

        try
        {
            if (_lessons != null)
            {
                // 「课表可用」= 已加载 **且** 已启用，两个都得看：
                // 课表没启用时 CurrentState 会长期停在「课后」，
                // 只按它判断就等于把老师的加分功能永久锁死。
                usable = _lessons.IsClassPlanLoaded && _lessons.IsClassPlanEnabled;
                onClass = _lessons.CurrentState == TimeState.OnClass;
            }
        }
        catch
        {
            // 读崩了就当课表不可用 —— 规则那边会放行，不会连累加分
        }

        return new ScoreAccessSnapshot(s.RestrictScoreToClassTime, usable, onClass);
    }

    // ==================== 科目白名单 ====================

    /// <summary>
    /// 悬浮球此刻该不该自动显示。
    /// 只管「科目对不对」这一层，「是不是正在上课」由调用方保证。
    /// </summary>
    private bool ShouldAutoShowNow()
        => LessonRules.ShouldAutoShow(_settings.Settings.AutoShowOnClass,
                                      _settings.Settings.BallSubjectFilter,
                                      _settings.Settings.BallSubjects,
                                      CurrentSubjectName());

    /// <summary>
    /// 当前正在上的科目名；没有科目（自习 / 课间 / 课表没加载）时返回 null。
    /// </summary>
    private string? CurrentSubjectName()
    {
        try
        {
            var subject = _lessons?.CurrentSubject;
            if (subject == null) return null;

            // 框架给「没有课 / 课间」准备了两个占位对象（Subject.Empty / Subject.Breaking）。
            // 既比对象也比名字，双重保险：哪天占位对象的 Name 不再是空串也不会漏判。
            if (ReferenceEquals(subject, Subject.Empty) || ReferenceEquals(subject, Subject.Breaking))
                return null;

            var name = subject.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 设置改了之后重新判定一次该不该显示。
    ///
    /// 典型场景：老师在设置页把「语文」从白名单里划掉，而此刻正在上语文 ——
    /// 球应该当场收回去，而不是等下节课才生效。
    /// 没开「上课自动弹出」时直接返回：那种情况下球的显示完全归老师手动/自动化管，不插手。
    /// </summary>
    public void RefreshAutoVisibility()
    {
        if (!_settings.Settings.AutoShowOnClass) return;
        if (ShouldAutoShowNow()) Show();
        else Hide();
    }

    // ==================== 课程时段联动 ====================

    private void HandleOnClass(object? sender, EventArgs e)
    {
        // 科目白名单在这里生效：没勾中的科目，这节课就不弹。
        // （ShouldAutoShowNow 内部已经含了「总开关是否打开」这一层。）
        if (!ShouldAutoShowNow()) return;
        Show();
    }

    private void HandleOnBreakingTime(object? sender, EventArgs e)
    {
        if (!_settings.Settings.AutoHideOnBreak) return;
        Hide();
    }

    private void HandleOnAfterSchool(object? sender, EventArgs e)
    {
        if (!_settings.Settings.AutoHideOnBreak) return;
        Hide();
    }

    // ==================== 显示控制 ====================

    /// <summary>显示悬浮球（收起成小球状态）。</summary>
    public void Show() => OnUiThread(() =>
    {
        if (_disposed) return;
        _window ??= CreateWindow();
        _window.ShowBall();
    });

    /// <summary>隐藏悬浮球（窗口还在，只是不显示）。</summary>
    public void Hide() => OnUiThread(() => _window?.Hide());

    /// <summary>
    /// 收起为圆球状态。目前和 <see cref="Show"/> 等价，
    /// 单独留一个方法是为了让「展开面板」的撤销读起来是它真正想做的事：
    /// 把面板收回去，而不是把整个球藏起来。
    /// </summary>
    public void Collapse() => Show();

    /// <summary>
    /// 切换显示状态。
    /// 判断和执行都放在同一个 UI 线程回调里完成 ——
    /// 如果在外面先读 IsVisible 再调 Show/Hide，两件事之间隔着一次线程切换，
    /// 连续调用两次就可能读到同一个旧值、做出同一个决定。
    /// </summary>
    public void Toggle() => OnUiThread(() =>
    {
        if (_disposed) return;

        if (_window?.IsVisible == true)
        {
            _window.Hide();
            return;
        }

        _window ??= CreateWindow();
        _window.ShowBall();
    });

    /// <summary>
    /// 直接展开操作面板。供自动化行动调用，
    /// 这样老师还能在「自动化」里配置「满足某条件时直接弹面板」。
    /// </summary>
    public void ShowExpanded() => OnUiThread(() =>
    {
        if (_disposed) return;
        _window ??= CreateWindow();
        _window.ShowExpanded();
    });

    /// <summary>
    /// 让悬浮球立刻按当前设置归位。
    /// 设置页里「恢复默认位置」（清掉拖动记忆）和改「停靠位置」之后调它，
    /// 免得老师改完发现球还杵在原来的地方。
    /// </summary>
    public void ResetPosition() => OnUiThread(() => _window?.SnapToConfiguredPosition());

    // ==================== 内部 ====================

    private FloatingBallWindow CreateWindow()
    {
        var window = new FloatingBallWindow(_score, _settings);
        window.Closed += (_, _) => _window = null;
        return window;
    }

    /// <summary>
    /// 所有碰界面的调用都必须回到 UI 线程。
    /// 课程事件的触发源是主计时器，不保证在 UI 线程上。
    /// 同时兜住所有异常：窗口创建 / 屏幕信息读取在个别环境下会失败，
    /// 宁可积分球不显示，也不能让异常冒泡把 ClassIsland 带崩。
    /// </summary>
    private static void OnUiThread(Action action)
    {
        void Guarded()
        {
            try
            {
                action();
            }
            catch
            {
                // 有意吞掉：悬浮球是锦上添花的功能，不能让它影响宿主稳定性
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Guarded();
        else Dispatcher.UIThread.Post(Guarded);
    }

    private void HandleAppStopping(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _disposed = true;

        if (_lessons != null)
        {
            _lessons.OnClass -= HandleOnClass;
            _lessons.OnBreakingTime -= HandleOnBreakingTime;
            _lessons.OnAfterSchool -= HandleOnAfterSchool;
        }
        AppBase.Current.AppStopping -= HandleAppStopping;

        // 把最后一次改动落盘，别让下课铃响后加的最后一分丢掉
        _score.FlushToDisk();
        _settings.Save();

        try
        {
            _window?.Close();
        }
        catch
        {
            // 退出流程里窗口已失效是正常的
        }
    }
}
