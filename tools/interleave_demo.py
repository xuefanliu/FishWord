#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
分级间隔穿插（Graduated-Lag Interleaving）调度器演示

验证三件事：
  1. 单词穿插出现（不连续同词）
  2. 间隔逐级拉长（遍1→遍2 短，遍2→遍3 长）
  3. "没记住"的词按短间隔插回队列中部（非末尾）

运行：python tools/interleave_demo.py
"""

import heapq


class InterleaveScheduler:
    """小顶堆 + 全局 step 计数器的穿插调度器。"""

    def __init__(self, lag12=4, lag23=8, lag_fail=3):
        self.lag12 = lag12
        self.lag23 = lag23
        self.lag_fail = lag_fail
        self.heap = []      # (due_step, seq, word, pass)
        self.step = 0
        self.seq = 0        # 入堆序号，用于同 due_step 时稳定排序
        self.trace = []     # 产出序列，形如 "w1.1"

    def _push(self, word, pass_no, due_step):
        heapq.heappush(self.heap, (due_step, self.seq, word, pass_no))
        self.seq += 1

    def add_new_words(self, words):
        """把今日新词按 0,1,2,... 错开入堆（遍1）。"""
        for i, w in enumerate(words):
            self._push(w, 1, due_step=i)

    def run(self, fail_words=()):
        """跑完整个队列。fail_words：第 3 遍会'没记住'一次的词（下次再出现即记住）。"""
        pending_fail = set(fail_words)  # 每个词只失败一次，避免死循环
        while self.heap:
            _, _, w, p = heapq.heappop(self.heap)
            self.trace.append(f"{w}.{p}")
            if p == 1:
                self._push(w, 2, self.step + self.lag12)
            elif p == 2:
                self._push(w, 3, self.step + self.lag23)
            else:  # p == 3
                if w in pending_fail:
                    pending_fail.discard(w)
                    # 没记住 → 短间隔插回队列中部，下次再测
                    self._push(w, 3, self.step + self.lag_fail)
                # 否则：记住 → 完成，离开队列
            self.step += 1
        return self.trace


def fmt(trace):
    return "  " + " → ".join(trace)


if __name__ == "__main__":
    words = [f"w{i}" for i in range(1, 11)]

    print("=== 正常学习（10 词，LAG12=4, LAG23=8）===")
    s1 = InterleaveScheduler()
    s1.add_new_words(words)
    print(fmt(s1.run()))
    print()

    print("=== w2 第 3 遍'没记住'（LAG_FAIL=3，观察 w2.3 重复出现的位置）===")
    s2 = InterleaveScheduler()
    s2.add_new_words(words)
    print(fmt(s2.run(fail_words={"w2"})))
    print()

    # 统计：每个词第 3 遍与第 2 遍之间的间隔（验证"间隔拉长"）
    s3 = InterleaveScheduler()
    s3.add_new_words(words)
    tr = s3.run()
    pos = {x: i for i, x in enumerate(tr)}
    print("=== 间隔验证（w1 的遍1→遍2 vs 遍2→遍3）===")
    for w in ["w1", "w2"]:
        p1, p2, p3 = pos[f"{w}.1"], pos[f"{w}.2"], pos[f"{w}.3"]
        print(f"  {w}: 遍1→遍2 隔 {p2 - p1 - 1} 词，遍2→遍3 隔 {p3 - p2 - 1} 词")
