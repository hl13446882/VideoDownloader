#!/usr/bin/env python3
"""Small, self-contained licensing service for VideoDownloader."""
import base64
import bcrypt
import hashlib
import hmac
import html
import json
import os
import secrets
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse, urlencode

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature


DATA_DIR = Path(os.environ.get("VD_LICENSE_DATA", "/var/lib/videodownloader-license"))
DATABASE = DATA_DIR / "licenses.db"
PRIVATE_KEY_FILE = DATA_DIR / "signing-key.pem"
SESSION_SECRET_FILE = DATA_DIR / "session-secret.bin"
SESSION_TTL = timedelta(hours=8)
LICENSE_TTL = timedelta(days=7)


def utcnow():
    return datetime.now(timezone.utc)


def iso(value):
    return value.replace(microsecond=0).isoformat().replace("+00:00", "Z")


def db():
    connection = sqlite3.connect(DATABASE)
    connection.row_factory = sqlite3.Row
    return connection


def initialize():
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    with db() as connection:
        connection.executescript("""
            CREATE TABLE IF NOT EXISTS activations (
                machine_id TEXT PRIMARY KEY,
                first_seen_at TEXT NOT NULL,
                approved_at TEXT NULL,
                approved INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS administrators (
                username TEXT PRIMARY KEY,
                password_hash BLOB NOT NULL,
                created_at TEXT NOT NULL
            );
        """)
        # Migrate the first deployment schema: heartbeats must not mutate records.
        columns = {row[1] for row in connection.execute("PRAGMA table_info(activations)")}
        if "last_seen_at" in columns:
            connection.executescript("""
                CREATE TABLE activations_new (
                    machine_id TEXT PRIMARY KEY,
                    first_seen_at TEXT NOT NULL,
                    approved_at TEXT NULL,
                    approved INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO activations_new(machine_id, first_seen_at, approved_at, approved)
                    SELECT machine_id, first_seen_at, approved_at, approved FROM activations;
                DROP TABLE activations;
                ALTER TABLE activations_new RENAME TO activations;
            """)
        columns = {row[1] for row in connection.execute("PRAGMA table_info(activations)")}
        if "revoked_at" not in columns:
            connection.execute("ALTER TABLE activations ADD COLUMN revoked_at TEXT NULL")
        connection.executescript("""
            CREATE TABLE IF NOT EXISTS audit_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT NOT NULL,
                old_value TEXT NOT NULL,
                new_value TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_target ON audit_logs(target);
        """)
        exists = connection.execute("SELECT 1 FROM administrators LIMIT 1").fetchone()
        if not exists:
            password = os.environ.get("VD_LICENSE_INITIAL_PASSWORD", "admin")
            connection.execute(
                "INSERT INTO administrators(username, password_hash, created_at) VALUES (?, ?, ?)",
                ("admin", bcrypt.hashpw(password.encode(), bcrypt.gensalt()), iso(utcnow())))

    if not PRIVATE_KEY_FILE.exists():
        key = ec.generate_private_key(ec.SECP256R1())
        PRIVATE_KEY_FILE.write_bytes(key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption()))
        os.chmod(PRIVATE_KEY_FILE, 0o600)
    if not SESSION_SECRET_FILE.exists():
        SESSION_SECRET_FILE.write_bytes(secrets.token_bytes(32))
        os.chmod(SESSION_SECRET_FILE, 0o600)


def audit(connection, actor, action, target, old_value="", new_value="", when=None):
    connection.execute(
        "INSERT INTO audit_logs(occurred_at, actor, action, target, old_value, new_value) VALUES (?, ?, ?, ?, ?, ?)",
        (when or iso(utcnow()), actor, action, target, old_value, new_value))


def private_key():
    return serialization.load_pem_private_key(PRIVATE_KEY_FILE.read_bytes(), password=None)


def public_key_pem():
    return private_key().public_key().public_bytes(
        serialization.Encoding.PEM,
        serialization.PublicFormat.SubjectPublicKeyInfo).decode()


def base64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def license_payload(machine_id, approved):
    issued = utcnow()
    expires = issued + LICENSE_TTL
    edition = "full" if approved else "demo"
    canonical = "\n".join((machine_id, edition, iso(issued), iso(expires))).encode()
    der_signature = private_key().sign(canonical, ec.ECDSA(hashes.SHA256()))
    r, s = decode_dss_signature(der_signature)
    signature = r.to_bytes(32, "big") + s.to_bytes(32, "big")
    return {
        "machine_id": machine_id,
        "edition": edition,
        "issued_at": iso(issued),
        "expires_at": iso(expires),
        "signature": base64url(signature),
    }


def session_token(username):
    expires = int((utcnow() + SESSION_TTL).timestamp())
    nonce = secrets.token_urlsafe(18)
    body = f"{username}|{expires}|{nonce}".encode()
    signature = hmac.new(SESSION_SECRET_FILE.read_bytes(), body, hashlib.sha256).digest()
    return base64url(body) + "." + base64url(signature)


def parse_session(token):
    try:
        body_encoded, signature_encoded = token.split(".", 1)
        body = base64.urlsafe_b64decode(body_encoded + "=" * (-len(body_encoded) % 4))
        expected = hmac.new(SESSION_SECRET_FILE.read_bytes(), body, hashlib.sha256).digest()
        actual = base64.urlsafe_b64decode(signature_encoded + "=" * (-len(signature_encoded) % 4))
        if not hmac.compare_digest(expected, actual):
            return None
        username, expires, _ = body.decode().split("|", 2)
        return username if int(expires) >= int(utcnow().timestamp()) else None
    except Exception:
        return None


class Handler(BaseHTTPRequestHandler):
    server_version = "VideoDownloaderLicense/1.0"

    def log_message(self, format, *args):
        sys.stdout.write("%s - %s\n" % (self.address_string(), format % args))

    def send_json(self, status, payload):
        data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def read_json(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length > 4096:
            raise ValueError("request too large")
        return json.loads(self.rfile.read(length).decode("utf-8"))

    def read_form(self):
        length = int(self.headers.get("Content-Length", "0"))
        if length > 8192:
            raise ValueError("request too large")
        return {key: values[0] for key, values in parse_qs(self.rfile.read(length).decode()).items()}

    def current_user(self):
        cookie = SimpleCookie(self.headers.get("Cookie"))
        item = cookie.get("vd_admin")
        return parse_session(item.value) if item else None

    def set_login_cookie(self, token):
        self.send_header("Set-Cookie", f"vd_admin={token}; HttpOnly; SameSite=Strict; Path=/; Max-Age={int(SESSION_TTL.total_seconds())}")

    def redirect(self, location, cookie=None):
        self.send_response(HTTPStatus.SEE_OTHER)
        if cookie:
            self.set_login_cookie(cookie)
        self.send_header("Location", location)
        self.end_headers()

    def require_admin(self):
        user = self.current_user()
        if not user:
            self.redirect("/admin/login")
            return None
        return user

    def page(self, title, body):
        return f"""<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><title>{html.escape(title)}</title>
<style>body{{font-family:Segoe UI,Arial,sans-serif;max-width:1080px;margin:36px auto;color:#182026}}table{{border-collapse:collapse;width:100%;margin:16px 0}}th,td{{border:1px solid #ccd3da;padding:8px;text-align:left}}form{{display:inline}}input{{padding:7px;margin:4px}}button{{padding:7px 11px;cursor:pointer}}.ok{{color:#087b3f}}.no{{color:#9b2020}}.bar{{display:flex;justify-content:space-between;align-items:center}}</style></head><body>{body}</body></html>"""

    def send_page(self, title, body, status=HTTPStatus.OK):
        data = self.page(title, body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def records_page(self, user, logs=False):
        args = parse_qs(urlparse(self.path).query)
        query = args.get("q", [""])[0][:128].strip()
        columns = [("machine_id", "机器 ID"), ("first_seen_at", "首次查询时间"),
                   ("approved_at", "获批时间"), ("revoked_at", "取消时间")]
        if logs:
            columns = [("id", "序号"), ("occurred_at", "时间"), ("actor", "操作人"),
                       ("action", "操作"), ("target", "对象 / 机器 ID"),
                       ("old_value", "变更前"), ("new_value", "变更后")]
        sort = args.get("sort", ["id" if logs else "first_seen_at"])[0]
        if sort not in dict(columns): sort = "id" if logs else "first_seen_at"
        direction = "ASC" if args.get("dir", ["desc"])[0] == "asc" else "DESC"
        try: page = max(1, int(args.get("page", ["1"])[0]))
        except ValueError: page = 1
        table, field = ("audit_logs", "target") if logs else ("activations", "machine_id")
        with db() as connection:
            # Identifiers above are selected exclusively from fixed allowlists.
            where = f"instr({field}, ?) > 0"
            count = connection.execute(f"SELECT count(*) FROM {table} WHERE {where}", (query,)).fetchone()[0]
            pages = max(1, (count + 49) // 50)
            page = min(page, pages)
            rows = connection.execute(
                f"SELECT * FROM {table} WHERE {where} ORDER BY {sort} {direction}, {field} ASC LIMIT 50 OFFSET ?",
                (query, (page - 1) * 50)).fetchall()
        path = "/admin/logs" if logs else "/admin"
        def link(**updates):
            params = dict(q=query, sort=sort, dir=direction.lower(), page=page)
            params.update(updates)
            return html.escape(path + "?" + urlencode(params), quote=True)
        headers = "".join(
            f"<th><a href='{link(sort=key, dir='asc' if key == sort and direction == 'DESC' else 'desc', page=1)}'>{label}{(' ↑' if direction == 'ASC' else ' ↓') if key == sort else ''}</a></th>"
            for key, label in columns)
        records = []
        for row in rows:
            cells = "".join(f"<td>{html.escape(str(row[key]) if row[key] is not None else '-')}</td>" for key, _ in columns)
            if not logs:
                machine = html.escape(row['machine_id'])
                cells += f"<td>{'通过' if row['approved'] else '未通过'}</td><td><form method='post' action='/admin/activation/{machine}'><input type='hidden' name='approved' value='{0 if row['approved'] else 1}'><button>{'取消正版' if row['approved'] else '设为正版'}</button></form> <a href='/admin/logs?{html.escape(urlencode(dict(q=row['machine_id'])))}'>查看记录</a></td>"
            records.append("<tr>" + cells + "</tr>")
        if not logs: headers += "<th>状态</th><th>操作</th>"
        title = "操作日志" if logs else "授权管理"
        body = f"<div class='bar'><h1>{title}</h1><div>{html.escape(user)} | <a href='/admin/logout'>退出</a></div></div><p><a href='/admin'>授权管理</a> | <a href='/admin/logs'>查看日志</a> | <a href='/admin/users'>管理员账号</a> | <a href='/admin/password'>修改我的密码</a></p>"
        body += f"<form method='get' action='{path}'><input name='q' value='{html.escape(query, quote=True)}' placeholder='查询机器 ID'><input type='hidden' name='sort' value='{sort}'><input type='hidden' name='dir' value='{direction.lower()}'><button>查询</button> <a href='{path}'>清除</a></form>"
        body += "<style>td{overflow-wrap:anywhere;max-width:280px}th{white-space:nowrap}h1{font-size:26px}body{padding:0 12px}</style>"
        body += "<div style='overflow-x:auto'><table><thead><tr>" + headers + "</tr></thead><tbody>" + ("".join(records) or "<tr><td colspan='7'>暂无记录</td></tr>") + "</tbody></table></div>"
        body += f"<p>共 {count} 条，第 {page} / {pages} 页"
        if page > 1: body += f" | <a href='{link(page=page-1)}'>上一页</a>"
        if page < pages: body += f" | <a href='{link(page=page+1)}'>下一页</a>"
        return self.send_page(title, body + "</p>")

    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/health":
            return self.send_json(HTTPStatus.OK, {"status": "ok"})
        if path == "/api/v1/public-key":
            return self.send_json(HTTPStatus.OK, {"algorithm": "ECDSA-P256-SHA256", "public_key_pem": public_key_pem()})
        if path == "/admin/login":
            return self.send_page("管理员登录", "<h1>VideoDownloader 授权管理</h1><form method='post'><input name='username' placeholder='管理员账号' required autofocus><input name='password' type='password' placeholder='密码' required><button>登录</button></form>")
        if path == "/admin/logout":
            self.send_response(HTTPStatus.SEE_OTHER)
            self.send_header("Set-Cookie", "vd_admin=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0")
            self.send_header("Location", "/admin/login")
            return self.end_headers()
        if path in ("/admin", "/admin/logs"):
            user = self.require_admin()
            if not user:
                return
            return self.records_page(user, logs=path == "/admin/logs")
        if path == "/admin/users":
            if not self.require_admin(): return
            return self.send_page("管理员账号", "<h1>添加管理员</h1><form method='post'><input name='username' placeholder='账号' required><input name='password' type='password' placeholder='初始密码，至少 8 位' required><button>添加</button></form><p><a href='/admin'>返回</a></p>")
        if path == "/admin/password":
            if not self.require_admin(): return
            return self.send_page("修改密码", "<h1>修改密码</h1><form method='post'><input name='current_password' type='password' placeholder='当前密码' required><input name='new_password' type='password' placeholder='新密码，至少 8 位' required><button>保存</button></form><p><a href='/admin'>返回</a></p>")
        self.send_error(HTTPStatus.NOT_FOUND)

    def do_POST(self):
        path = urlparse(self.path).path
        if path == "/api/v1/license/check":
            try:
                payload = self.read_json()
                machine_id = payload.get("machine_id", "")
                if not isinstance(machine_id, str) or len(machine_id) != 64 or any(c not in "0123456789abcdef" for c in machine_id):
                    return self.send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid_machine_id"})
                now = iso(utcnow())
                with db() as connection:
                    inserted = connection.execute("INSERT INTO activations(machine_id, first_seen_at) VALUES (?, ?) ON CONFLICT(machine_id) DO NOTHING", (machine_id, now))
                    if inserted.rowcount:
                        audit(connection, "客户端", "首次查询", machine_id, new_value="DEMO", when=now)
                    row = connection.execute("SELECT approved FROM activations WHERE machine_id=?", (machine_id,)).fetchone()
                return self.send_json(HTTPStatus.OK, license_payload(machine_id, bool(row["approved"])))
            except (ValueError, json.JSONDecodeError):
                return self.send_json(HTTPStatus.BAD_REQUEST, {"error": "invalid_request"})
        if path == "/admin/login":
            form = self.read_form()
            with db() as connection:
                row = connection.execute("SELECT password_hash FROM administrators WHERE username=?", (form.get("username", ""),)).fetchone()
            if row and bcrypt.checkpw(form.get("password", "").encode(), row["password_hash"]):
                return self.redirect("/admin", session_token(form["username"]))
            return self.send_page("管理员登录", "<h1>登录失败</h1><p>账号或密码错误。</p><p><a href='/admin/login'>返回</a></p>", HTTPStatus.UNAUTHORIZED)
        user = self.require_admin()
        if not user:
            return
        if path.startswith("/admin/activation/"):
            machine_id = path.rsplit("/", 1)[-1]
            form = self.read_form()
            if len(machine_id) != 64 or form.get("approved") not in ("0", "1"):
                return self.send_error(HTTPStatus.BAD_REQUEST)
            approved = int(form["approved"])
            with db() as connection:
                row = connection.execute("SELECT approved FROM activations WHERE machine_id=?", (machine_id,)).fetchone()
                if row is None: return self.send_error(HTTPStatus.NOT_FOUND)
                if row["approved"] != approved:
                    now = iso(utcnow())
                    field = "approved_at" if approved else "revoked_at"
                    connection.execute(f"UPDATE activations SET approved=?, {field}=? WHERE machine_id=?", (approved, now, machine_id))
                    audit(connection, user, "设为正版" if approved else "取消正版", machine_id,
                          "DEMO" if approved else "正版", "正版" if approved else "DEMO", now)
            return self.redirect("/admin")
        if path == "/admin/users":
            form = self.read_form(); username = form.get("username", "").strip(); password = form.get("password", "")
            if not username or len(username) > 64 or len(password) < 8:
                return self.send_page("添加管理员", "<p>账号不能为空，密码至少 8 位。</p><p><a href='/admin/users'>返回</a></p>", HTTPStatus.BAD_REQUEST)
            try:
                with db() as connection:
                    connection.execute("INSERT INTO administrators(username, password_hash, created_at) VALUES (?, ?, ?)", (username, bcrypt.hashpw(password.encode(), bcrypt.gensalt()), iso(utcnow())))
                    audit(connection, user, "添加管理员", username, new_value="已创建")
            except sqlite3.IntegrityError:
                return self.send_page("添加管理员", "<p>账号已存在。</p><p><a href='/admin/users'>返回</a></p>", HTTPStatus.CONFLICT)
            return self.redirect("/admin")
        if path == "/admin/password":
            form = self.read_form(); current = form.get("current_password", ""); new = form.get("new_password", "")
            with db() as connection:
                row = connection.execute("SELECT password_hash FROM administrators WHERE username=?", (user,)).fetchone()
                if not row or not bcrypt.checkpw(current.encode(), row["password_hash"]) or len(new) < 8:
                    return self.send_page("修改密码", "<p>当前密码错误，或新密码不足 8 位。</p><p><a href='/admin/password'>返回</a></p>", HTTPStatus.BAD_REQUEST)
                connection.execute("UPDATE administrators SET password_hash=? WHERE username=?", (bcrypt.hashpw(new.encode(), bcrypt.gensalt()), user))
                audit(connection, user, "修改密码", user, new_value="已修改")
            return self.redirect("/admin")
        self.send_error(HTTPStatus.NOT_FOUND)


if __name__ == "__main__":
    initialize()
    port = int(os.environ.get("VD_LICENSE_PORT", "11111"))
    ThreadingHTTPServer(("0.0.0.0", port), Handler).serve_forever()
