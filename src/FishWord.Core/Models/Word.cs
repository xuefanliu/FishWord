namespace FishWord.Core.Models;

/// <summary>单词的一条释义。</summary>
public sealed class WordDefinition
{
    public string? Pos { get; set; }
    public string? MeaningCn { get; set; }
    public string? MeaningEn { get; set; }
}

/// <summary>例句（英 + 中）。</summary>
public sealed class ExampleSentence
{
    public string? En { get; set; }
    public string? Cn { get; set; }
}

/// <summary>助记信息：词根词缀、搭配、同根/派生词、词源说明。</summary>
public sealed class Mnemonics
{
    public List<string> Root { get; set; } = new();
    public List<string> Collocation { get; set; } = new();
    public List<string> Related { get; set; } = new();

    /// <summary>词源说明（单词级叙述，如"诞生于16世纪，前缀 ab- 引向偏离……"）。</summary>
    public string? Etymology { get; set; }

    public bool HasAny => Root.Count > 0 || Collocation.Count > 0 || Related.Count > 0
                          || !string.IsNullOrEmpty(Etymology);
}

/// <summary>单词实体（全局唯一，归属由标签/词书表达）。</summary>
public sealed class Word
{
    public int Id { get; init; }
    public string Spelling { get; init; } = "";
    public string? PhoneticUk { get; init; }
    public string? PhoneticUs { get; init; }
    public string? AudioPath { get; init; }
    public int? FreqRank { get; init; }

    /// <summary>主词根 id（roots 表），0 = 无词根。</summary>
    public int RootId { get; init; }

    public List<WordDefinition> Definitions { get; init; } = new();
    public List<ExampleSentence> Examples { get; init; } = new();
    public Mnemonics Mnemonics { get; init; } = new();

    /// <summary>优先英式音标，缺省美式。</summary>
    public string Phonetic => PhoneticUk ?? PhoneticUs ?? "";

    /// <summary>基础释义（取第一条中文释义），用于熟词列表等简版展示。</summary>
    public string BasicMeaning => Definitions.FirstOrDefault()?.MeaningCn ?? "";
}

/// <summary>词根信息（roots 表，多词共享，用于同根词串记）。</summary>
public sealed record RootInfo(int Id, string Root, string? Gloss);
