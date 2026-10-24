using GroupScorePlugin.Models;

namespace GroupScorePlugin.Services;

/// <summary>
/// 决定这次到底用哪个后端，并把旧数据搬过去。整个插件里**唯一**知道
/// 「数据存在哪、要不要迁移、失败了怎么办」的地方。
///
/// 四道关卡，任何一道过不去都静默回落到 JSON，而不是抛异常打断插件加载：
/// 1. 原生库 e_sqlite3.dll 加载成功了吗；
/// 2. 这台机器上 SQLite 真的能开库吗（临时文件上试一次）；
/// 3. 数据库文件能读出来吗（损坏 / 被占用 / 磁盘满）；
/// 4. 都过了 —— 才把 SQLite 当成正式后端。
///
/// 这个「宁可退回旧方案也不让老师打不开记分板」的策略是有意为之：
/// 教室里最坏的结局不是「存储格式老了」，而是「这节课的分没记上」。
/// </summary>
internal static class ScorePersistenceSelector
{
    /// <summary>新的数据库文件名。</summary>
    internal const string DbFileName = "score-data.db";

    /// <summary>升级前用的 JSON 文件名。迁移完成后会被改名留档。</summary>
    internal const string LegacyJsonFileName = "score-data.json";

    /// <param name="Persistence">最终选定的后端。</param>
    /// <param name="Data">从该后端读出来的数据。</param>
    /// <param name="Note">给老师看的说明：迁移了什么、或为什么退回了 JSON。正常情况为空串。</param>
    public readonly record struct Selection(IScorePersistence Persistence, ScoreDatabase Data, string Note);

    public static Selection SelectAndLoad(string folder)
    {
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, DbFileName);
        var jsonPath = Path.Combine(folder, LegacyJsonFileName);

        // 关卡 1：原生库。这是唯一一个「编译期完全看不出来」的风险点，所以先撞它。
        SqliteNativeLoader.EnsureLoaded();
        if (!SqliteNativeLoader.IsLoaded)
            return Fallback(jsonPath, dbPath,
                "SQLite 原生库加载失败，本次已改用 JSON 存储。" +
                "原数据未受影响。诊断信息：" + SqliteNativeLoader.Detail);

        // 关卡 2：临时文件上真开一次库，确认这台机器上 SQLite 确实能用。
        if (!SqliteScorePersistence.Probe(folder, out var probeDetail))
            return Fallback(jsonPath, dbPath,
                $"SQLite 无法使用（{probeDetail}），本次已改用 JSON 存储。原数据未受影响。");

        var sqlite = new SqliteScorePersistence(dbPath);

        // 关卡 3：真的去读库。读不动就回落。
        try
        {
            var dbReady = SqliteScorePersistence.HasUsableDb(dbPath);
            var jsonExists = File.Exists(jsonPath);

            // 什么情况下该迁移？
            // - 数据库还没建起来（老用户第一次装上新版）；
            // - 或者数据库建起来了但一条记录都没有，而旧 JSON 还在 ——
            //   这一条覆盖「先用上了新版、后来才把旧 JSON 拷回数据目录」的情形。
            // 反过来，只要库里已经有记录，就绝不再碰 JSON，避免覆盖老师后来的记分。
            if (jsonExists && (!dbReady || SqliteScorePersistence.CountRecords(dbPath) == 0))
            {
                var legacy = JsonStore.Load<ScoreDatabase>(jsonPath);

                // Save 内部是一个事务：要么整份进去，要么一条都不进。
                // 即使这一步中途失败，旧 JSON 也还原封不动躺在原处。
                sqlite.Save(legacy);

                var archived = TryArchive(jsonPath);
                var note =
                    $"已把原来的 JSON 数据迁移到 SQLite（{legacy.Groups.Count} 个小组、" +
                    $"{legacy.Members.Count} 位成员、{legacy.ScoreRecords.Count} 条积分记录）。" +
                    (archived is null
                        ? "原 JSON 文件这次没能改名（可能正被其它程序占用），但数据已经在数据库里了；" +
                          "下次启动不会再重复迁移。"
                        : $"原文件已留档为 {Path.GetFileName(archived)}，确认无误后可自行删除。");

                return new Selection(sqlite, legacy, note);
            }

            return new Selection(sqlite, sqlite.Load(), "");
        }
        catch (Exception ex)
        {
            return Fallback(jsonPath, dbPath,
                $"SQLite 数据文件无法读取（{ex.GetType().Name}：{ex.Message}），本次已改用 JSON 存储。");
        }
    }

    /// <summary>
    /// 退回 JSON 后端。注意这里**不删不改任何文件** ——
    /// 无论是那个读不动的 .db 还是旧的 .json，都原样留着，方便事后人工抢救。
    /// </summary>
    private static Selection Fallback(string jsonPath, string dbPath, string reason)
    {
        var json = new JsonScorePersistence(jsonPath);

        ScoreDatabase data;
        try
        {
            data = json.Load();
        }
        catch
        {
            data = new ScoreDatabase();
        }

        // 最尴尬的一种情况：SQLite 里有数据、但 JSON 那条路是空的。
        // 这时候老师会看到「分全没了」——数据其实还躺在 .db 里，只是这次读不出来。
        // 必须把话说清楚，否则他很可能把那个文件当成垃圾删掉，那才是真的丢了。
        if (data.ScoreRecords.Count == 0
            && SqliteScorePersistence.HasUsableDb(dbPath)
            && SqliteScorePersistence.CountRecords(dbPath) > 0)
        {
            reason += $"注意：SQLite 数据文件里仍有数据，请不要删除它，路径为 {dbPath}";
        }

        return new Selection(json, data, reason);
    }

    /// <summary>
    /// 把迁移完的旧 JSON 改名留档（不是删除）。改名失败返回 null，上层会据此调整提示文案。
    /// </summary>
    public static string? TryArchive(string jsonPath)
    {
        try
        {
            var target = $"{jsonPath}.migrated-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Move(jsonPath, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }
}
