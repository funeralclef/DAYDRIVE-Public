#!/usr/bin/env python3
"""Run only inside verify-niri.sh's isolated compositor, never on the user's desktop."""
import json
import os
from pathlib import Path
import subprocess
import time

root = Path(os.environ['DAYDRIVE_VERIFY_DIR'])
assert str(root / 'runtime') == os.environ['XDG_RUNTIME_DIR']
assert os.environ['NIRI_SOCKET'].startswith(str(root / 'runtime'))


def windows():
    return json.loads(subprocess.check_output(['niri', 'msg', '-j', 'windows']))


def action(name, *arguments):
    subprocess.run(['niri', 'msg', 'action', name, *map(str, arguments)], check=True)
    time.sleep(0.3)


assert windows() == [], 'The isolated compositor should have no other application windows'
env = os.environ.copy()
env['INTELLIGENT_SCHEDULER_DATA_DIR'] = str(root / 'app data')
# Deliberately let the real application auto-detect Wayland; no --shell-mode override.
env.pop('DAYDRIVE_SHELL_MODE', None)
env['DAYDRIVE_LAYER_SHELL'] = '0'  # Exercise automatic fallback when the native host is unavailable.
with (root / 'application.log').open('w') as log:
    process = subprocess.Popen([os.environ['DAYDRIVE_VERIFY_DOTNET'],
        'src/IntelligentScheduler.Desktop/bin/Release/net10.0/DAYDRIVE.dll', '--language=en'], env=env, stdout=log, stderr=log)
    try:
        deadline = time.monotonic() + 30
        while not windows() and time.monotonic() < deadline:
            assert process.poll() is None, 'Application exited before showing its window'
            time.sleep(0.1)
        time.sleep(2)
        opened = windows()
        assert len(opened) == 1, opened
        window_id = opened[0]['id']
        assert opened[0]['title'] == 'DAYDRIVE', opened
        if not opened[0]['is_floating']:
            action('toggle-window-floating', '--id', window_id)
        action('set-window-width', '--id', window_id, 900)
        action('set-window-height', '--id', window_id, 650)
        action('move-floating-window', '--id', window_id, '-x', 100, '-y', 30)
        placed = windows()[0]
        time.sleep(4)  # Longer than the old edge handle's recurring three-second guard.
        later = windows()
        assert len(later) == 1 and later[0]['id'] == window_id, later
        assert later[0]['layout']['tile_pos_in_workspace_view'] == placed['layout']['tile_pos_in_workspace_view'], (placed, later)
        assert later[0]['layout']['window_size'] == [900, 650], later
        action('close-window', '--id', window_id)
        assert process.wait(timeout=30) == 0, (root / 'application.log').read_text()
        assert windows() == []
        print('Real app startup, one top-level window, compositor-controlled position/resize, and clean exit passed in niri.')
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
