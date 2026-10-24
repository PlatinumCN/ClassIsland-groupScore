using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using GroupScorePlugin.Actions;
using GroupScorePlugin.Components;
using GroupScorePlugin.Services;
using GroupScorePlugin.Triggers;
using GroupScorePlugin.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GroupScorePlugin;

/// <summary>
/// 插件入口点。
///
/// ClassIsland 加载插件时，会从 manifest.yml 指定的入口程序集里
/// 找到带 [PluginEntrance] 且继承 PluginBase 的类，然后调用 Initialize。
/// 所以「注册」这件事只有这一个地方要做 —— 组件、设置页、自己的服务，全在这里挂上去。
/// </summary>
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // ---------- 数据层 ----------
        // 用工厂注册而不是 AddSingleton<T>()，是为了把「插件配置目录」这个
        // 只有插件自己才知道的路径传进去。
        // 注意：ScoreStore 必须只注册一次。凡是需要积分数据的地方都从这里取，
        // 任何地方再 new 一个 ScoreStore 都会造出第二份内存数据，最后互相覆盖。
        services.AddSingleton(_ => new ScoreStore(ResolveConfigFolder()));
        services.AddSingleton(_ => new SettingsService(ResolveConfigFolder()));

        // ---------- 业务层 ----------
        services.AddSingleton<ScoreService>();

        // ---------- 悬浮球 ----------
        services.AddSingleton<FloatingBallHost>();

        // ---------- 主界面组件 ----------
        // 注册后，老师可以在「应用设置 → 组件」的组件库里找到「小组积分榜」，
        // 拖到主界面上就能用。
        services.AddComponent<ScoreBoardComponent>();

        // ---------- 设置页面（三 Tab 管理台）----------
        // 会出现在「应用设置 → 插件」分类下。
        services.AddSettingsPage<GroupScoreSettingsPage>();

        // ---------- 自动化行动 ----------
        // 注册后，老师在「应用设置 → 自动化 → 添加行动」里能看到一个
        // 「小组积分」分组，里面是这四条悬浮球控制行动。
        // 有了它们，悬浮球的显示时机就可以完全交给 ClassIsland 的自动化去编排，
        // 不再只依赖插件内置的「上课弹出、下课收起」。
        //
        // 注：每条行动都重写了 OnRevert，所以注册时会自动被标记为「可恢复」，
        // 在自动化里可以勾选「可恢复」让 ClassIsland 帮你执行反向操作。
        services.AddAction<ShowBallAction>();
        services.AddAction<HideBallAction>();
        services.AddAction<ToggleBallAction>();
        services.AddAction<ExpandBallAction>();

        // 这一条不一样：它带设置界面。
        // 用 AddAction<TAction, TSettingsControl> 才能把设置控件一起注册进去，
        // 老师在自动化里点中它时会弹出面板，选「哪个小组 / 加减多少分 / 原因」。
        // （注意第二个泛型参数是**控件**，设置数据类是从行动的泛型基类推出来的。）
        services.AddAction<AddScoreAction, AddScoreActionSettingsControl>();

        // ---------- 自动化触发器 ----------
        // 注册后，老师在「应用设置 → 自动化 → 添加触发器」里能看到「小组积分达到阈值」。
        // 它跟上面的「小组加减分」行动正好配成闭环：
        //
        //   触发器「小组积分达到阈值」(第 3 小组 ≥ 20) → 行动「小组加减分」(+2，第 3 小组)
        //
        // 同样是带设置界面的一条，同样用 AddTrigger<TTrigger, TSettingsControl>，
        // 第二个泛型参数也同样是**控件**而不是设置模型。
        services.AddTrigger<GroupScoreThresholdTrigger, GroupScoreThresholdTriggerSettingsControl>();

        // ---------- 启动后初始化悬浮球 ----------
        // 必须在 AppStarted 之后做：此时 DI 容器和主窗口都已就绪，
        // 创建窗口、订阅课程事件才安全。
        AppBase.Current.AppStarted += (_, _) =>
            IAppHost.TryGetService<FloatingBallHost>()?.Initialize();
    }

    /// <summary>
    /// 取插件配置目录。
    /// 写成本地函数（而不是在 Initialize 里算一次存起来）是为了让它「延迟取值」：
    /// 工厂 lambda 在服务真正被解析时才调用它，那时 PluginConfigFolder 一定已经就绪。
    /// 万一它还是空的，就退到一个自建目录 —— 路径不对总比插件加载失败强。
    /// </summary>
    private string ResolveConfigFolder()
    {
        if (!string.IsNullOrWhiteSpace(PluginConfigFolder))
            return PluginConfigFolder;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GroupScorePlugin");
    }
}
