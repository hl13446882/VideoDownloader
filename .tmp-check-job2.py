import sqlite3, json
from pathlib import Path
db = Path.home() / "AppData/Local/VideoDownloader/data/downloads.db"
c = sqlite3.connect(db)
row = c.execute("SELECT request_context_meta_json, source_url, media_family, downloaded_bytes, total_bytes FROM download_jobs WHERE id=?",
                ("7a31dc29-97b3-4723-9acd-e809f0f09ab9",)).fetchone()
meta = json.loads(row[0])
print(json.dumps({k: meta[k] for k in meta if k not in ('Cookies','Headers')}, ensure_ascii=False, indent=2)[:3000])
print('source_url_file', row[1].split('?')[0].split('/')[-1])
print('family', row[2], 'progress', row[3], '/', row[4])
