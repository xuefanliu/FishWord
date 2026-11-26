namespace FishWord.Core.Models;

/// <summary>首页"学习"卡片摘要：Completed = 当日已完成词数，Goal = 每日目标。</summary>
public sealed record StudySummary(int Completed, int Goal);

/// <summary>首页"复习"卡片摘要：Completed = 今日已复习词数，DueTotal = 到期词总数。</summary>
public sealed record ReviewSummary(int Completed, int DueTotal);
