#!/usr/bin/env bash
# Builds every release artifact into packaging/out:
#   Xperience-<ver>-linux-x64.tar.gz        self-contained, glibc distributions
#   Xperience-<ver>-linux-musl-x64.tar.gz   self-contained, musl distributions (Alpine)
#   xperience_<ver>_amd64.deb               Debian / Ubuntu
#   SHA256SUMS
# The version is <VersionPrefix> in Directory.Build.props. Every bundle carries the settings app, the driver
# object and the helper that installs it.
# Then packaging/install-test.sh installs the .deb on Debian and Ubuntu and the musl bundle on Alpine, in
# containers, and fails the release if any of them cannot open the window (needs podman).
# --skip-install-test leaves that out, for a machine without podman; never for a published release.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f scratch/env.sh ] && . scratch/env.sh

INSTALL_TEST=1
for arg in "$@"; do
    case $arg in
        --skip-install-test) INSTALL_TEST=0 ;;
        *) echo "usage: $0 [--skip-install-test]" >&2; exit 2 ;;
    esac
done
if (( INSTALL_TEST )) && ! command -v podman >/dev/null; then
    echo "!! The install test needs podman. Install it, or pass --skip-install-test." >&2
    exit 2
fi
[[ -f LICENSE ]] || { echo "!! There is no LICENSE file: a release has to say what it is licensed under." >&2; exit 2; }

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=${XPERIENCE_OUT:-packaging/out}
APP=src/Xperience.App/Xperience.App.csproj
DRIVER=0010-XPPen__Artist16_3rd.bpf.o
ID=io.github.codingncaffeine.Xperience
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_NOLOGO=1
rm -rf "${OUT:?}" && mkdir -p "$OUT"

# Stale obj/ output can publish XAML that was never compiled: every release starts from clean trees.
for p in src/Xperience.App src/Xperience.Core; do rm -rf "${p:?}/bin" "${p:?}/obj"; done

echo "── driver"
tools/build-driver.sh > /dev/null
[[ -s bin/$DRIVER ]] || { echo "!! The driver object was not built." >&2; exit 3; }
# One probe goes into both bundles, so it must not depend on this machine's C library.
readelf -d bin/xperience-probe 2> /dev/null | grep -q NEEDED && { echo "!! bin/xperience-probe is not static: install the static C library and build again." >&2; exit 3; }

publish() { # rid dir
    echo "── publish $1"
    nice -n 10 dotnet publish "$APP" -c Release -r "$1" --self-contained true -o "$2" -v q --nologo -maxcpucount:4
    rm -f "$2"/*.pdb "$2"/*.xml
}

payload() { # dir
    grep -qa CompiledAvaloniaXaml "$1/xperience.dll" || { echo "!! $1/xperience.dll carries no compiled XAML: refusing to package." >&2; exit 3; }
    [[ -x "$1/xperience" ]] || { echo "!! xperience is missing from $1." >&2; exit 3; }
    [[ -s "$1/xperience.png" ]] || { echo "!! The icon is missing from $1." >&2; exit 3; }
    cp "bin/$DRIVER" "$1/"
    install -m 755 tools/xperience-driver-helper "$1/xperience-driver-helper"
    install -m 755 bin/xperience-probe "$1/xperience-probe"
    cp LICENSE packaging/README.txt "$1/"
    mkdir -p "$1/LICENSES" && cp LICENSES/* "$1/LICENSES/"
    cp "packaging/$ID.desktop" "$1/"
}

for rid in linux-x64 linux-musl-x64; do
    dir=$OUT/publish-$rid
    publish "$rid" "$dir"
    payload "$dir"
    tar -C "$dir" -czf "$OUT/Xperience-$VER-$rid.tar.gz" .
done

echo "── deb"
DEB=$OUT/debroot
PUB=$OUT/publish-linux-x64
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/xperience" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/doc/xperience"
cp -a "$PUB/." "$DEB/usr/lib/xperience/"
rm -rf "$DEB/usr/lib/xperience/"{README.txt,LICENSE,LICENSES,"$ID.desktop"}
printf '#!/bin/sh\nexec /usr/lib/xperience/xperience "$@"\n' > "$DEB/usr/bin/xperience"
chmod 755 "$DEB/usr/bin/xperience"
for s in 16 22 32 48 64 128 256; do
    install -d "$DEB/usr/share/icons/hicolor/${s}x${s}/apps"
    magick assets/icon.png -resize "${s}x${s}" "$DEB/usr/share/icons/hicolor/${s}x${s}/apps/xperience.png"
done
cp "packaging/$ID.desktop" "$DEB/usr/share/applications/"
cp packaging/README.txt "$DEB/usr/share/doc/xperience/README.txt"
{ cat LICENSE; printf '\n\nThe driver (%s) and its sources are under the GNU General Public License, version 2 only:\n\n' "$DRIVER"; cat LICENSES/GPL-2.0-only.txt; } > "$DEB/usr/share/doc/xperience/copyright"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: xperience
Version: $VER
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, zlib1g
Recommends: udev-hid-bpf, pkexec | policykit-1
Maintainer: Xperience <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Xperience
Description: Settings and driver for XPPen pen tablets and pen displays
 Xperience sets up XPPen tablets on KDE Plasma, covering most of the lineup:
 which monitor and which part of it the pen works on, rotation, calibration,
 the pressure curve, and what each of the tablet's keys, dials and the pen's
 buttons does, with a profile per application.
 .
 It comes with an HID-BPF driver, loaded by udev-hid-bpf, that turns the
 tablet's keys and dials from fixed keyboard shortcuts into real tablet
 controls. The settings window installs and removes it.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/xperience_${VER}_amd64.deb" > /dev/null
rm -rf "${DEB:?}"

# Publishing for a runtime leaves a second, frozen build under bin/Release/net10.0/<rid>; running that by
# mistake looks exactly like a fix that never landed.
for p in src/Xperience.App src/Xperience.Core; do
    for rid in linux-x64 linux-musl-x64; do
        rm -rf "${p:?}/bin/Release/net10.0/$rid" "${p:?}/obj/Release/net10.0/$rid"
    done
done

(cd "$OUT" && sha256sum "Xperience-$VER-linux-x64.tar.gz" "Xperience-$VER-linux-musl-x64.tar.gz" "xperience_${VER}_amd64.deb" > SHA256SUMS)

if (( INSTALL_TEST )); then
    XPERIENCE_OUT=$OUT packaging/install-test.sh
else
    echo "!! Install test skipped: these artifacts were not tried on Debian, Ubuntu or Alpine." >&2
fi

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
