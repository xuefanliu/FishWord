# -*- coding: utf-8 -*-
import sqlite3

con = sqlite3.connect("resources/fishword.db")
cur = con.cursor()

print("== 同根词抽样（≥4 个同族词的词根）==")
rows = cur.execute("""
    SELECT r.root, r.gloss, COUNT(w.id) AS n
    FROM roots r JOIN words w ON w.root_id = r.id
    GROUP BY r.id HAVING n >= 4
    ORDER BY n DESC LIMIT 12
""").fetchall()
for root, gloss, n in rows:
    family = cur.execute(
        "SELECT spelling FROM words WHERE root_id = (SELECT id FROM roots WHERE root = ?) "
        "ORDER BY spelling LIMIT 8", (root,)).fetchall()
    print(f"  {root}（{gloss}）{n} 词: {', '.join(x[0] for x in family)}")

print()
print("== 用户例子的词根验证 ==")
for rt in ("solv", "solu", "spect", "vis", "vid"):
    r = cur.execute("SELECT id, root, gloss FROM roots WHERE root = ?", (rt,)).fetchone()
    if r:
        family = cur.execute(
            "SELECT spelling FROM words WHERE root_id = ? LIMIT 8", (r[0],)).fetchall()
        print(f"  {r[1]}（{r[2]}）: {', '.join(x[0] for x in family)}")
    else:
        print(f"  {rt}: 无此词根")

print()
print("== 总体 ==")
print("有主词根:", cur.execute("SELECT COUNT(*) FROM words WHERE root_id IS NOT NULL").fetchone()[0],
      "/ 14942")
print("有助记(root非空或etymology):", cur.execute(
    "SELECT COUNT(*) FROM words WHERE mnemonics IS NOT NULL AND mnemonics != ''").fetchone()[0])
con.close()
