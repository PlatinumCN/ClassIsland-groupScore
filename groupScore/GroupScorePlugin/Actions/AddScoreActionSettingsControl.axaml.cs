using Avalonia;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Shared;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Actions;

/// <summary>
/// 「小组加减分」行动的设置界面。
///
/// 老师在「自动化」里点中这条行动时，ClassIsland 会从 DI 里取出这个控件
/// （注册靠 <c>AddAction&lt;AddScoreAction, AddScoreActionSettingsControl&gt;</c>）
/// 并把它塞进抽屉里显示。
/// </summary>
public partial class AddScoreActionSettingsControl : ActionSettingsControlBase<AddScoreActionSettings>
{
    /// <summary>
    /// 装载期间把「写回设置」关掉。
    /// 因为给下拉框 / 数字框赋值本身就会触发变更事件，
    /// 不挡住的话会把刚读出来的值再当成用户编辑写回去，还会顺手改掉行动显示名。
    /// </summary>
    private bool _loading;

    private List<TargetOption> _options = new();

    public AddScoreActionSettingsControl()
    {
        InitializeComponent();

        // 全部改成显式事件订阅，不用绑定：
        // 绑定出错是静默的（界面不报错、值就是不保存），事件订阅则是编译期检查的。
        TargetCombo.SelectionChanged += (_, _) => OnEdited();
        DeltaBox.ValueChanged += (_, _) => OnEdited();
        ReasonBox.TextChanged += (_, _) => OnEdited();
    }

    /// <summary>
    /// ⚠️ 构造函数里**不要**读 <c>Settings</c>。
    /// 这个控件是「先被创建、之后才被塞进 SettingsInternal」的，
    /// 过早访问会直接抛 ArgumentNullException —— 基类故意这么设计来暴露误用。
    /// 挂到可视化树之后再读才安全。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);   // 基类在 base 调用里触发 OnAdded()，必须在自己逻辑之前
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        try
        {
            _loading = true;

            var groups = IAppHost.TryGetService<ScoreService>()?.Groups;

            var options = new List<TargetOption> { new(null, "全部小组") };
            if (groups != null)
                options.AddRange(groups.Select(g => new TargetOption(g.Id, g.Name)));

            // 存的目标组已经被删掉了：补一条占位项并选中它。
            // 否则下拉框会「什么都没选」，老师以为设置丢了。
            var current = Settings.GroupId;
            var selected = options.FirstOrDefault(o => o.GroupId == current);
            if (selected == null && !string.IsNullOrEmpty(current))
            {
                selected = new TargetOption(current, "（已删除的小组）请重新选择");
                options.Add(selected);
            }

            _options = options;
            TargetCombo.ItemsSource = options;
            TargetCombo.SelectedItem = selected ?? options[0];

            DeltaBox.Value = Settings.Delta;
            ReasonBox.Text = Settings.Reason;

            UpdateHint();
            UpdateActionName();
        }
        catch
        {
            // 设置界面绝不该因为读不到数据整个崩掉，那是把老师直接挡在门外
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnEdited()
    {
        if (_loading) return;

        try
        {
            if (TargetCombo.SelectedItem is TargetOption option)
                Settings.GroupId = option.GroupId;

            Settings.Delta = (int)(DeltaBox.Value ?? 1);
            Settings.Reason = ReasonBox.Text ?? "";

            UpdateHint();
            UpdateActionName();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 顺手改掉行动在自动化列表里显示的名字。
    /// 一排「小组加减分」谁也认不出哪个是哪个，带上参数就清楚了。
    /// </summary>
    private void UpdateActionName()
    {
        var delta = Settings.Delta >= 0 ? $"+{Settings.Delta}" : Settings.Delta.ToString();
        ChangeActionName($"小组加减分　{delta} · {CurrentTargetLabel()}");
    }

    private string CurrentTargetLabel() =>
        Settings.ApplyToAllGroups
            ? "全部小组"
            : _options.FirstOrDefault(o => o.GroupId == Settings.GroupId)?.Label ?? "未指定小组";

    private void UpdateHint()
    {
        var delta = Settings.Delta;
        var effect = delta > 0 ? $"加 {delta} 分" : (delta < 0 ? $"扣 {-delta} 分" : "不做任何事");
        var count = Settings.ApplyToAllGroups ? _options.Count - 1 : 1;
        var reason = string.IsNullOrWhiteSpace(Settings.Reason) ? "自动化" : Settings.Reason.Trim();

        var lines = new List<string>
        {
            $"执行一次：给 {CurrentTargetLabel()}（{Math.Max(count, 0)} 个）{effect}，" +
            $"并写入一条原因「{reason}」的积分记录。"
        };

        if (Settings.ApplyToAllGroups)
            lines.Add("「全部小组」在执行的那一刻才取名单，所以以后新加的小组下次执行也会带上。");

        if (delta != 0)
            lines.Add("恢复：行动上勾了「可恢复」后，恢复时会精确扣回**这次执行时**的那些小组，" +
                      "中途新建的小组不会被误扣。恢复记的是一条反向量，不是删记录 —— " +
                      "两者之间老师手动加的分不会被连累。");

        HintText.Text = string.Join("\n", lines);
    }
}

/// <summary>
/// 目标下拉框里的一项。<see cref="GroupId"/> 为 null 表示「全部小组」。
/// 用 record 是为了白拿一个值语义的相等比较，选中项判断会干净很多。
/// </summary>
public sealed record TargetOption(string? GroupId, string Label);
