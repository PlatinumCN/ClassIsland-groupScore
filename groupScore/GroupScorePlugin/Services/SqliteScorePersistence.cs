using System.Globalization;
using GroupScorePlugin.Models;
using Microsoft.Data.Sqlite;

namespace GroupScorePlugin.Services;

/// <summary>
/// SQLite 后端。
///
/// **架构上的关键取舍：SQLite 只是「落盘格式」，运行时的真相仍然是内存里的
/// <see cref="ScoreDatabase"/>。** 加载时一次性把四张表读进内存，之后所有读写
/// 都还是原来的内存操作，落盘时才整份同步下去。这样做的好处是：
/// 视图层、服务层的 4600 行代码一行都不用改，性能特征也和 JSON 时期完全一样
/// （排行榜渲染读的还是内存里的缓存总分，不会退化成每次查库）。
///
/// **换来的是什么**：「加一次分」要同时改记录表与小组总分缓存这两处，
/// 在 JSON 时代只能整份重写文件、靠「先写 .tmp 再替换」保证不写坏；
/// 现在这两处改动可以放进同一个事务，中途断电也不会留下
/// 「记录进去了但总分没更新」的坏数据。
///
/// **增量策略**：四张表里只有 SCORE_RECORDS 会长大（一学期几千条），
/// 所以只有它做增量 —— 由 <see cref="RecordWriteDecision"/> 判定是
/// 「什么都不写 / 只追加尾部 / 整表重写」。其余三张表本来就只有几十行，全量重写最省心。
/// </summary>
internal sealed class SqliteScorePersistence : IScorePersistence
{
    /// <summary>当前数据库结构版本，写在 meta 表里。</summary>
    public const int SchemaVersion = 1;

    private readonly string _dbPath;
    private readonly object _gate = new();

    // 「库里现在长什么样」的记忆。增量落盘全靠它来判断哪一段是新的。
    private int _storedRecordCount;
    private string? _storedLastRecordId;

    // 本次运行是否已经建过表。建表语句全是 IF NOT EXISTS，重复执行是空操作，
    // 但一秒一次的落盘没必要每次都去解析一遍 DDL。
    private bool _schemaReady;

    public SqliteScorePersistence(string dbPath) => _dbPath = dbPath;

    public string BackendName => "SQLite";

    public string Location => _dbPath;

    /// <summary>库里当前的记录条数。自检台用它确认增量策略真的按预期走了。</summary>
    public int StoredRecordCount => _storedRecordCount;

    /// <summary>库里最后一条记录的 Id。同上，供自检台断言。</summary>
    public string? StoredLastRecordId => _storedLastRecordId;

    // ---------------------------------------------------------------- 探测

    /// <summary>
    /// 探一下这台机器上 SQLite 到底能不能用。**刻意在临时文件上试**，
    /// 而不是在真正的数据文件上试 —— 否则探测本身就会把目标文件建出来，
    /// 后面「数据文件是否存在」的判断全部失真。
    ///
    /// 只发一条 `select sqlite_version()`，不建任何表：这样连临时文件都不会被
    /// 真正创建（SQLite 是惰性的），探测过程对磁盘零副作用。
    /// </summary>
    public static bool Probe(string directory, out string detail)
    {
        var temp = Path.Combine(directory, "sqlite-probe-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(temp));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select sqlite_version()";
            var version = cmd.ExecuteScalar()?.ToString() ?? "未知";
            detail = $"SQLite {version} 可用";
            return true;
        }
        catch (Exception ex)
        {
            // 原生库没加载进来（DllNotFoundException）、目录不可写、架构不匹配……
            // 全部收敛成「不可用」，由上层回落到 JSON。
            detail = $"{ex.GetType().Name}：{ex.Message}";
            return false;
        }
        finally
        {
            TryDelete(temp);
            TryDelete(temp + "-wal");
            TryDelete(temp + "-shm");
        }
    }

    public static string BuildConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // ★ 刻意关掉连接池。开着的话连接在池里一直不真正关闭，数据库文件会被本进程
        // 长期占住 —— 老师想在资源管理器里复制一份数据备份、或者删掉不要的库，
        // 都会撞上「文件正在被另一个程序使用」。
        // 我们的落盘是 1 秒防抖的批量写，一秒最多开一次连接，
        // SQLite 的打开开销是微秒级的，省这点开销换「数据文件随时可以搬走」很划算。
        Pooling = false
    }.ToString();

    /// <summary>
    /// 这个路径上有没有一个**可用**的数据库文件。
    /// 特别注意 0 字节的情况：那是在「文件刚被建出来、schema 还没写进去」的瞬间断电
    /// 留下的残骸，必须当成不存在来重建，否则会被误判成「已经有库了」从而跳过迁移。
    /// </summary>
    public static bool HasUsableDb(string dbPath)
    {
        try
        {
            var info = new FileInfo(dbPath);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>只数一下记录条数，不把整库读出来。文件不在、表不存在、库读不动，一律返回 0。</summary>
    public static int CountRecords(string dbPath)
    {
        if (!HasUsableDb(dbPath)) return 0;
        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(dbPath));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "select count(*) from score_records";
            return (int)Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        catch
        {
            return 0;
        }
    }

    // ---------------------------------------------------------------- 时间格式

    /// <summary>
    /// 时间入库格式。用往返格式 "O" 而不是默认的 ToString()：
    /// 默认格式会丢掉亚秒精度与 Kind，而记录是靠它做「按时间段统计」的，
    /// 精度一丢，同一秒内的多条记录就会在历史对比里排序不稳。
    /// </summary>
    public static string ToDb(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>时间的出库格式，与 <see cref="ToDb"/> 严格互逆（含毫秒与 Kind）。</summary>
    public static DateTime FromDb(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---------------------------------------------------------------- 读

    public ScoreDatabase Load()
    {
        var data = new ScoreDatabase();
        using var conn = Open();
        EnsureSchemaOnce(conn);

        data.SchemaVersion = (int)ReadMetaLong(conn, "schema_version", SchemaVersion);
        data.Seeded = ReadMetaLong(conn, "seeded", 0) != 0;

        // 一张表一张表地读。**一律按 rowid（插入顺序）读，不按业务字段排序** ——
        // JSON 后端返回的就是文件里的原始顺序，两个后端的行为必须一模一样，
        // 否则「换个后端顺序就变了」。至于顺序本身该不该修正，是 ScoreStore 加载后
        // 那几段归一化逻辑的职责，两边共用同一套。
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select id, name, color, sort_order, cached_total from groups order by rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Groups.Add(new Group
                {
                    Id = Str(r, 0),
                    Name = Str(r, 1),
                    Color = Str(r, 2),
                    SortOrder = r.GetInt32(3),
                    CachedTotal = r.GetInt32(4)
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select id, group_id, name, sort_order from members order by rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Members.Add(new Member
                {
                    Id = Str(r, 0),
                    GroupId = Str(r, 1),
                    Name = Str(r, 2),
                    SortOrder = r.GetInt32(3)
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "select id, group_id, member_id, delta, reason, preset_id, created_at " +
                "from score_records order by seq";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.ScoreRecords.Add(new ScoreRecord
                {
                    Id = Str(r, 0),
                    GroupId = Str(r, 1),
                    MemberId = r.IsDBNull(2) ? null : r.GetString(2),
                    Delta = r.GetInt32(3),
                    Reason = Str(r, 4),
                    PresetId = r.IsDBNull(5) ? null : r.GetString(5),
                    CreatedAt = FromDb(Str(r, 6))
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "select id, label, delta, sort_order from presets order by rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                data.Presets.Add(new ScorePreset
                {
                    Id = Str(r, 0),
                    Label = Str(r, 1),
                    Delta = r.GetInt32(2),
                    SortOrder = r.GetInt32(3)
                });
            }
        }

        // 记住库里的样子，供后续增量落盘比对
        _storedRecordCount = data.ScoreRecords.Count;
        _storedLastRecordId = data.ScoreRecords.Count > 0 ? data.ScoreRecords[^1].Id : null;

        return data;
    }

    // ---------------------------------------------------------------- 写

    public void Save(ScoreDatabase data)
    {
        lock (_gate)
        {
            using var conn = Open();

            // ★ 这里必须建表，不能假设「一定先 Load 过」。
            // 一次性迁移就是反例：那个流程是「读旧 JSON -> 直接写进一个新库」，
            // Save 是新库上的第一个动作，此时表还不存在。
            // 少了这一句，迁移会抛 no such table: meta，而且会被上层的回落逻辑
            // 吞掉、悄悄换成 JSON —— 表现是「一切正常」，实则一次都没迁移成功。
            EnsureSchemaOnce(conn);

            // 整个落盘过程放在一个事务里：四张表要么全变成新的，要么全是旧的。
            using var tx = conn.BeginTransaction();

            WriteMeta(conn, tx, data);
            RewriteGroups(conn, tx, data.Groups);
            RewriteMembers(conn, tx, data.Members);
            RewritePresets(conn, tx, data.Presets);
            WriteRecords(conn, tx, data.ScoreRecords);

            tx.Commit();

            // 提交成功之后才更新记忆。顺序反过来的话，一旦提交失败，
            // 下次就会因为「以为已经写进去了」而漏掉这一批记录。
            _storedRecordCount = data.ScoreRecords.Count;
            _storedLastRecordId = data.ScoreRecords.Count > 0 ? data.ScoreRecords[^1].Id : null;
        }
    }

    private void WriteRecords(SqliteConnection conn, SqliteTransaction tx, List<ScoreRecord> records)
    {
        var ids = new List<string>(records.Count);
        foreach (var record in records) ids.Add(record.Id);

        var plan = RecordWriteDecision.Decide(_storedRecordCount, _storedLastRecordId, ids);

        switch (plan.Mode)
        {
            case RecordWriteMode.None:
                return;

            case RecordWriteMode.Full:
                Exec(conn, tx, "delete from score_records");
                InsertRecords(conn, tx, records, 0, records.Count);
                return;

            case RecordWriteMode.Tail:
                InsertRecords(conn, tx, records, plan.KeepCount, records.Count);
                return;
        }
    }

    private static void InsertRecords(
        SqliteConnection conn, SqliteTransaction tx, List<ScoreRecord> records, int from, int to)
    {
        if (to <= from) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "insert into score_records(id, group_id, member_id, delta, reason, preset_id, created_at) " +
            "values ($id, $gid, $mid, $delta, $reason, $pid, $at)";

        // 参数预先建好，循环里只改 Value：省掉每条记录重新解析一遍 SQL 的开销。
        // tail 场景下一次可能要插几百条，这个差别是实打实的。
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pGroup = cmd.Parameters.Add("$gid", SqliteType.Text);
        var pMember = cmd.Parameters.Add("$mid", SqliteType.Text);
        var pDelta = cmd.Parameters.Add("$delta", SqliteType.Integer);
        var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
        var pPreset = cmd.Parameters.Add("$pid", SqliteType.Text);
        var pAt = cmd.Parameters.Add("$at", SqliteType.Text);

        for (var i = from; i < to; i++)
        {
            var record = records[i];
            pId.Value = record.Id;
            pGroup.Value = record.GroupId;
            pMember.Value = Nullable(record.MemberId);
            pDelta.Value = record.Delta;
            pReason.Value = record.Reason ?? "";
            pPreset.Value = Nullable(record.PresetId);
            pAt.Value = ToDb(record.CreatedAt);
            cmd.ExecuteNonQuery();
        }
    }

    private static void RewriteGroups(SqliteConnection conn, SqliteTransaction tx, List<Group> groups)
    {
        Exec(conn, tx, "delete from groups");
        if (groups.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "insert into groups(id, name, color, sort_order, cached_total) " +
            "values ($id, $name, $color, $order, $total)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pColor = cmd.Parameters.Add("$color", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);
        var pTotal = cmd.Parameters.Add("$total", SqliteType.Integer);

        foreach (var group in groups)
        {
            pId.Value = group.Id;
            pName.Value = group.Name ?? "";
            pColor.Value = group.Color ?? "";
            pOrder.Value = group.SortOrder;
            pTotal.Value = group.CachedTotal;
            cmd.ExecuteNonQuery();
        }
    }

    private static void RewriteMembers(SqliteConnection conn, SqliteTransaction tx, List<Member> members)
    {
        Exec(conn, tx, "delete from members");
        if (members.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "insert into members(id, group_id, name, sort_order) values ($id, $gid, $name, $order)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pGroup = cmd.Parameters.Add("$gid", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);

        foreach (var member in members)
        {
            pId.Value = member.Id;
            pGroup.Value = member.GroupId;
            pName.Value = member.Name ?? "";
            pOrder.Value = member.SortOrder;
            cmd.ExecuteNonQuery();
        }
    }

    private static void RewritePresets(SqliteConnection conn, SqliteTransaction tx, List<ScorePreset> presets)
    {
        Exec(conn, tx, "delete from presets");
        if (presets.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "insert into presets(id, label, delta, sort_order) values ($id, $label, $delta, $order)";
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pLabel = cmd.Parameters.Add("$label", SqliteType.Text);
        var pDelta = cmd.Parameters.Add("$delta", SqliteType.Integer);
        var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);

        foreach (var preset in presets)
        {
            pId.Value = preset.Id;
            pLabel.Value = preset.Label ?? "";
            pDelta.Value = preset.Delta;
            pOrder.Value = preset.SortOrder;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteMeta(SqliteConnection conn, SqliteTransaction tx, ScoreDatabase data)
    {
        SetMeta(conn, tx, "schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta(conn, tx, "seeded", data.Seeded ? "1" : "0");
    }

    // ---------------------------------------------------------------- 骨架

    /// <summary>
    /// 建表。全部 `IF NOT EXISTS`，所以对已有库是幂等的空操作，
    /// 也能把「升级前建的旧库」自动补齐缺的表。
    /// </summary>
    public static void EnsureSchema(SqliteConnection conn)
    {
        Exec(conn, null, """
            create table if not exists meta (
                key   text primary key,
                value text not null
            );

            create table if not exists groups (
                id           text primary key,
                name         text not null,
                color        text not null,
                sort_order   integer not null,
                cached_total integer not null
            );

            create table if not exists members (
                id         text primary key,
                group_id   text not null,
                name       text not null,
                sort_order integer not null
            );

            create table if not exists score_records (
                seq        integer primary key autoincrement,
                id         text not null unique,
                group_id   text not null,
                member_id  text,
                delta      integer not null,
                reason     text not null,
                preset_id  text,
                created_at text not null
            );

            create table if not exists presets (
                id         text primary key,
                label      text not null,
                delta      integer not null,
                sort_order integer not null
            );

            create index if not exists ix_score_records_group  on score_records(group_id);
            create index if not exists ix_score_records_at     on score_records(created_at);
            create index if not exists ix_members_group        on members(group_id);
            """);
    }

    /// <summary>每个实例只建一次表。Load 和 Save 都会调它，谁先来谁负责。</summary>
    private void EnsureSchemaOnce(SqliteConnection conn)
    {
        if (_schemaReady) return;
        EnsureSchema(conn);
        _schemaReady = true;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(BuildConnectionString(_dbPath));
        conn.Open();
        return conn;
    }

    private static void SetMeta(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "insert or replace into meta(key, value) values ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static long ReadMetaLong(SqliteConnection conn, string key, long fallback)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select value from meta where key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var raw = cmd.ExecuteScalar()?.ToString();
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Str(SqliteDataReader reader, int index)
        => reader.IsDBNull(index) ? "" : reader.GetString(index);

    /// <summary>可空字符串入参：SQL 里 null 必须显式写成 DBNull.Value，不能直接塞 null。</summary>
    private static object Nullable(string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 探测残留清不掉不算事，不影响插件运行
        }
    }
}
