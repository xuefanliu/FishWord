#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成 505 基础词表 resources/basic_505.json，并统计与词库(words)的命中率。

数据来源：《义务教育英语课程标准(2022年版)》附录三「二级词汇表」，共 505 个小学阶段核心词（最基础高频词）。
用途：账号初始化时，把词库中命中本名单的词标为「系统熟词」(known_source='system')，全局排除。

清洗规则：
- a/an -> a, an；be (am, is, are) -> be, am, is, are；have (has) -> have, has（动词人称变形展开）
- bike (=bicycle) / photo (=photograph) / TV (=television) -> 展开等义全称
- colour (AmE color) / favourite (AmE favorite) -> 展开美式拼写
- father (dad) / mother (mum) 等 -> 展开口语变体
- 复数变形 child (pl. children) / foot (pl. feet) 等 -> 仅保留单数主词
- 短语(含空格)不纳入（如 ice cream，无法匹配单词表 spelling）
- 统一转小写，去重排序

运行：
    python tools/build_basic_505.py
"""
import json
import sqlite3
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "resources" / "basic_505.json"
DB = ROOT / "resources" / "fishword.db"

# 清洗后的基础词（含拆分变体，小写，按 A-Z 排列）
WORDS = """
a an about after afternoon again age ago air all also always and angry animal
answer any apple arm art ask astronaut at aunt autumn
baby back bad bag ball banana basketball be am is are beach bear beautiful
because bed bee before begin behind best between big bike bicycle bird birthday
black blackboard blue boat body book box boy bread breakfast bring brother brown
bus busy but buy by
cake call can candle candy cap car card careful cat catch chair chicken child
china chinese chore cinema city class classroom clean clever clock close clothes
cloudy coat cold colour color come computer cook cool cousin cow cup cute
dance day dear desk difficult dinner dirty do doctor dog doll door down draw
dress drink driver duck
ear early earth easy eat egg elephant email english evening every excited exercise eye
face family fan far farm farmer fast father dad favourite favorite feel film find
fine fire fish floor flower food foot football for free friend from front fruit
game garden get gift girl give glass go good goodbye bye grandfather grandpa
grandmother grandma grape grass great green
hair half hand happy hard have has he head healthy hear heavy hello help helpful
her here hi hill him his holiday home hometown horse hospital hot hour house how
hungry hurry hurt
i idea ill in interesting internet it its
job juice jump
keep kid kind kite know
lake last late learn leg lesson let letter library light like lion listen little
live long look lot love lovely lunch
make man many map maths mathematics math may me meat meet middle milk minute miss
money monkey month morning mother mum mouse move mr mrs ms much music must my
name near never new next nice night no noodle nose not now nurse
o'clock of off often ok old on open or orange our out over
panda paper parent park party pe pen pencil people pet phone photograph photo piano
picture pig ping-pong pink place plane plant play playground please police potato put
question quiet
rabbit rain read red rice right river robot room ruler run
sad safe say school schoolbag science sea season see sell share she sheep ship
shirt shoe shop short shorts should show sing sister sit skirt sleep small snow so
some sometimes song sorry soup space speak sport spring stand star stop story street
strong student study subject summer sun sunny supermarket sure sweep swim
table tail take talk tall taxi tea teacher tell thank that the their them then
there these they thin think this those tidy tiger time tired to today toilet tomato
tomorrow too top toy train travel tree trousers try turn tv television
umbrella uncle under up us use
vegetable very visit
wait walk wall want warm wash watch water way we wear weather week well what when
where which white who whose why will win window windy winter wish with woman word
work worker world worry write wrong
year yellow yes you young your
zoo
"""


def build() -> None:
    words: list[str] = []
    for w in WORDS.split():
        w = w.strip().lower()
        if w and w not in words:
            words.append(w)
    words.sort()

    OUT.write_text(json.dumps(words, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[basic_505] 写入 {OUT}，共 {len(words)} 词")

    # 与词库匹配统计（words 只含"有 tag"词条，部分虚词/基础词可能不在，打标时自然跳过）
    conn = sqlite3.connect(DB)
    try:
        cur = conn.cursor()
        spellings = {r[0].strip().lower() for r in cur.execute("SELECT spelling FROM words")}
        matched = [w for w in words if w in spellings]
        missing = [w for w in words if w not in spellings]
        print(f"[basic_505] 词库 words 命中 = {len(matched)} / {len(words)}")
        if missing:
            print(f"[basic_505] 未命中（不在 words 表，打标时跳过）：{missing}")
    finally:
        conn.close()


def mark() -> None:
    """执行 505 打标：把词库中命中名单的词标为系统熟词（幂等），并写 settings 标志位。

    等价于应用启动时 C# 的 `ProgressRepository.MarkBasicWords505`，用于开发期手动打标/测试。
    运行：python tools/build_basic_505.py --mark
    """
    words = json.loads(OUT.read_text(encoding="utf-8"))
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    conn = sqlite3.connect(DB)
    try:
        cur = conn.cursor()
        names = ",".join(["?"] * len(words))
        cur.execute(
            "INSERT INTO user_words(word_id, status, is_known, known_source, created_at) "
            "SELECT w.id, 'known', 1, 'system', ? FROM words w "
            f"WHERE LOWER(w.spelling) IN ({names}) "
            "ON CONFLICT(word_id) DO NOTHING",
            [now] + words,
        )
        cur.execute(
            "INSERT INTO settings(key, value) VALUES('basic_505_marked', '1') "
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value"
        )
        conn.commit()
        total = cur.execute(
            "SELECT COUNT(*) FROM user_words WHERE known_source='system'"
        ).fetchone()[0]
        print(f"[basic_505] 打标完成，system 熟词 = {total}")
    finally:
        conn.close()


if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1 and sys.argv[1] == "--mark":
        mark()
    else:
        build()
