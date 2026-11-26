namespace FishWord.Core.Scheduling;

/// <summary>
/// 分级间隔穿插（Graduated-Lag Interleaving）调度器。
///
/// 用于"会话内"单词排序：让单词穿插出现（不连续同词）、间隔逐级拉长、
/// 没记住的词按短间隔插回队列中部（非末尾）。
///
/// 参考：Anki Learning Steps + Pimsleur 分级间歇回想。算法详见 docs/概要设计方案.md 6.5。
/// 与跨天遗忘曲线调度（Sm2Scheduler）是两个独立维度。
/// </summary>
public sealed class InterleaveScheduler
{
    private readonly int _lag12;
    private readonly int _lag23;
    private readonly int _lagFail;
    private readonly int _passCount;
    private readonly PriorityQueue<ScheduledWord, (long Due, long Seq)> _heap = new(new EntryComparer());
    private long _step;
    private long _seq;

    /// <param name="lag12">遍 1→遍 2 间隔词数（默认 4）。</param>
    /// <param name="lag23">遍 2→遍 3 间隔词数（默认 8，两遍模式下不参与）。</param>
    /// <param name="lagFail">最后一遍"没记住"的重试间隔词数（默认 3）。</param>
    /// <param name="passCount">总遍数：学习 = 3，复习 = 2（默认 3）。</param>
    public InterleaveScheduler(int lag12 = 4, int lag23 = 8, int lagFail = 3, int passCount = 3)
    {
        _lag12 = lag12;
        _lag23 = lag23;
        _lagFail = lagFail;
        _passCount = passCount;
    }

    public int Count => _heap.Count;

    /// <summary>把今日新词按 0,1,2,... 错开入队（遍 1）。</summary>
    public void Seed(IEnumerable<int> wordIds)
    {
        long i = 0;
        foreach (var id in wordIds)
            Enqueue(id, 1, i++);
    }

    /// <summary>按给定遍数入队（断点续学用：从指定遍继续）。</summary>
    public void Seed(IEnumerable<(int WordId, int Pass)> items)
    {
        long i = 0;
        foreach (var (id, pass) in items)
            Enqueue(id, pass, i++);
    }

    /// <summary>取出下一个要展示的 (wordId, pass)。队列空时返回 (-1, 0)。</summary>
    public (int WordId, int Pass) Next()
    {
        if (_heap.Count == 0)
            return (-1, 0);
        var item = _heap.Dequeue();
        return (item.WordId, item.Pass);
    }

    /// <summary>在展示并完成一遍后调用，把该词重新调度到下一遍（或插回重试）。</summary>
    public void Complete(int wordId, int pass, bool remembered)
    {
        if (pass < _passCount)
        {
            // 非最后一遍：进入下一遍。遍 1→遍 2 用 lag12，其余用 lag23。
            int lag = pass == 1 ? _lag12 : _lag23;
            Enqueue(wordId, pass + 1, _step + lag);
        }
        else if (!remembered)
        {
            // 最后一遍"没记住"：短间隔插回同遍重试
            Enqueue(wordId, pass, _step + _lagFail);
        }
        // 最后一遍"记住"：完成，离开队列
        _step++;
    }

    /// <summary>运行时追加单个词到指定遍数（熟词补位等）。</summary>
    public void Add(int wordId, int pass)
    {
        Enqueue(wordId, pass, _step);
    }

    private void Enqueue(int wordId, int pass, long dueStep)
    {
        _heap.Enqueue(new ScheduledWord(wordId, pass), (dueStep, _seq++));
    }

    private readonly record struct ScheduledWord(int WordId, int Pass);

    private sealed class EntryComparer : IComparer<(long Due, long Seq)>
    {
        public int Compare((long Due, long Seq) x, (long Due, long Seq) y)
        {
            int c = x.Due.CompareTo(y.Due);
            return c != 0 ? c : x.Seq.CompareTo(y.Seq);
        }
    }
}
