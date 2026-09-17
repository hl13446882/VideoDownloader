import sqlite3, os
path = os.path.expandvars(r"%LOCALAPPDATA%\VideoDownloader\data\downloads.db")
c = sqlite3.connect(path)
rows = c.execute("""
SELECT substr(id,1,8), status, downloaded_bytes, total_bytes, last_error_code,
       substr(display_name,1,50), substr(source_url,1,100), updated_at
FROM download_jobs
ORDER BY updated_at DESC LIMIT 15
""").fetchall()
for r in rows:
    print(r)
print("--- by status ---")
print(c.execute("SELECT status, COUNT(*) FROM download_jobs GROUP BY status").fetchall())
