using System.IO;
using Microsoft.Data.Sqlite;

namespace FishWord.App;

/// <summary>
/// 资源定位：用户数据目录（%LocalAppData%\FishWord\）优先，程序目录种子资源兜底。
/// 安装版装在 Program Files（只读）时，运行期 db / 音频读写全部走用户数据目录。
/// 种子数据版本更新（PRAGMA user_version）时自动迁移用户数据（学习记录保留、词库内容更新）。
/// </summary>
public static class ResourceLocator
{
    /// <summary>用户数据目录：%LocalAppData%\FishWord\（db + 音频的运行时副本，可写）。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishWord");

    /// <summary>迁移用用户表清单（列名，跳过自增 id）。</summary>
    private static readonly (string Table, string Columns)[] UserTables =
    {
        ("user_words",    "word_id, status, is_known, created_at, known_source"),
        ("daily_study",   "date, word_id, pass_count, is_extra"),
        ("memory_states", "word_id, algorithm, interval_days, ease_factor, repetitions, due_at, last_review_at"),
        ("review_logs",   "word_id, reviewed_at, rating, interval_days"),
        ("settings",      "key, value"),
    };

    /// <summary>查找资源：优先用户数据目录（首启由 EnsureUserData 从种子复制），其次程序目录向上查找种子。</summary>
    public static string? Find(string relativePath)
    {
        // 1) 用户数据目录
        var user = Path.Combine(DataDir, relativePath);
        if (File.Exists(user))
            return user;

        // 2) 程序目录向上查找种子（开发期与便携版）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 首启初始化：把种子 db 与音频复制到用户数据目录（幂等）；
    /// 种子数据版本（PRAGMA user_version）高于用户副本时，迁移用户数据到新种子副本——
    /// 学习记录/熟词/设置原样保留，词库内容（例句/词根等）更新为最新。
    /// </summary>
    public static void EnsureUserData()
    {
        Directory.CreateDirectory(DataDir);

        var seedDb = FindSeed("resources/fishword.db");
        if (seedDb is null)
            return;

        var dataDb = Path.Combine(DataDir, "fishword.db");
        if (!File.Exists(dataDb))
        {
            File.Copy(seedDb, dataDb);
            CopySeedAudio();
            return;
        }

        int seedVer = GetUserVersion(seedDb);
        int dataVer = GetUserVersion(dataDb);
        if (seedVer > dataVer)
        {
            MigrateUserData(dataDb, seedDb);
            CopySeedAudio(); // 词库升级时音频也覆盖更新
        }
    }

    /// <summary>读取 SQLite 数据版本号（PRAGMA user_version），失败按 0 处理。</summary>
    private static int GetUserVersion(string dbPath)
    {
        try
        {
            using var con = new SqliteConnection($"Data Source={dbPath}");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>用户数据迁移：备份旧库 → 种子副本 + ATTACH 旧库导入用户表 → 原子替换。</summary>
    private static void MigrateUserData(string dataDb, string seedDb)
    {
        string backup = dataDb + ".bak";
        string tmp = dataDb + ".new";

        try
        {
            File.Copy(dataDb, backup, overwrite: true);
            File.Copy(seedDb, tmp, overwrite: true);

            string escaped = dataDb.Replace("'", "''");
            using var con = new SqliteConnection($"Data Source={tmp}");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"ATTACH DATABASE '{escaped}' AS old;";
            cmd.ExecuteNonQuery();
            foreach (var (table, columns) in UserTables)
            {
                // 先清空种子副本的用户表再导入——用户副本才是权威状态：
                // 生产种子（prepare_release 纯净库）user_words 有 505 预填需让位；
                // 开发种子本身含用户数据（重复主键冲突）。
                cmd.CommandText = $"DELETE FROM main.{table};";
                cmd.ExecuteNonQuery();
                cmd.CommandText =
                    $"INSERT INTO main.{table}({columns}) SELECT {columns} FROM old.{table};";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = "DETACH DATABASE old;";
            cmd.ExecuteNonQuery();
            con.Close();
            // 释放连接池持有的文件句柄（GetUserVersion/ATTACH 用过的池化连接仍占用 dataDb，
            // File.Replace 需要独占访问）
            SqliteConnection.ClearAllPools();

            File.Replace(tmp, dataDb, backup);
            TryDelete(backup);
        }
        catch (Exception ex)
        {
            // 迁移失败：保留旧库（下次启动重试），清理半成品并留错误日志便于排查
            TryDelete(tmp);
            TryDelete(backup);
            try
            {
                File.WriteAllText(Path.Combine(DataDir, "migration_error.log"), ex.ToString());
            }
            catch { /* 忽略 */ }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 忽略 */ }
    }

    private static void CopySeedAudio()
    {
        var seedAudio = FindSeed("resources/audio");
        if (seedAudio is not null)
            CopyDirectory(seedAudio, Path.Combine(DataDir, "audio"));
    }

    /// <summary>只在程序目录侧查找种子（不查用户数据目录，避免自引用）。</summary>
    private static string? FindSeed(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
