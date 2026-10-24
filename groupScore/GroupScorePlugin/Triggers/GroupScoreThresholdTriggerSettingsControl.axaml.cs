using Avalonia;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Shared;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Triggers;

/// <summary>
/// 「小组积分达到阈值」触发器的设置界面。
///
/// 老师在「自动化」里添加这个触发器、或点中它时，
/// ClassIsland 会从 DI 里取出这个控件（注册靠
/// <c>AddTrigger&lt;GroupScoreThresholdTrigger, GroupScoreThresholdTriggerSettingsControl&gt;</c>）
/// 并塞进抽屉里显示。
///
/// 注意：触发器设置控件的基类<b>没有</b> ChangeTriggerName 之类的改名方法
/// （行动那边才有 ChangeActionName），所以这里不去动触发器在列表里的显示名。
/// </summary>
public partial class GroupScoreThresholdTriggerSettingsControl
    : TriggerSettingsControlBase<GroupScoreThresholdTriggerSettings>
{
    /// <summary>
    /// 装载期间把「写回设置」关掉。
    /// 给下拉框/数字框赋值本身就会触发变更事件，不挡住的话会把刚读出来的值
    /// 再当成用户编辑写回去。
    /// </summary>
    private bool _loading;

    private List<TriggerTargetOption> _options = new();

    public GroupScoreThresholdTriggerSettingsControl()
    {
        InitializeComponent();

        // 全部用显式事件订阅而不是数据绑定：
        // 绑定失败是静默的（界面看着正常，值就是不保存），事件订阅至少能编译期检查。
        TargetCombo.SelectionChanged += (_, _) => OnEdited();
        ComparisonCombo.SelectionChanged += (_, _) => OnEdited();
        ThresholdBox.ValueChanged += (_, _) => OnEdited();
    }

    /// <summary>
    /// ⚠️ 构造函数里<b>不要</b>读 <c>Settings</c>。
    /// 控件是「先被创建、之后才被塞进 SettingsInternal」的，
    /// 过早访问会直接抛异常 —— 基类故意这么设计来暴露误用。
    /// 挂到可视化树之后再读才安全。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        try
        {
            _loading = true;

            var groups = IAppHost.TryGetService<ScoreService>()?.Groups;

            var options = new List<TriggerTargetOption> { new(null, "任意小组") };
            if (groups != null)
                options.AddRange(groups.Select(g => new TriggerTargetOption(g.Id, g.Name)));

            // 存的目标组已经被删掉了：补一条占位项并选中它。
            // 否则下拉框会「什么都没选」，老师以为设置丢了。
            var current = Settings.GroupId;
            var selected = options.FirstOrDefault(o => o.GroupId == current);
            if (selected == null && !string.IsNullOrEmpty(current))
            {
                selected = new TriggerTargetOption(current, "（已删除的小组）请重新选择");
                options.Add(selected);
            }

            _options = options;
            TargetCombo.ItemsSource = options;
            TargetCombo.SelectedItem = selected ?? options[0];

            ComparisonCombo.ItemsSource = ComparisonOptions;
            ComparisonCombo.SelectedItem =
                ComparisonOptions.FirstOrDefault(o => o.Value == Settings.Comparison) ?? ComparisonOptions[0];

            ThresholdBox.Value = Settings.Threshold;

            UpdateHint();
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
            if (TargetCombo.SelectedItem is TriggerTargetOption option)
                Settings.GroupId = option.GroupId;

            if (ComparisonCombo.SelectedItem is ComparisonOption cmp)
                Settings.Comparison = cmp.Value;

            Settings.Threshold = (int)(ThresholdBox.Value ?? 0);

            UpdateHint();
        }
        catch
        {
        }
    }

    private static readonly List<ComparisonOption> ComparisonOptions = new()
    {
        new ComparisonOption(ThresholdComparison.AtLeast, "达到或超过"),
        new ComparisonOption(ThresholdComparison.AtMost, "掉到（含）以下")
    };

    /// <summary>
    /// 提示区。把「什么时候会触发 / 会不会把分数扣回去」这两件最容易搞错的事写清楚。
    /// </summary>
    private void UpdateHint()
    {
        var groupName = Settings.AnyGroup
            ? null
            : _options.FirstOrDefault(o => o.GroupId == Settings.GroupId)?.Label;

        var condition = Settings.Describe(groupName);
        var verb = Settings.Comparison == ThresholdComparison.AtLeast ? "冲到" : "掉到";

        var lines = new List<string>
        {
            $"当 {condition} 时，执行本工作流的行动。",
            $"只在「刚刚{verb}这条线」的那一瞬间触发一次 —— 之后继续加分不会反复触发，" +
            "免得庆祝音效响个不停。"
        };

        if (Settings.AnyGroup)
            lines.Add("「任意小组」：只要有一个组够线就触发，是哪个组都算。");

        lines.Add(
            Settings.Comparison == ThresholdComparison.AtLeast
                ? "行动集上勾了「可恢复」的话，这个组再掉回线下面时会自动恢复。"
                : "行动集上勾了「可恢复」的话，这个组再涨回线上面时会自动恢复。");

        HintText.Text = string.Join("\n", lines);
    }
}

/// <summary>
/// 目标下拉框里的一项。<see cref="GroupId"/> 为 null 表示「任意小组」。
/// 用 record 白拿一个值语义的相等比较。
/// </summary>
public sealed record TriggerTargetOption(string? GroupId, string Label);

/// <summary>比较方式下拉框里的一项。</summary>
public sealed record ComparisonOption(ThresholdComparison Value, string Label);
