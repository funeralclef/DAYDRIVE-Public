#!/usr/bin/env python3
"""End-to-end checks against the real app in a disposable niri session."""
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time
import tempfile

from PIL import Image, ImageChops

spec = importlib.util.spec_from_file_location('wm_input', Path(__file__).with_name('wm-test-input.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
pointer = module.Input()
scale = float(os.environ.get('DAYDRIVE_WM_TEST_SCALE', '1'))
assert 1 <= scale <= 1.5
physical_size = (round(1600 * scale), round(1000 * scale))
pointer.resize_compositor(*physical_size)
if scale != 1:
    subprocess.run(['niri', 'msg', 'output', 'winit', 'scale', str(scale)], check=True, capture_output=True)
    time.sleep(0.5)
root = Path(os.environ['DAYDRIVE_VERIFY_DIR'])
data = Path(tempfile.mkdtemp(prefix='edge app data ', dir=root))
dotnet = os.environ.get('DAYDRIVE_VERIFY_DOTNET', 'dotnet')
command = ([os.environ['DAYDRIVE_VERIFY_APP']] if os.environ.get('DAYDRIVE_VERIFY_APP') else
           [dotnet, 'src/IntelligentScheduler.Desktop/bin/Release/net10.0/DAYDRIVE.dll']) + [f'--data-dir={data}']
env = os.environ.copy()
env.pop('DAYDRIVE_SHELL_MODE', None)
env.pop('DAYDRIVE_LAYER_SHELL', None)
env.pop('GDK_BACKEND', None)
# Keep desktop palette tests completely separate from the user's shell files.
config = root / 'appearance-config'
cache = root / 'appearance-cache'
(config / 'noctalia').mkdir(parents=True, exist_ok=True)
(cache / 'noctalia').mkdir(parents=True, exist_ok=True)
env['XDG_CONFIG_HOME'] = str(config)
env['XDG_CACHE_HOME'] = str(cache)
env['NOCTALIA_CONFIG_DIR'] = str(config / 'noctalia')
env['NOCTALIA_CACHE_DIR'] = str(cache / 'noctalia')
env.pop('DAYDRIVE_WALLPAPER', None)
palette_file = config / 'noctalia/colors.json'
palette_file.write_text('{"mPrimary":"#45B9E0"}')
wallpaper_file = cache / 'wallpaper test.png'
Image.new('RGB', (96, 96), (40, 140, 220)).save(wallpaper_file)
(cache / 'noctalia/wallpapers.json').write_text(json.dumps({'wallpapers': {'winit': str(wallpaper_file)}}))


def query(kind):
    result = json.loads(subprocess.check_output(['niri', 'msg', '-j', kind]))
    return [layer for layer in result if layer['namespace'] != 'daydrive-input-target'] if kind == 'layers' else result


def wait_until(check, description, timeout=25):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        assert process.poll() is None, (root / 'edge-application.log').read_text()
        if check(): return
        time.sleep(0.1)
    raise AssertionError(description)


def invoke(*args):
    result = subprocess.run(command + list(args), env=env, capture_output=True, text=True, timeout=15)
    assert result.returncode == 0, result.stderr
    time.sleep(2)
    if '--exit' not in args: assert process.poll() is None
    assert query('windows') == [], 'Edge panels must never become tiled top-level windows'


def snapshot(name):
    path = root / (name + '.png')
    path.unlink(missing_ok=True)
    subprocess.run(['niri', 'msg', 'action', 'screenshot-screen', '--show-pointer=false', '--path', str(path)], check=True, capture_output=True)
    wait_until(lambda: path.exists(), 'Screenshot was not written')
    with Image.open(path) as image:
        assert image.size == physical_size, image.size
        return image.convert('RGB').resize((1600, 1000), Image.Resampling.NEAREST)


with (root / 'edge-application.log').open('w') as log:
    input_log = root / 'background-input.log'
    target = subprocess.Popen([os.environ['DAYDRIVE_VERIFY_INPUT_SURFACE'], str(input_log)], env=env, stdout=log, stderr=log)
    launch = command + ['--show', '--language=en']
    process = subprocess.Popen(launch, env=env, stdout=log, stderr=log)
    (root / 'application.pid').write_text(str(process.pid))
    try:
        wait_until(lambda: (data / '.desktop-instance.lock').exists(), 'The first instance did not acquire its data directory')
        # A second launch can arrive before Avalonia has created its UI dispatcher.
        early = subprocess.run(command + ['--show'], env=env, capture_output=True, text=True, timeout=15)
        assert early.returncode == 0, early.stderr
        wait_until(lambda: len(query('layers')) >= 2, 'Native edge surfaces were not mapped')
        time.sleep(3)  # Allow the application's normal side-window warm-up to finish.
        layers = query('layers')
        assert layers and all(layer['namespace'] == 'daydrive' for layer in layers), layers
        assert all(layer['layer'] == 'Top' for layer in layers), layers
        assert all(layer['keyboard_interactivity'] != 'Exclusive' for layer in layers), layers
        assert query('windows') == []
        initial = snapshot('edge-planner')
        background = initial.getpixel((100, 500))
        assert initial.getpixel((1200, 500)) != background, 'Planner was not placed at the right edge'
        assert initial.getpixel((1155, 127)) == background, 'Rounded corner is not transparent'
        # Prove that the surface beneath DAYDRIVE receives real clicks and keyboard input.
        pointer.click(100, 500)
        pointer.press('F12')
        time.sleep(0.3)
        before_input = input_log.read_text()
        assert 'button' in before_input and 'key' in before_input, 'Background input recorder is not active'
        pointer.move(1200, 500)  # Keep the planner open after calibrating outside its surface.

        sections = [] if os.environ.get('DAYDRIVE_WM_TEST_INPUT_ONLY') == '1' else [
            ('calendar', (400, 500)), ('settings', (750, 500)), ('analytics', (550, 500))]
        for section, sample in sections:
            invoke('--section=' + section)
            wait_until(lambda: len(query('layers')) == 3, f'{section} did not open beside the planner')
            image = snapshot('edge-' + section)
            deadline = time.monotonic() + 15
            while image.getpixel(sample) == background and time.monotonic() < deadline:
                time.sleep(0.5)
                image = snapshot('edge-' + section)
            assert image.getpixel(sample) != background, f'{section} was not positioned to the left'
            assert image.getpixel((1200, 500)) != background, 'Planner disappeared when opening a section'
            pointer.click(1129, 500)
            pointer.press('F12')
            assert input_log.read_text() == before_input, f'Input leaked between the planner and {section}'

            if section == 'settings':
                pointer.click(800, 238)
                pointer.press('F12')
                # Gaps between both sets of petals, the bridge between panels, and the
                # narrow space beside the main capsule must all keep input inside DAYDRIVE.
                for point in [(691, 320), (691, 440), (691, 560), (691, 680),
                              (1144, 380), (1144, 500), (1144, 620), (1129, 500), (1581, 500)]:
                    pointer.click(*point)
                    pointer.press('F12')
                    assert input_log.read_text() == before_input, f'Input leaked through an application gap at {point}'
                    assert len(query('layers')) == 3, f'A gap click closed a panel at {point}'
                snapshot('edge-focus-bridges')
                # Wheel down must survive the smooth-scroll stop event. Previously GTK's
                # absent discrete direction was read as UP, undoing each downward tick.
                region = (735, 230, 1080, 830)
                before = image.crop(region)
                pointer.scroll(1060, 700, 4)
                time.sleep(1)
                after = snapshot('edge-settings-scrolled').crop(region)
                assert ImageChops.difference(before, after).getbbox(), 'Wheel down returned the settings to their initial position'
                pointer.scroll(1060, 700, -4)
                time.sleep(1)
                restored = snapshot('edge-settings-scroll-restored').crop(region)
                assert ImageChops.difference(after, restored).getbbox(), 'Wheel up did not move the settings'
                # A useful icon hint uses the application palette and wraps its explanation.
                pointer.move(696, 407)
                time.sleep(1.1)
                snapshot('edge-settings-tooltip')
                pointer.move(1000, 640)
                time.sleep(1.1)
                snapshot('edge-settings-tooltip-explanation')
                pointer.move(500, 100)

                # Keep the real Linux hardware summary and the enlarged settings tabs
                # reviewable without installing or starting a local model.
                pointer.click(702, 500)
                time.sleep(0.4)
                pointer.scroll(1060, 700, 5)
                time.sleep(0.6)
                snapshot('edge-settings-hardware')
                pointer.click(702, 400)
                pointer.scroll(1060, 700, -20)
                time.sleep(0.6)
                snapshot('edge-settings-appearance')
                pointer.scroll(1060, 700, 3)
                time.sleep(0.6)
                snapshot('edge-settings-appearance-controls')
                pointer.click(702, 300)
                pointer.move(500, 100)

        invoke('--section=tasks')
        pointer.click(1480, 423)  # New task, in the real planner view.
        time.sleep(0.4)
        snapshot('edge-task-composer')
        pointer.click(1320, 522)
        entered_title = 'native wayland input'
        pointer.text(entered_title)
        for point in [(1144, 380), (1144, 500), (1144, 620), (1581, 500)]:
            pointer.click(*point)
            pointer.text('x')
            entered_title += 'x'
        assert input_log.read_text() == before_input, 'A gap click moved typing to the desktop'
        pointer.key('Control_L')
        pointer.press('a')
        pointer.press('c')
        pointer.key('Control_L', False)
        pointer.press('End')
        pointer.key('Control_L')
        pointer.press('v')
        pointer.key('Control_L', False)
        time.sleep(0.3)
        expected_title = entered_title * 2
        if os.environ.get('DAYDRIVE_WM_TEST_COMPOSE') == '1':
            pointer.press('End')
            pointer.press('apostrophe')  # A dead acute accent on the US international layout.
            pointer.press('e')
            time.sleep(0.3)
            expected_title += 'é'
        pointer.click(1280, 587)
        wait_until(lambda: len(query('layers')) == 3, 'The duration picker did not open')
        time.sleep(0.4)
        snapshot('edge-time-picker')
        pointer.click(1460, 432)
        wait_until(lambda: len(query('layers')) == 2, 'The duration picker did not close')
        pointer.click(1300, 653)
        wait_until(lambda: len(query('layers')) == 3, 'The date picker did not open')
        time.sleep(0.4)
        snapshot('edge-date-picker')
        pointer.click(1490, 671)
        wait_until(lambda: len(query('layers')) == 2, 'The date picker did not close')
        pointer.click(1420, 792)
        time.sleep(0.8)
        snapshot('edge-task-input')
        with sqlite3.connect(data / 'intelligent-scheduler.db') as database:
            saved_titles = database.execute('SELECT Title FROM Tasks').fetchall()
            assert saved_titles == [(expected_title,)], (
                f'Native keyboard editing, composition or clipboard did not save the task: {saved_titles!r}')

        if os.environ.get('DAYDRIVE_WM_TEST_INPUT_ONLY') == '1':
            invoke('--exit')
            assert process.wait(timeout=30) == 0
            assert query('layers') == []
            print('Connected input areas, retained text-field focus, keyboard editing and clean exit passed.')
            raise SystemExit(0)

        invoke('--action=ToggleShell')
        wait_until(lambda: len(query('layers')) == 1, 'Hiding the planner did not leave only the strip')
        pointer.move(1590, 500)
        time.sleep(1.3)
        floating = snapshot('edge-strip-hover')
        assert floating.getpixel((1590, 500)) != background, 'The floating capsule is missing'
        assert all(floating.getpixel((x, 500)) == background for x in range(1594, 1600)), 'The capsule is flush with the screen edge'
        pointer.click(1598, 500)
        wait_until(lambda: any(layer['keyboard_interactivity'] == 'OnDemand' for layer in query('layers')),
                   'Clicking the screen edge beside the capsule did not reveal the planner')
        invoke('--action=ToggleWidget')
        wait_until(lambda: any(layer['keyboard_interactivity'] == 'None' for layer in query('layers')) and len(query('layers')) >= 2,
                   'Quick widget was not shown')
        widget = snapshot('edge-widget')
        assert widget.getpixel((1500, 500)) != background, 'Quick widget is not at the edge'
        assert all(layer['keyboard_interactivity'] == 'None' for layer in query('layers')), 'Widget stole keyboard focus'
        pointer.click(1590, 500)  # Inside the widget shadow and the floating capsule.
        wait_until(lambda: any(layer['keyboard_interactivity'] == 'OnDemand' for layer in query('layers')),
                   'The widget shadow intercepted the strip click')
        invoke('--action=ToggleWidget')
        invoke('--action=ToggleWidget')
        pointer.click(1598, 500, button=3)
        time.sleep(0.6)
        snapshot('edge-right-click-widget')
        invoke('--exit')
        assert process.wait(timeout=30) == 0
        assert query('layers') == []

        def color_hits(image, warm):
            region = image.crop((1165, 240, 1560, 840))
            pixels = zip(*[iter(region.tobytes())] * 3)
            return sum(1 for r, g, b in pixels if (r - b > 50 if warm else b - r > 50) and max(r, g, b) - min(r, g, b) > 60)

        for source in ['SystemAccent', 'Wallpaper']:
            # Change only the disposable application's database, while it is stopped.
            with sqlite3.connect(data / 'intelligent-scheduler.db') as database:
                database.execute('UPDATE AppSettings SET ThemeColorSource = ?', (source,))
            palette_file.write_text('{"mPrimary":"#45B9E0"}')
            Image.new('RGB', (96, 96), (40, 140, 220)).save(wallpaper_file)
            process = subprocess.Popen(command + ['--show', '--language=en'], env=env, stdout=log, stderr=log)
            wait_until(lambda: len(query('layers')) >= 2, source + ' did not start')
            time.sleep(4)
            cool = snapshot('edge-' + source + '-cool')
            assert color_hits(cool, False) > 100, source + ' did not apply the desktop colour'
            if source == 'SystemAccent': palette_file.write_text('{"mPrimary":"#F09072"}')
            else: Image.new('RGB', (96, 96), (220, 90, 40)).save(wallpaper_file)
            # A background read, UI dispatch and compositor frame can take more than one
            # polling interval under load. Wait for the observable change, with a deadline.
            deadline = time.monotonic() + 15
            warm = snapshot('edge-' + source + '-warm')
            while color_hits(warm, False) >= color_hits(cool, False) and time.monotonic() < deadline:
                time.sleep(0.5)
                warm = snapshot('edge-' + source + '-warm')
            assert color_hits(warm, True) > 100, source + ' did not update the running theme'
            assert color_hits(warm, False) < color_hits(cool, False), source + ' kept the old accent'
            invoke('--exit')
            assert process.wait(timeout=30) == 0
            assert query('layers') == []
        print('Native placement, connected input areas, screen-edge activation, wheel scrolling, tooltips, live system/wallpaper colours, input, passive focus, IPC and clean exit passed.')
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=5)
            except subprocess.TimeoutExpired: process.kill()
        target.terminate()
        target.wait(timeout=5)
