using System.Text.Json;
using FishWord.Core.Models;
using FishWord.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace FishWord.Infrastructure.Data;

/// <summary>用户学习进度（学习/复习/熟词）读写。首次打开时自动建进度表。</summary>
public sealed class ProgressRepository : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SqliteConnection _connection;

    public ProgressRepository(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS user_words (
                id INTEGER PRIMARY KEY,
                word_id INTEGER UNIQUE,
                status TEXT,
                is_known INTEGER DEFAULT 0,
                known_source TEXT DEFAULT 'user',
                created_at TEXT
            );
            CREATE TABLE IF NOT EXISTS memory_states (
                id INTEGER PRIMARY KEY,
                word_id INTEGER UNIQUE,
                algorithm TEXT,
                interval_days REAL,
                ease_factor REAL,
                repetitions INTEGER,
                due_at TEXT,
                last_review_at TEXT
            );
            CREATE TABLE IF NOT EXISTS review_logs (
                id INTEGER PRIMARY KEY,
                word_id INTEGER,
                reviewed_at TEXT,
                rating INTEGER,
                interval_days REAL
            );
            CREATE TABLE IF NOT EXISTS daily_study (
                date TEXT,
                word_id INTEGER,
                pass_count INTEGER DEFAULT 0,
                is_extra INTEGER DEFAULT 0,
                PRIMARY KEY (date, word_id)
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // 迁移：旧库 user_words 可能缺 known_source 列（熟词来源区分）
        if (!ColumnExists("user_words", "known_source"))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE user_words ADD COLUMN known_source TEXT DEFAULT 'user'";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>判断表是否含指定列（PRAGMA table_info）。</summary>
    private bool ColumnExists(string table, string column)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == column)
                return true;
        }
        return false;
    }

    /// <summary>今日学习摘要：Completed = 当日 pass&gt;=3 的词数；Goal = 每日目标（settings.daily_goal，默认 30）。</summary>
    public StudySummary GetStudySummary(string today)
    {
        int completed = ScalarInt(
            "SELECT COUNT(*) FROM daily_study WHERE date = $d AND pass_count >= 3",
            ("$d", today));
        int goal = GetStudyGoal();
        return new StudySummary(completed, goal);
    }

    /// <summary>每日学习目标（settings.daily_goal，默认 10）。</summary>
    public int GetStudyGoal() => GetSettingInt("daily_goal", 10);

    /// <summary>当前选中的词书 id（settings.current_book_id，默认 1 = 雅思核心）。</summary>
    public int GetCurrentBookId() => GetSettingInt("current_book_id", 1);

    /// <summary>持久化当前选中的词书 id。</summary>
    public void SetCurrentBookId(int bookId) => SetSetting("current_book_id", bookId.ToString());

    /// <summary>当天各词的学习遍数（word_id → pass_count）。</summary>
    public IReadOnlyDictionary<int, int> GetDailyPasses(string today)
    {
        var map = new Dictionary<int, int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word_id, pass_count FROM daily_study WHERE date = $d";
        cmd.Parameters.AddWithValue("$d", today);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int wordId = reader.GetInt32(0);
            int pass = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            map[wordId] = pass;
        }
        return map;
    }

    /// <summary>记录某词过完一遍（pass_count 只增不减）。</summary>
    public void RecordStudyPass(string today, int wordId, int pass)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_study(date, word_id, pass_count) VALUES($d, $w, $p)
            ON CONFLICT(date, word_id) DO UPDATE SET pass_count = MAX(pass_count, excluded.pass_count)
            """;
        cmd.Parameters.AddWithValue("$d", today);
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$p", pass);
        cmd.ExecuteNonQuery();
    }

    /// <summary>是否有进行中的学习（当天存在 1&lt;=pass&lt;3 的词）。</summary>
    public bool HasInProgressStudy(string today)
    {
        return ScalarInt(
            "SELECT COUNT(*) FROM daily_study WHERE date = $d AND pass_count >= 1 AND pass_count < 3",
            ("$d", today)) > 0;
    }

    /// <summary>把词加入当天队列（pass_count=0 表示尚未开始）。</summary>
    public void EnqueueStudyWord(string today, int wordId, bool isExtra = false)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO daily_study(date, word_id, pass_count, is_extra) VALUES($d, $w, 0, $e)
            ON CONFLICT(date, word_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$d", today);
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$e", isExtra ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 复习摘要：Completed = 今日已复习词数；DueTotal = 今日到期词总数。
    /// 复习后 due_at 会顺延到未来，故"今日到期总数" = 已复习(done) + 剩余到期(due)。
    /// 两者互斥（复习过的词 due_at 已 > 今日，不再计入 due），相加即为恒定总量。
    /// </summary>
    public ReviewSummary GetReviewSummary(string today)
    {
        int due = ScalarInt(
            "SELECT COUNT(*) FROM memory_states WHERE due_at <= $d",
            ("$d", today));
        int done = ScalarInt(
            "SELECT COUNT(DISTINCT word_id) FROM review_logs WHERE date(reviewed_at) = $d",
            ("$d", today));
        return new ReviewSummary(done, done + due);
    }

    /// <summary>熟词列表（words JOIN user_words where is_known=1，按来源过滤，完整字段）。</summary>
    public IReadOnlyList<Word> GetKnownWords(string source)
    {
        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT w.id, w.spelling, w.phonetic_uk, w.phonetic_us,
                   w.definitions, w.examples, w.mnemonics, w.audio_path, w.freq_rank, w.root_id
            FROM user_words uw JOIN words w ON w.id = uw.word_id
            WHERE uw.is_known = 1 AND uw.known_source = $source
            ORDER BY w.spelling
            """;
        cmd.Parameters.AddWithValue("$source", source);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

    /// <summary>到期复习词（memory_states.due_at &lt;= today 且未标记熟词），返回完整 Word。</summary>
    public IReadOnlyList<Word> GetDueReviewWords(string today)
    {
        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT w.id, w.spelling, w.phonetic_uk, w.phonetic_us,
                   w.definitions, w.examples, w.mnemonics, w.audio_path, w.freq_rank, w.root_id
            FROM memory_states ms
            JOIN words w ON w.id = ms.word_id
            LEFT JOIN user_words uw ON uw.word_id = w.id
            WHERE ms.due_at <= $today
              AND COALESCE(uw.is_known, 0) = 0
            ORDER BY ms.due_at, w.freq_rank DESC, w.spelling
            """;
        cmd.Parameters.AddWithValue("$today", today);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

    /// <summary>单词学满三遍后进入复习队列（首次到期 = 明天，SM-2 初始态）。</summary>
    public void InitMemoryState(int wordId)
    {
        string tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_states(word_id, algorithm, interval_days, ease_factor, repetitions, due_at)
            VALUES($w, 'sm2', 0, 2.5, 0, $due)
            ON CONFLICT(word_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$due", tomorrow);
        cmd.ExecuteNonQuery();
    }

    /// <summary>复习自评回写：SM-2 计算下次间隔，更新 memory_states 并追加 review_logs。</summary>
    public void RecordReview(int wordId, bool remembered)
    {
        var state = GetMemoryState(wordId) ?? new Sm2State();
        var next = Sm2Scheduler.Next(state, remembered);

        int intervalDays = (int)Math.Round(next.IntervalDays);
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string nextDue = DateTime.Today.AddDays(intervalDays).ToString("yyyy-MM-dd");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_states(word_id, algorithm, interval_days, ease_factor, repetitions, due_at, last_review_at)
            VALUES($w, 'sm2', $iv, $ef, $rp, $due, $now)
            ON CONFLICT(word_id) DO UPDATE SET
                algorithm = 'sm2',
                interval_days = excluded.interval_days,
                ease_factor = excluded.ease_factor,
                repetitions = excluded.repetitions,
                due_at = excluded.due_at,
                last_review_at = excluded.last_review_at
            """;
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$iv", next.IntervalDays);
        cmd.Parameters.AddWithValue("$ef", next.EaseFactor);
        cmd.Parameters.AddWithValue("$rp", next.Repetitions);
        cmd.Parameters.AddWithValue("$due", nextDue);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        using var log = _connection.CreateCommand();
        log.CommandText = "INSERT INTO review_logs(word_id, reviewed_at, rating, interval_days) VALUES($w, $now, $r, $iv)";
        log.Parameters.AddWithValue("$w", wordId);
        log.Parameters.AddWithValue("$now", now);
        log.Parameters.AddWithValue("$r", remembered ? 5 : 0);
        log.Parameters.AddWithValue("$iv", next.IntervalDays);
        log.ExecuteNonQuery();
    }

    private Sm2State? GetMemoryState(int wordId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ease_factor, repetitions, interval_days FROM memory_states WHERE word_id = $w";
        cmd.Parameters.AddWithValue("$w", wordId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        double ease = reader.IsDBNull(0) ? 2.5 : reader.GetDouble(0);
        int reps = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        double interval = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
        return new Sm2State(ease, reps, interval);
    }

    /// <summary>按固定字段序从 reader 读取一个完整 Word（id,spelling,phonetic_uk,phonetic_us,definitions,examples,mnemonics,audio_path,freq_rank）。</summary>
    private static Word ReadWord(SqliteDataReader reader)
    {
        return new Word
        {
            Id = reader.GetInt32(0),
            Spelling = reader.GetString(1),
            PhoneticUk = reader.IsDBNull(2) ? null : reader.GetString(2),
            PhoneticUs = reader.IsDBNull(3) ? null : reader.GetString(3),
            Definitions = Parse<List<WordDefinition>>(reader, 4) ?? new List<WordDefinition>(),
            Examples = Parse<List<ExampleSentence>>(reader, 5) ?? new List<ExampleSentence>(),
            Mnemonics = Parse<Mnemonics>(reader, 6) ?? new Mnemonics(),
            AudioPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            FreqRank = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            RootId = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
        };
    }

    private static T? Parse<T>(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return default;
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>标记熟词：写入 user_words.is_known=1，并从当天学习记录与复习队列移除。</summary>
    public void MarkKnown(string today, int wordId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_words(word_id, status, is_known, known_source, created_at) VALUES($w, 'known', 1, 'user', $now)
            ON CONFLICT(word_id) DO UPDATE SET is_known = 1, status = 'known', known_source = 'user'
            """;
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();

        using var del = _connection.CreateCommand();
        del.CommandText = "DELETE FROM daily_study WHERE date = $d AND word_id = $w";
        del.Parameters.AddWithValue("$d", today);
        del.Parameters.AddWithValue("$w", wordId);
        del.ExecuteNonQuery();

        using var delMs = _connection.CreateCommand();
        delMs.CommandText = "DELETE FROM memory_states WHERE word_id = $w";
        delMs.Parameters.AddWithValue("$w", wordId);
        delMs.ExecuteNonQuery();
    }

    /// <summary>取消熟词（恢复为可学习）。</summary>
    public void UnmarkKnown(int wordId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE user_words SET is_known = 0, status = 'new' WHERE word_id = $w";
        cmd.Parameters.AddWithValue("$w", wordId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 账号初始化：把命中 505 基础词名单的词一次性标为「系统熟词」(known_source='system')，全局排除。
    /// 幂等：ON CONFLICT DO NOTHING，不会覆盖已有记录（含用户已取消的词）。
    /// </summary>
    public int MarkBasicWords505(IReadOnlyList<string> spellings)
    {
        if (spellings.Count == 0)
            return 0;

        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var names = new string[spellings.Count];
        for (int i = 0; i < spellings.Count; i++)
            names[i] = $"$s{i}";
        string placeholders = string.Join(", ", names);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO user_words(word_id, status, is_known, known_source, created_at)
            SELECT w.id, 'known', 1, 'system', $now
            FROM words w
            WHERE LOWER(w.spelling) IN ({placeholders})
            ON CONFLICT(word_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$now", now);
        for (int i = 0; i < spellings.Count; i++)
            cmd.Parameters.AddWithValue($"$s{i}", spellings[i].ToLowerInvariant());

        return cmd.ExecuteNonQuery();
    }

    /// <summary>505 打标是否已完成（settings 标志位）。</summary>
    public bool IsBasic505Marked() => GetSettingInt("basic_505_marked", 0) == 1;

    /// <summary>标记 505 打标已完成。</summary>
    public void SetBasic505Marked() => SetSetting("basic_505_marked", "1");

    public void SetSetting(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>所有熟词的 word_id 集合。</summary>
    public IReadOnlySet<int> GetKnownWordIds()
    {
        var set = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word_id FROM user_words WHERE is_known = 1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetInt32(0));
        return set;
    }

    /// <summary>所有「已学习」（已进复习队列）的 word_id 集合。选新词时排除，实现跨词书不重复学。</summary>
    public IReadOnlySet<int> GetLearnedWordIds()
    {
        var set = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word_id FROM memory_states";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetInt32(0));
        return set;
    }

    /// <summary>删除当天「尚未开始」（pass_count=0）的学习占位词，切换词书时释放名额。</summary>
    public void DeleteUnstartedDailyWords(string today)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM daily_study WHERE date = $d AND pass_count = 0";
        cmd.Parameters.AddWithValue("$d", today);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除当天指定单词的学习记录（目标调小时裁剪多余的未开始占位词）。</summary>
    public void DeleteDailyWords(string today, IEnumerable<int> wordIds)
    {
        foreach (var wid in wordIds)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM daily_study WHERE date = $d AND word_id = $w";
            cmd.Parameters.AddWithValue("$d", today);
            cmd.Parameters.AddWithValue("$w", wid);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 每本内置词书的「已学习」词数（book_id → 已学习数），用于词书选择列表展示进度。
    /// 已学习 = 熟词（user_words.is_known=1，含系统排除 + 用户标记）∪ 已过三遍进复习队列（memory_states）。
    /// </summary>
    public IReadOnlyDictionary<int, int> GetBookLearnedCounts()
    {
        var map = new Dictionary<int, int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, COUNT(learned.word_id)
            FROM books b
            LEFT JOIN book_words bw ON bw.book_id = b.id
            LEFT JOIN (
                SELECT word_id FROM user_words WHERE is_known = 1
                UNION
                SELECT word_id FROM memory_states
            ) learned ON learned.word_id = bw.word_id
            WHERE b.is_builtin = 1
            GROUP BY b.id
            ORDER BY b.id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetInt32(0)] = reader.GetInt32(1);
        return map;
    }

    private int ScalarInt(string sql, params (string, object)[] ps)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in ps)
            cmd.Parameters.AddWithValue(key, value);
        var result = cmd.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    /// <summary>读 settings 字符串值（无则返回 null）。</summary>
    public string? GetSetting(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    private int GetSettingInt(string key, int fallback)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var result = cmd.ExecuteScalar();
        if (result is null)
            return fallback;
        return int.TryParse(result.ToString(), out var v) ? v : fallback;
    }

    public void Dispose() => _connection.Dispose();
}
