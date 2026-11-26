using System.IO;

namespace FishWord.App;

/// <summary>
/// 资源定位：用户数据目录（%LocalAppData%\FishWord\）优先，程序目录种子资源兜底。
/// 安装版装在 Program Files（只读）时，运行期 db / 音频读写全部走用户数据目录。
/// </summary>
public static class ResourceLocator
{
    /// <summary>用户数据目录：%LocalAppData%\FishWord\（db + 音频的运行时副本，可写）。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishWord");

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

    /// <summary>首启初始化：把种子 db 与音频复制到用户数据目录（幂等，已存在不覆盖——老用户升级保持原数据）。</summary>
    public static void EnsureUserData()
    {
        Directory.CreateDirectory(DataDir);

        var dataDb = Path.Combine(DataDir, "fishword.db");
        if (!File.Exists(dataDb))
        {
            var seed = FindSeed("resources/fishword.db");
            if (seed is not null)
                File.Copy(seed, dataDb);
        }

        var dataAudio = Path.Combine(DataDir, "audio");
        if (!Directory.Exists(dataAudio))
        {
            var seedAudio = FindSeed("resources/audio");
            if (seedAudio is not null)
                CopyDirectory(seedAudio, dataAudio);
        }
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
            File.Copy(file, target);
        }
    }
}
