namespace FishWord.Core.Models;

/// <summary>词书信息（用于词书选择列表与首页当前词书展示）。</summary>
public sealed record BookInfo(int Id, string Name, int WordCount);
