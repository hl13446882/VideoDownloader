import sqlite3, json
from pathlib import Path
db = Path.home() / "AppData/Local/VideoDownloader/data/downloads.db"
c = sqlite3.connect(db)
cols = [x[1] for x in c.execute("PRAGMA table_info(Downloads)")]
print("cols", cols)
# find table names
print("tables", [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")])
for table in [r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table'")]:
    info = list(c.execute(f"PRAGMA table_info({table})"))
    print(table, [x[1] for x in info])
    try:
        rows = c.execute(f"SELECT * FROM {table} WHERE CAST(Id AS TEXT) LIKE '7a31dc29%' LIMIT 1").fetchall()
        if rows:
            print("FOUND in", table)
            row = dict(zip([x[1] for x in info], rows[0]))
            for k,v in row.items():
                s = str(v)
                if len(s) > 200: s = s[:200]+"..."
                print(f"  {k}={s}")
    except Exception as e:
        print(table, e)
