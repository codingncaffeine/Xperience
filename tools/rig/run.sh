#!/bin/bash
# SPDX-License-Identifier: GPL-2.0-only
# Proves the driver and its install on a stand-in tablet, with nothing on the desk: boots this machine's own
# kernel in QEMU on the host's root (read-only, under a tmpfs), plugs a virtual Artist 16 3rd into a virtual
# USB bus, installs the driver with the real helper and loader, plays the driver's test reports through the
# kernel and judges what comes out. No network, no sound device, no display. The exit code is the verdict;
# the guest's own record is in build/rig/out.
#   tools/rig/run.sh
# For planted faults, RIG_HELPER, RIG_DRIVER and RIG_PROBE name a changed copy of the helper, of the driver's
# source or of the probe's source, and RIG_STOP_AT the failed line the run may end at.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
rig="$root/build/rig"
out="$rig/out"
kernel_version=$(uname -r)
modules=/lib/modules/$kernel_version
if [ ! -f "$modules/vmlinuz" ]; then
	# The running kernel was replaced on disk; the guest boots the one whose modules are there.
	kernel_version=$(ls /lib/modules | sort -V | tail -n 1)
	modules=/lib/modules/$kernel_version
fi

# What the run needs, checked before anything is built.
missing=""
for tool in qemu-system-x86_64 gcc cpio zstd udev-hid-bpf udevadm clang llvm-strip; do
	command -v "$tool" > /dev/null 2>&1 || missing="$missing $tool"
done
[ -n "$missing" ] && { echo "rig: missing:$missing" >&2; exit 2; }
[ -r /dev/kvm ] && [ -w /dev/kvm ] || { echo "rig: /dev/kvm is not usable" >&2; exit 2; }
[ -f "$modules/vmlinuz" ] || { echo "rig: no kernel image in $modules" >&2; exit 2; }
[ -x "$root/build/venv/bin/python" ] || { echo "rig: run tools/test-driver.sh once first (it makes build/venv)" >&2; exit 2; }
for module in dummy_hcd raw_gadget; do
	modinfo -k "$kernel_version" "$module" > /dev/null 2>&1 || { echo "rig: the kernel has no $module module" >&2; exit 2; }
done
memory_mb=1024
available_mb=$(awk '/MemAvailable/ { print int($2 / 1024) }' /proc/meminfo)
[ "$available_mb" -gt $((memory_mb * 2)) ] || { echo "rig: only ${available_mb} MB free; the guest takes ${memory_mb}" >&2; exit 2; }

rm -rf "$rig"
mkdir -p "$rig/initramfs/modules" "$out"
# A planted fault's run ends at the line it must fail on (tools/rig/controls.sh).
[ -n "${RIG_STOP_AT:-}" ] && printf '%s\n' "$RIG_STOP_AT" > "$out/stop-at"
gcc -O2 -Wall -Wextra -Werror -pthread "$root/tools/rig/fake-tablet.c" -o "$rig/fake-tablet"
gcc -O2 -Wall -Wextra -Werror -static "$root/tools/rig/rig-init.c" -o "$rig/initramfs/init"
"$root/tools/build-driver.sh" "$rig" "${RIG_DRIVER:-$root/driver/0010-XPPen__Artist16_3rd.bpf.c}" > /dev/null
cp "${RIG_HELPER:-$root/tools/xperience-driver-helper}" "$rig/xperience-driver-helper"
if [ -n "${RIG_PROBE:-}" ]; then
	gcc -O2 -Wall -Wextra -Werror -static -I "$root/build/gen" "$RIG_PROBE" -o "$rig/xperience-probe"
fi

for module in netfs 9pnet 9pnet_virtio 9p overlay; do
	file=$(modinfo -k "$kernel_version" -n "$module")
	case "$file" in
	*.ko.zst) cp "$file" "$rig/initramfs/modules/$module.ko.zst" ;;
	*.ko) zstd -q "$file" -o "$rig/initramfs/modules/$module.ko.zst" ;;
	*) echo "rig: $module is built in or missing ($file)" >&2; exit 2 ;;
	esac
done
(cd "$rig/initramfs" && find . | cpio -o -H newc -R 0:0 --quiet) > "$rig/initramfs.cpio"

# The guest may be the first thing the kernel takes away when memory runs short, never the desktop.
echo 1000 > /proc/self/oom_score_adj
status=0
timeout --kill-after=10 300 taskset -c 0-7 nice -n 10 qemu-system-x86_64 \
	-enable-kvm -cpu host -smp 2 -m "$memory_mb" \
	-nodefaults -no-user-config -display none -no-reboot \
	-serial "file:$out/console.log" \
	-kernel "$modules/vmlinuz" -initrd "$rig/initramfs.cpio" \
	-append "console=ttyS0 panic=-1 loglevel=4 rigrepo=$root rigscript=$root/tools/rig/guest.sh" \
	-virtfs "local,path=/,mount_tag=hostroot,security_model=none,readonly=on,multidevs=remap" \
	-virtfs "local,path=$root,mount_tag=repo,security_model=none,readonly=on,multidevs=remap" \
	-virtfs "local,path=$out,mount_tag=out,security_model=none" \
	|| status=$?

if [ ! -f "$out/rc" ]; then
	echo "rig: the guest did not finish (qemu $status); the end of its console:" >&2
	tail -n 30 "$out/console.log" >&2
	exit 1
fi
cat "$out/result.txt" 2> /dev/null
exit "$(cat "$out/rc")"
