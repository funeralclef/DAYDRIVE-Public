#!/usr/bin/env python3
"""Check hashes and exercise Linux installed/portable packages without touching user data."""
import argparse
import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import tarfile
import tempfile
import zipfile


def run(*args, **kwargs):
    subprocess.run([str(arg) for arg in args], check=True, timeout=180, **kwargs)


def check_database(directory):
    database = directory / 'intelligent-scheduler.db'
    assert database.is_file(), f'Missing database: {database}'
    with sqlite3.connect(database) as connection:
        assert connection.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        assert connection.execute('SELECT count(*) FROM CalendarEvents').fetchone()[0] > 0
        assert connection.execute('SELECT count(*) FROM Tasks').fetchone()[0] > 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    packages = args.directory.resolve()
    entries = (packages / 'SHA256SUMS.txt').read_text().splitlines()
    assert len(entries) == 4, 'Expected exactly four release packages'
    for line in entries:
        expected, name = line.split('  ', 1)
        with (packages / name).open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected, name
    windows = next(packages.glob('*win-x64-portable.zip'))
    with zipfile.ZipFile(windows) as archive:
        assert archive.testzip() is None
        names = archive.namelist()
        assert any(name.endswith('/daydrive.portable') for name in names)
        assert any(name.endswith('/docs/USER-MANUAL.md') for name in names)
        assert not any('/Data/' in name or name.endswith('.db') for name in names)
    with tempfile.TemporaryDirectory(prefix='daydrive package check ') as temporary:
        root = Path(temporary)
        with tarfile.open(next(packages.glob('*linux-x64-portable.tar.gz'))) as archive:
            archive.extractall(root, filter='data')
        portable = next(root.glob('*linux-x64-portable'))
        env = os.environ.copy()
        env.pop('INTELLIGENT_SCHEDULER_DATA_DIR', None)
        # Start elsewhere, without --data-dir: this must stay inside the portable folder.
        run(portable / 'DAYDRIVE', '--seed-demo=7', cwd=root, env=env)
        check_database(portable / 'Data')
        assert not (root / 'Data').exists()
        override = root / 'explicit data'
        run(portable / 'DAYDRIVE', '--seed-demo=7', f'--data-dir={override}', cwd=root, env=env)
        check_database(override)
        environment_data = root / 'environment data'
        env['INTELLIGENT_SCHEDULER_DATA_DIR'] = str(environment_data)
        run(portable / 'DAYDRIVE', '--seed-demo=7', cwd=root, env=env)
        check_database(environment_data)
        run('ar', 'x', next(packages.glob('*.deb')), cwd=root)
        assert (root / 'debian-binary').read_text() == '2.0\n'
        installed_root = root / 'installed'
        installed_root.mkdir()
        with tarfile.open(root / 'data.tar.gz') as archive:
            for member in archive.getmembers():
                assert not member.pax_headers, 'dpkg does not support PAX extension headers'
                assert member.uid == member.gid == 0, 'Debian files must be owned by root'
            # Package is built locally; the /usr/bin symlink intentionally points at /opt.
            archive.extractall(installed_root, filter='fully_trusted')
        install = installed_root / 'opt/daydrive'
        assert not (install / 'daydrive.portable').exists()
        assert (install / 'docs/USER-MANUAL.md').is_file()
        assert (installed_root / 'usr/share/applications/daydrive.desktop').is_file()
        installed_data = root / 'installed data'
        run(install / 'DAYDRIVE', '--seed-demo=7', f'--data-dir={installed_data}', cwd=root, env=env)
        check_database(installed_data)
    print('Four package hashes, archive contents, and Linux installed/portable startup passed.')


if __name__ == '__main__':
    main()
