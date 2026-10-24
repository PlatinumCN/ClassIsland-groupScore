using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using GroupScorePlugin.Models;
using GroupScorePlugin.Services;

namespace GroupScorePlugin.Views;

/// <summary>
/// 小组积分管理台，注册在「应用设置 → 插件」下，分五个标签页：
///   1. 小组管理 —— 增删改、排序、批量导入
///   2. 小组成员 —— 成员增删改、组内排序、转组、给个人加减分
///   3. 积分操作 —— 选组后一键加减分、撤销、看记录
///   4. 历史对比 —— 本周 vs 上周 / 本月 vs 上月
///   5. 预设与设置 —— 预设项、悬浮球行为、积分榜条数、历史口径、数据导入导出
///
/// 为什么管理功能全放在这里，而不是塞进主界面组件里：
/// ClassIsland 的组件设置是「每个摆放实例各自独立」的，
/// 主界面上放两个积分榜组件就会有两套互相打架的配置；
/// 而且大屏上出现输入框本身就容易误触。
/// 所以约定：组件只读展示，设置页才是唯一的写入入口。
/// </summary>
[SettingsPageInfo(
    "bojinxia.groupscore.settings",
    "小组积分",
    "\ue92f",      // FluentIconKind.GroupRegular —— 未选中时的图标
    "\ue92e")]     // FluentIconKind.GroupFilled  —— 选中时的图标
public partial class GroupScoreSettingsPage : SettingsPageBase
{
    // 角落 id 的唯一来源在 BallPlacement 里（窗口判断和设置页共用同一份），
    // 这里只负责给它们配上中文标签，顺序必须一一对应。
    private static string[] CornerValues => BallPlacement.CornerIds;

    private static readonly string[] CornerLabels = { "右下角", "右上角", "左下角", "左上角" };

    /// <summary>
    /// 加分面板样式的中文标签。**顺序必须与 <see cref="ScorePanelStyles.All"/> 完全一致** ——
    /// 下拉框选中的是索引，两边错位会挑到另一个样式。
    /// 数量对不上时自检台第 11 组会直接报出来。
    /// </summary>
    private static readonly string[] PanelStyleLabels =
    {
        "逐个选择（先挑小组再点按钮）",
        "全部展开（每行行尾直接加分）"
    };

    // 同理：可选的周起始日由 PeriodStats 定义，这里只配标签。
    private static readonly string[] WeekStartLabels = { "周一", "周日" };

    // 历史对比页的两个档位，顺序与 HistoryKindLabels 一一对应。
    private static readonly PeriodKind[] HistoryKinds = { PeriodKind.Week, PeriodKind.Month };
    private static readonly string[] HistoryKindLabels = { "本周 vs 上周", "本月 vs 上月" };

    private ScoreService? _score;
    private SettingsService? _settings;
    private ScoreStore? _store;

    /// <summary>初始化控件期间为 true，用来屏蔽「程序改控件」触发的回写。</summary>
    private bool _loading = true;

    private string _groupIdSignature = "";
    private string? _pendingDeleteGroupId;

    // ---- 「小组成员」页的状态 ----
    /// <summary>当前页展示的成员行（重建列表后保留一份，供「按 Id 找回选中项」使用）。</summary>
    private List<MemberScoreItem> _memberItems = new();
    private string _memberGroupSignature = "";
    private string _memberSignature = "";
    private string? _pendingDeleteMemberId;

    public GroupScoreSettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        BindGroupComboTemplate();
        HookSettingControls();
        HookMemberControls();
        HookHistoryControls();
    }

    // ==================== 初始化 ====================

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_score != null)
        {
            // 设置页实例被复用时不会再走一遍初始化，但「现在是不是上课时间」
            // 这类跟时间有关的状态行已经过期了，重算一次。
            RefreshScoreAccessText();
            RefreshSubjectHint();
            return;
        }

        _score = IAppHost.TryGetService<ScoreService>();
        _settings = IAppHost.TryGetService<SettingsService>();
        _store = IAppHost.TryGetService<ScoreStore>();
        if (_score == null || _settings == null) return;

        _score.ScoresChanged += OnScoresChanged;
        _score.ScoreBlocked += OnScoreBlocked;

        // 先把设置值灌进控件，再解除屏蔽，否则灌值本身会触发回写
        LoadSettingsIntoControls();

        RefreshGroups();
        BuildPresetButtons();
        RefreshPresetList();
        RefreshRecords();

        RefreshMemberGroupCombo();
        BuildMemberPresetButtons();
        RefreshMembers();

        RefreshHistory();

        if (_store != null)
        {
            // 存储格式这种事不该让老师去猜：到底存在哪个文件、用的是 SQLite 还是
            // 回落后退回的 JSON、有没有搬迁过旧数据、最近一次落盘有没有出问题，
            // 都要在这里一次说清楚。
            var text = $"数据文件：{_store.FilePath}（{_store.BackendName}）";
            if (!string.IsNullOrEmpty(_store.StorageNote)) text += "\n" + _store.StorageNote;
            if (_store.LastSaveError != null) text += $"\n最近一次保存失败：{_store.LastSaveError}";
            DataPathText.Text = text;
        }

        _loading = false;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_score != null)
        {
            _score.ScoresChanged -= OnScoresChanged;
            _score.ScoreBlocked -= OnScoreBlocked;
        }
    }

    /// <summary>
    /// 加分被「只在上课时加分」挡下时，把原因写到状态行上。
    /// 两个 Tab 的状态行都写：此刻老师可能正看着任意一个，
    /// 而事件里没有便宜办法判断哪个可见（也不值得为此引入可见性查询）。
    /// </summary>
    private void OnScoreBlocked(object? sender, string reason)
    {
        MemberStatusText.Text = reason;
        OperateStatusText.Text = reason;
    }

    private void LoadSettingsIntoControls()
    {
        if (_settings == null) return;
        var s = _settings.Settings;

        AutoShowCheck.IsChecked = s.AutoShowOnClass;
        AutoHideCheck.IsChecked = s.AutoHideOnBreak;
        SubjectFilterCheck.IsChecked = s.BallSubjectFilter;
        RestrictScoreCheck.IsChecked = s.RestrictScoreToClassTime;

        // 科目勾选框是照着课表动态建的，必须在 _loading 还是 true 的时候建 ——
        // 否则「灌初值」这件事会被当成老师的手动勾选，反过来写回设置。
        BuildSubjectChecks();

        CornerCombo.ItemsSource = CornerLabels;
        var idx = Array.IndexOf(CornerValues, s.Corner);
        CornerCombo.SelectedIndex = idx >= 0 ? idx : 0;

        PanelStyleCombo.ItemsSource = PanelStyleLabels;
        var styleIdx = Array.IndexOf(ScorePanelStyles.All,
            ScorePanelStyles.Normalize(s.ScorePanelStyle));
        PanelStyleCombo.SelectedIndex = styleIdx >= 0 ? styleIdx : 0;

        TopNBox.Value = s.BoardTopN;

        WeekStartCombo.ItemsSource = WeekStartLabels;
        var weekStartIndex = Array.IndexOf(PeriodStats.WeekStartChoices,
            PeriodStats.NormalizeWeekStart(s.WeekStartsOn));
        WeekStartCombo.SelectedIndex = weekStartIndex >= 0 ? weekStartIndex : 0;

        RefreshBallPositionText();
        RefreshScoreAccessText();
    }

    /// <summary>把「当前是拖动记忆还是角落停靠」显示出来，并决定那个恢复按钮能不能点。</summary>
    private void RefreshBallPositionText()
    {
        if (_settings == null) return;

        var s = _settings.Settings;
        if (s.HasCustomPosition)
        {
            BallPositionText.Text = $"当前：拖动记忆位置（{s.BallX}, {s.BallY}）";
            ResetBallPositionButton.IsEnabled = true;
        }
        else
        {
            // 认不出的角落字符串（手改过配置文件）就原样显示，不要硬套一个错的标签
            var i = Array.IndexOf(CornerValues, s.Corner);
            var label = i >= 0 && i < CornerLabels.Length ? CornerLabels[i] : s.Corner;
            BallPositionText.Text = $"当前：按{label}停靠";
            ResetBallPositionButton.IsEnabled = false;
        }
    }

    // ==================== 科目白名单 ====================

    /// <summary>
    /// 从 ClassIsland 的课表里读出全部科目名。
    /// 读不到（没配课表 / 服务拿不到）就返回空列表，由界面给出提示 —— 不凭空造科目。
    /// </summary>
    private static List<string> ReadSubjectNames()
    {
        var names = new List<string>();
        try
        {
            var profile = IAppHost.TryGetService<IProfileService>()?.Profile;
            if (profile == null) return names;

            foreach (var subject in profile.Subjects.Values)
            {
                var name = subject?.Name?.Trim();
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        catch
        {
            // 课表服务抽风不该让设置页打不开
        }

        return names
            .Distinct(StringComparer.CurrentCulture)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 按课表渲染科目勾选框。必须在 _loading 期间调用（见 LoadSettingsIntoControls）。
    /// </summary>
    private void BuildSubjectChecks()
    {
        SubjectCheckPanel.Children.Clear();
        if (_settings == null) return;

        var allowed = _settings.Settings.BallSubjects;
        var fromPlan = ReadSubjectNames();

        foreach (var name in fromPlan) AddSubjectCheck(name, inClassPlan: true, allowed);

        // 白名单里留着、但当前课表已经查不到的科目名也要画出来。
        // 不画的话会出现最难查的那种状态：筛选开着、界面上一个框都没勾，
        // 但设置里其实压着几个旧名字 —— 球死活不弹，而屏幕上完全看不出原因。
        foreach (var saved in allowed)
        {
            var name = saved?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (fromPlan.Any(x => string.Equals(x, name, StringComparison.CurrentCultureIgnoreCase))) continue;
            AddSubjectCheck(name, inClassPlan: false, allowed);
        }

        RefreshSubjectHint();
    }

    private void AddSubjectCheck(string name, bool inClassPlan, List<string> allowed)
    {
        var box = new CheckBox
        {
            Content = inClassPlan ? name : $"{name}（课表中已无此科目）",
            Tag = name,
            Margin = new Thickness(0, 0, 16, 0),
            IsChecked = allowed.Any(x => string.Equals(x?.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
        };

        box.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            ApplySubjectChecks();
        };

        SubjectCheckPanel.Children.Add(box);
    }

    /// <summary>
    /// 把当前勾选状态整体写回设置。
    ///
    /// 特意用「整份替换」而不是原地增删：INPC 只在属性赋值那一下发通知，
    /// 原地改列表的话设置根本存不下去，而且一声不吭。
    /// </summary>
    private void ApplySubjectChecks()
    {
        if (_settings == null) return;

        var picked = new List<string>();
        foreach (var child in SubjectCheckPanel.Children)
        {
            if (child is CheckBox { IsChecked: true } box && box.Tag is string name) picked.Add(name);
        }

        _settings.Settings.BallSubjects = picked;
        RefreshSubjectHint();

        // 现改现生效
        IAppHost.TryGetService<FloatingBallHost>()?.RefreshAutoVisibility();
    }

    private void RefreshSubjectHint()
    {
        if (_settings == null) return;
        var s = _settings.Settings;

        if (!s.BallSubjectFilter)
        {
            SubjectHintText.Text = "未启用：任何课都会自动弹出悬浮球。";
            return;
        }

        SubjectHintText.Text = s.BallSubjects.Count == 0
            ? "⚠ 已启用但一个科目都没勾 —— 悬浮球不会再自动弹出（仍可用「自动化」里的行动手动唤出）。"
            : $"已启用：只有上「{string.Join("、", s.BallSubjects)}」这几门课时才会自动弹出。";
    }

    /// <summary>
    /// 刷新「现在到底允不允许加分」那行状态。
    /// 它读的是 ScoreService 手里的实时快照，所以跟真正拦截用的是同一份判断。
    /// </summary>
    private void RefreshScoreAccessText()
    {
        if (_settings == null) return;

        ScoreAccessText.Text = _score?.Access is { } snap
            ? LessonRules.DescribeScoreAccess(snap.RestrictToClassTime, snap.ClassPlanUsable, snap.IsOnClass)
            : LessonRules.DescribeScoreAccess(_settings.Settings.RestrictScoreToClassTime, false, false);
    }

    /// <summary>
    /// 设置类控件的事件用代码挂，而不是写在 XAML 上。
    /// 好处是 lambda 让编译器推断事件参数类型，不必担心签名写错。
    /// </summary>
    private void HookSettingControls()
    {
        AutoShowCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            _settings.Settings.AutoShowOnClass = AutoShowCheck.IsChecked == true;
        };

        AutoHideCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            _settings.Settings.AutoHideOnBreak = AutoHideCheck.IsChecked == true;
        };

        SubjectFilterCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            _settings.Settings.BallSubjectFilter = SubjectFilterCheck.IsChecked == true;
            RefreshSubjectHint();

            // 现改现生效：正在上的这门课如果被排除掉了，球应该当场收回去
            IAppHost.TryGetService<FloatingBallHost>()?.RefreshAutoVisibility();
        };

        RestrictScoreCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            _settings.Settings.RestrictScoreToClassTime = RestrictScoreCheck.IsChecked == true;
            RefreshScoreAccessText();
        };

        PanelStyleCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            var i = PanelStyleCombo.SelectedIndex;
            if (i < 0 || i >= ScorePanelStyles.All.Length) return;

            // 写成 All[i] 而不是界面上那串中文标签 —— 中文标签只是给人看的，
            // 落进 settings.json 的必须是稳定标识符，换个说法不至于让配置失效。
            _settings.Settings.ScorePanelStyle = ScorePanelStyles.All[i];
        };

        CornerCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            var i = CornerCombo.SelectedIndex;
            if (i < 0 || i >= CornerValues.Length) return;

            _settings.Settings.Corner = CornerValues[i];

            // 改角落只是改了「没拖过时用哪个角」，所以顺手把拖动记忆也清掉并让球归位，
            // 否则老师会看到「选了右上角但球还在自己拖出来的地方」。
            _settings.Settings.ClearCustomPosition();
            IAppHost.TryGetService<FloatingBallHost>()?.ResetPosition();
            RefreshBallPositionText();
        };

        TopNBox.ValueChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            _settings.Settings.BoardTopN = (int)(TopNBox.Value ?? -1);
        };

        WeekStartCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || _settings == null) return;
            var i = WeekStartCombo.SelectedIndex;
            if (i < 0 || i >= PeriodStats.WeekStartChoices.Length) return;

            _settings.Settings.WeekStartsOn = PeriodStats.WeekStartChoices[i];

            // 周起始一变，上周就是另一段时间了，历史对比必须立刻重算
            RefreshHistory();
        };
    }

    /// <summary>
    /// 三个小组下拉框（积分操作的目标组、成员页的当前组、成员页的转组目标）
    /// 都用同一套条目模板：条目本身是 <see cref="Group"/>，显示文本走 DisplayText。
    /// 它实现了 INPC，所以小组被改名或分数变化时下拉里的文字会自己更新。
    /// </summary>
    private void BindGroupComboTemplate()
    {
        foreach (var combo in new[] { ScoreGroupCombo, MemberGroupCombo, MemberMoveTargetCombo })
        {
            combo.ItemTemplate = new FuncDataTemplate<Group>((_, _) =>
            {
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(Group.DisplayText)));
                return text;
            }, true);
        }
    }

    // ==================== 数据变化 ====================

    private void OnScoresChanged(object? sender, EventArgs e)
    {
        RefreshGroups();
        BuildPresetButtons();
        RefreshPresetList();
        RefreshRecords();

        RefreshMemberGroupCombo();
        BuildMemberPresetButtons();
        RefreshMembers();

        RefreshHistory();
    }

    /// <summary>
    /// 刷新小组列表。
    /// 用 Id 序列签名短路：单纯加分不改变小组构成，
    /// 这时直接返回，免得每次加分都把列表和下拉框重建一遍、把老师的选择清掉。
    /// </summary>
    private void RefreshGroups()
    {
        if (_score == null) return;

        var groups = _score.Groups.ToList();
        var signature = string.Join("|", groups.Select(g => g.Id));
        if (signature == _groupIdSignature && GroupListBox.ItemCount > 0) return;
        _groupIdSignature = signature;

        var previousListId = (GroupListBox.SelectedItem as Group)?.Id;
        GroupListBox.ItemsSource = groups;
        GroupListBox.SelectedItem =
            groups.FirstOrDefault(g => g.Id == previousListId) ?? groups.FirstOrDefault();

        var previousComboId = (ScoreGroupCombo.SelectedItem as Group)?.Id;
        ScoreGroupCombo.ItemsSource = groups;
        ScoreGroupCombo.SelectedItem =
            groups.FirstOrDefault(g => g.Id == previousComboId) ?? groups.FirstOrDefault();
    }

    private void RefreshRecords()
    {
        if (_score == null) return;
        RecordListBox.ItemsSource = _score.GetRecentRecords(20)
            .Select(r => _score.DescribeRecord(r))
            .ToList();
    }

    private void RefreshPresetList()
    {
        if (_score == null) return;
        PresetListBox.ItemsSource = _score.Presets.Select(p => p.DisplayText).ToList();
    }

    /// <summary>预设按钮数量随设置变化，所以按需动态生成。</summary>
    private void BuildPresetButtons()
    {
        if (_score == null) return;

        ScorePresetPanel.Children.Clear();
        foreach (var preset in _score.Presets)
        {
            var button = new Button
            {
                Content = preset.DisplayText,
                FontSize = 13,
                Padding = new Thickness(12, 6),
                Margin = new Thickness(0, 0, 8, 8)
            };
            var captured = preset;
            button.Click += (_, _) => ApplyPreset(captured);
            ScorePresetPanel.Children.Add(button);
        }

        if (ScorePresetPanel.Children.Count == 0)
        {
            ScorePresetPanel.Children.Add(new TextBlock
            {
                Text = "还没有预设项，请在「预设与设置」页添加。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6E7686"))
            });
        }
    }

    // ==================== Tab 1：小组管理 ====================

    private void OnAddGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;

        var name = NewGroupName.Text ?? "";
        var group = _score.AddGroup(string.IsNullOrWhiteSpace(name) ? "新小组" : name);
        NewGroupName.Text = "";
        GroupListBox.SelectedItem = group;
        GroupStatusText.Text = $"已添加：{group.Name}";
    }

    private void OnRenameGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (GroupListBox.SelectedItem is not Group group)
        {
            GroupStatusText.Text = "请先在上面的列表里选中一个小组";
            return;
        }

        var newName = NewGroupName.Text ?? "";
        if (string.IsNullOrWhiteSpace(newName))
        {
            GroupStatusText.Text = "请把新名称写在左上角的输入框里";
            return;
        }

        _score.RenameGroup(group.Id, newName);
        NewGroupName.Text = "";
        GroupStatusText.Text = $"已重命名：{group.Name}";
    }

    private void OnMoveUpClick(object? sender, RoutedEventArgs e) => MoveSelectedGroup(-1);

    private void OnMoveDownClick(object? sender, RoutedEventArgs e) => MoveSelectedGroup(1);

    private void MoveSelectedGroup(int direction)
    {
        if (_score == null) return;
        if (GroupListBox.SelectedItem is not Group group) return;

        if (!_score.MoveGroup(group.Id, direction))
        {
            GroupStatusText.Text = direction < 0 ? "已经在最上面了" : "已经在最下面了";
            return;
        }

        // MoveGroup 改变了顺序，RefreshGroups 会重建列表，这里把选中项找回来
        GroupListBox.SelectedItem = group;
        GroupStatusText.Text = "";
    }

    /// <summary>
    /// 两步删除：第一次点只是变成「再点一次确认删除」，第二次才真删。
    /// 比弹一个模态确认框更省事，也不依赖 ClassIsland 的对话框 API。
    /// </summary>
    private void OnDeleteGroupClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (GroupListBox.SelectedItem is not Group group) return;

        if (_pendingDeleteGroupId != group.Id)
        {
            _pendingDeleteGroupId = group.Id;
            DeleteGroupButton.Content = "再点一次确认删除";
            GroupStatusText.Text = $"将删除「{group.Name}」及其全部积分记录";
            return;
        }

        var name = group.Name;
        _score.DeleteGroup(group.Id);
        _pendingDeleteGroupId = null;
        DeleteGroupButton.Content = "删除选中";
        GroupStatusText.Text = $"已删除：{name}";
    }

    private void OnBulkImportClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;

        var lines = (BulkImportBox.Text ?? "").Replace("\r\n", "\n").Split('\n');
        var added = _score.ImportGroups(lines);
        BulkImportBox.Text = "";
        GroupStatusText.Text = added > 0 ? $"已批量导入 {added} 个小组" : "没有可导入的内容";
    }

    // ==================== Tab 2：小组成员 ====================

    /// <summary>
    /// 成员页上的选择事件单独挂。
    /// 「换组 → 刷新成员列表」和「换人 → 作废上一次的删除确认」这两件事
    /// 都不是设置项，不该混进 HookSettingControls。
    /// </summary>
    private void HookMemberControls()
    {
        MemberGroupCombo.SelectionChanged += (_, _) =>
        {
            // 换组之后成员列表必然不同，但签名有可能撞上（两个组恰好同名同分），
            // 所以这里直接把签名清掉，强制重建一次。
            _memberSignature = "";
            RefreshMoveTargets();
            RefreshMembers();
        };

        MemberListBox.SelectionChanged += (_, _) =>
        {
            // 换了选中的人，之前那次「再点一次确认删除」就作废，
            // 否则会出现「确认了 A 的删除，结果删掉的是 B」。
            _pendingDeleteMemberId = null;
            DeleteMemberButton.Content = "删除选中";
        };
    }

    /// <summary>同步「当前小组」下拉。用 Id 签名短路，避免每次加分都重建把老师的选择清掉。</summary>
    private void RefreshMemberGroupCombo()
    {
        if (_score == null) return;

        var groups = _score.Groups.ToList();
        var signature = string.Join("|", groups.Select(g => g.Id));
        if (signature == _memberGroupSignature && MemberGroupCombo.ItemCount > 0) return;
        _memberGroupSignature = signature;

        var previousId = (MemberGroupCombo.SelectedItem as Group)?.Id;
        MemberGroupCombo.ItemsSource = groups;
        MemberGroupCombo.SelectedItem =
            groups.FirstOrDefault(g => g.Id == previousId) ?? groups.FirstOrDefault();
    }

    /// <summary>转组目标下拉：永远排除当前选中的小组（转到自己那组没有意义）。</summary>
    private void RefreshMoveTargets()
    {
        if (_score == null) return;

        var current = MemberGroupCombo.SelectedItem as Group;
        var targets = _score.Groups.Where(g => g.Id != current?.Id).ToList();

        var previousId = (MemberMoveTargetCombo.SelectedItem as Group)?.Id;
        MemberMoveTargetCombo.ItemsSource = targets;
        MemberMoveTargetCombo.SelectedItem =
            targets.FirstOrDefault(g => g.Id == previousId) ?? targets.FirstOrDefault();
    }

    /// <summary>
    /// 重建成员列表。
    ///
    /// 签名里必须带上「顺序 + 分数」这两样：
    ///   - 只比成员 Id 的话，给某人加分时成员构成没变，列表不会重建，
    ///     老师会看到「加了分但列表上的数字没动」；
    ///   - 不带顺序的话，两个同分成员互换位置时签名不变，
    ///     点了「上移」界面纹丝不动。所以签名里带上索引。
    /// </summary>
    private void RefreshMembers()
    {
        if (_score == null) return;

        var group = MemberGroupCombo.SelectedItem as Group;
        if (group == null)
        {
            _memberSignature = "";
            _memberItems = new List<MemberScoreItem>();
            MemberListBox.ItemsSource = null;
            MemberBalanceText.Text = "";
            if (MemberGroupCombo.ItemCount == 0)
                MemberStatusText.Text = "还没有小组。请先在「小组管理」页建立小组。";
            return;
        }

        _memberItems = _score.GetMemberScores(group.Id);

        var signature = $"{group.Id}:{group.CachedTotal}|" +
                        string.Join("|", _memberItems.Select((i, idx) => $"{idx}:{i.MemberId}:{i.Total}"));

        if (signature != _memberSignature || MemberListBox.ItemCount == 0)
        {
            _memberSignature = signature;

            // 重建列表会把选中项清掉，按 Id 找回来
            var previousId = (MemberListBox.SelectedItem as MemberScoreItem)?.MemberId;
            MemberListBox.ItemsSource = _memberItems;
            MemberListBox.SelectedItem =
                _memberItems.FirstOrDefault(i => i.MemberId == previousId) ?? _memberItems.FirstOrDefault();
        }

        RefreshMemberBalance(group);
    }

    /// <summary>
    /// 把这笔账摆在明面上：组总分 = 成员分合计 + 未指派到个人的分。
    ///
    /// 这一行不能省。老师建了成员之后很自然会去加个人分，发现加起来比组总分少，
    /// 不说清楚就会以为是软件算错了 —— 而那些「全组作业优秀」之类的分
    /// 本来就是按组记的，不属于任何人。
    /// </summary>
    private void RefreshMemberBalance(Group group)
    {
        if (_score == null) return;

        var unassigned = _score.GetUnassignedTotal(group.Id);

        MemberBalanceText.Text = _memberItems.Count == 0
            ? $"「{group.Name}」还没有成员，{group.CachedTotal} 分全部记在小组一级。"
            : $"分账：成员分合计 {_memberItems.Sum(i => i.Total)} ＋ 未指派到个人 {unassigned} ＝ 组总分 {group.CachedTotal}";

        // 运行时自检：这个等式只在有人绕过 ScoreService 的双写直接改数据时才会破。
        if (!_score.CheckGroupBalance(group.Id))
            MemberStatusText.Text = "⚠ 数据不一致：成员分与小组总分对不上，可能存在绕过记账的改动。";
    }

    /// <summary>取「当前选中的小组 + 成员」，取不到就把原因写进状态栏。</summary>
    private bool TryGetSelectedMember(out Group? group, out MemberScoreItem? member)
    {
        group = MemberGroupCombo.SelectedItem as Group;
        member = MemberListBox.SelectedItem as MemberScoreItem;

        if (group == null) { MemberStatusText.Text = "请先选择小组"; return false; }
        if (member == null) { MemberStatusText.Text = "请先在上面的列表里选中一个成员"; return false; }
        return true;
    }

    private void OnAddMemberClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (MemberGroupCombo.SelectedItem is not Group group)
        {
            MemberStatusText.Text = "请先选择小组";
            return;
        }

        var name = NewMemberName.Text ?? "";
        var m = _score.AddMember(group.Id, string.IsNullOrWhiteSpace(name) ? "新成员" : name);
        NewMemberName.Text = "";

        // AddMember 会触发 ScoresChanged 把列表重建，这里把刚建的人选中，方便接着给他加分
        MemberListBox.SelectedItem = _memberItems.FirstOrDefault(i => i.MemberId == m.Id);
        MemberStatusText.Text = $"已添加成员：{m.Name}";
    }

    private void OnRenameMemberClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (MemberListBox.SelectedItem is not MemberScoreItem item)
        {
            MemberStatusText.Text = "请先在上面的列表里选中一个成员";
            return;
        }

        var newName = (NewMemberName.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            MemberStatusText.Text = "请把新姓名写在左侧的输入框里";
            return;
        }

        var old = item.Name;
        _score.RenameMember(item.MemberId, newName);
        NewMemberName.Text = "";
        MemberStatusText.Text = $"已重命名：{old} → {newName}";
    }

    /// <summary>
    /// 两步删除，和「小组管理」页一个套路。
    /// 提示语要花笔墨解释「记录会保留」这件事 —— 这是整个成员功能里最反直觉的一点。
    /// </summary>
    private void OnDeleteMemberClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (MemberListBox.SelectedItem is not MemberScoreItem item)
        {
            MemberStatusText.Text = "请先在上面的列表里选中一个成员";
            return;
        }

        if (_pendingDeleteMemberId != item.MemberId)
        {
            _pendingDeleteMemberId = item.MemberId;
            DeleteMemberButton.Content = "再点一次确认删除";

            var count = _score.Data.ScoreRecords.Count(r => r.MemberId == item.MemberId);
            MemberStatusText.Text = count == 0
                ? $"将删除成员「{item.Name}」"
                : $"将删除成员「{item.Name}」。他名下的 {count} 条记录会保留，转为只记在小组名下，「{MemberGroupCombo.SelectedItem}」的总分不变。";
            return;
        }

        var name = item.Name;
        _score.DeleteMember(item.MemberId, out var orphaned);
        _pendingDeleteMemberId = null;
        DeleteMemberButton.Content = "删除选中";
        MemberStatusText.Text = orphaned == 0
            ? $"已删除成员：{name}"
            : $"已删除成员：{name}，其 {orphaned} 条记录转为未指派（小组总分不变）";
    }

    private void OnMemberUpClick(object? sender, RoutedEventArgs e) => MoveSelectedMember(-1);

    private void OnMemberDownClick(object? sender, RoutedEventArgs e) => MoveSelectedMember(1);

    private void MoveSelectedMember(int direction)
    {
        if (_score == null) return;
        if (MemberListBox.SelectedItem is not MemberScoreItem item) return;

        if (!_score.MoveMember(item.MemberId, direction))
        {
            MemberStatusText.Text = direction < 0 ? "已经在最上面了" : "已经在最下面了";
            return;
        }

        // MoveMember 内部已经触发 ScoresChanged → RefreshMembers 重建过列表，
        // 但重建会把选中项清掉，这里按 Id 找回来。
        MemberListBox.SelectedItem = _memberItems.FirstOrDefault(i => i.MemberId == item.MemberId);
        MemberStatusText.Text = "";
    }

    private void OnMoveMemberClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (MemberListBox.SelectedItem is not MemberScoreItem item)
        {
            MemberStatusText.Text = "请先选中要转组的成员";
            return;
        }
        if (MemberMoveTargetCombo.SelectedItem is not Group target)
        {
            MemberStatusText.Text = "请先选择要转到哪个小组";
            return;
        }

        var name = item.Name;
        _score.MoveMemberToGroup(item.MemberId, target.Id);
        MemberStatusText.Text = $"已把 {name} 转到「{target.Name}」，他名下的分数一并转了过去。";
    }

    /// <summary>成员页的快捷按钮。和积分页一样按预设项动态生成。</summary>
    private void BuildMemberPresetButtons()
    {
        if (_score == null) return;

        MemberPresetPanel.Children.Clear();
        foreach (var preset in _score.Presets)
        {
            var button = new Button
            {
                Content = preset.DisplayText,
                FontSize = 13,
                Padding = new Thickness(12, 6),
                Margin = new Thickness(0, 0, 8, 8)
            };
            var captured = preset;
            button.Click += (_, _) => ApplyPresetToMember(captured);
            MemberPresetPanel.Children.Add(button);
        }

        if (MemberPresetPanel.Children.Count == 0)
        {
            MemberPresetPanel.Children.Add(new TextBlock
            {
                Text = "还没有预设项，请在「预设与设置」页添加。",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#6E7686"))
            });
        }
    }

    private void ApplyPresetToMember(ScorePreset preset)
    {
        if (_score == null) return;
        if (!TryGetSelectedMember(out var group, out var member)) return;

        // 被时段挡下时不覆盖状态行：原因已经由 ScoreBlocked 事件写进去了
        if (!_score.AddScore(group!.Id, preset.Delta, preset.Label, preset.Id, member!.MemberId)) return;
        MemberStatusText.Text =
            $"已记：{member.Name} {preset.DisplayText}（{group.Name} 当前 {group.CachedTotal} 分）";
    }

    private void OnApplyMemberCustomClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (!TryGetSelectedMember(out var group, out var member)) return;

        var delta = (int)(MemberDeltaBox.Value ?? 0m);
        if (delta == 0)
        {
            MemberStatusText.Text = "分值不能为 0";
            return;
        }

        if (!_score.AddScore(group!.Id, delta, MemberReasonBox.Text ?? "", null, member!.MemberId)) return;
        MemberReasonBox.Text = "";
        MemberStatusText.Text =
            $"已记：{member.Name} {(delta > 0 ? "+" : "")}{delta}（{group.Name} 当前 {group.CachedTotal} 分）";
    }

    // ==================== Tab 3：积分操作 ====================

    private void ApplyPreset(ScorePreset preset)
    {
        if (_score == null) return;
        if (ScoreGroupCombo.SelectedItem is not Group group)
        {
            OperateStatusText.Text = "请先选择小组";
            return;
        }

        if (!_score.AddScore(group.Id, preset.Delta, preset.Label, preset.Id)) return;
        OperateStatusText.Text = $"已记录：{group.Name} {preset.DisplayText}";
    }

    private void OnApplyCustomClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        if (ScoreGroupCombo.SelectedItem is not Group group)
        {
            OperateStatusText.Text = "请先选择小组";
            return;
        }

        var delta = (int)(CustomDeltaBox.Value ?? 0m);
        if (delta == 0)
        {
            OperateStatusText.Text = "分值不能为 0";
            return;
        }

        if (!_score.AddScore(group.Id, delta, CustomReasonBox.Text ?? "")) return;
        CustomReasonBox.Text = "";
        OperateStatusText.Text = $"已记录：{group.Name} {(delta > 0 ? "+" : "")}{delta}（当前 {group.CachedTotal} 分）";
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;
        OperateStatusText.Text = _score.UndoLast(out var description)
            ? $"已撤销：{description}"
            : "没有可撤销的记录";
    }

    // ==================== Tab 4：历史对比 ====================

    private void HookHistoryControls()
    {
        HistoryKindCombo.ItemsSource = HistoryKindLabels;
        HistoryKindCombo.SelectedIndex = 0;
        HistoryKindCombo.SelectionChanged += (_, _) =>
        {
            // 构造期间 _loading 为 true，此时 _score 还是 null，刷新也没意义
            if (_loading) return;
            RefreshHistory();
        };
    }

    private void OnRefreshHistoryClick(object? sender, RoutedEventArgs e) => RefreshHistory();

    /// <summary>
    /// 重算并重画历史对比表。
    ///
    /// 每次都整体重建，不做签名短路 —— 每一行的「本期得分」是算出来的快照，
    /// 不会自己更新，给某组加一分就必须重建，否则界面上数字纹丝不动。
    /// 重建会清掉选中项，所以按小组 Id 找回来。
    /// </summary>
    private void RefreshHistory()
    {
        if (_score == null || _settings == null) return;
        if (HistoryKindCombo.SelectedIndex < 0) return;

        var kind = HistoryKinds[HistoryKindCombo.SelectedIndex];
        var comparison = _score.GetPeriodComparison(kind, _settings.Settings.WeekStartsOn);

        var previousId = (HistoryListBox.SelectedItem as PeriodGroupRow)?.Group.Id;

        HistorySummaryText.Text = BuildHistorySummary(comparison);
        HistoryListBox.ItemsSource = comparison.Rows;
        HistoryListBox.SelectedItem = comparison.Rows.FirstOrDefault(r => r.Group.Id == previousId);
    }

    /// <summary>把「本期多少、上期多少」讲成一句话。</summary>
    private static string BuildHistorySummary(PeriodComparison comparison)
    {
        var currentTitle = PeriodStats.TitleOf(comparison.Current.Kind, true);
        var previousTitle = PeriodStats.TitleOf(comparison.Current.Kind, false);
        var currentRange = PeriodStats.FormatRange(comparison.Current);
        var previousRange = PeriodStats.FormatRange(comparison.Previous);

        if (comparison.Rows.Count == 0)
            return "还没有小组可以对比，先到「小组管理」页建几个组。";

        if (comparison.CurrentRecordCount == 0 && comparison.PreviousRecordCount == 0)
            return $"{currentTitle}（{currentRange}）和{previousTitle}（{previousRange}）都还没有任何记录。";

        var previousPart =
            $"{previousTitle}（{previousRange}）全班净增 {PeriodGroupRow.FormatSigned(comparison.PreviousTotal)} 分（{comparison.PreviousRecordCount} 条记录）";

        if (comparison.CurrentRecordCount == 0)
            return $"{currentTitle}（{currentRange}）还没有记录；{previousPart}。";

        return $"{currentTitle}（{currentRange}）全班净增 {PeriodGroupRow.FormatSigned(comparison.CurrentTotal)} 分（{comparison.CurrentRecordCount} 条记录），" +
               $"{previousPart}，相比 {comparison.TotalChangeText} 分。";
    }

    // ==================== Tab 5：预设与设置 ====================

    private void OnAddPresetClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;

        var label = NewPresetLabel.Text ?? "";
        if (string.IsNullOrWhiteSpace(label))
        {
            DataStatusText.Text = "请先填写预设名称";
            return;
        }

        var delta = (int)(NewPresetDelta.Value ?? 0m);
        if (delta == 0)
        {
            DataStatusText.Text = "预设分值不能为 0";
            return;
        }

        _score.AddPreset(label, delta);
        NewPresetLabel.Text = "";
        DataStatusText.Text = $"已添加预设：{label} {(delta > 0 ? "+" : "")}{delta}";
    }

    private void OnDeletePresetClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;

        var presets = _score.Presets;
        var index = PresetListBox.SelectedIndex;
        if (index < 0 || index >= presets.Count)
        {
            DataStatusText.Text = "请先在列表里选中要删除的预设";
            return;
        }

        _score.DeletePreset(presets[index].Id);
        DataStatusText.Text = "已删除预设";
    }

    /// <summary>
    /// 清掉拖动记忆，恢复成按「停靠位置」停靠。
    /// 只改设置是不够的（球还杵在原地），所以再让宿主把窗口搬一次。
    /// </summary>
    private void OnResetBallPositionClick(object? sender, RoutedEventArgs e)
    {
        if (_settings == null) return;

        _settings.Settings.ClearCustomPosition();
        IAppHost.TryGetService<FloatingBallHost>()?.ResetPosition();
        RefreshBallPositionText();
    }

    // ==================== Tab 5：数据 ====================

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (_score == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出小组积分",
                SuggestedFileName = $"小组积分-{DateTime.Now:yyyyMMdd-HHmm}.csv",
                DefaultExtension = "csv",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("CSV 表格") { Patterns = new List<string> { "*.csv" } }
                }
            });
            if (file == null) return;

            // 带 BOM 的 UTF-8：不加这一条，Excel 打开中文会变成乱码
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            await writer.WriteAsync(_score.BuildCsv());
            await writer.FlushAsync();

            DataStatusText.Text = "已导出。";
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        try
        {
            // 先把内存里的改动刷下去，免得打开目录看不到刚加的分
            _score?.FlushToDisk();
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(_store?.FilePath) ?? "",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DataStatusText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private void OnSaveNowClick(object? sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        _store.SaveNow();
        _settings?.Save();
        DataStatusText.Text = "已立即保存。";
    }
}
