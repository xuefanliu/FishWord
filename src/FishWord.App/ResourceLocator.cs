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

    /// <summary>启动日志（排查资源定位问题用，追加写入 logs/data.log）。</summary>
    private static void Log(string message)
    {
        try
        {
            var logDir = Path.Combine(DataDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "data.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>迁移用用户表清单（列名，跳过自增 id）。</summary>
    private static readonly (string Table, string Columns)[] UserTables =
    {
        ("user_words",    "word_id, status, is_known, created_at, known_source"),
        ("daily_study",   "date, word_id, pass_count, is_extra"),
        ("memory_states", "word_id, algorithm, interval_days, ease_factor, repetitions, due_at, last_review_at"),
        ("review_logs",   "word_id, reviewed_at, rating, interval_days"),
        ("settings",      "key, value"),
    };

    /// <summary>
    /// 查找资源：用户数据目录优先。
    /// fishword.db 强制使用用户数据目录（副本缺失时从种子复制），**绝不回退到程序目录种子**——
    /// 防止覆盖安装后误用「无用户数据」的新种子库（学习记录丢失）。其他只读资源（音频/505 名单）回退种子无风险。
    /// </summary>
    /// <summary>把仓库相对路径映射为用户数据目录路径（剥掉 resources/ 前缀，统一正反斜杠：
    /// resources/fishword.db → DataDir\fishword.db；resources/audio/x.mp3 → DataDir\audio\x.mp3）。</summary>
    private static string MapUserPath(string relativePath)
    {
        string rel = relativePath.Replace('\\', '/');
        string mapped = rel.StartsWith("resources/", StringComparison.Ordinal)
            ? rel["resources/".Length..]
            : rel;
        return Path.Combine(DataDir, mapped.Replace('/', Path.DirectorySeparatorChar));
    }

    public static string? Find(string relativePath)
    {
        var user = MapUserPath(relativePath);

        if (relativePath.Replace('\\', '/') == "resources/fishword.db")
        {
            Log($"Find(db): DataDir={DataDir}, user 存在={File.Exists(user)}");
            if (File.Exists(user))
                return user;

            // 副本缺失：从种子复制（正常情况下 EnsureUserData 已建立；这里兜底防回退）
            var seed = FindSeed(relativePath);
            if (seed is null)
            {
                Log("Find(db): 种子未找到 → 返回 null");
                return null;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(user)!);
                File.Copy(seed, user, overwrite: false);
                CopySeedAudio();
                Log($"Find(db): 副本缺失，已从种子 {seed} 复制");
                return File.Exists(user) ? user : null;
            }
            catch (Exception ex)
            {
                Log($"Find(db): 复制失败 → 返回 null：{ex.Message}");
                return null; // 复制失败：明确失败，绝不回退读种子
            }
        }

        // 其他资源：用户目录优先，回退程序目录种子（只读使用）
        if (File.Exists(user))
            return user;
        return FindSeed(relativePath);
    }

    /// <summary>
    /// 首启初始化：把种子 db 与音频复制到用户数据目录（幂等）；
    /// 种子数据版本（PRAGMA user_version）高于用户副本时，迁移用户数据到新种子副本——
    /// 学习记录/熟词/设置原样保留，词库内容（例句/词根等）更新为最新。
    /// </summary>
    public static void EnsureUserData()
    {
        Log("EnsureUserData 开始");
        Directory.CreateDirectory(DataDir);

        var seedDb = FindSeed("resources/fishword.db");
        if (seedDb is null)
        {
            Log("EnsureUserData: 种子未找到，退出");
            return;
        }
        Log($"EnsureUserData: 种子 = {seedDb}");

        var dataDb = Path.Combine(DataDir, "fishword.db");
        if (!File.Exists(dataDb))
        {
            Log("EnsureUserData: 用户副本不存在，从种子复制");
            File.Copy(seedDb, dataDb);
            CopySeedAudio();
            return;
        }

        int seedVer = GetUserVersion(seedDb);
        int dataVer = GetUserVersion(dataDb);
        Log($"EnsureUserData: 版本 seed={seedVer} data={dataVer}");
        if (seedVer > dataVer)
        {
            Log("EnsureUserData: 种子版本更新，开始迁移");
            MigrateUserData(dataDb, seedDb);
            CopySeedAudio(); // 词库升级时音频也覆盖更新
        }
        // 注：旧版数据找回（RecoverLegacyData）已于 2026-11-27 移除——所有旧版用户已迁移完成，
        // 新版严格读写 AppData 副本；应急恢复走 tools/merge_legacy_db.py 手动工具。
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
