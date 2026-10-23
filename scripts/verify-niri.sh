#!/usr/bin/env bash
# Run GUI checks in a separate niri/Xwayland session inside Xvfb.
set -euo pipefail
cd "$(dirname "$0")/.."
daydrive_root=$PWD
daydrive_dotnet=${DAYDRIVE_DOTNET:-dotnet}
daydrive_checks=$(mktemp -d /tmp/daydrive-niri-check.XXXXXX)
mkdir -m 700 "$daydrive_checks/runtime"
trap 'rm -rf "$daydrive_checks"' EXIT
"$daydrive_dotnet" build tests/IntelligentScheduler.Desktop.Tests -c Release
cat > "$daydrive_checks/config.kdl" <<'KDL'
hotkey-overlay { skip-at-startup; }
layout { gaps 0; }
KDL
export DAYDRIVE_VERIFY_ROOT="$daydrive_root"
export DAYDRIVE_VERIFY_DOTNET="$daydrive_dotnet"
export DAYDRIVE_VERIFY_DIR="$daydrive_checks"
cat > "$daydrive_checks/child.sh" <<'CHILD'
#!/usr/bin/env bash
set +e
cd "$DAYDRIVE_VERIFY_ROOT" || exit 1
export DAYDRIVE_NATIVE_WM_TEST=1
"$DAYDRIVE_VERIFY_DOTNET" test --project tests/IntelligentScheduler.Desktop.Tests -c Release --no-build > "$DAYDRIVE_VERIFY_DIR/tests.log" 2>&1
daydrive_status=$?
if [[ "$daydrive_status" == 0 ]]; then
  python3 scripts/verify-niri-startup.py > "$DAYDRIVE_VERIFY_DIR/startup.log" 2>&1
  daydrive_status=$?
fi
printf '%s\n' "$daydrive_status" > "$DAYDRIVE_VERIFY_DIR/result"
niri msg action quit --skip-confirmation
CHILD
set +e
timeout 180s xvfb-run -a -s '-screen 0 1600x1000x24' dbus-run-session -- \
  env -u WAYLAND_DISPLAY -u NIRI_SOCKET XDG_RUNTIME_DIR="$daydrive_checks/runtime" \
  XDG_CURRENT_DESKTOP=niri WINIT_UNIX_BACKEND=x11 \
  niri --config "$daydrive_checks/config.kdl" -- bash "$daydrive_checks/child.sh" > "$daydrive_checks/compositor.log" 2>&1
daydrive_status=$?
set -e
cat "$daydrive_checks/tests.log" 2>/dev/null || true
cat "$daydrive_checks/startup.log" 2>/dev/null || true
if [[ "$daydrive_status" != 0 || ! -f "$daydrive_checks/result" || $(cat "$daydrive_checks/result") != 0 ]]; then
  cat "$daydrive_checks/application.log" 2>/dev/null || true
  cat "$daydrive_checks/compositor.log"
  exit 1
fi
