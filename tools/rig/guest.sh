#!/bin/bash
# SPDX-License-Identifier: GPL-2.0-only
# The guest side of tools/rig: runs as the test machine's first process, on the host's root seen read-only
# under a tmpfs. Brings up the parts of a system the driver's install needs (udev, bpffs, the virtual USB
# bus), then hands over to the judge. Its verdict lands in /rig-out; the machine then powers off.
set -u
export PATH=/usr/local/sbin:/usr/local/bin:/usr/bin
repo=$(dirname "$(dirname "$(readlink -f "$0")")")
repo=$(dirname "$repo")

mount -t proc proc /proc
mount -t sysfs sysfs /sys
mount -t devtmpfs devtmpfs /dev
mkdir -p /dev/pts /dev/shm
mount -t devpts devpts /dev/pts
# The repository is shared at its path on the host, which may be under /run; it is carried across the
# guest's own /run.
mkdir -p /rig-repo
mount --move "$repo" /rig-repo
for dir in /run /tmp /var/tmp /var/log; do mount -t tmpfs tmpfs "$dir"; done
mkdir -p "$repo"
mount --move /rig-repo "$repo"
mount -t bpf bpf /sys/fs/bpf
mount -t debugfs debugfs /sys/kernel/debug 2>/dev/null
hostname xperience-rig

finish() {
	echo "$1" > /rig-out/rc
	sync
	echo o > /proc/sysrq-trigger
	sleep 10
}

# A clean system: whatever of Xperience the host has installed is taken away again, in the overlay only.
rm -f /etc/udev/rules.d/70-xperience.rules /etc/udev/rules.d/99-hid-bpf-0010-XPPen__Artist16_3rd*.rules \
	/etc/udev-hid-bpf/0010-XPPen__Artist16_3rd.bpf.o

/usr/lib/systemd/systemd-udevd --daemon > /rig-out/udevd.log 2>&1 || finish 90
udevadm trigger --action=add > /dev/null 2>&1
udevadm settle --timeout=20
modprobe dummy_hcd || finish 91
modprobe raw_gadget || finish 91

"$repo/build/venv/bin/python" -I "$repo/tools/rig/check.py" "$repo" /rig-out
finish $?
