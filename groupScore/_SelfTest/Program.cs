using GroupScorePlugin.Actions;
using GroupScorePlugin.Models;
using GroupScorePlugin.Services;
using GroupScorePlugin.Triggers;
using GroupScorePlugin.Views;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  [PASS] {name}"); }
    else { fail++; Console.WriteLine($"  [FAIL] {name}{(detail == null ? "" : " -> " + detail)}"); }
}

// 本次运行创建过的临时数据目录，收尾时统一删掉。
var tempDirs = new List<string>();

// 清理抽成函数，并挂到两个退出钩子上：
// 负向测试就是靠「故意把代码改坏」来验检查的，自检台中途抛异常是家常便饭，
// 只靠末尾那一次清理的话，每崩溃一次就在 %TEMP% 里留一窝目录。
//
// 注意两个钩子都要挂，实测缺一不可：
//   - ProcessExit          管正常退出和 Environment.Exit；
//   - UnhandledException   管未处理异常 —— 它**不会**触发 ProcessExit
//     （亲手验过：只挂 ProcessExit 时崩溃那次照样留下一个目录）。
void CleanUpTempDirs()
{
    foreach (var d in tempDirs)
    {
        try { Directory.Delete(d, recursive: true); }
        catch { /* 删不掉就算了，这是清理，不是被测逻辑 */ }
    }
}

AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUpTempDirs();
AppDomain.CurrentDomain.UnhandledException += (_, _) => CleanUpTempDirs();

string TempDir()
{
    var d = Path.Combine(Path.GetTempPath(), "gs-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(d);
    tempDirs.Add(d);
    return d;
}

// 造一个 0 字节的文件，用来模拟「数据库文件刚建出来、schema 还没写进去就被断电了」。
// 这种残骸必须被判成「没有可用的库」，否则会被误认为「已经有库了」从而跳过迁移。
string MakeEmptyFile(string dir)
{
    var path = Path.Combine(dir, "empty-" + Guid.NewGuid().ToString("N")[..6] + ".db");
    File.WriteAllBytes(path, Array.Empty<byte>());
    return path;
}

// ==================== 第 11 节用的辅助 ====================
// 说明：以下三个函数不改任何业务数据，只是「静态体检」——
// 直接读插件源码，检查那些编译器不会报错、但运行起来一定难看的问题
// （比如把沙漏的字形当成奖杯填进图标参数）。

// 插件源码根目录：自检台的输出目录在 _SelfTest/bin/... 里，往上找到同级的 GroupScorePlugin
string? FindPluginRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var candidate = Path.Combine(dir.FullName, "GroupScorePlugin");
        if (Directory.Exists(Path.Combine(candidate, "Actions"))) return candidate;
        dir = dir.Parent;
    }
    return null;
}

bool IsGeneratedFile(string path)
{
    var sep = Path.DirectorySeparatorChar;
    return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}");
}

// 收集源码里所有「独立成串的图标字面量」，形如 "\ue813"。
// 只认整串就是一个 \uXXXX 的情况，所以注释里写 "\uXXXX 这种东西" 不会误报。
// 同时把同一行注释里写的 FluentIconKind 名字一并取出来 ——
// 让「注释里写的图标名」也变成被校验的断言，而不是纯装饰。
IEnumerable<(string File, int Line, string Glyph, string? Declared)> CollectIconLiterals(string root)
{
    var rxGlyph = new System.Text.RegularExpressions.Regex(@"""\\u([0-9a-fA-F]{4})""");
    var rxName = new System.Text.RegularExpressions.Regex(@"FluentIconKind\.([A-Za-z0-9]+)");

    foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
    {
        if (IsGeneratedFile(f)) continue;
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext != ".cs" && ext != ".axaml" && ext != ".yml") continue;

        var lines = File.ReadAllLines(f);
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (System.Text.RegularExpressions.Match m in rxGlyph.Matches(lines[i]))
            {
                var nm = rxName.Match(lines[i]);
                yield return (Path.GetFileName(f), i + 1,
                              m.Groups[1].Value.ToLowerInvariant(),
                              nm.Success ? nm.Groups[1].Value : null);
            }
        }
    }
}

// 收集源码与文档里出现的「应用设置 → X」，把 X 抽出来。
//
// 为什么要专门查这个：ClassIsland 2.1 里那一页叫「插件」，**不叫「扩展」**——
// 「扩展」这两个字在 ClassIsland 的任何一个程序集里都查不到（在 app 目录里 grep 过），
// 而界面提示里写错一个词，老师就会去一个根本不存在的页面里找小组管理。
// 编译器不管、自检台原来的检查不管、插件照样加载成功，只有人会卡住。
//
// 正则里容忍 `**`（Markdown 加粗）和「」，因为文档里常写成「应用设置 → **插件**」。
IEnumerable<(string File, int Line, string Page)> CollectSettingsPaths(string root)
{
    var rx = new System.Text.RegularExpressions.Regex(@"应用设置\s*→\s*[\s*「]*([\u4e00-\u9fa5A-Za-z]+)");
    foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
    {
        if (IsGeneratedFile(f)) continue;
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext != ".cs" && ext != ".axaml" && ext != ".md") continue;

        var lines = File.ReadAllLines(f);
        for (var i = 0; i < lines.Length; i++)
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(lines[i]))
                yield return (Path.GetFileName(f), i + 1, m.Groups[1].Value);
    }
}

// 收集扩展身份字符串：ComponentInfo 的 GUID、SettingsPageInfo 的 id、
// ActionInfo / TriggerInfo 的 id
// —— 它们都是「发布后不能改」的身份标识，必须全局唯一。
IEnumerable<(string Kind, string Id)> CollectExtensionIds(string root)
{
    var rx = new System.Text.RegularExpressions.Regex(
        @"\[\s*(ComponentInfo|SettingsPageInfo|ActionInfo|TriggerInfo)\s*\(\s*""([^""]+)""");
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
    {
        if (IsGeneratedFile(f)) continue;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f)))
            yield return (m.Groups[1].Value, m.Groups[2].Value);
    }
}

// 收集「标了某个 Info 特性的扩展类」：类名 + id。
// 特意不用目录里的文件名去猜类 —— 那些目录里还有设置类、设置控件，
// 拿文件名当扩展类会直接误报。
// 行动和触发器的特性写法一模一样，所以共用一个函数，免得改了一边忘了另一边。
IEnumerable<(string Class, string Id)> CollectInfoClasses(string root, string attribute)
{
    var rx = new System.Text.RegularExpressions.Regex(
        @"\[" + attribute + @"\s*\(\s*""([^""]+)""[\s\S]*?class\s+(\w+)");
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
    {
        if (IsGeneratedFile(f)) continue;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f)))
            yield return (m.Groups[2].Value, m.Groups[1].Value);
    }
}

IEnumerable<(string Class, string Id)> CollectActionInfoClasses(string root) =>
    CollectInfoClasses(root, "ActionInfo");

IEnumerable<(string Class, string Id)> CollectTriggerInfoClasses(string root) =>
    CollectInfoClasses(root, "TriggerInfo");

// 收集「类名 -> 直接基类原文（含泛型实参）」。
// 用来验证插件最阴的一条规矩：行动/设置控件的设置类型是从 BaseType 的泛型参数推的，
// 所以必须**直接**继承框架泛型基类，中间插一层就静默失效。
Dictionary<string, string> CollectDirectBases(string root)
{
    var rx = new System.Text.RegularExpressions.Regex(
        @"^\s*(?:public|internal)\s+(?:(?:abstract|sealed|partial|static)\s+)*class\s+(\w+)\s*:\s*([^\r\n{]+)",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    var map = new Dictionary<string, string>();
    foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
    {
        if (IsGeneratedFile(f)) continue;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(f)))
            map[m.Groups[1].Value] = m.Groups[2].Value.Trim();
    }
    return map;
}

// 剥掉 // 行注释再扫代码。
// 注册代码旁边必然带说明注释，而注释里常常写着 AddAction<TAction, TSettingsControl>
// 这种「示例」，不剥掉就会被当成真实调用 —— 第一版就是这么误报的。
string StripLineComments(string text) =>
    string.Join("\n", text.Split('\n').Select(l =>
    {
        var i = l.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 ? l[..i] : l;
    }));

// 从 "ActionBase<Foo>" 里取出 Foo；不是泛型就返回 null。
string? GenericArgOf(string baseText)
{
    var i = baseText.IndexOf('<');
    var j = baseText.LastIndexOf('>');
    return i >= 0 && j > i ? baseText[(i + 1)..j].Trim() : null;
}

// 取出 XAML 里某个元素开始标签上的全部属性名。
//
// 为什么要**按元素**取，而不是「整个文件里有没有出现过 PointerPressed=」：
// 悬浮球的收起态（球）和展开态（面板）是两个不同的元素。
// 球上挂齐了三个指针事件、面板一个都没挂，全局检查照样通过 ——
// 而那样一展开就再也拖不动了，因为球那时已经被藏起来（唯一能按的地方就是面板）。
// 这种「检查写得太松」的毛病，非得真删代码做负向测试才照得出来。
List<string> ElementAttributes(string xaml, string elementName)
{
    var names = new List<string>();
    var m = System.Text.RegularExpressions.Regex.Match(
        xaml, "x:Name=\"" + elementName + "\"([\\s\\S]*?)>");
    if (!m.Success) return names;

    foreach (System.Text.RegularExpressions.Match a in
             System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"([\w.:]+)\s*="))
        names.Add(a.Groups[1].Value);
    return names;
}

// 检查「带设置界面的扩展」：扩展类和它的设置控件类是否都**直接**继承了框架泛型基类，
// 且两边的设置类型是同一个。
//
// 这条规矩对行动和触发器一字不差 —— 框架两处取设置类型的写法完全相同：
//   info.SettingsControlType.BaseType?.GetGenericArguments().First()
// 所以在 auditor 里合成一个函数。触发器的代价还要更大一点：
// 框架要是取不到设置类型，它连 SettingsInternal 都不注入，
// 触发器的 Settings 会永远是 null，然后每次分数变化都白跑一趟。
List<string> AuditPairedInheritance(string entryCode, Dictionary<string, string> bases,
                                    string addMethod, string ownerBase, string controlBase, string kind)
{
    var bad = new List<string>();
    var rx = new System.Text.RegularExpressions.Regex(
        addMethod + @"<([A-Za-z0-9_]+)\s*,\s*([A-Za-z0-9_]+)>");

    foreach (System.Text.RegularExpressions.Match m in rx.Matches(entryCode))
    {
        var owner = m.Groups[1].Value;
        var control = m.Groups[2].Value;

        if (!bases.TryGetValue(owner, out var ownerBaseText)) { bad.Add($"{owner} 找不到类声明"); continue; }
        if (!bases.TryGetValue(control, out var controlBaseText)) { bad.Add($"{control} 找不到类声明"); continue; }

        var ownerGeneric = GenericArgOf(ownerBaseText);
        var controlGeneric = GenericArgOf(controlBaseText);

        if (!ownerBaseText.StartsWith(ownerBase))
            bad.Add($"{owner} 的直接基类是 {ownerBaseText}，不是 {ownerBase}<T>");
        else if (ownerGeneric == null)
            bad.Add($"{owner} 直接继承了非泛型的 {ownerBase} —— 框架拿不到设置类型");
        else if (!controlBaseText.StartsWith(controlBase))
            bad.Add($"{control} 的直接基类是 {controlBaseText}，不是 {controlBase}<T>");
        else if (controlGeneric == null)
            bad.Add($"{control} 直接继承了非泛型的 {controlBase}");
        else if (ownerGeneric != controlGeneric)
            bad.Add($"{owner} 的设置是 {ownerGeneric}，但 {control} 的是 {controlGeneric}（两边必须是同一个类型）");
        else
            Console.WriteLine($"     [{kind}] {owner} + {control}  →  设置类型 {ownerGeneric}");
    }

    return bad;
}

Console.WriteLine("=== 1. 空库首次运行：播种 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);

    Check("默认生成 4 个小组", svc.Data.Groups.Count == 4, $"实际 {svc.Data.Groups.Count}");
    Check("默认生成 5 个预设项", svc.Data.Presets.Count == 5, $"实际 {svc.Data.Presets.Count}");
    Check("初始总分为 0", svc.Data.Groups.All(g => g.CachedTotal == 0));
    Check("初始无记录", svc.Data.ScoreRecords.Count == 0);
    Check("播种标记已写入", svc.Data.Seeded);
    Check("分组顺序有序", svc.Groups.Select(g => g.SortOrder).SequenceEqual(new[] { 0, 1, 2, 3 }));
}

Console.WriteLine("=== 2. 加分 / 扣分：双写一致性 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);
    var g = svc.Groups[0];

    svc.AddScore(g.Id, 2, "回答正确");
    svc.AddScore(g.Id, 3, "作业优秀");
    svc.AddScore(g.Id, -1, "纪律扣分");

    Check("总分 = 2+3-1 = 4", g.CachedTotal == 4, $"实际 {g.CachedTotal}");
    Check("记录 3 条", svc.Data.ScoreRecords.Count == 3, $"实际 {svc.Data.ScoreRecords.Count}");
    Check("记录与总分求和一致",
        svc.Data.ScoreRecords.Sum(r => r.Delta) == g.CachedTotal,
        $"记录和 {svc.Data.ScoreRecords.Sum(r => r.Delta)} vs 缓存 {g.CachedTotal}");
    Check("delta=0 被忽略", !svc.AddScore(g.Id, 0, "无意义") && svc.Data.ScoreRecords.Count == 3);
    Check("未知小组被忽略", !svc.AddScore("不存在的ID", 5, "x"));
    Check("变更事件会触发", true);
}

Console.WriteLine("=== 3. 排行榜与并列名次（1,2,2,4）===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);
    var gs = svc.Groups.ToList();

    svc.AddScore(gs[0].Id, 10, "x");
    svc.AddScore(gs[1].Id, 8, "x");
    svc.AddScore(gs[2].Id, 8, "x");
    svc.AddScore(gs[3].Id, 5, "x");

    var rank = svc.GetRanking();
    Check("按总分降序", rank.Select(r => r.Total).SequenceEqual(new[] { 10, 8, 8, 5 }),
        string.Join(",", rank.Select(r => r.Total)));
    Check("名次为 1,2,2,4", rank.Select(r => r.Rank).SequenceEqual(new[] { 1, 2, 2, 4 }),
        string.Join(",", rank.Select(r => r.Rank)));
    Check("TopN=2 只返回 2 条", svc.GetRanking(2).Count == 2);
    Check("今日变化累计正确", rank[0].TodayDelta == 10, $"实际 {rank[0].TodayDelta}");
    Check("色块画刷可用", rank[0].Brush is not null);
}

Console.WriteLine("=== 4. 撤销上一步 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);
    var g = svc.Groups[0];

    svc.AddScore(g.Id, 5, "第一次");
    svc.AddScore(g.Id, 3, "第二次");
    Check("撤销前总分 8", g.CachedTotal == 8);

    var ok = svc.UndoLast(out var desc);
    Check("撤销返回成功", ok);
    Check("总分回退到 5", g.CachedTotal == 5, $"实际 {g.CachedTotal}");
    Check("记录减到 1 条", svc.Data.ScoreRecords.Count == 1);
    Check("撤销描述含原因", desc.Contains("第二次"), desc);

    svc.UndoLast(out _);
    Check("连续撤销到 0", g.CachedTotal == 0 && svc.Data.ScoreRecords.Count == 0);
    Check("空记录时撤销返回 false", !svc.UndoLast(out _));
}

Console.WriteLine("=== 5. 小组管理 + 级联删除 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);

    var added = svc.AddGroup("第六小组");
    Check("新增小组在末尾", svc.Groups[^1].Id == added.Id);

    svc.ImportGroups(new[] { "第七小组", "", "  第八小组  ", "- 第九小组" });
    Check("批量导入 3 个（忽略空行与项目符号）", svc.Data.Groups.Count == 8,
        $"实际 {svc.Data.Groups.Count}");
    Check("导入名称已去符号", svc.Data.Groups.Any(g => g.Name == "第九小组"));

    svc.RenameGroup(added.Id, "改过名的组");
    Check("重命名生效", svc.Data.Groups.First(g => g.Id == added.Id).Name == "改过名的组");

    // 上移到第一位
    for (var i = 0; i < 10; i++) svc.MoveGroup(added.Id, -1);
    Check("上移到首位", svc.Groups[0].Id == added.Id);
    Check("SortOrder 重排连续",
        svc.Groups.Select(g => g.SortOrder).SequenceEqual(Enumerable.Range(0, svc.Data.Groups.Count)));
    Check("已到顶部再上移返回 false", !svc.MoveGroup(added.Id, -1));

    svc.AddScore(added.Id, 7, "测试");
    svc.Data.Members.Add(new Member { GroupId = added.Id, Name = "张三" });
    var before = svc.Data.ScoreRecords.Count;
    Check("删除前有记录", before > 0);

    svc.DeleteGroup(added.Id);
    Check("小组已删除", svc.Data.Groups.All(g => g.Id != added.Id));
    Check("积分记录级联清除", svc.Data.ScoreRecords.All(r => r.GroupId != added.Id));
    Check("成员级联清除", svc.Data.Members.All(m => m.GroupId != added.Id));
}

Console.WriteLine("=== 6. 持久化往返（含中文）===");
{
    var dir = TempDir();
    {
        var store = new ScoreStore(dir);
        var svc = new ScoreService(store);
        svc.RenameGroup(svc.Groups[0].Id, "飞翔的第一组");
        svc.AddScore(svc.Groups[0].Id, 12, "课堂表现优秀，加两分");
        svc.AddScore(svc.Groups[1].Id, -3, "迟到");
        svc.AddPreset("黑板擦干净", 1);
        store.SaveNow();

        var file = store.FilePath;
        Check("数据文件已生成", File.Exists(file), file);

        // 落盘之后文件必须已经不再被本进程占用。
        // 老师要能自己把它复制一份当备份、或者删掉重来；如果连接池一直挂着句柄，
        // 他在资源管理器里会撞上「文件正在被另一个程序使用」。
        // 这条把「关掉连接池」这个决定变成被测试守住的不变量。
        var free = false;
        try
        {
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            free = true;
        }
        catch (IOException)
        {
            free = false;
        }
        Check("落盘后数据文件未被占用", free, file);
    }

    // 重新加载
    var store2 = new ScoreStore(dir);
    var svc2 = new ScoreService(store2);
    Check("重新加载：小组名保留", svc2.Data.Groups.Any(g => g.Name == "飞翔的第一组"));
    Check("重新加载：总分保留", svc2.Data.Groups.First(g => g.Name == "飞翔的第一组").CachedTotal == 12);
    Check("重新加载：记录保留", svc2.Data.ScoreRecords.Count == 2);
    Check("重新加载：预设保留", svc2.Data.Presets.Any(p => p.Label == "黑板擦干净"));
    Check("重新加载不会重复播种", svc2.Data.Groups.Count == 4, $"实际 {svc2.Data.Groups.Count}");
    Check("不会重复初始化预设", svc2.Data.Presets.Count == 6, $"实际 {svc2.Data.Presets.Count}");
}

Console.WriteLine("=== 7. 时间维度统计 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);
    var g = svc.Groups[0];

    svc.AddScore(g.Id, 4, "今天");
    // 契约：列表顺序 = 时间顺序。补历史记录必须插到最前面，而不是追加到末尾。
    svc.Data.ScoreRecords.Insert(0, new ScoreRecord
    {
        GroupId = g.Id, Delta = 100, Reason = "上周",
        CreatedAt = DateTime.Now.AddDays(-7)
    });
    g.CachedTotal += 100;

    Check("总计 104", g.CachedTotal == 104, $"实际 {g.CachedTotal}");
    Check("今日仅 4 分", svc.GetTotalSince(g.Id, DateTime.Today) == 4,
        $"实际 {svc.GetTotalSince(g.Id, DateTime.Today)}");
    var rank = svc.GetRanking();
    Check("排行榜今日变化只算今天", rank.First(r => r.GroupId == g.Id).TodayDelta == 4);
    Check("最近记录倒序（最新的在最前）",
        svc.GetRecentRecords(5).First().Reason == "今天",
        svc.GetRecentRecords(5).First().Reason);

    // 乱序文件的自愈：故意把顺序写乱，重新加载后应被拉正
    store.SaveNow();
    var svc3 = new ScoreService(new ScoreStore(dir));
    Check("重新加载会把乱序记录按时间拉正",
        svc3.GetRecentRecords(5).First().Reason == "今天",
        svc3.GetRecentRecords(5).First().Reason);
}

Console.WriteLine("=== 8. CSV 导出 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);
    var g = svc.Groups[0];
    svc.AddScore(g.Id, 3, "含,逗号的原因");
    svc.AddScore(g.Id, 2, "普通原因");

    var csv = svc.BuildCsv();
    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Check("含表头 时间,小组,成员,变动,原因", lines[0].Contains("时间,小组,成员,变动,原因"), lines[0]);
    Check("含小组汇总段", csv.Contains("小组,当前总分"));
    Check("含逗号的字段被引号包裹", csv.Contains("\"含,逗号的原因\""), csv);
    Check("行数 = 1表头 + 2记录 + 空行 + 1汇总头 + 4组", lines.Length >= 8, $"实际 {lines.Length}");
}

Console.WriteLine("=== 9. 损坏文件容错 ===");
{
    var dir = TempDir();
    File.WriteAllText(Path.Combine(dir, "score-data.json"), "{ 这不是合法 JSON ###");
    var store = new ScoreStore(dir);
    Check("损坏时回退为空库不抛异常", store.Data.Groups.Count == 0);
    Check("损坏文件已备份留档",
        Directory.GetFiles(dir, "*.corrupted-*.bak").Length == 1,
        string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));

    var svc = new ScoreService(store);
    Check("损坏后仍能正常播种", svc.Data.Groups.Count == 4);
}

Console.WriteLine("=== 10. 设置持久化 ===");
{
    var dir = TempDir();
    var s1 = new SettingsService(dir);
    Check("默认上课自动弹出", s1.Settings.AutoShowOnClass);
    Check("默认停靠右下角", s1.Settings.Corner == "BottomRight");
    s1.Settings.Corner = "TopLeft";
    s1.Settings.BoardTopN = 3;
    s1.Settings.BallOpacity = 5.0;   // 应该被夹到 1.0

    var s2 = new SettingsService(dir);
    Check("设置已落盘并重载", s2.Settings.Corner == "TopLeft" && s2.Settings.BoardTopN == 3);
    Check("不透明度被夹紧到 1.0", Math.Abs(s2.Settings.BallOpacity - 1.0) < 1e-9,
        s2.Settings.BallOpacity.ToString());
}

Console.WriteLine("=== 11. 图标码位与扩展身份体检（直接读插件源码）===");
{
    var root = FindPluginRoot();
    Check("找到插件源码目录", root != null, root ?? "往上找父目录，没有同时含 GroupScorePlugin/Actions 的目录");

    if (root != null)
    {
        // ---- 11.1 每个图标字面量都必须是 ClassIsland 认识的 Fluent 图标 ----
        // 这类错误编译器完全不管：\uE9A8 是个合法字形，但它叫 HourglassRegular（沙漏），
        // 填进「积分榜」组件里就是个沙漏图标，不跑起来根本发现不了。
        var icons = CollectIconLiterals(root).ToList();
        Check("源码里找得到图标字面量", icons.Count > 0, $"共 {icons.Count} 处");

        var coreDll = @"C:\Devc++\ClassIsland\app-2.1.0.1-0\ClassIsland.Core.dll";
        if (!File.Exists(coreDll))
        {
            Console.WriteLine($"  [跳过] 找不到 {coreDll}，无法校验图标码位");
        }
        else
        {
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(coreDll);
                var kind = asm.GetType("ClassIsland.Core.Icons.FluentIconKind")!;
                var byValue = new Dictionary<int, string>();
                foreach (var n in Enum.GetNames(kind))
                    byValue[Convert.ToInt32(Enum.Parse(kind, n))] = n;

                var distinct = icons.Select(x => x.Glyph).Distinct().OrderBy(x => x).ToList();
                var bad = new List<string>();
                var noName = new List<string>();
                var mismatch = new List<string>();

                foreach (var g in distinct)
                {
                    var cp = Convert.ToInt32(g, 16);
                    var known = byValue.TryGetValue(cp, out var actual);
                    if (!known) bad.Add("\\u" + g);

                    var first = icons.First(x => x.Glyph == g);
                    foreach (var it in icons.Where(x => x.Glyph == g))
                    {
                        if (it.Declared == null)
                            noName.Add($"{it.File}:{it.Line}");
                        else if (known && it.Declared != actual)
                            mismatch.Add($"{it.File}:{it.Line} 注释写的是 {it.Declared}，实际是 {actual}");
                    }

                    Console.WriteLine($"     \\u{g} = {(known ? actual ?? "?" : "【不是合法 Fluent 图标】")}" +
                                      $"   首次出现在 {first.File}:{first.Line}");
                }

                Check($"全部 {distinct.Count} 个图标码位都是合法 Fluent 图标", bad.Count == 0,
                    bad.Count == 0 ? null : "非法：" + string.Join(", ", bad));

                Check("每个图标字面量都在同行注释里注明了图标名", noName.Count == 0,
                    noName.Count == 0 ? null : "缺名字：" + string.Join(", ", noName));

                Check("同行注释声明的图标名与实际码位一致", mismatch.Count == 0,
                    mismatch.Count == 0 ? null : string.Join("；", mismatch));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [跳过] 加载 {Path.GetFileName(coreDll)} 失败：{ex.Message}");
            }
        }

        // ---- 11.2 身份字符串不能重复 ----
        var ids = CollectExtensionIds(root).ToList();
        Console.WriteLine("     扩展身份：" + string.Join("，", ids.Select(x => $"{x.Kind}={x.Id}")));

        var dup = ids.GroupBy(x => x.Id)
                     .Where(g => g.Count() > 1)
                     .Select(g => $"{g.Key}({string.Join("+", g.Select(x => x.Kind))})")
                     .ToList();
        Check("组件 GUID / 设置页 id / 行动 id 互不重复", ids.Count > 0 && dup.Count == 0,
            dup.Count == 0 ? null : "重复：" + string.Join(", ", dup));

        var dupGlyph = icons.GroupBy(x => x.Glyph).Where(g => g.Count() > 1).Count();
        Console.WriteLine($"     图标码位 {icons.Count} 处，去重后 {icons.Select(x => x.Glyph).Distinct().Count()} 个" +
                          $"（{dupGlyph} 个码位被多处复用，属正常）");

        // ---- 11.2b 界面/文档里引用的设置页名必须真实存在 ----
        // 白名单 = ClassIsland 2.1 实际存在的设置页名。要加新名字之前，
        // 先去 `app-2.1.0.1-0/` 里确认它真的存在（这套名册是踩过一次才定下来的：
        // 原来全项目都写成「应用设置 → 扩展」，而真实名字是「插件」）。
        var knownPages = new[] { "插件", "组件", "自动化", "课表", "主题", "提醒", "外观", "行为", "关于" };
        var settingsPaths = CollectSettingsPaths(root).ToList();
        Console.WriteLine($"     文档/界面里引用的设置页（{settingsPaths.Count} 处）：" +
                          string.Join("，", settingsPaths.Select(x => x.Page).Distinct()));

        var unknownPages = settingsPaths
            .Where(x => !knownPages.Contains(x.Page))
            .Select(x => $"{x.File}:{x.Line} 写的是「{x.Page}」")
            .Distinct()
            .ToList();
        Check("「应用设置 → X」里的 X 都是 ClassIsland 真实存在的页面名",
            settingsPaths.Count > 0 && unknownPages.Count == 0,
            unknownPages.Count == 0 ? null : string.Join("；", unknownPages));

        // ---- 11.2c 文档里写的「安装按钮」名字必须和界面一致 ----
        // ClassIsland 2.1「插件」页右上角 `···`（更多设置）里那个按钮叫
        // **「从本地安装」**，官方文档《插件》一节的原文就是这么写的
        // （docs.classisland.tech/app/plugin.html：「在更多设置中点击"从本地安装"按钮」）。
        // 之前 README 和发布方案里都写成「从文件安装」—— 措辞差一个字，
        // 老师照着找按钮就是找不到，而编译器、插件加载、其它断言全都不管。
        // 和 11.2b 的「扩展 → 插件」是同一类问题：界面文案不存在，代码照样全绿。
        var wrongButtonNames = new[] { "从文件安装", "安装插件包", "导入插件" };
        var buttonOffenders = new List<string>();
        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (IsGeneratedFile(f)) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".cs" && ext != ".axaml" && ext != ".md") continue;

            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                foreach (var wrong in wrongButtonNames)
                    if (lines[i].Contains(wrong))
                        buttonOffenders.Add($"{Path.GetFileName(f)}:{i + 1} 写的是「{wrong}」（应为「从本地安装」）");
        }
        Check("文档里提到的插件安装按钮名是「从本地安装」（界面上的真实文案）",
            buttonOffenders.Count == 0,
            buttonOffenders.Count == 0 ? null : string.Join("；", buttonOffenders));

        // ---- 11.3 行动类：注册了没？继承写法让框架取得到设置类型没？----
        // 「写了行动类却忘了注册」等于没写，编译器不会管。
        // 更阴的是继承写法：框架取设置类型用的是 BaseType.GetGenericArguments().First()，
        // 中间插一层自定义基类不会报错，只会让行动设置静默变成 null。
        var declared = CollectActionInfoClasses(root).ToList();
        var bases = CollectDirectBases(root);
        var pluginCs = Path.Combine(root, "Plugin.cs");
        var entryCode = File.Exists(pluginCs) ? StripLineComments(File.ReadAllText(pluginCs)) : "";

        Console.WriteLine("     带 [ActionInfo] 的行动类：" +
                          string.Join("，", declared.Select(x => $"{x.Class}({x.Id})")));

        var unregistered = declared
            .Where(x => !entryCode.Contains($"AddAction<{x.Class}>")
                     && !entryCode.Contains($"AddAction<{x.Class},"))
            .Select(x => x.Class)
            .ToList();
        Check("每个带 [ActionInfo] 的行动类都已在 Plugin.cs 里注册",
            declared.Count > 0 && unregistered.Count == 0,
            unregistered.Count == 0 ? null : "未注册：" + string.Join(", ", unregistered));

        // 带设置界面的那条（AddAction<TAction, TSettingsControl>）单独深查一遍
        var badActionInherit = AuditPairedInheritance(
            entryCode, bases, "AddAction", "ActionBase", "ActionSettingsControlBase", "行动");

        Check("带设置界面的行动：两边都直接继承框架泛型基类、且设置类型一致",
            System.Text.RegularExpressions.Regex.IsMatch(entryCode, @"AddAction<[A-Za-z0-9_]+\s*,") &&
            badActionInherit.Count == 0,
            badActionInherit.Count == 0 ? null : string.Join("；", badActionInherit));

        var plainRegistered = declared
            .Where(x => entryCode.Contains($"AddAction<{x.Class}>"))
            .Select(x => x.Class)
            .ToList();
        Console.WriteLine($"     无设置界面的行动（AddAction<T>）：{string.Join("，", plainRegistered)}");
        Check("无设置界面的行动仍然走 AddAction<T> 注册", plainRegistered.Count > 0,
            "一条都没有？确认没把悬浮球那四条行动误删");

        // ---- 11.4 触发器类：同样两条检查，而且失效后果更重 ----
        // 触发器要是被框架判定为「没有设置」，后果不只是设置读不到：
        // 框架连 SettingsInternal 都不会注入，这个类的 Settings 永远是 null，
        // 于是每次分数变化都白跑一趟，而且一声不吭。
        var declaredTriggers = CollectTriggerInfoClasses(root).ToList();

        Console.WriteLine("     带 [TriggerInfo] 的触发器类：" +
                          string.Join("，", declaredTriggers.Select(x => $"{x.Class}({x.Id})")));

        var unregisteredTriggers = declaredTriggers
            .Where(x => !entryCode.Contains($"AddTrigger<{x.Class}>")
                     && !entryCode.Contains($"AddTrigger<{x.Class},"))
            .Select(x => x.Class)
            .ToList();
        Check("每个带 [TriggerInfo] 的触发器类都已在 Plugin.cs 里注册",
            declaredTriggers.Count > 0 && unregisteredTriggers.Count == 0,
            declaredTriggers.Count == 0
                ? "一个触发器都没有？确认 Triggers 目录没被漏掉"
                : "未注册：" + string.Join(", ", unregisteredTriggers));

        var badTriggerInherit = AuditPairedInheritance(
            entryCode, bases, "AddTrigger", "TriggerBase", "TriggerSettingsControlBase", "触发器");

        Check("带设置界面的触发器：两边都直接继承框架泛型基类、且设置类型一致",
            System.Text.RegularExpressions.Regex.IsMatch(entryCode, @"AddTrigger<[A-Za-z0-9_]+\s*,") &&
            badTriggerInherit.Count == 0,
            badTriggerInherit.Count == 0 ? null : string.Join("；", badTriggerInherit));

        // ---- 11.5 触发器订阅了事件就必须能退订 ----
        // 「订阅了却忘了退订」编译器完全不管：运行起来就是内存泄漏，
        // 外加工作流已经被删掉之后还在后台触发的幽灵。
        // 这条只能看字符串，所以只做「有没有 -= / UnLoaded」这种粗筛，
        // 但粗筛也能拦住最常见的那个错误（只写了 Loaded 没写 UnLoaded）。
        var triggersDir = Path.Combine(root, "Triggers");
        var triggerFiles = Directory.Exists(triggersDir)
            ? Directory.EnumerateFiles(triggersDir, "*.cs", SearchOption.AllDirectories)
                       .Where(f => !IsGeneratedFile(f))
                       .Where(f => File.ReadAllText(f).Contains("TriggerBase<"))
                       .ToList()
            : new List<string>();

        var missingUnload = triggerFiles
            .Where(f =>
            {
                var code = StripLineComments(File.ReadAllText(f));
                return code.Contains("+= ") && (!code.Contains("-= ") || !code.Contains("UnLoaded()"));
            })
            .Select(Path.GetFileName)
            .ToList();

        Check("触发器订阅了事件就一定重写了 UnLoaded（否则退不掉）",
            triggerFiles.Count > 0 && missingUnload.Count == 0,
            triggerFiles.Count == 0
                ? "Triggers 目录下没找到继承 TriggerBase< 的文件"
                : "缺退订：" + string.Join(", ", missingUnload));

        // ---- 11.6 悬浮球拖动手势 ----
        // 这几样少了任何一样，编译都不会报错，但拖起来一定是坏的：
        //   少一个指针事件 → 手势断链（比如不挂 Released，落点永远存不下去）
        //   只在球上挂、面板上忘挂 → 面板一展开就把位置锁死，只剩「收起」能做
        //   不捕获指针     → 指针滑出 64 像素的球之后就收不到 Moved，球跟到半路停住
        //   自己另写一套角落算法 → 和 BallPlacement 里的那份慢慢漂开，两处结果不一致
        var ballXamlPath = Path.Combine(root, "Views", "FloatingBallWindow.axaml");
        var ballCodePath = Path.Combine(root, "Views", "FloatingBallWindow.axaml.cs");
        var ballXaml = File.Exists(ballXamlPath) ? File.ReadAllText(ballXamlPath) : "";
        var ballCode = File.Exists(ballCodePath)
            ? StripLineComments(File.ReadAllText(ballCodePath))
            : "";

        var gestureEvents = new[] { "PointerPressed", "PointerMoved", "PointerReleased" };

        // 必须**分元素**检查。只判断「整个 XAML 里出现过这三个字符串」是不够的：
        // 球上挂齐了、面板忘了挂，照样能过 —— 而那样展开之后就再也拖不动了。
        var ballGestures = ElementAttributes(ballXaml, "BallRoot");
        var missingOnBall = gestureEvents.Where(ev => !ballGestures.Contains(ev)).ToList();
        Check("圆球上挂齐了三个指针事件（缺一个拖动就断链）",
            ballGestures.Count > 0 && missingOnBall.Count == 0,
            ballGestures.Count == 0 ? "XAML 里找不到 BallRoot" : "缺：" + string.Join(", ", missingOnBall));

        var panelGestures = ElementAttributes(ballXaml, "PanelRoot");
        var missingOnPanel = gestureEvents.Where(ev => !panelGestures.Contains(ev)).ToList();
        Check("展开后的面板也挂齐了三个指针事件（否则面板一展开就把位置锁死）",
            panelGestures.Count > 0 && missingOnPanel.Count == 0,
            panelGestures.Count == 0 ? "XAML 里找不到 PanelRoot" : "缺：" + string.Join(", ", missingOnPanel));

        // 面板上的按下必须挂到 OnPanelPointerPressed，而不是随便挂个别的什么处理器
        Check("面板的按下事件接到了面板自己的处理器上",
            ballXaml.Contains("PointerPressed=\"OnPanelPointerPressed\""),
            "没找到 PointerPressed=\"OnPanelPointerPressed\"");

        // 只看 ".Capture(" 是不够的 —— 松开时的 Capture(null) 也能把它满足。
        // 要的是「按下时把指针捕获到当前被拖的那一块上」这个动作本身。
        Check("按下时把指针捕获到被拖的控件上（不捕获则指针滑出小球就收不到 Moved）",
            ballCode.Contains(".Capture(source)"),
            "没找到 e.Pointer.Capture(source) —— 只有松开时的 Capture(null) 不算");

        // 球和面板两条入口必须都走同一个 BeginDrag。各抄一份的话改一处漏一处，
        // 而漏掉的那处表现是「拖着不走」—— 只有在那个状态下才会被发现。
        var beginDragCalls = System.Text.RegularExpressions.Regex.Matches(
            ballCode, @"BeginDrag\(e,").Count;
        Check($"拖动逻辑只有一份，球和面板都指向它（找到 {beginDragCalls} 处入口）",
            beginDragCalls >= 2 && !ballCode.Contains(".Capture(BallRoot)"),
            beginDragCalls < 2
                ? "有一条拖动入口没走 BeginDrag，多半是各自抄了一份"
                : "球那侧还留着单独的 Capture 调用，说明拖动逻辑被抄成了两份");

        Check("窗口定位走 BallPlacement.Resolve（而不是在窗口里再抄一遍角落算法）",
            ballCode.Contains("BallPlacement.Resolve"),
            "位置算法被抄进窗口了，改一处会漏另一处");

        // ---- 11.6b 拖动位移必须在「屏幕物理坐标」里算（2026-10-17 实测出的真 bug）----
        // 事件的 GetPosition 给的是「相对窗口客户区」的坐标。窗口在拖动过程中自己也在动，
        // 同一个物理位置在客户区坐标系里的读数会跟着窗口一起变 —— 两次事件相减，
        // 位移会被窗口自身的位移抵消掉一部分。鼠标动得越快抵消越多，贴着屏幕边被钳住时
        // 抵消 100%，表现是「球越拖越跟不上手，最后粘在边上不动」。
        // 编译期完全看不出来，所以只能靠静态体检把写法钉住。
        Check("拖动位移取的是指针的屏幕坐标（客户区坐标相减会被窗口自身的位移抵消）",
            ballCode.Contains("BallPlacement.PointerOnScreen") &&
            System.Text.RegularExpressions.Regex.IsMatch(ballCode, @"_dragOriginScreen\s*\.\s*X") &&
            System.Text.RegularExpressions.Regex.IsMatch(ballCode, @"_dragOriginScreen\s*\.\s*Y"),
            "没找到 PointerOnScreen / _dragOriginScreen.X —— 位移多半又退回成两次客户区坐标相减");

        // 第二处：ClientSize / Bounds 是**逻辑单位**（Win32 后端里就是 GetClientRect 除以 RenderScaling），
        // 而工作区、窗口位置都是物理像素。不换算的话，150% 缩放下面板按 340 算、实际占 510，
        // 于是球贴边时露出一大截在屏幕外 —— 100% 缩放的机器上完全看不出来。
        Check("窗口尺寸换算成物理像素再跟工作区比（逻辑单位在缩放屏上会算错）",
            ballCode.Contains("BallPlacement.ToPhysicalSize"),
            "没找到 ToPhysicalSize —— 尺寸多半又直接拿 ClientSize / Bounds 当像素用了");

        Check("悬浮球窗口退订了自己订阅的事件（否则被关掉的窗口会被长命服务一直引用）",
            ballCode.Contains("-= OnScoresChanged") && ballCode.Contains("-= OnSettingsChanged"),
            "有 += 却没看到对应的 -=");

        // 停靠角落的清单只能有一份。这里拿运行时的 BallPlacement.CornerIds 去对
        // 设置页源码里的中文标签数组 —— 数量对不上就说明有人加了个角落却忘了配标签。
        var settingsPagePath = Path.Combine(root, "Views", "GroupScoreSettingsPage.axaml.cs");
        var settingsPageCode = File.Exists(settingsPagePath)
            ? StripLineComments(File.ReadAllText(settingsPagePath))
            : "";
        var labelMatch = System.Text.RegularExpressions.Regex.Match(
            settingsPageCode, @"CornerLabels\s*=\s*\{([^}]*)\}");
        var labelCount = labelMatch.Success
            ? labelMatch.Groups[1].Value.Split(',').Length
            : -1;
        Check($"停靠角落清单与中文标签一一对应（{BallPlacement.CornerIds.Length} 个角落 / {labelCount} 个标签）",
            labelCount == BallPlacement.CornerIds.Length,
            labelMatch.Success
                ? "数量对不上，ComboBox 会显示串位的标签"
                : "设置页里找不到 CornerLabels 数组");

        // 加分面板样式也是同一类接线：下拉框按**索引**选择，
        // 标签数组比取值数组短就会挑到另一个样式，而且看起来完全正常。
        var styleLabelMatch = System.Text.RegularExpressions.Regex.Match(
            settingsPageCode, @"PanelStyleLabels\s*=\s*\{([^}]*)\}");
        var styleLabelCount = styleLabelMatch.Success
            ? styleLabelMatch.Groups[1].Value.Split(',').Length
            : -1;
        Check($"加分面板样式清单与中文标签一一对应（{ScorePanelStyles.All.Length} 种样式 / {styleLabelCount} 个标签）",
            styleLabelCount == ScorePanelStyles.All.Length,
            styleLabelMatch.Success
                ? "数量对不上，ComboBox 会选到另一个样式"
                : "设置页里找不到 PanelStyleLabels 数组");

        // 悬浮球那边切换样式写进设置的，必须是 All 里的规范标识，不能是界面上的中文标签
        // —— 中文标签是给人看的，改了文案不该让存量配置失效。
        Check("设置页写回的是样式标识符而不是中文标签",
            settingsPageCode.Contains("ScorePanelStyles.All[i]"),
            "没找到 ScorePanelStyles.All[i]");

        // ---- 11.7 成员维度的接线 ----
        // 成员这条链路上有三个编译器完全不管的坑：
        //   1. 视图里自己再拼一套聚合 —— 和 MemberScores 里的那份慢慢漂开，两处结果不一致；
        //   2. 视图里直接 Members.Remove(...) —— 记录里的 MemberId 就成了悬空引用；
        //   3. XAML 里加了控件却忘了在代码里填数据 —— 得到一个永远空白的下拉框。
        var viewsDir = Path.Combine(root, "Views");
        var viewFiles = Directory.Exists(viewsDir)
            ? Directory.EnumerateFiles(viewsDir, "*.cs", SearchOption.AllDirectories)
                       .Where(f => !IsGeneratedFile(f))
                       .ToList()
            : new List<string>();

        // (a) 成员聚合的调用点只能有一个。
        // 视图要显示成员分，就得走 ScoreService.GetMemberScores —— 那是唯一入口。
        var summarizeCallers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGeneratedFile(f))
            .Where(f => StripLineComments(File.ReadAllText(f)).Contains("MemberScores.Summarize"))
            .Select(Path.GetFileName)
            .ToList();
        Check("成员聚合只有 ScoreService 一个调用点（视图不许自己拼一份，否则两处会漂开）",
            summarizeCallers.Count == 1 && summarizeCallers[0] == "ScoreService.cs",
            summarizeCallers.Count == 0
                ? "一处都没找到？确认 GetMemberScores 还在"
                : "调用点：" + string.Join(", ", summarizeCallers));

        // (b) 视图层不许自己对积分记录求和。
        // 把空白压掉再匹配，免得 "Sum(\n  r => r.Delta" 这种跨行写法漏网。
        var selfSumming = new List<string>();
        foreach (var f in viewFiles)
        {
            var flat = System.Text.RegularExpressions.Regex.Replace(
                StripLineComments(File.ReadAllText(f)), @"\s+", " ");
            if (System.Text.RegularExpressions.Regex.IsMatch(flat, @"ScoreRecords[^;]{0,80}?\.Sum\(") ||
                System.Text.RegularExpressions.Regex.IsMatch(flat, @"\.Sum\([^)]{0,40}?\.Delta\)"))
                selfSumming.Add(Path.GetFileName(f));
        }
        Check("视图层不自己求和积分（聚合一律走 ScoreService）",
            selfSumming.Count == 0,
            selfSumming.Count == 0 ? null : "自己算的：" + string.Join(", ", selfSumming));

        // (c) 删成员必须走 DeleteMember —— 它负责把记录孤儿化。
        // 直接 Members.Remove 会让记录里的 MemberId 悬空，那句「小组总分不变」就没了保障。
        var directRemove = viewFiles
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                StripLineComments(File.ReadAllText(f)), @"Members\s*\.\s*Remove"))
            .Select(Path.GetFileName)
            .ToList();
        Check("视图层不直接删成员（必须走 ScoreService.DeleteMember，否则记录会留下悬空引用）",
            directRemove.Count == 0,
            directRemove.Count == 0 ? null : "直接删的：" + string.Join(", ", directRemove));

        // (d) 悬浮球面板上的成员下拉：XAML 里得有这个控件，代码里得真的给它填过数据。
        Check("悬浮球面板上有成员下拉",
            ballXaml.Contains("x:Name=\"MemberCombo\""),
            "XAML 里找不到 MemberCombo");
        Check("悬浮球成员下拉真的被填过数据（不是个永远空白的框）",
            ballCode.Contains("MemberCombo.ItemsSource"),
            "代码里从没给 MemberCombo.ItemsSource 赋过值");

        // (e) 设置页的成员页要真的接上服务，而不是只画了几个控件
        var settingsPageXamlPath = Path.Combine(root, "Views", "GroupScoreSettingsPage.axaml");
        var settingsPageXaml = File.Exists(settingsPageXamlPath)
            ? File.ReadAllText(settingsPageXamlPath)
            : "";
        Check("设置页有「小组成员」标签页",
            settingsPageXaml.Contains("Header=\"小组成员\""),
            "XAML 里找不到这个 TabItem");

        var memberServiceCalls = new[] { "_score.AddMember(", "_score.DeleteMember(", "_score.GetMemberScores(" }
            .Where(call => !settingsPageCode.Contains(call))
            .ToList();
        Check("设置页的成员操作都走 ScoreService（增 / 删 / 查三个都要在）",
            memberServiceCalls.Count == 0,
            memberServiceCalls.Count == 0 ? null : "没找到：" + string.Join(", ", memberServiceCalls));

        // ---- 11.8 历史对比这条链路的接线 ----
        // 三个编译器完全不管的坑：
        //   1. 视图里自己算「上周从哪天起」—— 日期差一天是看不出来的，
        //      老师看到的「上周 +12 分」里其实混进了本周一的记录；
        //   2. XAML 里加了控件但代码里没接线 —— 得到一个永远空白的下拉框、一张空表；
        //   3. 下拉框的可选值与中文标签数量对不上 —— 显示串位的标签。
        var componentFiles = Directory.Exists(Path.Combine(root, "Components"))
            ? Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.cs", SearchOption.AllDirectories)
                       .Where(f => !IsGeneratedFile(f))
                       .ToList()
            : new List<string>();
        var uiCodeFiles = viewFiles.Concat(componentFiles).ToList();

        // (a) 日历算术只能待在 PeriodStats 里。
        var calendarMath = uiCodeFiles
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                StripLineComments(File.ReadAllText(f)), @"DayOfWeek|AddMonths\(|AddDays\("))
            .Select(Path.GetFileName)
            .ToList();
        Check("视图层不自己做日期算术（周起点 / 上期一律走 PeriodStats）",
            calendarMath.Count == 0,
            calendarMath.Count == 0 ? null : "自己算的：" + string.Join(", ", calendarMath));

        // (b) 视图层不该碰 CreatedAt —— 碰了就是在自己切时间段。
        var timeSlicing = uiCodeFiles
            .Where(f => StripLineComments(File.ReadAllText(f)).Contains("CreatedAt"))
            .Select(Path.GetFileName)
            .ToList();
        Check("视图层不按 CreatedAt 自己切时间段（切片只有 PeriodStats 一处）",
            timeSlicing.Count == 0,
            timeSlicing.Count == 0 ? null : "碰了 CreatedAt 的：" + string.Join(", ", timeSlicing));

        // (c) 服务层也一样：它只负责把数据喂给 PeriodStats。
        var servicePath = Path.Combine(root, "Services", "ScoreService.cs");
        var serviceCode = File.Exists(servicePath) ? StripLineComments(File.ReadAllText(servicePath)) : "";
        Check("服务层的历史对比走 PeriodStats.Compare（不自己算周期）",
            serviceCode.Contains("PeriodStats.Compare(") &&
            !System.Text.RegularExpressions.Regex.IsMatch(serviceCode, @"DayOfWeek|AddMonths\("),
            "要么没调用 PeriodStats.Compare，要么自己算起了周期");

        // (d) XAML 里出现的每个控件名，代码里都得用到。
        // 这是「加了控件忘了接线」唯一能被机器抓住的地方 ——
        // 不跑起来的话，一个空白的 ComboBox 看起来跟一个正常的一样。
        var unwired = new List<string>();
        var viewPairs = new[]
        {
            (Xaml: "Views/GroupScoreSettingsPage.axaml", Code: "Views/GroupScoreSettingsPage.axaml.cs"),
            (Xaml: "Views/FloatingBallWindow.axaml",    Code: "Views/FloatingBallWindow.axaml.cs"),
            (Xaml: "Components/ScoreBoardComponent.axaml", Code: "Components/ScoreBoardComponent.axaml.cs")
        };
        foreach (var pair in viewPairs)
        {
            var xamlPath = Path.Combine(root, pair.Xaml.Replace('/', Path.DirectorySeparatorChar));
            var codePath = Path.Combine(root, pair.Code.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(xamlPath) || !File.Exists(codePath)) continue;

            var code = File.ReadAllText(codePath);
            var matches = System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(xamlPath), @"x:Name=""([A-Za-z0-9_]+)""");

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var name = m.Groups[1].Value;
                if (!code.Contains(name)) unwired.Add($"{pair.Xaml.Split('/')[^1]} 的 {name}");
            }
        }
        Check("每个 x:Name 控件都在代码里被用到（防「加了控件忘了接线」）",
            unwired.Count == 0,
            unwired.Count == 0 ? null : "没接线的：" + string.Join("，", unwired));

        // (e) 历史对比页真的接上了服务，而不是只画了一张空表。
        Check("设置页有「历史对比」标签页",
            settingsPageXaml.Contains("Header=\"历史对比\""),
            "XAML 里找不到这个 TabItem");
        Check("历史对比页真的去取过数据（不是一张永远空白的表）",
            settingsPageCode.Contains("_score.GetPeriodComparison(") &&
            settingsPageCode.Contains("HistoryListBox.ItemsSource"),
            "代码里没找到 GetPeriodComparison，或者从没给列表赋过值");

        // (f) 两个下拉框的「值」与「标签」数量必须一一对应。
        static int CountArrayItems(string code, string fieldName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                code, fieldName + @"\s*=\s*\{([^}]*)\}");
            return m.Success
                ? m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length
                : -1;
        }

        var weekLabelCount = CountArrayItems(settingsPageCode, "WeekStartLabels");
        Check($"周起始的可选值与中文标签一一对应（{PeriodStats.WeekStartChoices.Length} 个值 / {weekLabelCount} 个标签）",
            weekLabelCount == PeriodStats.WeekStartChoices.Length,
            weekLabelCount < 0 ? "设置页里找不到 WeekStartLabels" : "数量对不上，ComboBox 会显示串位的标签");

        var kindCount = CountArrayItems(settingsPageCode, "HistoryKinds");
        var kindLabelCount = CountArrayItems(settingsPageCode, "HistoryKindLabels");
        Check($"历史对比的档位与标题一一对应（{kindCount} 个档位 / {kindLabelCount} 个标题）",
            kindCount > 0 && kindCount == kindLabelCount,
            "数量对不上，下拉框选「本月」可能拿到「周」");
    }
}

Console.WriteLine("=== 12. 「小组加减分」自动化的设置模型与执行语义 ===");
{
    // AddScoreActionSettings 刻意不引用 ClassIsland 的任何类型，
    // 所以能当普通模型编进来直接跑 —— 包括它最关键的「序列化往返」。
    // ClassIsland 存行动设置就是 JsonSerializer 往返，这里等价。
    var st = new AddScoreActionSettings();
    Check("默认分值为 +1", st.Delta == 1, st.Delta.ToString());
    Check("默认原因为「自动化」", st.Reason == "自动化", st.Reason);
    Check("默认作用于全部小组", st.ApplyToAllGroups && st.GroupId == null);
    Check("默认受影响名单为空", st.AffectedGroupIds.Count == 0);

    var json = System.Text.Json.JsonSerializer.Serialize<object>(st);
    var back = System.Text.Json.JsonSerializer.Deserialize<AddScoreActionSettings>(json)!;
    Check("序列化往返后分值不变", back.Delta == st.Delta);
    Check("序列化往返后原因不变", back.Reason == st.Reason);

    // 重点：受影响名单必须能往返。
    // 它要是丢了，「恢复时精确回退当初那些小组」就退化成按当前配置乱推。
    var st2 = new AddScoreActionSettings
    {
        GroupId = "g2", Delta = -3, Reason = "课堂纪律",
        AffectedGroupIds = new List<string> { "g1", "g2" }
    };
    var json2 = System.Text.Json.JsonSerializer.Serialize<object>(st2);
    var back2 = System.Text.Json.JsonSerializer.Deserialize<AddScoreActionSettings>(json2)!;
    Check("受影响名单能往返（恢复的精确性靠它）",
        back2.AffectedGroupIds is { Count: 2 } && back2.AffectedGroupIds[1] == "g2", json2);
    Check("负分值与指定小组也能往返",
        back2.Delta == -3 && back2.GroupId == "g2" && !back2.ApplyToAllGroups);
    Check("只读计算属性没被写进配置（JsonIgnore 生效）",
        !json2.Contains("ApplyToAllGroups"), json2);

    var st3 = new AddScoreActionSettings { GroupId = "" };
    Check("GroupId 是空字符串时同样按「全部小组」处理", st3.ApplyToAllGroups);

    // 执行 / 恢复落到 ScoreService 上的效果 —— 行动类本身依赖 ClassIsland 编不进来，
    // 但它做的事就是调这两个方法，所以语义可以照样验。
    var dir = TempDir();
    var svc = new ScoreService(new ScoreStore(dir));
    var target = svc.Groups[0];
    var before = target.CachedTotal;
    var recordsBefore = svc.Data.ScoreRecords.Count;

    svc.AddScore(target.Id, 2, "自动化");         // 执行
    svc.AddScore(target.Id, -2, "撤销：自动化");   // 恢复

    Check("执行 + 恢复后总分回到原值", target.CachedTotal == before, target.CachedTotal.ToString());
    Check("恢复是「记一条反向量」而不是删记录",
        svc.Data.ScoreRecords.Count == recordsBefore + 2,
        $"实际新增 {svc.Data.ScoreRecords.Count - recordsBefore} 条");
    Check("恢复记录带「撤销：」前缀，在记录列表里一眼能认出",
        svc.Data.ScoreRecords[^1].Reason == "撤销：自动化", svc.Data.ScoreRecords[^1].Reason);

    // 「全部小组」模式最关键的性质：执行时取一次名单快照，恢复照那份快照回退，
    // 中途新建的小组不会被误扣。
    var snapshot = svc.Groups.Select(g => g.Id).ToList();
    foreach (var id in snapshot) svc.AddScore(id, 1, "自动化");
    var newcomer = svc.AddGroup("执行之后才建的组");
    foreach (var id in snapshot) svc.AddScore(id, -1, "撤销：自动化");

    Check("恢复只回退执行时快照里的小组，中途新建的组不受影响",
        newcomer.CachedTotal == 0, $"新组总分 {newcomer.CachedTotal}");
    Check("快照里的小组也都回到原值",
        svc.Groups.Where(g => snapshot.Contains(g.Id)).All(g => g.CachedTotal == 0));
}

Console.WriteLine("=== 13. 「小组积分达到阈值」触发器的设置模型与判定语义 ===");
{
    // 设置模型、条件判定、边沿检测都刻意不依赖 ClassIsland，所以能当普通代码编进来直接跑。
    // 触发器本体做不到（它必须继承 TriggerBase），但它做的事只有两件：
    // 「条件满不满足」和「是不是刚刚跨过去」—— 这两件都抽出来了，都在这里验。
    var st = new GroupScoreThresholdTriggerSettings();
    Check("默认阈值 10 分", st.Threshold == 10, st.Threshold.ToString());
    Check("默认盯着任意小组", st.AnyGroup && st.GroupId == null);
    Check("默认比较方向是「达到或超过」", st.Comparison == ThresholdComparison.AtLeast);

    var json = System.Text.Json.JsonSerializer.Serialize<object>(st);
    var back = System.Text.Json.JsonSerializer.Deserialize<GroupScoreThresholdTriggerSettings>(json)!;
    Check("序列化往返后阈值不变", back.Threshold == st.Threshold);
    Check("序列化往返后比较方向不变", back.Comparison == st.Comparison);
    Check("只读计算属性没被写进配置（JsonIgnore 生效）", !json.Contains("AnyGroup"), json);

    var st2 = new GroupScoreThresholdTriggerSettings
    {
        GroupId = "g3",
        Threshold = 20,
        Comparison = ThresholdComparison.AtMost
    };
    var back2 = System.Text.Json.JsonSerializer.Deserialize<GroupScoreThresholdTriggerSettings>(
        System.Text.Json.JsonSerializer.Serialize<object>(st2))!;
    Check("指定小组 + 反向比较也能完整往返",
        back2.GroupId == "g3" && back2.Threshold == 20 && back2.Comparison == ThresholdComparison.AtMost);
    Check("GroupId 是空字符串时同样按「任意小组」处理",
        new GroupScoreThresholdTriggerSettings { GroupId = "" }.AnyGroup);

    // ---- 条件判定：拿真实形状的数据跑 ----
    var roster = new (string Id, int Total)[] { ("g1", 5), ("g2", 12), ("g3", 20) };

    Check("任意小组 / 达到 10 → 满足（g2 够线）",
        GroupScoreThresholdRule.IsSatisfied(roster, null, 10, ThresholdComparison.AtLeast));

    Check("任意小组 / 达到 25 → 不满足",
        !GroupScoreThresholdRule.IsSatisfied(roster, null, 25, ThresholdComparison.AtLeast));

    Check("指定 g1 / 达到 10 → 不满足（g1 只有 5 分）",
        !GroupScoreThresholdRule.IsSatisfied(roster, "g1", 10, ThresholdComparison.AtLeast));

    Check("指定 g3 / 达到 20 → 满足（正好等于也算）",
        GroupScoreThresholdRule.IsSatisfied(roster, "g3", 20, ThresholdComparison.AtLeast));

    Check("指定 g1 / 掉到 10 及以下 → 满足（5 ≤ 10）",
        GroupScoreThresholdRule.IsSatisfied(roster, "g1", 10, ThresholdComparison.AtMost));

    Check("指定 g3 / 掉到 10 及以下 → 不满足（20 > 10）",
        !GroupScoreThresholdRule.IsSatisfied(roster, "g3", 10, ThresholdComparison.AtMost));

    Check("指定的组不存在 → 不满足（小组被删掉后不会误触发）",
        !GroupScoreThresholdRule.IsSatisfied(roster, "已经删掉的组", 0, ThresholdComparison.AtLeast));

    Check("一个小组都没有 → 不满足",
        !GroupScoreThresholdRule.IsSatisfied(Array.Empty<(string Id, int Total)>(), null, 0,
            ThresholdComparison.AtLeast));

    Check("任意小组 / 掉到 -1 及以下 → 不满足（最低分才 5）",
        !GroupScoreThresholdRule.IsSatisfied(roster, null, -1, ThresholdComparison.AtMost));

    // 配置文件里万一留下旧版本不认识的枚举值，要按「达到或超过」兜底，
    // 而不是掉进「掉到以下」那条分支上乱触发
    Check("未知比较值按「达到或超过」兜底",
        GroupScoreThresholdRule.IsSatisfied(roster, null, 10, (ThresholdComparison)99));

    // ---- 边沿检测：触发器最容易写错的地方 ----
    // ClassIsland 的 Trigger() 是电平式的：调一次就跑一遍行动集。
    // 而「第 3 组已经 20 分了」这个事实在之后的每一次加分里依然成立 ——
    // 没有边沿检测，老师每加一分庆祝音效就响一次。
    var d = new ThresholdEdgeDetector();
    d.Prime(false);

    Check("第一次跨过阈值 → Raised",
        d.Feed(true) == ThresholdEdge.Raised);
    Check("持续满足时不再重复触发（连喂三次都是 None）",
        d.Feed(true) == ThresholdEdge.None &&
        d.Feed(true) == ThresholdEdge.None &&
        d.Feed(true) == ThresholdEdge.None);
    Check("掉回阈值以下 → Fallen",
        d.Feed(false) == ThresholdEdge.Fallen);
    Check("持续不满足时不再重复恢复",
        d.Feed(false) == ThresholdEdge.None);
    Check("再次跨过去 → 又能触发一次",
        d.Feed(true) == ThresholdEdge.Raised);

    // Prime 的语义：装填基线，不产生事件。
    // 工作流刚加载时可能已经有组超过阈值了，那是上一节课的功劳，
    // 不该在这一刻补放一遍庆祝音效。
    var d2 = new ThresholdEdgeDetector();
    d2.Prime(true);
    Check("Prime 之后状态就是「已满足」", d2.IsSatisfied);
    Check("Prime 本身不产生事件（接着喂 true 还是 None）",
        d2.Feed(true) == ThresholdEdge.None);
    Check("从 Prime 的 true 掉下去，照样 Fallen",
        d2.Feed(false) == ThresholdEdge.Fallen);

    // 自激场景：触发器配「第 1 组 ≥10」，而行动又去改分数。
    // 边沿检测保证第二次进来时状态没变，形不成「触发 → 改分 → 再触发」的循环。
    var d3 = new ThresholdEdgeDetector();
    d3.Prime(false);
    d3.Feed(true);                                   // 触发
    Check("触发过程中行动又改了分，不会自激（重入时返回 None）",
        d3.Feed(true) == ThresholdEdge.None);
}

Console.WriteLine("=== 14. 悬浮球定位与拖动（跑的是插件里的真代码，不是重抄一遍）===");
{
    // 一块 1920×1080 的主屏，下边 40 像素被任务栏占掉
    var area = new WorkArea(0, 0, 1920, 1040);
    const int ballW = 64, ballH = 64;
    const int panelW = 340, panelH = 420;
    const int m = BallPlacement.DefaultMargin;

    // ---- 14.1 按角落停靠 ----
    Console.WriteLine($"     工作区 1920×1040，球 {ballW}×{ballH}，边距 {m}");
    Check("右下角：贴右贴下", BallPlacement.FromCorner("BottomRight", ballW, ballH, area) == (1920 - ballW - m, 1040 - ballH - m));
    Check("左上角：贴左贴上", BallPlacement.FromCorner("TopLeft", ballW, ballH, area) == (m, m));
    Check("右上角", BallPlacement.FromCorner("TopRight", ballW, ballH, area) == (1920 - ballW - m, m));
    Check("左下角", BallPlacement.FromCorner("BottomLeft", ballW, ballH, area) == (m, 1040 - ballH - m));

    // 副屏挂在主屏左边时工作区 X 是负的。若把坐标当「从 0 开始」算，球会跑到主屏上去。
    var leftScreen = new WorkArea(-1920, 0, 1920, 1040);
    Check("副屏在主屏左边（工作区坐标为负）也算得对",
        BallPlacement.FromCorner("TopLeft", ballW, ballH, leftScreen) == (-1920 + m, m));

    Check("认不出的角落字符串 → 按默认角落处理，不会算出乱坐标",
        BallPlacement.FromCorner("Middle", ballW, ballH, area) == BallPlacement.FromCorner(BallPlacement.DefaultCorner, ballW, ballH, area));

    // ---- 14.2 钳制：保证整窗可见 ----
    Check("拖到左上外面 → 拉回来", BallPlacement.Clamp(-500, -500, ballW, ballH, area) == (m, m));
    Check("拖到右下外面 → 拉回来", BallPlacement.Clamp(9999, 9999, ballW, ballH, area) == (1920 - ballW - m, 1040 - ballH - m));
    Check("本来就在区内 → 一个像素都不动", BallPlacement.Clamp(800, 500, ballW, ballH, area) == (800, 500));

    // 球展开成 340 宽的面板后，原本合法的 x 就可能出界。
    // 这正是「每次尺寸变化都要重新 ApplyPosition」的原因。
    // 取 1800：它对球（上限 1844）是合法的，对面板（上限 1568）就越界了。
    Check("同一个 x：对球合法，对展开面板要重新钳回来",
        BallPlacement.Clamp(1800, 500, ballW, ballH, area) == (1800, 500) &&
        BallPlacement.Clamp(1800, 500, panelW, panelH, area) == (1920 - panelW - m, 500));

    // 竖屏 / 高缩放的极端情形：窗口比工作区还大。
    // 此时「右下上限」会跑到「左上下限」的左边，Clamp 的语义崩掉。
    // 这里的选择是保左上角 —— 宁可右边被切一点，也要让球本身看得见、点得到。
    Check("窗口比工作区还大 → 保左上角，不返回越界的上限",
        BallPlacement.Clamp(500, 500, 4000, 3000, area) == (m, m));

    // ---- 14.3 Resolve：老师拖过没拖过 ----
    Check("没拖过 → 按角落",
        BallPlacement.Resolve(null, null, "BottomRight", ballW, ballH, area)
            == BallPlacement.FromCorner("BottomRight", ballW, ballH, area));
    Check("拖过 → 用记忆的位置",
        BallPlacement.Resolve(700, 300, "BottomRight", ballW, ballH, area) == (700, 300));
    Check("记忆位置落在屏幕外（拔掉副屏留下的坐标）→ 拉回主屏",
        BallPlacement.Resolve(9999, -800, "BottomRight", ballW, ballH, area) == (1920 - ballW - m, m));
    Check("只有 X 没有 Y（手改配置 / 写到一半）→ 当作没拖过",
        BallPlacement.Resolve(700, null, "TopLeft", ballW, ballH, area) == (m, m));
    Check("只有 Y 没有 X → 同样当作没拖过",
        BallPlacement.Resolve(null, 300, "TopLeft", ballW, ballH, area) == (m, m));

    // ---- 14.4 拖动阈值与落点 ----
    // 球只有 64 像素，老师的手一定会抖。小于阈值的手抖必须仍然算「点击」，
    // 否则会出现「想展开面板却纹丝不动」。
    Check("手抖 2 像素不算拖动（否则点不开面板）", !BallPlacement.ExceedsDragThreshold(2, -1));
    Check("位移到阈值算拖动", BallPlacement.ExceedsDragThreshold(4, 0));
    Check("纵向位移同样算拖动", BallPlacement.ExceedsDragThreshold(0, -4));

    var start = BallPlacement.FromCorner("BottomRight", ballW, ballH, area);
    Check("落点 = 按下时的位置 + 累计位移",
        BallPlacement.DragTo(start.X, start.Y, -100, -50, ballW, ballH, area) == (start.X - 100, start.Y - 50));
    Check("往屏幕外拖 → 落点被钳住，不会飞出去",
        BallPlacement.DragTo(start.X, start.Y, 500, 500, ballW, ballH, area) == start);

    // 关键：始终以「按下时的位置 + 累计位移」算，而不是在当前落点上累加。
    // 否则一旦被钳到边界，指针往回走时球会粘在边上不动。
    var pinned = BallPlacement.DragTo(start.X, start.Y, 9999, 0, ballW, ballH, area);
    var back = BallPlacement.DragTo(start.X, start.Y, -10, 0, ballW, ballH, area);
    Check("拖到边界后指针往回收，球能立刻跟回来（不会粘在边上）",
        pinned == start && back == (start.X - 10, start.Y));

    // ---- 14.4b 坐标换算（2026-10-17 实测出两个真 bug，都只跟坐标单位有关）----
    // ① 位移如果拿「两次窗口客户区坐标相减」，窗口自身的位移会被抵消掉 → 球跟不上手
    // ② 尺寸如果拿逻辑单位（ClientSize / Bounds）去跟工作区比 → 缩放屏上球露出一截在屏幕外
    // 两处都是纯算术，这里把契约直接钉死。

    Check("逻辑尺寸 → 物理像素：150% 缩放下 340 宽的展开面板实际占 510",
        BallPlacement.ToPhysicalSize(340, 64, 1.5) == (510, 96));
    Check("100% 缩放时尺寸原样不变", BallPlacement.ToPhysicalSize(64, 64, 1.0) == (64, 64));
    Check("尺寸向上取整（宁可多留一个像素，也不能少算导致越界）",
        BallPlacement.ToPhysicalSize(63.4, 63.4, 1.0) == (64, 64));

    Check("指针屏幕坐标 = 窗口位置 + 客户区偏移 × 缩放",
        BallPlacement.PointerOnScreen(1000, 500, 30, 30, 1.5) == (1045, 545));

    // 最关键的一条：**窗口自己挪到哪儿都不影响换算结果**。
    // 用客户区坐标相减的旧写法在这一组里三个答案会互不相同（窗口正好等于指针位置时甚至得到 0），
    // 也就是「球莫名其妙不动了」。这里要求三次换算都还原出同一个物理位置。
    {
        const double sc = 1.5;
        var pointer = (X: 1300, Y: 700);
        var windows = new[] { (1000, 500), (1150, 560), (1300, 700) };
        var restored = windows
            .Select(w =>
            {
                // 事件给我们的客户区坐标：由「指针屏幕位置」和「窗口当前位置」反推
                var cx = (pointer.X - w.Item1) / sc;
                var cy = (pointer.Y - w.Item2) / sc;
                return BallPlacement.PointerOnScreen(w.Item1, w.Item2, cx, cy, sc);
            })
            .ToList();

        Check("窗口移到任何位置，同一个物理指针对应同一个屏幕坐标（换算与窗口自身位移无关）",
            restored.All(p => p == pointer),
            "三次换算得到：" + string.Join(" / ", restored));
    }

    // 拖动的 1:1 跟手：指针在屏幕上走 300 像素，窗口也必须正好走 300 像素。
    {
        const double sc = 1.5;
        var win = (X: 1000, Y: 500);
        var press = BallPlacement.PointerOnScreen(win.X, win.Y, 30, 30, sc);
        var now = BallPlacement.PointerOnScreen(win.X, win.Y, 200, 133.33, sc);

        var (mx, my) = BallPlacement.DragTo(win.X, win.Y, now.X - press.X, now.Y - press.Y,
            ballW, ballH, area);
        Check("指针在屏幕上走多远，窗口就走多远（1:1 跟手，与鼠标速度无关）",
            mx - win.X == now.X - press.X && my - win.Y == now.Y - press.Y,
            $"指针走了 {now.X - press.X},{now.Y - press.Y}，窗口走了 {mx - win.X},{my - win.Y}");
    }

    // 阈值本身也要跟着缩放走（"4 像素"指逻辑像素），否则高缩放屏上轻轻一抖就变成拖动
    Check("拖动阈值按逻辑像素算：150% 缩放下要走满 6 个物理像素才算拖动",
        !BallPlacement.ExceedsDragThreshold(5, 0, BallPlacement.DragThreshold * 1.5) &&
        BallPlacement.ExceedsDragThreshold(6, 0, BallPlacement.DragThreshold * 1.5));

    // ---- 14.5 位置记忆落盘往返 ----
    var dir = TempDir();
    var s1 = new SettingsService(dir);
    Check("新装插件没有拖动记忆", !s1.Settings.HasCustomPosition);

    s1.Settings.BallX = 700;
    s1.Settings.BallY = 300;
    Check("两个坐标都写了才算有记忆", s1.Settings.HasCustomPosition);

    var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
    Check("拖动记忆真的写进了 settings.json（而不是只在内存里）",
        json.Contains("\"BallX\"") && json.Contains("\"BallY\""),
        json.Length > 200 ? json[..200] : json);
    Check("只读的 HasCustomPosition 没混进 json（[JsonIgnore] 生效）",
        !json.Contains("HasCustomPosition"));

    var s2 = new SettingsService(dir);
    Check("重载后记忆还在",
        s2.Settings.HasCustomPosition && s2.Settings.BallX == 700 && s2.Settings.BallY == 300);
    Check("重载后解析出来的位置就是当初拖到的那个点",
        BallPlacement.Resolve(s2.Settings.BallX, s2.Settings.BallY, s2.Settings.Corner, ballW, ballH, area) == (700, 300));

    s2.Settings.ClearCustomPosition();
    Check("「恢复默认位置」之后就回到角落停靠",
        !s2.Settings.HasCustomPosition &&
        BallPlacement.Resolve(s2.Settings.BallX, s2.Settings.BallY, s2.Settings.Corner, ballW, ballH, area)
            == BallPlacement.FromCorner(s2.Settings.Corner, ballW, ballH, area));

    var s3 = new SettingsService(dir);
    Check("「恢复默认位置」本身也落了盘（重启后不会又跳回拖过的地方）",
        !s3.Settings.HasCustomPosition);

    // ---- 14.6 角落清单的一致性 ----
    Check("设置里的默认角落 = CornerIds[0]（两处别写漂了）",
        new PluginSettings().Corner == BallPlacement.DefaultCorner,
        $"设置默认 {new PluginSettings().Corner}，CornerIds[0] 是 {BallPlacement.DefaultCorner}");
    Check("默认角落本身是个合法角落", BallPlacement.IsKnownCorner(BallPlacement.DefaultCorner));
    Check("乱填的角落字符串能被识别出来", !BallPlacement.IsKnownCorner("Middle"));
    Check("四个角落的 id 互不重复",
        BallPlacement.CornerIds.Distinct().Count() == BallPlacement.CornerIds.Length);
}

Console.WriteLine("=== 15. 成员维度积分：聚合、名次、删成员语义 ===");
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);

    var g1 = svc.Groups[0];
    var g2 = svc.Groups[1];

    // ---- 15.1 成员 CRUD ----
    var zhang = svc.AddMember(g1.Id, "张三");
    var li = svc.AddMember(g1.Id, "李四");
    var wang = svc.AddMember(g1.Id, "王五");
    var zhao = svc.AddMember(g2.Id, "赵六");

    Check("成员按组归属",
        svc.MembersOf(g1.Id).Count == 3 && svc.MembersOf(g2.Id).Count == 1,
        $"g1={svc.MembersOf(g1.Id).Count}, g2={svc.MembersOf(g2.Id).Count}");
    Check("组内顺序从 0 开始连续编号",
        svc.MembersOf(g1.Id).Select(m => m.SortOrder).SequenceEqual(new[] { 0, 1, 2 }),
        string.Join(",", svc.MembersOf(g1.Id).Select(m => m.SortOrder)));
    Check("FindMember 按 Id 找得到人", svc.FindMember(zhang.Id)?.Name == "张三");
    Check("FindMember 找不到时返回 null（而不是抛异常）", svc.FindMember("不存在的Id") == null);
    Check("FindMember 收到 null / 空串也返回 null", svc.FindMember(null) == null && svc.FindMember("") == null);

    // ---- 15.2 个人分聚合：个人分是从记录现算的 ----
    svc.AddScore(g1.Id, 5, "回答正确", null, zhang.Id);
    svc.AddScore(g1.Id, 3, "作业优秀", null, li.Id);

    Check("给个人加分同时计入小组总分", g1.CachedTotal == 8, $"实际 {g1.CachedTotal}");
    Check("个人分只算指名到自己的记录",
        svc.GetMemberTotal(zhang.Id) == 5 && svc.GetMemberTotal(li.Id) == 3,
        $"张三 {svc.GetMemberTotal(zhang.Id)} / 李四 {svc.GetMemberTotal(li.Id)}");
    Check("没被加过分的人个人分是 0", svc.GetMemberTotal(wang.Id) == 0);
    Check("此时还没有未指派的分", svc.GetUnassignedTotal(g1.Id) == 0);

    // 按组记的分不属于任何个人 —— 这是「成员分之和 ≠ 组总分」的唯一原因
    svc.AddScore(g1.Id, 2, "全组纪律优秀");
    Check("按组记的分不进任何人的个人分", svc.GetMemberTotal(zhang.Id) == 5);
    Check("按组记的分落到「未指派」里", svc.GetUnassignedTotal(g1.Id) == 2,
        $"实际 {svc.GetUnassignedTotal(g1.Id)}");
    Check("组总分 = 5 + 3 + 2 = 10", g1.CachedTotal == 10, $"实际 {g1.CachedTotal}");
    Check("加分不会串到别的小组", g2.CachedTotal == 0, $"实际 {g2.CachedTotal}");

    var items = svc.GetMemberScores(g1.Id);
    Check("恒等式成立：成员分合计 + 未指派分 = 组总分",
        MemberScores.Balances(g1.CachedTotal, items, svc.GetUnassignedTotal(g1.Id)),
        $"{items.Sum(i => i.Total)} + {svc.GetUnassignedTotal(g1.Id)} vs {g1.CachedTotal}");
    Check("ScoreService 自己也认这个恒等式", svc.CheckGroupBalance(g1.Id));

    // ---- 15.3 名次（并列规则必须和小组排行榜一致）----
    Check("名次按个人分降序", items[0].MemberId == zhang.Id && items[1].MemberId == li.Id);
    Check("名次为 1、2、3", items.Select(i => i.Rank).SequenceEqual(new[] { 1, 2, 3 }),
        string.Join(",", items.Select(i => i.Rank)));

    svc.AddScore(g1.Id, 2, "追平", null, li.Id);   // 李四追到 5，与张三并列
    var tied = svc.GetMemberScores(g1.Id);
    Check("同分并列：1、1、3（和小组成员排行榜同一个规则，不是 1、2、3）",
        tied.Select(i => i.Rank).SequenceEqual(new[] { 1, 1, 3 }),
        string.Join(",", tied.Select(i => i.Rank)));

    // ---- 15.4 撤销：个人分是现算的，所以会跟着退回去 ----
    // 王五现在 0 分，加 6 才能超过并列第一的张三、李四（各 5 分）
    svc.AddScore(g1.Id, 6, "临时加分", null, wang.Id);
    Check("给王五加分后他升到第一", svc.GetMemberScores(g1.Id)[0].MemberId == wang.Id);

    svc.UndoLast();
    Check("撤销一条成员分后，那个人的个人分跟着回到 0（不用另维护一份缓存）",
        svc.GetMemberTotal(wang.Id) == 0, $"实际 {svc.GetMemberTotal(wang.Id)}");
    Check("撤销后恒等式依然成立", svc.CheckGroupBalance(g1.Id));

    // ---- 15.5 memberId 归属校验：宁可归属粗略，也不能让分丢掉 ----
    var beforeCross = g1.CachedTotal;
    var zhaoBefore = svc.GetMemberTotal(zhao.Id);

    svc.AddScore(g1.Id, 7, "跨组测试", null, zhao.Id);   // 赵六在 g2，不属于 g1
    Check("跨组的 memberId 被降级为「不指名」：分照记",
        g1.CachedTotal == beforeCross + 7, $"实际 {g1.CachedTotal}，期望 {beforeCross + 7}");
    Check("跨组的 memberId 不会算到那个人的头上", svc.GetMemberTotal(zhao.Id) == zhaoBefore);
    Check("降级后记录里的 MemberId 确实是空的", svc.Data.ScoreRecords[^1].MemberId == null);

    svc.AddScore(g1.Id, 1, "幽灵成员", null, "根本不存在的Id");
    Check("指向不存在成员的 memberId 同样降级，分不丢",
        svc.Data.ScoreRecords[^1].MemberId == null && g1.CachedTotal == beforeCross + 8);
    Check("降级之后恒等式仍然成立", svc.CheckGroupBalance(g1.Id));

    // ---- 15.6 删成员：记录不删，只把归属抹掉 ----
    var recordsBefore = svc.Data.ScoreRecords.Count;
    var g1Before = g1.CachedTotal;
    var unassignedBefore = svc.GetUnassignedTotal(g1.Id);
    var zhangScore = svc.GetMemberTotal(zhang.Id);

    Check("删除成员会回报被孤儿化的记录条数",
        svc.DeleteMember(zhang.Id, out var orphaned) && orphaned == 1, $"孤儿化 {orphaned} 条");
    Check("删成员不删记录（删掉一个学生不等于他挣的分没发生过）",
        svc.Data.ScoreRecords.Count == recordsBefore,
        $"{svc.Data.ScoreRecords.Count} vs {recordsBefore}");
    Check("删成员不改小组总分", g1.CachedTotal == g1Before, $"{g1.CachedTotal} vs {g1Before}");
    Check("删成员后他的分原封不动转成「未指派」",
        svc.GetUnassignedTotal(g1.Id) == unassignedBefore + zhangScore,
        $"{svc.GetUnassignedTotal(g1.Id)} vs {unassignedBefore + zhangScore}");
    Check("删成员后成员列表里没有他了", svc.MembersOf(g1.Id).All(m => m.Id != zhang.Id));
    Check("删成员后记录里不再指向他", svc.Data.ScoreRecords.All(r => r.MemberId != zhang.Id));
    Check("删成员后组内顺序被收紧，不留空洞",
        svc.MembersOf(g1.Id).Select(m => m.SortOrder).SequenceEqual(new[] { 0, 1 }),
        string.Join(",", svc.MembersOf(g1.Id).Select(m => m.SortOrder)));
    Check("删成员后恒等式仍然成立", svc.CheckGroupBalance(g1.Id));
    Check("删除不存在的成员返回 false", !svc.DeleteMember("不存在的Id", out _));

    // ---- 15.7 转组：分数要跟着人走 ----
    svc.AddScore(g1.Id, 6, "转组前挣的分", null, wang.Id);
    var wangScore = svc.GetMemberTotal(wang.Id);
    var g1BeforeMove = g1.CachedTotal;
    var g2BeforeMove = g2.CachedTotal;

    Check("转组成功", svc.MoveMemberToGroup(wang.Id, g2.Id));
    Check("转组后成员归到新组", svc.MembersOf(g2.Id).Any(m => m.Id == wang.Id));
    Check("转组后原组总分减去他的分",
        g1.CachedTotal == g1BeforeMove - wangScore, $"{g1.CachedTotal} vs {g1BeforeMove - wangScore}");
    Check("转组后新组总分加上他的分",
        g2.CachedTotal == g2BeforeMove + wangScore, $"{g2.CachedTotal} vs {g2BeforeMove + wangScore}");
    Check("转组后原组仍然平衡", svc.CheckGroupBalance(g1.Id));
    Check("转组后新组也平衡", svc.CheckGroupBalance(g2.Id));
    Check("转到当前所在的组会被拒绝", !svc.MoveMemberToGroup(wang.Id, g2.Id));
    Check("转到不存在的小组会被拒绝", !svc.MoveMemberToGroup(wang.Id, "不存在的组"));

    // ---- 15.8 组内顺序 ----
    var g2Order = svc.MembersOf(g2.Id).Select(m => m.Id).ToList();
    Check("g2 里有两个成员可供排序", g2Order.Count == 2, $"实际 {g2Order.Count}");
    Check("把第二个人上移一位", svc.MoveMember(g2Order[1], -1));
    Check("顺序确实交换了", svc.MembersOf(g2.Id)[0].Id == g2Order[1]);
    Check("已经在最上面的再上移会被拒绝", !svc.MoveMember(g2Order[1], -1));
    Check("已经在最下面的再下移会被拒绝", !svc.MoveMember(g2Order[0], 1));

    // ---- 15.9 顺序归一化（老数据里没有 SortOrder 字段）----
    var allZero = new List<Member>
    {
        new() { Name = "甲" }, new() { Name = "乙" }, new() { Name = "丙" }
    };
    MemberScores.NormalizeOrder(allZero);
    Check("全是 0 的老数据被归一化成 0、1、2，并保持原来的排列",
        allZero.Select(m => m.SortOrder).SequenceEqual(new[] { 0, 1, 2 }) &&
        allZero.Select(m => m.Name).SequenceEqual(new[] { "甲", "乙", "丙" }),
        string.Join(",", allZero.Select(m => m.SortOrder)));

    var holed = new List<Member>
    {
        new() { Name = "甲", SortOrder = 5 }, new() { Name = "乙", SortOrder = 9 }
    };
    MemberScores.NormalizeOrder(holed);
    Check("带空洞的顺序被收紧成 0、1",
        holed.OrderBy(m => m.SortOrder).Select(m => m.SortOrder).SequenceEqual(new[] { 0, 1 }));

    // ---- 15.10 真读一份「老格式」数据文件 ----
    // 上面测的是纯函数，这里测的是整条加载路径：文件里没有 SortOrder 字段时，
    // ScoreStore 会不会真的去归一化。
    var legacyDir = TempDir();
    File.WriteAllText(Path.Combine(legacyDir, "score-data.json"), """
    {
      "SchemaVersion": 1,
      "Seeded": true,
      "Groups": [
        { "Id": "g1", "Name": "第一小组", "Color": "#378ADD", "SortOrder": 0, "CachedTotal": 0 }
      ],
      "Members": [
        { "Id": "m1", "GroupId": "g1", "Name": "甲" },
        { "Id": "m2", "GroupId": "g1", "Name": "乙" }
      ],
      "ScoreRecords": [],
      "Presets": []
    }
    """, new System.Text.UTF8Encoding(false));

    var legacySvc = new ScoreService(new ScoreStore(legacyDir));
    Check("老数据（成员没有 SortOrder 字段）读进来后顺序被归一化成 0、1",
        legacySvc.MembersOf("g1").Select(m => m.SortOrder).SequenceEqual(new[] { 0, 1 }),
        string.Join(",", legacySvc.MembersOf("g1").Select(m => m.SortOrder)));
    Check("老数据的成员姓名没读丢，顺序也没乱",
        legacySvc.MembersOf("g1").Select(m => m.Name).SequenceEqual(new[] { "甲", "乙" }));

    // ---- 15.11 落盘往返 ----
    // 必须先手动刷盘：ScoreStore 是「防抖写」的（一秒内没有新改动才真正落盘），
    // 不刷这一次，紧接着新开的那个 store 读到的还是旧文件 ——
    // 这一条断言第一次跑就是红的，原因在这里，不在成员逻辑。
    svc.FlushToDisk();
    var svc2 = new ScoreService(new ScoreStore(dir));
    Check("成员被持久化了（重开一次还在）",
        svc2.MembersOf(g2.Id).Select(m => m.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(new[] { "王五", "赵六" }),
        string.Join(",", svc2.MembersOf(g2.Id).Select(m => m.Name)));
    Check("成员顺序也被持久化",
        svc2.MembersOf(g2.Id).Select(m => m.Id).SequenceEqual(svc.MembersOf(g2.Id).Select(m => m.Id)));

    // ---- 15.12 脏数据：指向不存在成员的记录 ----
    // 手工编辑 JSON 有可能造出这种记录。它必须算进「未指派」，
    // 否则恒等式会被脏数据打破，而那正是我们用来发现「有人绕过双写」的信号。
    var g2Group = svc.Data.Groups.First(g => g.Id == g2.Id);
    svc.Data.ScoreRecords.Add(new ScoreRecord { GroupId = g2.Id, MemberId = "幽灵", Delta = 3 });
    g2Group.CachedTotal += 3;
    Check("指向不存在成员的记录算作「未指派」，恒等式不会被脏数据打破",
        svc.CheckGroupBalance(g2.Id), "未指派分漏算了这种记录");

    // ---- 15.13 杂项 ----
    Check("空名字有兜底，不会造出无名成员", svc.AddMember(g2.Id, "   ").Name == "新成员");
    Check("成员显示文本带姓名和分数",
        new MemberScoreItem { Name = "张三", Total = 5 }.DisplayText.Contains("张三"));
    Check("对空记录求个人分返回 0", MemberScores.TotalOf(new List<ScoreRecord>(), "任意Id") == 0);
}

Console.WriteLine("=== 16. 历史对比：周期切片、净增、累计与名次 ===");
{
    // 时间相关的断言必须钉死一个「观测时刻」：
    // 拿 DateTime.Now 当观测点的话，同一份代码周日跑和周一跑结论不同，
    // 这种测试会变成「今天绿明天红」的噪音。
    var now = new DateTime(2026, 10, 16, 15, 30, 0);   // 2026-10-16 是周五

    // ---- 16.1 周起始 ----
    Check("2026-10-16 是周五（后面几条断言的前提）",
        now.DayOfWeek == DayOfWeek.Friday, now.DayOfWeek.ToString());
    Check("默认一周从周一算起", PeriodStats.DefaultWeekStartsOn == 1);
    Check("周一为起点：10-16（周五）所在的周从 10-12（周一）开始",
        PeriodStats.StartOfWeek(now, 1) == new DateTime(2026, 10, 12));
    Check("周日为起点：10-16 所在的周从 10-11（周日）开始",
        PeriodStats.StartOfWeek(now, 0) == new DateTime(2026, 10, 11));
    Check("周日当天按「周一为起点」算，仍归上一周（10-18 → 10-12）",
        PeriodStats.StartOfWeek(new DateTime(2026, 10, 18), 1) == new DateTime(2026, 10, 12));
    Check("周一当天按「周日为起点」算，归到上周日（10-12 → 10-11）",
        PeriodStats.StartOfWeek(new DateTime(2026, 10, 12), 0) == new DateTime(2026, 10, 11));
    Check("同一天的凌晨和深夜算出的周起点完全一样",
        PeriodStats.StartOfWeek(new DateTime(2026, 10, 16, 0, 0, 0), 1) ==
        PeriodStats.StartOfWeek(new DateTime(2026, 10, 16, 23, 59, 59), 1));
    Check("非法周起始值被拉回默认（-1 和 9 都不该让周期算术变形）",
        PeriodStats.NormalizeWeekStart(-1) == 1 &&
        PeriodStats.NormalizeWeekStart(9) == 1 &&
        PeriodStats.NormalizeWeekStart(0) == 0);

    // ---- 16.2 周期区间 ----
    var thisWeek = PeriodStats.Of(now, PeriodKind.Week, 1);
    var lastWeek = PeriodStats.PreviousOf(now, PeriodKind.Week, 1);
    var thisMonth = PeriodStats.Of(now, PeriodKind.Month, 1);
    var lastMonth = PeriodStats.PreviousOf(now, PeriodKind.Month, 1);

    Check("本周 = 10-12 起、10-19 止（右端不含）",
        thisWeek.Start == new DateTime(2026, 10, 12) &&
        thisWeek.EndExclusive == new DateTime(2026, 10, 19));
    Check("上周 = 10-05 ~ 10-12",
        lastWeek.Start == new DateTime(2026, 10, 5) &&
        lastWeek.EndExclusive == new DateTime(2026, 10, 12));
    Check("相邻两期严丝合缝：上一期的终点就是这一期的起点",
        lastWeek.EndExclusive == thisWeek.Start);
    Check("整周长度是 7 天", thisWeek.Length == TimeSpan.FromDays(7));

    Check("本月 = 10-01 ~ 11-01",
        thisMonth.Start == new DateTime(2026, 10, 1) &&
        thisMonth.EndExclusive == new DateTime(2026, 11, 1));
    Check("上月 = 09-01 ~ 10-01",
        lastMonth.Start == new DateTime(2026, 9, 1) &&
        lastMonth.EndExclusive == new DateTime(2026, 10, 1));

    // 月末往回找一个月是最容易写错的地方：
    // 拿 3 月 31 日直接 AddMonths(-1) 会被截断成 2 月 28 日，
    // 得到一个从月中开始的残缺区间，而它看起来还挺合理。
    var fromMar31 = PeriodStats.PreviousOf(new DateTime(2026, 3, 31), PeriodKind.Month, 1);
    Check("3 月 31 日的「上月」是完整的 02-01 ~ 03-01（不是被截断的 02-28）",
        fromMar31.Start == new DateTime(2026, 2, 1) &&
        fromMar31.EndExclusive == new DateTime(2026, 3, 1),
        fromMar31.Start.ToString("yyyy-MM-dd"));

    Check("平年 2 月是 28 天",
        PeriodStats.Of(new DateTime(2026, 2, 15), PeriodKind.Month, 1).Length == TimeSpan.FromDays(28));
    Check("闰年 2 月是 29 天",
        PeriodStats.Of(new DateTime(2028, 2, 15), PeriodKind.Month, 1).Length == TimeSpan.FromDays(29));

    var crossingWeek = PeriodStats.Of(new DateTime(2026, 10, 1), PeriodKind.Week, 1);
    Check("跨月的周按整体算：10-01 所在的那一周从 09-28 起、到 10-05 止",
        crossingWeek.Start == new DateTime(2026, 9, 28) &&
        crossingWeek.EndExclusive == new DateTime(2026, 10, 5));

    // ---- 16.3 半开区间：边界那一刻只能算一次 ----
    // 边界是「上一期的终点 == 这一期的起点」，也就是 10-12 00:00:00。
    // （别顺手写成上一期的起点 10-05 —— 那是另一条边界，测不到这里想测的东西。）
    var boundary = thisWeek.Start;
    Check("边界时刻 10-12 00:00:00 不算在上周里", !lastWeek.Contains(boundary));
    Check("同一边界时刻算在本周里（不重不漏，全靠右端取开）", thisWeek.Contains(boundary));
    Check("上一期的最后一毫秒仍算上一期", lastWeek.Contains(boundary.AddTicks(-1)));
    Check("本期的最后一毫秒仍算本期", thisWeek.Contains(thisWeek.EndExclusive.AddTicks(-1)));
    Check("整点结束时刻本身不算本期", !thisWeek.Contains(thisWeek.EndExclusive));
    Check("同一条边界记录只被算一次：归本周、不进上周",
        BoundaryTotals(thisWeek) == 4 && BoundaryTotals(lastWeek) == 0);

    int BoundaryTotals(ScorePeriod period) => PeriodStats
        .TotalsByGroup(new[] { new ScoreRecord { GroupId = "任意组", Delta = 4, CreatedAt = boundary } }, period)
        .Values.Sum();

    // ---- 16.4 净增、累计与合计 ----
    var g1 = new Group { Name = "第一小组", SortOrder = 0 };
    var g2 = new Group { Name = "第二小组", SortOrder = 1 };
    var twoGroups = new List<Group> { g1, g2 };

    var records = new List<ScoreRecord>
    {
        new() { GroupId = g1.Id, Delta = 5,  CreatedAt = new DateTime(2026, 10, 6, 9, 0, 0) },   // 上周
        new() { GroupId = g2.Id, Delta = 2,  CreatedAt = new DateTime(2026, 10, 7, 9, 0, 0) },   // 上周
        new() { GroupId = g1.Id, Delta = 3,  CreatedAt = new DateTime(2026, 10, 13, 9, 0, 0) },  // 本周
        new() { GroupId = g2.Id, Delta = -1, CreatedAt = new DateTime(2026, 10, 14, 9, 0, 0) },  // 本周
        new() { GroupId = g1.Id, Delta = 2,  CreatedAt = new DateTime(2026, 10, 15, 9, 0, 0) }   // 本周
    };

    var cmp = PeriodStats.Compare(twoGroups, records, thisWeek, lastWeek);

    Check("本期 3 条记录、上期 2 条",
        cmp.CurrentRecordCount == 3 && cmp.PreviousRecordCount == 2,
        $"{cmp.CurrentRecordCount}/{cmp.PreviousRecordCount}");
    Check("本期全班净增 +4、上期 +7、相比 -3",
        cmp.CurrentTotal == 4 && cmp.PreviousTotal == 7 && cmp.TotalChange == -3,
        $"{cmp.CurrentTotal}/{cmp.PreviousTotal}/{cmp.TotalChange}");
    Check("合计永远等于各行之和（已删小组的记录不进合计）",
        cmp.CurrentTotal == cmp.Rows.Sum(r => r.CurrentDelta));

    // 取行一律用 FirstOrDefault + 属性模式匹配：
    // 万一对比表算空了，应该只是这几条断言红掉，而不是整个自检台崩在第一条、
    // 后面几十条全都不跑 —— 那种情况最容易被误读成「后面都没问题」。
    static PeriodGroupRow? RowOf(PeriodComparison c, Group g) =>
        c.Rows.FirstOrDefault(r => r.Group.Id == g.Id);

    static string Show(PeriodGroupRow? row) =>
        row == null
            ? "对比表里没有这个小组"
            : $"本期 {row.CurrentDelta}／上期 {row.PreviousDelta}／相比 {row.DeltaChange}";

    var row1 = RowOf(cmp, g1);
    var row2 = RowOf(cmp, g2);
    Check("第一小组：本期 +5、上期 +5、相比 0",
        row1 is { CurrentDelta: 5, PreviousDelta: 5, DeltaChange: 0 }, Show(row1));
    Check("第二小组：本期 -1、上期 +2、相比 -3",
        row2 is { CurrentDelta: -1, PreviousDelta: 2, DeltaChange: -3 }, Show(row2));
    Check("期末累计 = 上期末累计 + 本期净增（逐组都成立）",
        row1 is { CumulativeNow: 10, CumulativeBefore: 5 } &&
        row2 is { CumulativeNow: 1, CumulativeBefore: 2 },
        $"{row1?.CumulativeNow}/{row1?.CumulativeBefore}，{row2?.CumulativeNow}/{row2?.CumulativeBefore}");
    Check("截至本期结束的累计分 = 全部已有记录之和",
        PeriodStats.CumulativeByGroup(records, thisWeek.EndExclusive).Values.Sum() == 11);
    Check("本期得分降序：第一小组在前",
        cmp.Rows.Count == 2 && cmp.Rows[0].Group.Id == g1.Id, $"行数 {cmp.Rows.Count}");
    Check("本期得分名次：第一小组 1、第二小组 2",
        row1 is { PeriodRank: 1 } && row2 is { PeriodRank: 2 },
        $"{row1?.PeriodRank}/{row2?.PeriodRank}");

    // ---- 16.5 反超：第二小组本周爆发，累计名次换位 ----
    var bursting = new List<ScoreRecord>(records)
    {
        new() { GroupId = g2.Id, Delta = 20, CreatedAt = new DateTime(2026, 10, 16, 10, 0, 0) }
    };
    var afterBurst = PeriodStats.Compare(twoGroups, bursting, thisWeek, lastWeek);
    var burst1 = RowOf(afterBurst, g1);
    var burst2 = RowOf(afterBurst, g2);

    Check("反超后本期第一名换成了第二小组",
        afterBurst.Rows.Count == 2 && afterBurst.Rows[0].Group.Id == g2.Id, $"行数 {afterBurst.Rows.Count}");
    Check("累计名次：第一小组从第 1 掉到第 2",
        burst1 is { PreviousCumulativeRank: 1, CurrentCumulativeRank: 2 },
        $"{burst1?.PreviousCumulativeRank}→{burst1?.CurrentCumulativeRank}");
    Check("累计名次：第二小组从第 2 升到第 1",
        burst2 is { PreviousCumulativeRank: 2, CurrentCumulativeRank: 1 },
        $"{burst2?.PreviousCumulativeRank}→{burst2?.CurrentCumulativeRank}");
    Check("名次变化的符号约定：正数 = 前进",
        burst2 is { RankChange: 1 } && burst1 is { RankChange: -1 },
        $"{burst1?.RankChange}/{burst2?.RankChange}");
    Check("箭头方向画对了（↑ 前进 / ↓ 后退）",
        burst2?.RankChangeText == "↑1" && burst1?.RankChangeText == "↓1",
        $"{burst2?.RankChangeText} / {burst1?.RankChangeText}");
    Check("相比为 0 时显示破折号而不是 +0",
        burst1?.DeltaChangeText == "—", burst1?.DeltaChangeText ?? "对比表里没有这个小组");

    // ---- 16.6 并列名次（与排行榜同一套规则）----
    var g3 = new Group { Name = "第三小组", SortOrder = 2 };
    var g4 = new Group { Name = "第四小组", SortOrder = 3 };
    var tieGroups = new List<Group> { g1, g3, g4 };
    var tieRecords = new List<ScoreRecord>
    {
        new() { GroupId = g1.Id, Delta = 3, CreatedAt = new DateTime(2026, 10, 13, 9, 0, 0) },
        new() { GroupId = g3.Id, Delta = 3, CreatedAt = new DateTime(2026, 10, 13, 9, 0, 0) },
        new() { GroupId = g4.Id, Delta = 1, CreatedAt = new DateTime(2026, 10, 13, 9, 0, 0) }
    };
    var tieCmp = PeriodStats.Compare(tieGroups, tieRecords, thisWeek, lastWeek);
    Check("并列名次是 1、1、3（不是 1、2、3，也不是 1、1、2）",
        tieCmp.Rows.Select(r => r.PeriodRank).SequenceEqual(new[] { 1, 1, 3 }),
        string.Join(",", tieCmp.Rows.Select(r => r.PeriodRank)));

    // ---- 16.7 已删除小组的记录不会污染合计 ----
    var ghostRecords = new List<ScoreRecord>(records)
    {
        new() { GroupId = "已删除的小组", Delta = 99, CreatedAt = new DateTime(2026, 10, 14, 9, 0, 0) }
    };
    var ghostCmp = PeriodStats.Compare(twoGroups, ghostRecords, thisWeek, lastWeek);
    Check("已删除小组的 99 分不进合计、也不占一行",
        ghostCmp.CurrentTotal == 4 && ghostCmp.Rows.Count == 2, ghostCmp.CurrentTotal.ToString());
    Check("已删除小组的记录也不计入「本期几条记录」",
        ghostCmp.CurrentRecordCount == 3, ghostCmp.CurrentRecordCount.ToString());

    // ---- 16.8 区间与符号的文案 ----
    Check("「本周」显示成 10-12 ~ 10-18（末日记的是最后一天，不是结束时刻 10-19）",
        PeriodStats.FormatRange(thisWeek) == "10-12 ~ 10-18", PeriodStats.FormatRange(thisWeek));
    Check("「上月」显示成 09-01 ~ 09-30",
        PeriodStats.FormatRange(lastMonth) == "09-01 ~ 09-30", PeriodStats.FormatRange(lastMonth));
    Check("周期标题：本周 / 上周 / 本月 / 上月",
        PeriodStats.TitleOf(PeriodKind.Week, true) == "本周" &&
        PeriodStats.TitleOf(PeriodKind.Week, false) == "上周" &&
        PeriodStats.TitleOf(PeriodKind.Month, true) == "本月" &&
        PeriodStats.TitleOf(PeriodKind.Month, false) == "上月");
    Check("带符号格式化：正数带 +、负数带 -、0 显示 +0",
        PeriodGroupRow.FormatSigned(3) == "+3" &&
        PeriodGroupRow.FormatSigned(-2) == "-2" &&
        PeriodGroupRow.FormatSigned(0) == "+0");

    // ---- 16.9 走一遍真路径（ScoreService）----
    // 注意这里**故意不传固定的观测时刻**：喂给它的记录用的是 DateTime.Now，
    // 观测点也就必须用真实的现在，两边才一定落在同一个周期里。
    // （拿固定时刻去对真实记录，过几天再跑就会红 —— 那是测试的错，不是代码的错。）
    var periodDir = TempDir();
    var periodSvc = new ScoreService(new ScoreStore(periodDir));
    var before = periodSvc.GetPeriodComparison(PeriodKind.Week, 1);
    Check("ScoreService 能直接产出对比：行数与小组数一致",
        before.Rows.Count == periodSvc.Groups.Count, $"{before.Rows.Count}/{periodSvc.Groups.Count}");
    Check("空库的本期净增是 0", before.CurrentTotal == 0 && before.CurrentRecordCount == 0);

    var target = periodSvc.Groups[0];
    periodSvc.AddScore(target.Id, 7, "测试");
    var after = periodSvc.GetPeriodComparison(PeriodKind.Week, 1);
    var targetRow = after.Rows.FirstOrDefault(r => r.Group.Id == target.Id);
    Check("刚加的分立刻出现在本期的对比里",
        targetRow is { CurrentDelta: 7 }, $"本期 {targetRow?.CurrentDelta}");
    Check("加了分之后「期末累计」也跟着涨（累计是真算出来的，不是抄 CachedTotal 的旧值）",
        targetRow is { CumulativeNow: 7 }, $"累计 {targetRow?.CumulativeNow}");

    // ---- 16.10 换周起始 = 换了「怎么切」，不是换了数据 ----
    // 10-11 那条记录（周日）：周一为起点时它落在上周，周日为起点时它落在本周。
    var sundayOnly = new List<ScoreRecord>
    {
        new() { GroupId = g1.Id, Delta = 9, CreatedAt = new DateTime(2026, 10, 11, 12, 0, 0) }
    };
    var mondayRule = PeriodStats.Compare(twoGroups, sundayOnly,
        PeriodStats.Of(now, PeriodKind.Week, 1), PeriodStats.PreviousOf(now, PeriodKind.Week, 1));
    var sundayRule = PeriodStats.Compare(twoGroups, sundayOnly,
        PeriodStats.Of(now, PeriodKind.Week, 0), PeriodStats.PreviousOf(now, PeriodKind.Week, 0));

    Check("周日 10-11 的记录：按「周一为起点」算属于上周",
        mondayRule.CurrentTotal == 0 && mondayRule.PreviousTotal == 9);
    Check("同一条记录：按「周日为起点」算属于本周",
        sundayRule.CurrentTotal == 9 && sundayRule.PreviousTotal == 0);
    var mondayRow = RowOf(mondayRule, g1);
    var sundayRow = RowOf(sundayRule, g1);
    Check("两种口径下期末累计完全相同（改的只是怎么看，数据一分没动）",
        mondayRow != null && mondayRow.CumulativeNow == sundayRow?.CumulativeNow,
        $"{mondayRow?.CumulativeNow} / {sundayRow?.CumulativeNow}");
}

Console.WriteLine("=== 17. 存储后端：SQLite 落盘、一次性迁移与回落 ===");
{
    // 这一组测的是整个第 6 步：把落盘从 JSON 换成 SQLite。
    // 最要命的特征是**失败是静默的** —— 原生库加载不了、包没复制进插件目录、
    // 后端选择的分支写错，插件都会一声不吭地退回 JSON 继续跑，
    // 功能全对、测试全绿，但 SQLite 一天都没用上。
    // 所以这里第一条就是正面钉住「到底用了哪个后端」，不许靠「测试都过了」去反推。

    // ---- 17.1 全新目录必须真的选中 SQLite ----
    var dir = TempDir();
    var store = new ScoreStore(dir);
    Check("全新目录选中的后端是 SQLite", store.BackendName == "SQLite",
        $"{store.BackendName}｜{store.StorageNote}");
    Check("原生库已成功加载进进程", SqliteNativeLoader.IsLoaded, SqliteNativeLoader.Detail);
    Check("数据文件是 score-data.db",
        Path.GetFileName(store.FilePath) == ScorePersistenceSelector.DbFileName, store.FilePath);
    Check("建库时文件已落地（Load 会建表）", SqliteScorePersistence.HasUsableDb(store.FilePath));
    Check("全新库没有任何存储提示（没有旧数据要搬，也没出错）", store.StorageNote == "", store.StorageNote);

    // ---- 17.2 原生库候选路径的顺序（纯字符串逻辑，不碰磁盘）----
    var paths = SqliteNativeLoader.CandidatePaths(@"C:\plugin", @"C:\host");
    Check("候选路径第一位是平铺在插件根目录的那份",
        paths.Count > 0 && paths[0] == Path.Combine(@"C:\plugin", "e_sqlite3.dll"),
        paths.Count > 0 ? paths[0] : "(空)");
    Check("候选路径第二位是 NuGet 默认的 runtimes 布局",
        paths.Count > 1 &&
        paths[1] == Path.Combine(@"C:\plugin", "runtimes", "win-x64", "native", "e_sqlite3.dll"),
        paths.Count > 1 ? paths[1] : "(空)");
    Check("宿主目录被去重（F5 调试时插件目录与宿主目录是同一个）",
        SqliteNativeLoader.CandidatePaths(@"C:\same", @"C:\same").Count == 2);
    Check("候选路径全是绝对路径（不会退化成靠当前目录碰运气）",
        SqliteNativeLoader.CandidatePaths(@"C:\plugin", null).All(Path.IsPathFullyQualified));
    Check("插件目录为空时只剩宿主那一项",
        SqliteNativeLoader.CandidatePaths(null, @"C:\host").Count == 1);
    Check("两个目录都拿不到时返回空列表而不是抛异常",
        SqliteNativeLoader.CandidatePaths(null, null).Count == 0);

    var missing = SqliteNativeLoader.TryLoad(Path.Combine(dir, "nope"), Path.Combine(dir, "nope2"));
    Check("找不到原生库时返回失败而不是抛异常", !missing.Loaded);
    Check("失败说明里写清了试过哪些路径，便于现场排查",
        missing.Detail.Contains("未能加载") && missing.Detail.Contains("文件不存在"), missing.Detail);

    // ---- 17.3 时间格式必须往返无损 ----
    // 记录的时间参与「按时间段统计」，精度一丢，同一秒内的多条记录在历史对比里排序就不稳。
    var moment = new DateTime(2026, 10, 16, 23, 13, 30, 123, DateTimeKind.Local).AddTicks(4567);
    var roundTrip = SqliteScorePersistence.FromDb(SqliteScorePersistence.ToDb(moment));
    Check("时间往返到刻度级不丢精度", roundTrip == moment, $"{roundTrip:O}");
    Check("时间往返保持 Kind（本地时间不会变成未指定）", roundTrip.Kind == moment.Kind,
        roundTrip.Kind.ToString());

    // ---- 17.4 增量写判定（纯函数，每种组合都跑一遍）----
    // 判断错了不会抛异常，只会让某些记录悄悄写不进去 ——
    // 等老师发现分数不对时，往往已经过去好几节课了。所以这里必须逐种组合验。
    var abc = new[] { "A", "B", "C" };
    Check("库里空、内存也空 -> 什么都不用写",
        RecordWriteDecision.Decide(0, null, Array.Empty<string>()) is { Mode: RecordWriteMode.None });
    Check("库里空、内存 3 条 -> 全部当新的追加",
        RecordWriteDecision.Decide(0, null, abc) is { Mode: RecordWriteMode.Tail, KeepCount: 0 });
    Check("库里 3 条、内存一模一样 -> 一条都不用写",
        RecordWriteDecision.Decide(3, "C", abc) is { Mode: RecordWriteMode.None });
    Check("库里 3 条、内存追加到 5 条 -> 只追加后 2 条",
        RecordWriteDecision.Decide(3, "C", new[] { "A", "B", "C", "D", "E" })
            is { Mode: RecordWriteMode.Tail, KeepCount: 3 });
    Check("内存比库少（撤销过）-> 整表重写",
        RecordWriteDecision.Decide(3, "C", new[] { "A", "B" }) is { Mode: RecordWriteMode.Full });
    Check("数量没变但最后一条对不上（内容被改过）-> 整表重写",
        RecordWriteDecision.Decide(3, "C", new[] { "A", "B", "X" }) is { Mode: RecordWriteMode.Full });
    Check("追加时前缀对不上（中间被动过）-> 整表重写",
        RecordWriteDecision.Decide(3, "C", new[] { "A", "B", "X", "D" }) is { Mode: RecordWriteMode.Full });
    Check("数量对得上但没有可比对的基准 -> 退到整表重写",
        RecordWriteDecision.Decide(3, null, abc) is { Mode: RecordWriteMode.Full });
    Check("内存被清空、库里有货 -> 整表重写（也就等于清空）",
        RecordWriteDecision.Decide(3, "C", Array.Empty<string>()) is { Mode: RecordWriteMode.Full });

    // ---- 17.5 真的写进库、真的读得回来 ----
    var svc = new ScoreService(store);
    var first = svc.Groups[0];
    var zhang = svc.AddMember(first.Id, "张三");
    svc.AddMember(first.Id, "李四");
    svc.AddScore(first.Id, 5, "中文原因：回答正确");
    svc.AddScore(first.Id, -2, "迟到");
    svc.AddScore(first.Id, 3, "小组合作", null, zhang.Id);
    svc.AddPreset("举手发言", 2);
    store.SaveNow();

    var reloaded = new ScoreStore(dir);
    Check("重载后仍然是 SQLite", reloaded.BackendName == "SQLite", reloaded.BackendName);
    Check("重载：小组数一致", reloaded.Data.Groups.Count == store.Data.Groups.Count,
        $"{reloaded.Data.Groups.Count}/{store.Data.Groups.Count}");
    Check("重载：成员数一致", reloaded.Data.Members.Count == store.Data.Members.Count,
        $"{reloaded.Data.Members.Count}/{store.Data.Members.Count}");
    Check("重载：记录数一致", reloaded.Data.ScoreRecords.Count == store.Data.ScoreRecords.Count,
        $"{reloaded.Data.ScoreRecords.Count}/{store.Data.ScoreRecords.Count}");
    Check("重载：预设数一致", reloaded.Data.Presets.Count == store.Data.Presets.Count,
        $"{reloaded.Data.Presets.Count}/{store.Data.Presets.Count}");
    Check("重载：中文小组名保留", reloaded.Data.Groups.Any(g => g.Name == first.Name));
    Check("重载：中文成员名保留", reloaded.Data.Members.Any(m => m.Name == "李四"));
    Check("重载：中文原因保留", reloaded.Data.ScoreRecords.Any(r => r.Reason == "中文原因：回答正确"));
    Check("重载：负分保留（不是被当成无符号数读回来）",
        reloaded.Data.ScoreRecords.Count(r => r.Delta == -2) == 1);
    Check("重载：指名到成员的记录 MemberId 保留",
        reloaded.Data.ScoreRecords.Any(r => r.MemberId == zhang.Id));
    Check("重载：按组记的那条记录 MemberId 仍是 null（没被写成空字符串）",
        reloaded.Data.ScoreRecords.Count(r => r.MemberId == null) ==
        store.Data.ScoreRecords.Count(r => r.MemberId == null));
    Check("重载：总分缓存一致",
        reloaded.Data.Groups.First(g => g.Id == first.Id).CachedTotal == first.CachedTotal,
        $"{reloaded.Data.Groups.First(g => g.Id == first.Id).CachedTotal}/{first.CachedTotal}");
    Check("重载：记录顺序逐条一致（列表顺序 = 时间顺序这条契约没被数据库打乱）",
        reloaded.Data.ScoreRecords.Select(r => r.Id).SequenceEqual(store.Data.ScoreRecords.Select(r => r.Id)));
    Check("重载：预设顺序一致",
        reloaded.Data.Presets.Select(p => p.Id).SequenceEqual(store.Data.Presets.Select(p => p.Id)));
    Check("重载：成员顺序一致",
        reloaded.Data.Members.Select(m => m.Id).SequenceEqual(store.Data.Members.Select(m => m.Id)));
    Check("重载：恒等式仍成立（成员分之和 + 未指派 == 组总分）",
        new ScoreService(reloaded).CheckGroupBalance(first.Id));
    Check("重载：时间戳精确到刻度",
        reloaded.Data.ScoreRecords.All(r => store.Data.ScoreRecords.Any(
            o => o.Id == r.Id && o.CreatedAt == r.CreatedAt)));

    // ---- 17.6 增量落盘真的在走增量（而不是每次都整表重写）----
    // 这一条看的是数据库内部的自增序号：整表重写会把 seq 顶到很高，
    // 只有「真的只追加了一条」才会让 max(seq) 刚好只涨 1。
    long MaxSeq(string path)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            SqliteScorePersistence.BuildConnectionString(path));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select coalesce(max(seq), 0) from score_records";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    var incDir = TempDir();
    var incPath = Path.Combine(incDir, "inc.db");
    var inc = new SqliteScorePersistence(incPath);
    var incData = new ScoreDatabase();
    var incGroup = new Group { Name = "甲组" };
    incData.Groups.Add(incGroup);
    for (var i = 0; i < 3; i++)
        incData.ScoreRecords.Add(new ScoreRecord { GroupId = incGroup.Id, Delta = 1, Reason = $"第{i}条" });

    inc.Save(incData);
    Check("首次落盘写入 3 条记录",
        inc.StoredRecordCount == 3 && SqliteScorePersistence.CountRecords(incPath) == 3,
        $"{inc.StoredRecordCount}/{SqliteScorePersistence.CountRecords(incPath)}");

    inc.Save(incData);
    Check("内容没变时库里不会多出记录", SqliteScorePersistence.CountRecords(incPath) == 3);
    Check("内容没变时走的是「什么都不写」，没有把整表推倒重来",
        MaxSeq(incPath) == 3, $"max(seq)={MaxSeq(incPath)}（整表重写会顶到 7 以上）");

    incData.ScoreRecords.Add(new ScoreRecord { GroupId = incGroup.Id, Delta = 7, Reason = "新增" });
    inc.Save(incData);
    Check("追加一条后库里变成 4 条", SqliteScorePersistence.CountRecords(incPath) == 4);
    Check("追加走的是尾部增量：自增序号只涨 1",
        MaxSeq(incPath) == 4, $"max(seq)={MaxSeq(incPath)}");
    Check("增量写之后内部记忆同步更新", inc.StoredRecordCount == 4 && inc.StoredLastRecordId == incData.ScoreRecords[^1].Id);

    incData.ScoreRecords.Add(new ScoreRecord { GroupId = incGroup.Id, Delta = 1, Reason = "又一条" });
    inc.Save(incData);
    Check("连续追加仍然只涨 1（两次增量各自只写自己那条）",
        MaxSeq(incPath) == 5, $"max(seq)={MaxSeq(incPath)}");

    // 撤销 = 删最后一条 -> 数量对不上，退到整表重写，内容仍须完全正确
    incData.ScoreRecords.RemoveAt(incData.ScoreRecords.Count - 1);
    inc.Save(incData);
    Check("撤销后库里回到 4 条", SqliteScorePersistence.CountRecords(incPath) == 4);
    Check("撤销后读回来的记录与内存逐条一致",
        inc.Load().ScoreRecords.Select(r => r.Id).SequenceEqual(incData.ScoreRecords.Select(r => r.Id)));

    // ---- 17.7 一次性迁移：老用户的 JSON 必须原样搬过去，且只搬一次 ----
    var legacyDir = TempDir();
    var legacyPath = Path.Combine(legacyDir, ScorePersistenceSelector.LegacyJsonFileName);
    var legacyData = new ScoreDatabase { Seeded = true };
    var legacyGroup = new Group { Name = "迁移过来的组", Color = "#FF0000", CachedTotal = 9 };
    legacyData.Groups.Add(legacyGroup);
    legacyData.Members.Add(new Member { Id = "m1", GroupId = legacyGroup.Id, Name = "王五" });
    legacyData.ScoreRecords.Add(new ScoreRecord
    {
        Id = "r1", GroupId = legacyGroup.Id, MemberId = "m1",
        Delta = 9, Reason = "老数据", CreatedAt = new DateTime(2026, 10, 1, 8, 0, 0)
    });
    legacyData.Presets.Add(new ScorePreset { Id = "p1", Label = "老预设", Delta = 1 });
    JsonStore.Save(legacyPath, legacyData);

    var migrated = new ScoreStore(legacyDir);
    Check("有旧 JSON 时仍然选中 SQLite（不是继续用 JSON）",
        migrated.BackendName == "SQLite", migrated.StorageNote);
    Check("迁移提示里说明了搬过数据", migrated.StorageNote.Contains("迁移"), migrated.StorageNote);
    Check("迁移后小组名一致",
        migrated.Data.Groups.Count == 1 && migrated.Data.Groups[0].Name == "迁移过来的组");
    Check("迁移后成员名一致",
        migrated.Data.Members.Count == 1 && migrated.Data.Members[0].Name == "王五");
    Check("迁移后预设一致",
        migrated.Data.Presets.Count == 1 && migrated.Data.Presets[0].Label == "老预设");
    Check("迁移后总分缓存跟着搬过来（不是从记录重算成 0）",
        migrated.Data.Groups[0].CachedTotal == 9, $"{migrated.Data.Groups[0].CachedTotal}");
    Check("迁移后记录的时间戳精确保留",
        migrated.Data.ScoreRecords.Count == 1 &&
        migrated.Data.ScoreRecords[0].CreatedAt == new DateTime(2026, 10, 1, 8, 0, 0));
    Check("旧 JSON 已被改名留档（是留档，不是删除）", !File.Exists(legacyPath));
    Check("留档文件确实在（老师想回退还有救）",
        Directory.GetFiles(legacyDir, "*" + ScorePersistenceSelector.LegacyJsonFileName + ".migrated-*.bak").Length == 1,
        string.Join("｜", Directory.GetFiles(legacyDir).Select(Path.GetFileName)));
    Check("db 文件已生成", File.Exists(migrated.FilePath), migrated.FilePath);

    // 再启动一次：必须是「从库里真正读回来」，而且不能再迁移一遍
    var again = new ScoreStore(legacyDir);
    Check("再次启动不再迁移（提示为空）", again.StorageNote == "", again.StorageNote);
    Check("再次启动能真正读回库里的数据（证明上次确实写进库了，不是只改了内存）",
        again.Data.ScoreRecords.Count == 1 && again.Data.Groups[0].Name == "迁移过来的组",
        $"{again.Data.ScoreRecords.Count} 条");
    Check("再次启动数据没有翻倍", again.Data.ScoreRecords.Count == 1, $"{again.Data.ScoreRecords.Count}");
    Check("再次启动成员与预设也都在",
        again.Data.Members.Count == 1 && again.Data.Presets.Count == 1);

    // ---- 17.8 库里已经有记分时，绝不能被「回来的旧 JSON」覆盖 ----
    // 场景：用户先用上新版记了几天分，之后又把旧 JSON 拷回数据目录。
    // 这时如果无脑迁移，老师好几天的记分会被一份过期文件冲掉。
    var guardDir = TempDir();
    var guardStore = new ScoreStore(guardDir);
    var guardSvc = new ScoreService(guardStore);
    guardSvc.AddScore(guardSvc.Groups[0].Id, 20, "库里已有的分");
    guardStore.SaveNow();
    var guardCount = guardStore.Data.ScoreRecords.Count;

    var intruder = new ScoreDatabase { Seeded = true };
    intruder.Groups.Add(new Group { Name = "外来组" });
    JsonStore.Save(Path.Combine(guardDir, ScorePersistenceSelector.LegacyJsonFileName), intruder);

    var afterIntruder = new ScoreStore(guardDir);
    Check("库里已有数据时不会被回来的 JSON 覆盖",
        afterIntruder.Data.Groups.All(g => g.Name != "外来组"),
        string.Join("｜", afterIntruder.Data.Groups.Select(g => g.Name)));
    Check("记录数也没被覆盖", afterIntruder.Data.ScoreRecords.Count == guardCount,
        $"{afterIntruder.Data.ScoreRecords.Count}/{guardCount}");
    Check("那份 JSON 也原样留着没被动过",
        File.Exists(Path.Combine(guardDir, ScorePersistenceSelector.LegacyJsonFileName)));

    // ---- 17.9 回落：数据库路径不可用时，插件必须还能正常记分 ----
    // 把 score-data.db 这个**路径**占成一个目录，SQLite 就打不开了。
    // 这条链路真的会走到，比如数据目录权限变了、或者被杀毒软件锁了。
    var failDir = TempDir();
    Directory.CreateDirectory(Path.Combine(failDir, ScorePersistenceSelector.DbFileName));
    var failStore = new ScoreStore(failDir);
    Check("数据库打不开时静默回落到 JSON", failStore.BackendName == "JSON",
        $"{failStore.BackendName}｜{failStore.StorageNote}");
    Check("回落提示里说明了原因，而不是悄悄换后端",
        failStore.StorageNote.Contains("无法读取"), failStore.StorageNote);

    var failSvc = new ScoreService(failStore);
    failSvc.AddScore(failSvc.Groups[0].Id, 4, "回落之后照常用");
    failStore.SaveNow();
    Check("回落之后照常落盘：JSON 文件已生成",
        File.Exists(Path.Combine(failDir, ScorePersistenceSelector.LegacyJsonFileName)));
    Check("回落之后没有落盘错误", failStore.LastSaveError == null, failStore.LastSaveError);

    var failReload = new ScoreStore(failDir);
    Check("回落期间记的分能读回来", failReload.Data.ScoreRecords.Count == 1,
        $"{failReload.Data.ScoreRecords.Count}");

    // ---- 17.10 JSON 后端本身仍然完好（它是回落的落脚点，不能失修）----
    var jsonDir = TempDir();
    var jsonPath = Path.Combine(jsonDir, "json-only.json");
    var jsonBackend = new JsonScorePersistence(jsonPath);
    Check("JSON 后端自报家门", jsonBackend.BackendName == "JSON");
    Check("JSON 后端落盘位置就是给定路径", jsonBackend.Location == jsonPath);
    jsonBackend.Save(legacyData);
    Check("JSON 后端文件已生成", File.Exists(jsonPath));
    var rawJson = File.ReadAllText(jsonPath);
    Check("JSON 后端中文未被转义成 \\uXXXX",
        rawJson.Contains("迁移过来的组"), rawJson[..Math.Min(120, rawJson.Length)]);
    Check("JSON 后端结构含四张表",
        rawJson.Contains("\"Groups\"") && rawJson.Contains("\"Members\"") &&
        rawJson.Contains("\"ScoreRecords\"") && rawJson.Contains("\"Presets\""));
    Check("JSON 后端往返记录数一致",
        jsonBackend.Load().ScoreRecords.Count == legacyData.ScoreRecords.Count);

    // ---- 17.11 探测过程不许留下垃圾 ----
    var probeDir = TempDir();
    var probeOk = SqliteScorePersistence.Probe(probeDir, out var probeDetail);
    Check("探测 SQLite 可用", probeOk, probeDetail);
    Check("探测之后一个临时文件都不留",
        Directory.GetFiles(probeDir).Length == 0,
        string.Join("｜", Directory.GetFiles(probeDir).Select(Path.GetFileName)));
    Check("不存在的库文件被判为不可用",
        !SqliteScorePersistence.HasUsableDb(Path.Combine(probeDir, "nope.db")));
    Check("数不存在的库的记录数得到 0",
        SqliteScorePersistence.CountRecords(Path.Combine(probeDir, "nope.db")) == 0);
    Check("0 字节的库文件被判为不可用（那是上次建到一半断电的残骸）",
        !SqliteScorePersistence.HasUsableDb(MakeEmptyFile(probeDir)));
}

Console.WriteLine("=== 18. 上课时段：科目白名单 + 加分守门 ===");
{
    // ---- 18.1 科目白名单：这节课到底该不该自动弹球 ----
    var none = Array.Empty<string>();
    var chinese = new[] { "语文" };
    var two = new[] { "语文", "数学" };

    Check("18.1 总开关关掉 → 勾没勾科目都不弹",
        !LessonRules.ShouldAutoShow(false, true, chinese, "语文") &&
        !LessonRules.ShouldAutoShow(false, false, null, "语文"));
    Check("18.1 没开科目筛选 → 任何课都弹（老行为不变）",
        LessonRules.ShouldAutoShow(true, false, null, "语文") &&
        LessonRules.ShouldAutoShow(true, false, null, null));
    Check("18.1 开了筛选但一个科目都没勾 → 谁都不弹",
        !LessonRules.ShouldAutoShow(true, true, none, "语文"));
    Check("18.1 开了筛选而白名单是 null → 也不弹，且不炸",
        !LessonRules.ShouldAutoShow(true, true, null, "语文"));
    Check("18.1 勾中的科目 → 弹",
        LessonRules.ShouldAutoShow(true, true, chinese, "语文"));
    Check("18.1 没勾中的科目 → 不弹",
        !LessonRules.ShouldAutoShow(true, true, chinese, "数学"));
    Check("18.1 多选里命中任意一个就算",
        LessonRules.ShouldAutoShow(true, true, two, "数学"));
    Check("18.1 没有科目的时段（自习 / 课间 / 课表没加载）→ 不弹",
        !LessonRules.ShouldAutoShow(true, true, chinese, null) &&
        !LessonRules.ShouldAutoShow(true, true, chinese, "") &&
        !LessonRules.ShouldAutoShow(true, true, chinese, "   "));
    Check("18.1 两边首尾空格不影响判定",
        LessonRules.ShouldAutoShow(true, true, new[] { "  语文 " }, "语文") &&
        LessonRules.ShouldAutoShow(true, true, chinese, " 语文 "));
    Check("18.1 科目名大小写不敏感（英文科目名也能对上）",
        LessonRules.ShouldAutoShow(true, true, new[] { "Math" }, "math"));

    // ---- 18.2 加分守门：把「课表不可用就放行」这条兜底一起钉死 ----
    Check("18.2 没开限制 → 任何时候都能加分",
        LessonRules.CanAddScore(false, true, false) &&
        LessonRules.CanAddScore(false, false, false));
    Check("18.2 开了限制 + 课表可用 + 正在上课 → 允许",
        LessonRules.CanAddScore(true, true, true));
    Check("18.2 开了限制 + 课表可用 + 不在上课 → 拒绝",
        !LessonRules.CanAddScore(true, true, false));
    Check("18.2 开了限制但课表不可用 → 放行（否则没配课表的老师永远加不了分）",
        LessonRules.CanAddScore(true, false, false) &&
        LessonRules.CanAddScore(true, false, true));
    Check("18.2 八种组合里只有「限制开 + 课表可用 + 不在上课」会被拒",
        new[] { (false, false, false), (false, true, false), (true, false, false), (true, true, true) }
            .All(c => LessonRules.CanAddScore(c.Item1, c.Item2, c.Item3)) &&
        !LessonRules.CanAddScore(true, true, false));
    Check("18.2 被拒时有原因、放行时为 null",
        LessonRules.ScoreBlockedReason(true, true, false) != null &&
        LessonRules.ScoreBlockedReason(true, true, true) == null &&
        LessonRules.ScoreBlockedReason(false, false, false) == null);
    Check("18.2 四种状态的说明文字互不相同（防复制粘贴时写错分支）",
        new[]
        {
            LessonRules.DescribeScoreAccess(false, true, false),
            LessonRules.DescribeScoreAccess(true, false, false),
            LessonRules.DescribeScoreAccess(true, true, true),
            LessonRules.DescribeScoreAccess(true, true, false)
        }.Distinct().Count() == 4);

    // ---- 18.3 快照类型：判断能力跟着数据走 ----
    var blockedSnap = new ScoreAccessSnapshot(true, true, false);
    var allowedSnap = new ScoreAccessSnapshot(true, true, true);
    Check("18.3 快照自己能算出允不允许",
        !blockedSnap.AllowsScore && allowedSnap.AllowsScore);
    Check("18.3 快照自己能给出拒绝原因",
        blockedSnap.BlockedReason != null && allowedSnap.BlockedReason == null);

    // ---- 18.4 端到端：走 ScoreService 那个唯一入口真拦一遍 ----
    RunSection184();
}

// 端到端那一段单独放一个局部函数，是为了能在一处提前 return ——
// 种子数据万一为空，后面几十条断言会因为空引用把整个进程带崩，
// 那就看不出「到底是哪一条坏了」了。
void RunSection184()
{
    var dir = TempDir();
    var store = new ScoreStore(dir);
    var svc = new ScoreService(store);

    var group = store.Data.Groups.FirstOrDefault();
    Check("18.4 种子数据里有小组可测", group != null);
    if (group == null) return;

    var totalBefore = group.CachedTotal;
    var recordsBefore = store.Data.ScoreRecords.Count;

    var blockedEvents = new List<string>();
    svc.ScoreBlocked += (_, reason) => blockedEvents.Add(reason);

    // 课间：限制开着、课表可用、不在上课
    svc.SetAccessProvider(() => new ScoreAccessSnapshot(true, true, false));
    Check("18.4 不在上课时间 → 加分被拒（返回 false）",
        !svc.AddScore(group.Id, 5, "课间试加"));
    Check("18.4 被拒时一条记录都没写",
        store.Data.ScoreRecords.Count == recordsBefore);
    Check("18.4 被拒时小组总分一分没动",
        group.CachedTotal == totalBefore);
    Check("18.4 被拒时记下了原因",
        !string.IsNullOrEmpty(svc.LastBlockedReason));
    Check("18.4 被拒时发出了 ScoreBlocked（界面靠它告诉老师为什么）",
        blockedEvents.Count == 1 && !string.IsNullOrEmpty(blockedEvents[0]));
    Check("18.4 被拒之后 CanAddScoreNow 是 false", !svc.CanAddScoreNow);

    // 上课了
    svc.SetAccessProvider(() => new ScoreAccessSnapshot(true, true, true));
    Check("18.4 上课时间 → 放行", svc.AddScore(group.Id, 5, "课上加分"));
    Check("18.4 放行后记录 +1、总分 +5",
        store.Data.ScoreRecords.Count == recordsBefore + 1 &&
        group.CachedTotal == totalBefore + 5);
    Check("18.4 放行后 LastBlockedReason 清空", svc.LastBlockedReason == null);
    Check("18.4 放行后 CanAddScoreNow 是 true", svc.CanAddScoreNow);

    // 「课表不可用就放行」那条兜底，在真服务上再走一遍
    svc.SetAccessProvider(() => new ScoreAccessSnapshot(true, false, false));
    Check("18.4 课表不可用 → 照常能加分（插件不会变成点不动的砖头）",
        svc.AddScore(group.Id, 1, "课表没配时的加分"));

    // 没注入提供者那条路（自检台本身、以及宿主里取不到课程服务）
    var plain = new ScoreService(new ScoreStore(TempDir()));
    var plainGroup = plain.Data.Groups.FirstOrDefault();
    Check("18.4 没注入时段提供者 → 完全不限制",
        plain.Access == null && plain.CanAddScoreNow &&
        plainGroup != null && plain.AddScore(plainGroup.Id, 3, "无提供者"));

    // 提供者抛异常那条路
    plain.SetAccessProvider(() => throw new InvalidOperationException("模拟宿主服务崩了"));
    Check("18.4 时段提供者抛异常 → 当作不限制，加分不受影响",
        plain.CanAddScoreNow && plainGroup != null && plain.AddScore(plainGroup.Id, 2, "提供者崩了"));

    Check("18.4 CanAddScoreNow 与 Access.AllowsScore 始终一致",
        svc.CanAddScoreNow == (svc.Access?.AllowsScore ?? true));

    Check("18.4 设置里那个开关默认是开的",
        new PluginSettings().RestrictScoreToClassTime);
    Check("18.4 白名单被塞进 null 也会变成空列表，不能留成 null",
        new PluginSettings { BallSubjects = null! }.BallSubjects != null);
}

// ---- 18.5 静态检查：加分调用点必须看返回值 ----
{
    // 被时段挡下时 AddScore 返回 false 且**不写任何数据**。
    // 所以任何「不看返回值、照样显示已记」的调用点都是在骗老师。
    // 这条检查把这一点钉死，将来新增调用点也跑不掉。
    var root = FindPluginRoot();
    var offenders = new List<string>();

    if (root != null)
    {
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = file.Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;

            var lines = StripLineComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".AddScore(", StringComparison.Ordinal)) continue;
                // 调用点必须是「条件判断 + 提前收工」的形式
                if (lines[i].Contains("if (!", StringComparison.Ordinal)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
    }

    Check("18.5 每个加分调用点都检查了返回值（被拒时不要谎报成功）",
        offenders.Count == 0, string.Join(", ", offenders));
}

Console.WriteLine("=== 19. 加分面板样式与平铺行尾按钮 ===");
{
    // ---- 19.1 样式归一化 ----
    // 存的是字符串（要落 JSON），所以「认不出的值往哪落」必须有唯一答案：
    // 落不好就是「两个区域都不显示」的空面板，老师只会觉得插件坏了。
    Check("19.1 合法样式原样返回（返回的是规范写法，不是调用方传进来的原文）",
        ScorePanelStyles.Normalize(ScorePanelStyles.Picker) == ScorePanelStyles.Picker &&
        ScorePanelStyles.Normalize(ScorePanelStyles.Grid) == ScorePanelStyles.Grid);

    Check("19.1 大小写不敏感（手改配置写 grid / GRID 都认）",
        ScorePanelStyles.Normalize("grid") == ScorePanelStyles.Grid &&
        ScorePanelStyles.Normalize("GRID") == ScorePanelStyles.Grid &&
        ScorePanelStyles.IsGrid("gRiD"));

    var badStyles = new string?[] { null, "", "  ", "Gridd", "0", "全部展开", "Picker " };
    var badNormalized = badStyles
        .Select(s => ScorePanelStyles.Normalize(s))
        .ToList();
    Check("19.1 认不出的值一律回落默认样式（不会落进两个区域都不显示的空面板）",
        badNormalized.All(v => v == ScorePanelStyles.Default),
        string.Join(" / ", badStyles.Select((s, i) => $"{s ?? "<null>"}→{badNormalized[i]}")));

    Check("19.1 取值清单是第一项为默认、且没有重复项",
        ScorePanelStyles.All.Length > 0 &&
        ScorePanelStyles.Default == ScorePanelStyles.All[0] &&
        ScorePanelStyles.All.Distinct().Count() == ScorePanelStyles.All.Length,
        string.Join(", ", ScorePanelStyles.All));

    Check("19.1 默认是「逐个选择」（老用户升级上来，界面行为不变）",
        ScorePanelStyles.Default == ScorePanelStyles.Picker && !ScorePanelStyles.IsGrid(null));

    // ---- 19.2 平铺行尾按钮 ----
    var fallback = QuickRows.BuildButtons(new List<ScorePreset>());
    Check("19.2 一条预设都没有时用 ±1 兜底（刚装好插件也有得点，不是一排空行）",
        fallback.Count == 2 && fallback[0].Delta == 1 && fallback[0].IsIncrease &&
        fallback[1].Delta == -1 && !fallback[1].IsIncrease,
        string.Join(" / ", fallback.Select(b => b.Text)));

    Check("19.2 传 null 也走兜底，不抛异常",
        QuickRows.BuildButtons(null).Count == 2);

    // 顺序按传入的顺序（也就是 ScoreService.Presets 的顺序），这里故意打乱再验证映射。
    var presets = new List<ScorePreset>
    {
        new() { Label = "回答正确", Delta = 2, SortOrder = 0 },
        new() { Label = "小组合作", Delta = 1, SortOrder = 1 },
        new() { Label = "违纪", Delta = -1, SortOrder = 2 }
    };
    var rows = QuickRows.BuildButtons(presets);

    Check("19.2 有预设时逐条对应，数量一致",
        rows.Count == presets.Count, $"{rows.Count} / {presets.Count}");

    // 按钮上只有数字，整句预设名放不下 —— 340 宽的面板一行要挤下三四个按钮。
    Check("19.2 按钮上用的是短标签（+2 / +1 / -1），不是整句预设名",
        rows.Select(b => b.Text).SequenceEqual(new[] { "+2", "+1", "-1" }),
        string.Join(" / ", rows.Select(b => b.Text)));

    Check("19.2 颜色方向跟着分值走（国内习惯：加分红、扣分绿）",
        rows.Where(b => b.Delta >= 0).All(b => b.IsIncrease) &&
        rows.Where(b => b.Delta < 0).All(b => !b.IsIncrease));

    // 短标签只有数字。不加 Tooltip 的话，老师看到一排「+2 +1 -1」根本不知道扣的是什么分。
    Check("19.2 悬停提示把原因和分值拼在一起",
        rows[0].Tooltip == "回答正确（+2）", rows[0].Tooltip);

    Check("19.2 写进记录的是预设名，不是按钮上的数字（否则期末复盘看不出为什么加分）",
        rows[0].Reason == "回答正确" && rows[2].Reason == "违纪",
        $"{rows[0].Reason} / {rows[2].Reason}");

    Check("19.2 兜底按钮的原因也说得清楚",
        fallback[0].Reason == "加 1 分" && fallback[1].Reason == "扣 1 分",
        $"{fallback[0].Reason} / {fallback[1].Reason}");

    Check("19.2 ShortLabel：正数带 +、负数自带 -、零也带 +",
        QuickRows.ShortLabel(3) == "+3" &&
        QuickRows.ShortLabel(-2) == "-2" &&
        QuickRows.ShortLabel(0) == "+0",
        $"{QuickRows.ShortLabel(3)} / {QuickRows.ShortLabel(-2)} / {QuickRows.ShortLabel(0)}");

    // BuildButtons 自己不管排序，靠 ScoreService.Presets 送进来的顺序。
    // 这条把「上游确实排过」钉住，否则界面上按钮的顺序会变成随机的。
    var presetOwner = new ScoreService(new ScoreStore(TempDir()));
    presetOwner.AddPreset("后加的", 5);
    presetOwner.AddPreset("先加的", 1);
    var orderedPresets = presetOwner.Presets;
    Check("19.2 ScoreService.Presets 已按显示顺序排好（BuildButtons 直接照搬顺序即可）",
        orderedPresets.Select(p => p.SortOrder)
                      .SequenceEqual(orderedPresets.Select(p => p.SortOrder).OrderBy(x => x)),
        string.Join(", ", orderedPresets.Select(p => p.SortOrder)));

    // ---- 19.3 设置往返 ----
    Check("19.3 新装插件的默认样式是「逐个选择」",
        new PluginSettings().ScorePanelStyle == ScorePanelStyles.Picker);

    var styleSettings = new PluginSettings { ScorePanelStyle = "grid" };
    Check("19.3 赋值时就归一化（写进来的是小写 grid，存下去的是规范标识）",
        styleSettings.ScorePanelStyle == ScorePanelStyles.Grid,
        styleSettings.ScorePanelStyle);

    var junkSettings = new PluginSettings { ScorePanelStyle = "完全不认识的词" };
    Check("19.3 赋值归一化：认不出的词回落默认",
        junkSettings.ScorePanelStyle == ScorePanelStyles.Picker,
        junkSettings.ScorePanelStyle);

    var readBack = System.Text.Json.JsonSerializer.Deserialize<PluginSettings>(
        """{"ScorePanelStyle":"Grid"}""")!;
    Check("19.3 从 JSON 读回来能拿到「全部展开」",
        readBack.ScorePanelStyle == ScorePanelStyles.Grid, readBack.ScorePanelStyle);

    var junkJson = System.Text.Json.JsonSerializer.Deserialize<PluginSettings>(
        """{"ScorePanelStyle":"?????"}""")!;
    Check("19.3 配置文件里写了乱七八糟的样式 → 回落默认，不抛异常",
        junkJson.ScorePanelStyle == ScorePanelStyles.Picker, junkJson.ScorePanelStyle);

    var missingJson = System.Text.Json.JsonSerializer.Deserialize<PluginSettings>("{}")!;
    Check("19.3 老配置文件里没有这个字段 → 用默认值（升级不炸）",
        missingJson.ScorePanelStyle == ScorePanelStyles.Picker);

    var nullJson = System.Text.Json.JsonSerializer.Deserialize<PluginSettings>(
        """{"ScorePanelStyle":null}""")!;
    Check("19.3 显式写成 null 也要能活",
        nullJson.ScorePanelStyle == ScorePanelStyles.Picker);

    // ---- 19.4 界面接线（静态体检）----
    var styleRoot = FindPluginRoot();
    if (styleRoot == null)
    {
        Check("19.4 找得到插件源码目录", false, "往上找父目录，没有同时含 GroupScorePlugin/Actions 的目录");
    }
    else
    {
        var ballXaml2 = File.Exists(Path.Combine(styleRoot, "Views", "FloatingBallWindow.axaml"))
            ? File.ReadAllText(Path.Combine(styleRoot, "Views", "FloatingBallWindow.axaml"))
            : "";
        var ballCode2 = File.Exists(Path.Combine(styleRoot, "Views", "FloatingBallWindow.axaml.cs"))
            ? StripLineComments(File.ReadAllText(Path.Combine(styleRoot, "Views", "FloatingBallWindow.axaml.cs")))
            : "";

        Check("19.4 面板上的样式切换按钮接了处理器（不然点了没反应）",
            ballXaml2.Contains("Click=\"OnToggleStyleClick\""),
            "没找到 Click=\"OnToggleStyleClick\"");

        // 切换时必须**两个区域都设显隐**。只设一个的话，切过去那一瞬间
        // 会出现「旧区域还在、新区域也冒出来」，或者干脆两边都不显示。
        Check("19.4 切换时两个区域都设了显隐",
            ballCode2.Contains("PickerArea.IsVisible") && ballCode2.Contains("GridArea.IsVisible"),
            "只找到其中一个 IsVisible 赋值");

        // 行是代码生成的，切换样式时必须重建，否则切过去是一片空白
        Check("19.4 切到「全部展开」时会重建行（不重建就是一片空白）",
            ballCode2.Contains("BuildGridRows()"),
            "没找到 BuildGridRows() 的调用");

        // 数据一变就得重建那些行，否则刚加完分，行里的数字还是旧的
        Check("19.4 数据变化时也会重建行（否则刚加完分，行里的分数还是旧的）",
            ballCode2.Contains("if (ScorePanelStyles.IsGrid(_settings.Settings.ScorePanelStyle)) BuildGridRows();"),
            "RefreshAll 里没看到按当前样式重建行的分支");

        // 面板里全是按钮和输入框。不做这个判断的话，按住按钮的瞬间就被当成拖窗口，
        // 控件再也收不到一次完整的点击 —— 表现是「按钮点不响」，很难联想到是拖动抢的。
        Check("19.4 面板拖动会避开按钮 / 下拉框 / 输入框（否则点不响）",
            ballCode2.Contains("IsInteractiveSource(e.Source)"),
            "OnPanelPointerPressed 里没看到 IsInteractiveSource 判断");

        // 展开时以球的位置为锚点，而不是按设置重算 —— 否则球会先跳回角落、面板再冒出来
        Check("19.4 展开时以球的当前位置为锚点（不然一展开球就先跳走）",
            ballCode2.Contains("_expandAnchor"),
            "没找到 _expandAnchor");
    }
}

// 收尾：把这次跑出来的临时数据目录删掉。
// 正常走到这里时删一次；真中途崩了，前面挂的 ProcessExit 兜底也会删。
CleanUpTempDirs();

Console.WriteLine();
Console.WriteLine($"================ 结果：{pass} 通过 / {fail} 失败 ================");
return fail == 0 ? 0 : 1;
