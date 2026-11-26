using FishWord.Core.Models;

namespace FishWord.App;

/// <summary>词书选择列表的一行（词书 + 是否当前选中 + 已学习数）。</summary>
public sealed class BookRow
{
    public BookInfo Book { get; }
    public bool IsCurrent { get; }
    public int LearnedCount { get; }

    public string Name => Book.Name;
    public string CountDisplay => $"{LearnedCount}/{Book.WordCount} 词";

    public BookRow(BookInfo book, bool isCurrent, int learnedCount)
    {
        Book = book;
        IsCurrent = isCurrent;
        LearnedCount = learnedCount;
    }
}
