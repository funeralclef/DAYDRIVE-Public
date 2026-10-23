#!/usr/bin/env python3
"""Build self-contained x64 packages on Linux. Requires dotnet, makensis, and ar."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'src/IntelligentScheduler.Desktop/IntelligentScheduler.Desktop.csproj'
VERSION = ET.parse(PROJECT).getroot().findtext('PropertyGroup/Version')


def run(*args, **kwargs):
    subprocess.run([str(arg) for arg in args], check=True, **kwargs)


def archive_tree(source, destination):
    def root_owner(info):
        info.uid = info.gid = 0
        info.uname = info.gname = 'root'
        return info

    # dpkg accepts GNU/ustar, but rejects Python's default PAX extension headers.
    with tarfile.open(destination, 'w:gz', format=tarfile.GNU_FORMAT) as archive:
        for path in sorted(source.iterdir()):
            archive.add(path, arcname=path.name, filter=root_owner)


def copy_dependency_notices(publish):
    assets = json.loads((PROJECT.parent / 'obj/project.assets.json').read_text())
    folders = [Path(folder) for folder in assets['packageFolders']]
    for identity, library in assets['libraries'].items():
        if library['type'] != 'package':
            continue
        package_directory = next((folder / library['path'] for folder in folders
                                  if (folder / library['path']).is_dir()), None)
        if package_directory is None:
            raise RuntimeError(f'Missing restored package: {identity}')
        package = next(package_directory.glob('*.nupkg'))
        with zipfile.ZipFile(package) as archive:
            for name in archive.namelist():
                basename = Path(name).name
                if not (name.endswith('.nuspec') or re.match(
                    r'^(licen[cs]e|copying|copyright|notice|third-party-notices|thirdpartynotices|ofl)([.\-].*)?$',
                    basename, re.IGNORECASE)):
                    continue
                # Flatten archive paths to keep package metadata separate from executable code.
                target = publish / 'licenses' / identity.replace('/', '-') / name.replace('/', '_')
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(name))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/release')
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    work = ROOT / 'artifacts/package-work'
    if work.exists():
        shutil.rmtree(work)
    work.mkdir(parents=True)
    packages = []
    for rid in ('win-x64', 'linux-x64'):
        publish = work / rid
        run(args.dotnet, 'publish', PROJECT, '-c', 'Release', '-r', rid,
            '--self-contained', 'true', '-p:PublishSingleFile=false',
            '-p:PublishTrimmed=false', '-o', publish, cwd=ROOT)
        copy_dependency_notices(publish)
        # A package must never inherit developer databases, marker files, or credentials.
        for path in publish.rglob('*'):
            if path.name in ('Data', 'daydrive.portable', '.assistant-secrets.key') or path.suffix in ('.db', '.log'):
                raise RuntimeError(f'Unexpected runtime data in publish output: {path}')
        for path in publish.rglob('*.pdb'):
            path.unlink()
        stem = f'DAYDRIVE-{VERSION}-{rid}'
        portable_name = stem + '-portable'
        portable = work / portable_name
        shutil.copytree(publish, portable)
        (portable / 'daydrive.portable').write_text('Keep this file beside DAYDRIVE to store data in ./Data.\n')
        if rid == 'win-x64':
            package = output / (portable_name + '.zip')
            with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
                for path in sorted(portable.rglob('*')):
                    if path.is_file():
                        archive.write(path, path.relative_to(work))
            packages.append(package)
            # Enumerate owned files instead of recursively deleting the installation directory.
            uninstall = work / 'uninstall-files.nsh'
            files = sorted((p for p in publish.rglob('*') if p.is_file()))
            directories = sorted((p for p in publish.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True)
            def nsis_path(path):
                return str(path.relative_to(publish)).replace('/', '\\')
            uninstall.write_text('\n'.join(
                [f'Delete "$INSTDIR\\{nsis_path(p)}"' for p in files] +
                [f'RMDir "$INSTDIR\\{nsis_path(p)}"' for p in directories]) + '\n')
            package = output / (stem + '-setup.exe')
            run('makensis', '-V2', f'-DVERSION={VERSION}', f'-DPUBLISH_DIR={publish}',
                f'-DOUTPUT={package}', f'-DUNINSTALL_FILES={uninstall}', ROOT / 'packaging/windows.nsi')
            packages.append(package)
        else:
            (publish / 'DAYDRIVE').chmod(0o755)
            (portable / 'DAYDRIVE').chmod(0o755)
            package = output / (portable_name + '.tar.gz')
            with tarfile.open(package, 'w:gz') as archive:
                archive.add(portable, arcname=portable_name)
            packages.append(package)
            deb = work / 'deb'
            install = deb / 'data/opt/daydrive'
            shutil.copytree(publish, install)
            applications = deb / 'data/usr/share/applications'
            applications.mkdir(parents=True)
            (applications / 'daydrive.desktop').write_text(
                '[Desktop Entry]\nType=Application\nName=DAYDRIVE\n'
                'Comment=Plan your day and track your work\nExec=daydrive\n'
                'Terminal=false\nCategories=Office;Calendar;\nStartupWMClass=DAYDRIVE\n')
            binaries = deb / 'data/usr/bin'
            binaries.mkdir(parents=True)
            (binaries / 'daydrive').symlink_to('/opt/daydrive/DAYDRIVE')
            control = deb / 'control'
            control.mkdir()
            installed_kib = sum(p.stat().st_size for p in install.rglob('*') if p.is_file()) // 1024
            (control / 'control').write_text(
                f'Package: daydrive\nVersion: {VERSION}\nArchitecture: amd64\n'
                'Maintainer: funeralclef <funeralclef@users.noreply.github.com>\n'
                'Section: utils\nPriority: optional\n'
                f'Installed-Size: {installed_kib}\n'
                'Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libicu70 | libicu72 | libicu74 | libicu76 | libicu78, '
                'libssl3 | libssl3t64, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libxext6, libxrandr2, libxi6, libxcursor1, '
                'libgtk-3-0t64 | libgtk-3-0, libgtk-layer-shell0 (>= 0.6)\n'
                'Recommends: xwayland\n'
                'Homepage: https://github.com/funeralclef/DAYDRIVE-Public\n'
                'Description: Desktop planner with optional AI assistance\n'
                ' Tasks, calendar, project steps, work sessions, and analytics.\n')
            (deb / 'debian-binary').write_text('2.0\n')
            archive_tree(control, deb / 'control.tar.gz')
            archive_tree(deb / 'data', deb / 'data.tar.gz')
            package = output / (stem + '.deb')
            package.unlink(missing_ok=True)
            run('ar', 'r', package, 'debian-binary', 'control.tar.gz', 'data.tar.gz', cwd=deb)
            packages.append(package)
    checksums = output / 'SHA256SUMS.txt'
    checksums.write_text(''.join(f'{hashlib.file_digest(path.open("rb"), "sha256").hexdigest()}  {path.name}\n' for path in sorted(packages)))
    for path in packages + [checksums]:
        print(f'{path.name}: {path.stat().st_size:,} bytes')


if __name__ == '__main__':
    main()
