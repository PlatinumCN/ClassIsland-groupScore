# 小组积分 · ClassIsland 插件

![小组积分 · ClassIsland 课堂小组计分插件](GroupScorePlugin/cover.png)

给班级大屏课表软件 [ClassIsland](https://classisland.tech) 加一套小组积分管理：
上课铃响，屏幕角落自动冒出一个积分球，点一下展开就能给小组（或某个学生）加减分；
主界面上还能摆一个实时积分榜。

面向 ClassIsland `2.1` 及以上 · 仅 Windows（Avalonia 11.3 / .NET 8）· 数据只存本机

- 作者：**PlatinumCN**
- 许可：[GPL-3.0](LICENSE) · Copyright © 2026 PlatinumCN

## 功能

- **积分球** — 上课自动出现、下课自动收起；可以只在指定科目上课时出现
- **一键加减分** — 预设项自带原因（如「回答正确 +2」），加分红、扣分绿
- **两种加分面板** — 逐个选择（下拉挑小组）/ 全部展开（每个小组一行，行尾直接点）
- **算到人头** — 指名某个学生加分；不指名就记在小组头上，谁挣的分都查得到
- **撤销上一步** — 点错了立刻撤回，连点就是连续撤销
- **实时积分榜** — 主界面组件，显示前几名，小组颜色一眼区分
- **本周比上周** — 自然周 / 自然月的净增、累计、名次升降
- **自动化** — 5 条行动 + 1 个触发器，可用 ClassIsland 自带的自动化编排积分球与计分
- **拖动记位置** — 球和展开后的面板都能拖，松手后记住，下次上课还在那儿
- **只在上课加分**（默认开）— 防止课后误加；没配课表时自动失效，不会把插件卡死

安装、使用、常见问题见 **[GroupScorePlugin/README.md](GroupScorePlugin/README.md)**。

## 目录

| 路径 | 是什么 |
|---|---|
| `GroupScorePlugin/` | 插件本体（源码 + 清单 + 图标 + 封面 + 文档） |
| `GroupScorePlugin/README.md` | **使用说明**：功能、安装、用法、常见问题 |
| `GroupScorePlugin/DEV.md` | **实现文档**：架构、数据表、存储层、交互流程、自检台、已知限制 |
| `_SelfTest/` | 自检台：442 条断言 / 19 组，**不依赖 ClassIsland**，直接跑插件里的真代码 |
| `_Cover/make_cover.py` | 封面与图标的生成脚本（Pillow 纯代码绘制，改文案重跑即可） |
| `_Pack/` | 打包脚本 + 仓库地址填写脚本 |
| `groupScore.yml` | 插件市场索引项，提交到 [ClassIsland/PluginIndex](https://github.com/ClassIsland/PluginIndex)（不进插件包） |

## 构建

```bash
cd GroupScorePlugin
dotnet build -c Release
```

只写插件、不做单步调试的话不需要改任何文件。
想在 IDE 里按 F5 用本机 ClassIsland 加载插件（`-epp` 参数自动加载），
默认路径指向 `C:\Devc++\ClassIsland`，换机器时**不用改源码**：

```bash
dotnet build -p:ClassIsland_DebugBinaryDirectory="D:\ClassIsland"
```

## 跑自检台

```bash
cd _SelfTest
dotnet run
```

它把插件里所有「纯逻辑」的源文件直接编进来跑，所以**不用装 ClassIsland、不用开大屏**，
`dotnet run` 就能验证加减分双写、并列名次、周期切分、时段守门、存储层迁移与回落、
拖动落点换算这些容易算错的地方；另外还会读源码做静态体检
（图标码位、扩展 id 唯一性、设置控件继承写法、指针事件挂没挂全……）。
**改代码之后请跑一遍。**

## 打包成 .cipx

```bash
cd GroupScorePlugin
dotnet build -c Release -p:CreateCipx=true -p:GenerateHashSummary=false
python ../_Pack/make_manual_zip.py cipx/groupScore.cipx
```

产物在 `GroupScorePlugin/cipx/` 下：`groupScore.cipx` 是插件市场 / 「从文件安装」用的正式包，
`groupScore-manual.zip` 是解压即用的绿色版（少数机器上 ClassIsland 的本地安装会崩，
详见 [`发布方案.md`](发布方案.md)）。

⚠️ 本机缺 PowerShell Core，官方那条「一键算 MD5」的命令会报错，所以包名、MD5 都是手动算的。
完整发布流程（填清单、发 Release、上架插件市场）见 [`发布方案.md`](发布方案.md)。

## 换成你自己的仓库地址

`GroupScorePlugin/manifest.yml` 和 `groupScore.yml` 里都有 `__USER__` / `__REPO__` 占位符：

```bash
python _Pack/fill-repo.py <你的GitHub用户名> <仓库名>
```

脚本只替换占位符，已经填过的会跳过。**改完必须重新打包** —— `manifest.yml` 是打进 `.cipx` 的。

## 已知限制

- **只有编译保证的部分**：窗口拖动、展开动画、面板观感这些没有自动化 UI 测试，
  跨分辨率 / 多显示器下可能还有没遇到过的样子问题
- **拖动只认主屏**：副屏上会把球拉回主屏范围
- **「转到其他组」没有二次确认**：点错只能再转回来

遇到问题欢迎开 issue，附上 ClassIsland 日志和当时的操作会好查很多。
