using System.Text.Json;
using FishWord.Core.Models;
using Microsoft.Data.Sqlite;

namespace FishWord.Infrastructure.Data;

/// <summary>从 fishword.db 读取词库（只读）。</summary>
public sealed class WordRepository : IDisposable
{
    // 数据库 JSON 字段用蛇形命名（meaning_cn 等），需映射到 PascalCase 属性
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly SqliteConnection _connection;

    public WordRepository(string dbPath)
    {
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
    }

    /// <summary>所有内置词书（含词数），供词书选择列表与首页当前词书展示。</summary>
    public IReadOnlyList<BookInfo> GetBooks()
    {
        var list = new List<BookInfo>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.name, COUNT(bw.word_id)
            FROM books b
            LEFT JOIN book_words bw ON bw.book_id = b.id
            WHERE b.is_builtin = 1
            GROUP BY b.id
            ORDER BY b.id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BookInfo(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return list;
    }

    /// <summary>按词书获取有序单词列表（默认词书 id=1 即"雅思核心"）。</summary>
    public IReadOnlyList<Word> GetBookWords(int bookId = 1)
    {
        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT w.id, w.spelling, w.phonetic_uk, w.phonetic_us,
                   w.definitions, w.examples, w.mnemonics, w.audio_path, w.freq_rank, w.root_id
            FROM book_words bw
            JOIN words w ON w.id = bw.word_id
            WHERE bw.book_id = $bookId
            ORDER BY bw.seq
            """;
        cmd.Parameters.AddWithValue("$bookId", bookId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

    /// <summary>词根信息（roots 表，多词共享）。</summary>
    public RootInfo? GetRoot(int rootId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, root, gloss FROM roots WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", rootId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new RootInfo(reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>同一词根下的其它单词（排除自身），按词频排序，用于同根词串记。</summary>
    public IReadOnlyList<Word> GetWordsByRoot(int rootId, int excludeWordId)
    {
        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, spelling, phonetic_uk, phonetic_us,
                   definitions, examples, mnemonics, audio_path, freq_rank, root_id
            FROM words
            WHERE root_id = $rid AND id != $ex
            ORDER BY freq_rank IS NULL, freq_rank, spelling
            """;
        cmd.Parameters.AddWithValue("$rid", rootId);
        cmd.Parameters.AddWithValue("$ex", excludeWordId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

    /// <summary>某个词的直接派生词（word_derivations 正向）。</summary>
    public IReadOnlyList<Word> GetDirectDerivations(int wordId)
    {
        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT w.id, w.spelling, w.phonetic_uk, w.phonetic_us,
                   w.definitions, w.examples, w.mnemonics, w.audio_path, w.freq_rank, w.root_id
            FROM word_derivations d
            JOIN words w ON w.id = d.derived_word_id
            WHERE d.word_id = $id
            ORDER BY w.freq_rank IS NULL, w.freq_rank, w.spelling
            """;
        cmd.Parameters.AddWithValue("$id", wordId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

    /// <summary>某个词的直接父词 id（谁派生了它，反向查询）。</summary>
    public IReadOnlyList<int> GetParentWordIds(int wordId)
    {
        var list = new List<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT word_id FROM word_derivations WHERE derived_word_id = $id";
        cmd.Parameters.AddWithValue("$id", wordId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetInt32(0));
        return list;
    }

    /// <summary>给定词集合内部的派生边（Parent→Child），用于同根词列表的派生去重。</summary>
    public IReadOnlyList<(int Parent, int Child)> GetDerivationEdgesAmong(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0)
            return Array.Empty<(int, int)>();

        var idArray = ids.ToArray();
        var names = new string[idArray.Length];
        for (int i = 0; i < idArray.Length; i++)
            names[i] = $"$id{i}";
        string placeholders = string.Join(", ", names);

        var list = new List<(int, int)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT word_id, derived_word_id FROM word_derivations
            WHERE word_id IN ({placeholders}) AND derived_word_id IN ({placeholders})
            """;
        for (int i = 0; i < idArray.Length; i++)
            cmd.Parameters.AddWithValue(names[i], idArray[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add((reader.GetInt32(0), reader.GetInt32(1)));
        return list;
    }

    /// <summary>按 id 批量读取完整单词（用于加载跨词书切换后队列中保留的「学习中」词）。</summary>
    public IReadOnlyList<Word> GetWordsByIds(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0)
            return Array.Empty<Word>();

        var idArray = ids.ToArray();
        var names = new string[idArray.Length];
        for (int i = 0; i < idArray.Length; i++)
            names[i] = $"$id{i}";
        string placeholders = string.Join(", ", names);

        var list = new List<Word>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, spelling, phonetic_uk, phonetic_us,
                   definitions, examples, mnemonics, audio_path, freq_rank, root_id
            FROM words
            WHERE id IN ({placeholders})
            """;
        for (int i = 0; i < idArray.Length; i++)
            cmd.Parameters.AddWithValue(names[i], idArray[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadWord(reader));
        return list;
    }

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

    public void Dispose() => _connection.Dispose();
}
