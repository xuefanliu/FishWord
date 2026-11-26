namespace FishWord.Core.Scheduling;

/// <summary>SM-2 记忆状态。</summary>
public sealed record Sm2State(double EaseFactor = 2.5, int Repetitions = 0, double IntervalDays = 0);

/// <summary>
/// SM-2 遗忘曲线调度（跨天复习）。二档评分：记住 = 5，没记住 = 0。
/// 数据模型已按 FSRS 兼容设计，后续可升级（见 docs/概要设计方案.md 6.1）。
/// </summary>
public static class Sm2Scheduler
{
    public static Sm2State Next(Sm2State state, bool remembered)
    {
        int q = remembered ? 5 : 0;
        if (q < 3)
            return state with { Repetitions = 0, IntervalDays = 1 }; // 没记住 → 重置

        double interval = state.Repetitions switch
        {
            0 => 1,
            1 => 6,
            _ => Math.Round(state.IntervalDays * state.EaseFactor)
        };

        double ease = state.EaseFactor + (0.1 - (5 - q) * (0.08 + (5 - q) * 0.02));
        ease = Math.Max(ease, 1.3);

        return new Sm2State(ease, state.Repetitions + 1, interval);
    }
}
