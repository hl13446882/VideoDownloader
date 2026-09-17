import sqlite3, os
db = os.path.expandvars(r"%LOCALAPPDATA%\VideoDownloader\data\downloads.db")
con = sqlite3.connect(db)
con.execute(
    """UPDATE download_jobs
       SET expected_total_bytes = 445523331,
           last_error_code = NULL
       WHERE id = ?""",
    ("7a31dc29-97b3-4723-9acd-e809f0f09ab9",),
)
con.commit()
print(con.execute(
    "SELECT status, expected_total_bytes, last_error_code, downloaded_bytes FROM download_jobs WHERE id = ?",
    ("7a31dc29-97b3-4723-9acd-e809f0f09ab9",),
).fetchone())
