#!/usr/bin/env bash
# Native Wayland edge-shell checks, without touching the user's WM configuration or data.
set -euo pipefail
cd "$(dirname "$0")/.."
daydrive_dotnet=${DAYDRIVE_DOTNET:-dotnet}
daydrive_compositor=${1:-niri}
if [[ "$daydrive_compositor" != niri && "$daydrive_compositor" != hyprland ]]; then
  printf 'Usage: %s [niri|hyprland]\n' "$0" >&2
  exit 2
fi
daydrive_checks=${DAYDRIVE_WM_TEST_OUTPUT:-$(mktemp -d /tmp/daydrive-edge-check.XXXXXX)}
mkdir -p "$daydrive_checks"
daydrive_checks=$(realpath "$daydrive_checks")
# Wayland and niri use AF_UNIX sockets; their paths must stay below sockaddr_un's limit.
daydrive_runtime=$(mktemp -d /tmp/ddwm.XXXXXX)
trap 'rm -rf "$daydrive_runtime"' EXIT
if [[ "${DAYDRIVE_WM_TEST_SKIP_BUILD:-0}" != 1 ]]; then
  "$daydrive_dotnet" build src/IntelligentScheduler.Desktop -c Release
fi
cat > "$daydrive_checks/config.kdl" <<'KDL'
hotkey-overlay { skip-at-startup; }
layout { gaps 0; }
window-rule { match app-id="labwc"; open-fullscreen true; }
KDL
if [[ "${DAYDRIVE_WM_TEST_COMPOSE:-0}" == 1 ]]; then
  cat >> "$daydrive_checks/config.kdl" <<'KDL'
input { keyboard { xkb { layout "us"; variant "intl"; }; }; }
KDL
fi
export DAYDRIVE_VERIFY_ROOT="$PWD"
export DAYDRIVE_VERIFY_DIR="$daydrive_checks"
export DAYDRIVE_VERIFY_DOTNET="$daydrive_dotnet"
export DAYDRIVE_VERIFY_RUNTIME="$daydrive_runtime"
export DAYDRIVE_VERIFY_COMPOSITOR="$daydrive_compositor"
if [[ "$daydrive_compositor" == niri ]]; then
  python3 - "$daydrive_checks/input-surface" <<'PY'
import shlex, subprocess, sys
flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'gtk+-3.0', 'gtk-layer-shell-0'], text=True))
subprocess.run(['cc', 'scripts/wm-test-input-surface.c', '-o', sys.argv[1], *flags], check=True)
PY
  export DAYDRIVE_VERIFY_INPUT_SURFACE="$daydrive_checks/input-surface"
fi
if [[ "${DAYDRIVE_WM_TEST_BAR:-0}" == 1 ]]; then
  python3 - "$daydrive_checks/test-bar" <<'PY'
import shlex, subprocess, sys
flags = shlex.split(subprocess.check_output(['pkg-config', '--cflags', '--libs', 'gtk+-3.0', 'gtk-layer-shell-0'], text=True))
subprocess.run(['cc', 'scripts/wm-test-bar.c', '-o', sys.argv[1], *flags], check=True)
PY
  export DAYDRIVE_VERIFY_BAR="$daydrive_checks/test-bar"
fi
cat > "$daydrive_checks/child.sh" <<'CHILD'
#!/usr/bin/env bash
cd "$DAYDRIVE_VERIFY_ROOT" || exit 1
if [[ "$DAYDRIVE_VERIFY_COMPOSITOR" == hyprland ]]; then
  python3 scripts/verify-hyprland-edge.py > "$DAYDRIVE_VERIFY_DIR/edge-tests.log" 2>&1
else
  python3 scripts/verify-linux-edge.py > "$DAYDRIVE_VERIFY_DIR/edge-tests.log" 2>&1
fi
printf '%s\n' "$?" > "$DAYDRIVE_VERIFY_DIR/result"
if [[ "$DAYDRIVE_VERIFY_COMPOSITOR" == hyprland ]]; then hyprctl dispatch exit
else niri msg action quit --skip-confirmation
fi
CHILD
daydrive_child="$daydrive_checks/child.sh"
if [[ "$daydrive_compositor" == hyprland ]]; then
  command -v "${DAYDRIVE_LABWC:-labwc}" >/dev/null
  command -v Hyprland >/dev/null
  mkdir -p "$daydrive_checks/labwc"
  cat > "$daydrive_checks/labwc/rc.xml" <<'XML'
<labwc_config><keyboard><default /></keyboard><mouse><default /></mouse></labwc_config>
XML
  touch "$daydrive_checks/labwc/autostart" "$daydrive_checks/labwc/environment" "$daydrive_checks/labwc/shutdown"
  python3 - "$daydrive_checks" <<'PY'
from pathlib import Path
import shlex, sys
p = Path(sys.argv[1])
(p / 'hyprland.conf').write_text('monitor = ,1600x1000,auto,1\n'
    'misc {\n disable_hyprland_logo = true\n disable_splash_rendering = true\n}\n'
    'ecosystem {\n no_update_news = true\n no_donation_nag = true\n}\n'
    'exec-once = bash ' + shlex.quote(str(p / 'child.sh')) + '\n')
PY
  cat > "$daydrive_checks/hyprland.sh" <<'HYPR'
#!/usr/bin/env bash
exec env HYPRLAND_NO_SD_VARS=1 HYPRLAND_NO_CRASHREPORTER=1 AQ_DRM_DEVICES=/dev/null \
  Hyprland --config "$DAYDRIVE_VERIFY_DIR/hyprland.conf" > "$DAYDRIVE_VERIFY_DIR/hyprland.log" 2>&1
HYPR
  chmod +x "$daydrive_checks/hyprland.sh"
  cat > "$daydrive_checks/outer.sh" <<'OUTER'
#!/usr/bin/env bash
cd "$DAYDRIVE_VERIFY_ROOT" || exit 1
python3 scripts/wm-test-input.py
export LABWC_UPDATE_ACTIVATION_ENV=0 WLR_BACKENDS=wayland WLR_RENDERER=gles2
if [[ -n "${DAYDRIVE_WM_TEST_DRM_DEVICE:-}" ]]; then export WLR_RENDER_DRM_DEVICE=$DAYDRIVE_WM_TEST_DRM_DEVICE; fi
"${DAYDRIVE_LABWC:-labwc}" --config-dir "$DAYDRIVE_VERIFY_DIR/labwc" \
  --session "$DAYDRIVE_VERIFY_DIR/hyprland.sh" > "$DAYDRIVE_VERIFY_DIR/labwc.log" 2>&1
niri msg action quit --skip-confirmation
OUTER
  daydrive_child="$daydrive_checks/outer.sh"
fi
export DAYDRIVE_VERIFY_CHILD="$daydrive_child"
set +e
timeout 150s xvfb-run -a -s '-screen 0 2400x1600x24' dbus-run-session -- bash -c '
  export DAYDRIVE_WM_TEST_OUTER_DISPLAY=$DISPLAY
  unset WAYLAND_DISPLAY NIRI_SOCKET HYPRLAND_INSTANCE_SIGNATURE
  export XDG_RUNTIME_DIR="$DAYDRIVE_VERIFY_RUNTIME"
  export XDG_CURRENT_DESKTOP=niri WINIT_UNIX_BACKEND=x11
  exec niri --config "$DAYDRIVE_VERIFY_DIR/config.kdl" -- bash "$DAYDRIVE_VERIFY_CHILD"
' > "$daydrive_checks/compositor.log" 2>&1
daydrive_status=$?
set -e
cat "$daydrive_checks/edge-tests.log" 2>/dev/null || true
printf 'Native WM check artifacts: %s\n' "$daydrive_checks"
if [[ "$daydrive_status" != 0 || ! -f "$daydrive_checks/result" || $(cat "$daydrive_checks/result") != 0 ]]; then
  tail -40 "$daydrive_checks/edge-application.log" 2>/dev/null || true
  tail -40 "$daydrive_checks/hyprland-application.log" 2>/dev/null || true
  tail -20 "$daydrive_checks/hyprland.log" 2>/dev/null || true
  tail -20 "$daydrive_checks/labwc.log" 2>/dev/null || true
  tail -40 "$daydrive_checks/compositor.log"
  exit 1
fi
