import http.client
import json
import tempfile
import threading
import unittest
from pathlib import Path
from urllib.parse import urlencode

import licensing_server as app


class AdminTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        app.DATA_DIR = Path(self.temp.name)
        app.UPDATE_ROOT = Path(self.temp.name) / 'updates'
        app.DATABASE = app.DATA_DIR / 'licenses.db'
        app.PRIVATE_KEY_FILE = app.DATA_DIR / 'key.pem'
        app.SESSION_SECRET_FILE = app.DATA_DIR / 'secret.bin'
        # Exercise migration of a previously deployed schema with approved data.
        with app.db() as connection:
            connection.execute('CREATE TABLE activations(machine_id TEXT PRIMARY KEY, first_seen_at TEXT NOT NULL, approved_at TEXT, approved INTEGER NOT NULL DEFAULT 0)')
            connection.execute('INSERT INTO activations VALUES (?, ?, ?, 1)', ('f'*64, '2020-01-01', '2021-01-01'))
        app.initialize()
        app.initialize()
        self.server = app.ThreadingHTTPServer(('127.0.0.1', 0), app.Handler)
        self.thread = threading.Thread(target=self.server.serve_forever)
        self.thread.start()
        self.cookie = 'vd_admin=' + app.session_token('admin')

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
        import shutil
        shutil.rmtree(self.temp.name, ignore_errors=True)

    def request(self, path, data=None, api=False, authorized=True):
        headers = {'Cookie': self.cookie} if authorized else {}
        body = None
        if data is not None:
            body = json.dumps(data) if api else urlencode(data)
            headers['Content-Type'] = 'application/json' if api else 'application/x-www-form-urlencoded'
        connection = http.client.HTTPConnection(*self.server.server_address)
        connection.request('POST' if data is not None else 'GET', path, body, headers)
        response = connection.getresponse()
        raw = response.read()
        status = response.status
        connection.close()
        if api and path.endswith('/delta'):
            return status, raw
        return status, raw.decode()

    def test_first_query_approval_revocation_and_history(self):
        machine = 'a'*64
        for _ in range(2):
            self.assertEqual(200, self.request('/api/v1/license/check', {'machine_id': machine}, api=True)[0])
        with app.db() as connection:
            first = connection.execute('SELECT first_seen_at FROM activations WHERE machine_id=?', (machine,)).fetchone()[0]
            self.assertEqual(1, connection.execute('SELECT count(*) FROM audit_logs').fetchone()[0])
            self.assertEqual(1, connection.execute('SELECT approved FROM activations WHERE machine_id=?', ('f'*64,)).fetchone()[0])
        for approved in (1, 1, 0):
            self.assertEqual(303, self.request('/admin/activation/' + machine, {'approved': approved})[0])
        with app.db() as connection:
            row = connection.execute('SELECT * FROM activations WHERE machine_id=?', (machine,)).fetchone()
            self.assertEqual(first, row['first_seen_at'])
            self.assertTrue(row['approved_at'])
            self.assertTrue(row['revoked_at'])
            self.assertEqual(0, row['approved'])
            self.assertEqual(3, connection.execute('SELECT count(*) FROM audit_logs').fetchone()[0])
        status, page = self.request('/admin/logs?q=' + machine)
        self.assertEqual(200, status)
        for label in ('首次查询', '设为正版', '取消正版'): self.assertIn(label, page)
        self.assertEqual(303, self.request('/admin/logs', authorized=False)[0])

    def test_search_sort_and_pagination(self):
        with app.db() as connection:
            for index in range(55):
                connection.execute('INSERT INTO activations(machine_id, first_seen_at) VALUES (?, ?)', (f'{index:064x}', f'2026-01-01T00:00:{index:02d}Z'))
        for column in ('machine_id', 'first_seen_at', 'approved_at', 'revoked_at'):
            for direction in ('asc', 'desc'):
                self.assertEqual(200, self.request(f'/admin?sort={column}&dir={direction}')[0])
        ascending = self.request('/admin?sort=machine_id&dir=asc')[1]
        self.assertLess(ascending.index(f'{0:064x}'), ascending.index(f'{1:064x}'))
        self.assertIn('下一页', ascending)
        self.assertNotIn(f'{54:064x}', ascending)
        self.assertIn(f'{54:064x}', self.request('/admin?sort=machine_id&dir=asc&page=2')[1])
        result = self.request('/admin?q=' + 'f'*64)[1]
        self.assertIn('共 1 条', result)
        self.assertNotIn(f'{0:064x}', result)
        self.assertEqual(200, self.request('/admin?sort=bad&page=invalid&q=%27')[0])

    def test_admin_changes_do_not_log_passwords(self):
        self.assertEqual(303, self.request('/admin/users', {'username': 'second', 'password': 'test-password-123'})[0])
        self.assertEqual(303, self.request('/admin/password', {'current_password': 'admin', 'new_password': 'new-password-456'})[0])
        with app.db() as connection:
            rows = connection.execute('SELECT * FROM audit_logs').fetchall()
            self.assertEqual(2, len(rows))
            dump = str([tuple(row) for row in rows])
            self.assertNotIn('test-password-123', dump)
            self.assertNotIn('new-password-456', dump)

    def _seed_release(self):
        channel = app.UPDATE_ROOT / 'beta'
        files = channel / 'files'
        files.mkdir(parents=True, exist_ok=True)
        payload = b'hello-update'
        target = files / 'app'
        target.mkdir(parents=True, exist_ok=True)
        (target / 'VideoDownloader.exe').write_bytes(payload)
        digest = __import__('hashlib').sha256(payload).hexdigest()
        manifest = {
            'channel': 'beta',
            'version': '0.2.1-beta',
            'published_at': '2026-09-12T00:00:00Z',
            'release_notes': 'test',
            'files': [{'path': 'app/VideoDownloader.exe', 'sha256': digest, 'size': len(payload)}],
        }
        (channel / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        return digest

    def test_update_requires_full_license_and_serves_delta(self):
        digest = self._seed_release()
        machine = 'b' * 64
        demo = json.loads(self.request('/api/v1/license/check', {'machine_id': machine}, api=True)[1])
        status, body = self.request('/api/v1/update/manifest', {'channel': 'beta', 'license': demo}, api=True)
        self.assertEqual(403, status)
        self.assertIn('not_full', body)
        self.assertEqual(303, self.request('/admin/activation/' + machine, {'approved': 1})[0])
        full = json.loads(self.request('/api/v1/license/check', {'machine_id': machine}, api=True)[1])
        status, body = self.request('/api/v1/update/manifest', {'channel': 'beta', 'license': full}, api=True)
        self.assertEqual(200, status)
        manifest = json.loads(body)
        self.assertEqual('0.2.1-beta', manifest['version'])
        status, raw = self.request(
            '/api/v1/update/delta',
            {'channel': 'beta', 'paths': ['app/VideoDownloader.exe'], 'license': full},
            api=True)
        self.assertEqual(200, status)
        self.assertEqual(b'PK', raw[:2])
        status, body = self.request(
            '/api/v1/update/delta',
            {'channel': 'beta', 'paths': ['../etc/passwd'], 'license': full},
            api=True)
        self.assertEqual(403, status)
        self.assertEqual(digest, manifest['files'][0]['sha256'])
        self.assertEqual(200, self.request('/admin/updates')[0])


if __name__ == '__main__':
    unittest.main()
