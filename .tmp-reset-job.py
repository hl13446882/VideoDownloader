import sqlite3
from datetime import datetime, timezone
from pathlib import Path
db = Path.home() / "AppData/Local/VideoDownloader/data/downloads.db"
job = "7a31dc29-97b3-4723-9acd-e809f0f09ab9"
c = sqlite3.connect(db)
# DownloadStatus.Paused = 2 (typical); verify from existing paused rows
print("sample statuses", c.execute("SELECT status, count(*) FROM download_jobs GROUP BY status").fetchall())
now = datetime.now(timezone.utc).isoformat()
c.execute(
    "UPDATE download_jobs SET status=?, last_error_code=NULL, updated_at=? WHERE id=?",
    (2, now, job),
)
c.commit()
print("updated", c.execute("SELECT status, last_error_code, downloaded_bytes, total_bytes FROM download_jobs WHERE id=?", (job,)).fetchone())
