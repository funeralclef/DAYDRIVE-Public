#!/usr/bin/env python3
"""Native placement smoke test, called only by the disposable compositor runner."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time

assert os.environ.get('DAYDRIVE_VERIFY_COMPOSITOR') == 'hyprland'
root = Path(os.environ['DAYDRIVE_VERIFY_DIR'])
data = tempfile.mkdtemp(prefix='hyprland app data ', dir=root)
command = ([os.environ['DAYDRIVE_VERIFY_APP']] if os.environ.get('DAYDRIVE_VERIFY_APP') else
           [os.environ.get('DAYDRIVE_VERIFY_DOTNET', 'dotnet'),
            'src/IntelligentScheduler.Desktop/bin/Release/net10.0/DAYDRIVE.dll']) + ['--data-dir=' + data]
env = os.environ.copy()
for name in ['DAYDRIVE_SHELL_MODE', 'DAYDRIVE_LAYER_SHELL', 'GDK_BACKEND']: env.pop(name, None)


def query(kind):
    return json.loads(subprocess.check_output(['hyprctl', '-j', kind], timeout=5))


def layers():
    result = []
    for output in query('layers').values():
        for level, surfaces in output['levels'].items():
            for surface in surfaces:
                if surface['namespace'] == 'daydrive' and surface['pid'] == process.pid:
                    assert level == '2', surface
                    result.append(surface)
    return result


def wait_for(check, description):
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        assert process.poll() is None, (root / 'hyprland-application.log').read_text()
        value = check()
        if value: return value
        time.sleep(0.15)
    raise AssertionError(description)


def invoke(*args):
    result = subprocess.run(command + list(args), env=env, capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stderr


with (root / 'hyprland-application.log').open('w') as log:
    bar = subprocess.Popen([env['DAYDRIVE_VERIFY_BAR']], env=env, stdout=log, stderr=log) if env.get('DAYDRIVE_VERIFY_BAR') else None
    if bar is not None: time.sleep(0.7)
    panel_y = 142 if bar is not None else 126
    process = subprocess.Popen(command + ['--show', '--language=en'], env=env, stdout=log, stderr=log)
    try:
        opened = wait_for(lambda: layers() if len(layers()) == 2 else None, 'The strip and planner did not map')
        assert query('clients') == [], 'Native edge panels became ordinary Hyprland clients'
        planner = next(surface for surface in opened if surface['w'] == 450)
        assert (planner['x'], planner['y'], planner['h']) == (1134, panel_y, 748), planner
        strip = next(surface for surface in opened if surface['w'] == 36)
        assert strip['x'] == 1564, strip
        for section, width, x in [('calendar', 881, 253), ('settings', 453, 681), ('analytics', 651, 483)]:
            invoke('--section=' + section)
            wait_for(lambda: any(surface['w'] == width and surface['x'] == x and surface['y'] == panel_y for surface in layers()),
                     section + ' did not align to the left of the planner')
            assert query('clients') == []
            (root / ('hyprland-' + section + '-layers.json')).write_text(json.dumps(query('layers'), indent=2))
        # Capture the disposable outer compositor. Some nested GPU paths do not complete
        # Hyprland screencopy requests; placement assertions above use Hyprland's own IPC.
        subprocess.run(['niri', 'msg', 'action', 'screenshot-screen', '--show-pointer=false',
                        '--path', str(root / 'hyprland-analytics.png')], check=True, timeout=5, capture_output=True)
        invoke('--action=ToggleWidget')
        wait_for(lambda: len(layers()) >= 2 and all(surface['w'] != 450 for surface in layers()), 'Widget did not replace the planner')
        invoke('--exit')
        assert process.wait(timeout=30) == 0
        assert layers() == []
        print('Hyprland: native strip, planner, side-panel geometry, widget, IPC and clean exit passed.')
    finally:
        if bar is not None:
            bar.terminate()
            bar.wait(timeout=5)
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill()
