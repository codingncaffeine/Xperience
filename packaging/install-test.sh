#!/usr/bin/env bash
# Installs each Linux artifact in packaging/out into a clean container and starts it under Xvfb.
# A system passes when the Xperience window exists under the class its desktop entry names, carries its icon,
# no native library is missing, and the driver object and its helper are where the app looks for them:
#   Debian 12, Debian 13, Ubuntu 22.04, Ubuntu 24.04   the .deb
#   Alpine 3.22                                         the linux-musl-x64 bundle
# There is no KDE in the containers, so the window opens saying it cannot reach the desktop's pen settings;
# that is the expected state there. Needs podman. Exits non-zero when any system fails; the report is
# packaging/out/install-test.txt.
set -uo pipefail
cd "$(dirname "$0")/.."

OUT=$(cd "${XPERIENCE_OUT:-packaging/out}" && pwd)
VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
REPORT=$OUT/install-test.txt

if ! command -v podman >/dev/null; then
    echo "!! The install test needs podman. Install it, or skip the test with --skip-install-test." >&2
    exit 2
fi
for f in "xperience_${VER}_amd64.deb" "Xperience-${VER}-linux-musl-x64.tar.gz"; do
    [[ -f "$OUT/$f" ]] || { echo "!! $OUT/$f is missing: build the release first." >&2; exit 2; }
done

# Runs inside each container with $APP (what to start) and $LIB (where the bundle is) set.
SMOKE='mkdir -p /tmp/x/r && chmod 700 /tmp/x/r && export XDG_DATA_HOME=/tmp/x/d XDG_CONFIG_HOME=/tmp/x/c XDG_STATE_HOME=/tmp/x/s XDG_RUNTIME_DIR=/tmp/x/r
Xvfb :5 -screen 0 1400x900x24 >/dev/null 2>&1 & sleep 2; export DISPLAY=:5
[ "$($APP --version)" = "Xperience '"$VER"'" ] && echo VERSION-OK || echo VERSION-WRONG
[ -s "$LIB/0010-XPPen__Artist16_3rd.bpf.o" ] && [ -x "$LIB/xperience-driver-helper" ] && echo DRIVER-OK || echo DRIVER-MISSING
($APP > /tmp/app.log 2>&1 &); sleep 12
if xprop -name Xperience WM_CLASS 2>/dev/null | grep -q "\"xperience\", \"xperience\""; then echo WINDOW-OK; else tail -20 /tmp/app.log; echo WINDOW-MISSING; fi
xprop -name Xperience _NET_WM_ICON 2>/dev/null | grep -q "Icon (" && echo ICON-OK || echo ICON-MISSING
[ -s /tmp/x/s/xperience/xperience.log ] && echo RECORD-OK || echo RECORD-MISSING'

: > "$REPORT"
failed=0
record() { # name output
    local line want
    line="$1: $(echo "$2" | tr '\n' ' ')"
    echo "$line" | tee -a "$REPORT"
    for want in VERSION-OK DRIVER-OK WINDOW-OK ICON-OK RECORD-OK; do
        [[ "$2" == *$want* ]] || failed=1
    done
    # A missing native library can leave the window opening and a feature broken.
    [[ "$2" == *MISSING-LIB* ]] && failed=1
    return 0
}

# Installing needs the network; the program does not, and is started without any. Rootless podman's own
# network needs the tun device; where that is missing the host's network serves the installing step instead.
NET=()
if ! podman run --rm docker.io/library/alpine:3.22 true >/dev/null 2>&1; then
    NET=(--network=host)
    echo "── podman's own network is not available here; installing through the host's network"
fi

# Installs in one container, keeps the result as an image, and runs the check in it with no network.
try() { # name image shell install-commands check-commands
    local name=$1 img=$2 sh=$3 install=$4 check=$5 id="xperience-install-test-$$" out
    podman rm -f "$id" >/dev/null 2>&1
    if ! out=$(timeout 900 podman run --name "$id" "${NET[@]}" -v "$OUT:/out:ro" "docker.io/library/$img" "$sh" -c "$install" 2>&1); then
        record "$name" "INSTALL-FAILED $(echo "$out" | tail -5)"
        podman rm -f "$id" >/dev/null 2>&1
        return
    fi
    podman commit -q "$id" "localhost/$id" >/dev/null
    podman rm -f "$id" >/dev/null 2>&1
    out=$(timeout 300 podman run --rm --network=none "localhost/$id" "$sh" -c "$check" 2>&1 | tail -10)
    podman rmi -f "localhost/$id" >/dev/null 2>&1
    record "$name" "$out"
}

for img in debian:12 debian:13 ubuntu:22.04 ubuntu:24.04; do
    echo "── install test: $img"
    try "$img" "$img" bash \
        "export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends /out/xperience_${VER}_amd64.deb xvfb x11-utils >/tmp/apt.log 2>&1 || { tail -5 /tmp/apt.log; exit 1; }" \
        "ldd /usr/lib/xperience/libSkiaSharp.so 2>&1 | grep -E 'not found|Error loading' | sed 's/^/MISSING-LIB /'; grep -q '^StartupWMClass=xperience\$' /usr/share/applications/io.github.codingncaffeine.Xperience.desktop && [ -s /usr/share/icons/hicolor/256x256/apps/xperience.png ] && echo ENTRY-OK || echo ENTRY-MISSING; APP=xperience; LIB=/usr/lib/xperience; $SMOKE"
    grep -q "^$img: .*ENTRY-OK" "$REPORT" || failed=1
done

echo "── install test: alpine:3.22 (musl)"
try "alpine:3.22 (musl bundle)" alpine:3.22 sh \
    "apk add -q bash icu-libs fontconfig font-dejavu libx11 libice libsm libxi libxcursor libxrandr libxext libxrender libstdc++ libgcc mesa-egl xvfb xprop >/tmp/apk.log 2>&1 || { tail -5 /tmp/apk.log; exit 1; }; mkdir -p /opt/xp && tar -xzf /out/Xperience-${VER}-linux-musl-x64.tar.gz -C /opt/xp" \
    "ldd /opt/xp/libSkiaSharp.so 2>&1 | grep -E 'not found|Error loading' | sed 's/^/MISSING-LIB /'; APP=/opt/xp/xperience LIB=/opt/xp bash -c '$SMOKE'"

if (( failed )); then
    echo "!! The install test failed on at least one system; see $REPORT." >&2
    exit 1
fi
echo "── install test: every system opened the window."
