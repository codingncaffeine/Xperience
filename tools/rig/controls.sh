#!/bin/bash
# SPDX-License-Identifier: GPL-2.0-only
# Shows that tools/rig/run.sh can see the faults it is there for: plants each fault below in a copy of the
# driver's source or of the helper (the repository's files are never changed), runs the rig on it, and
# requires the rig to fail with the predicted line. The exit code is 0 only when every plant was caught.
#   tools/rig/controls.sh
set -u

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
work="$root/build/rig-controls"
driver="$root/driver/0010-XPPen__Artist16_3rd.bpf.c"
helper="$root/tools/xperience-driver-helper"
probe="$root/driver/xperience-probe.c"
rm -rf "$work"
mkdir -p "$work"

missed=0
count=0
# plant NAME driver|helper|probe TEXT-REPLACED REPLACEMENT LINE-THE-RIG-MUST-FAIL-ON
plant() {
	local name=$1 target=$2 old=$3 new=$4 want=$5 source copy status
	count=$((count + 1))
	if [ "$target" = driver ]; then
		source=$driver
		copy="$work/$name/0010-XPPen__Artist16_3rd.bpf.c"
	elif [ "$target" = probe ]; then
		source=$probe
		copy="$work/$name/xperience-probe.c"
	else
		source=$helper
		copy="$work/$name/xperience-driver-helper"
	fi
	mkdir -p "$work/$name"
	# A literal replacement, done by Python so no character in it is read as a pattern.
	if ! python3 -I -c '
import sys
source, copy, old, new = sys.argv[1:5]
text = open(source).read()
if text.count(old) != 1:
    sys.exit(f"the text to replace is in the source {text.count(old)} times, not once")
open(copy, "w").write(text.replace(old, new))
' "$source" "$copy" "$old" "$new"; then
		echo "control $name: could not be planted" >&2
		missed=$((missed + 1))
		return
	fi
	if cmp -s "$source" "$copy"; then
		echo "control $name: the copy did not change" >&2
		missed=$((missed + 1))
		return
	fi
	chmod +x "$copy"
	if [ "$target" = driver ]; then
		RIG_STOP_AT="$want" RIG_DRIVER="$copy" "$root/tools/rig/run.sh" > "$work/$name.txt" 2>&1
	elif [ "$target" = probe ]; then
		RIG_STOP_AT="$want" RIG_PROBE="$copy" "$root/tools/rig/run.sh" > "$work/$name.txt" 2>&1
	else
		RIG_STOP_AT="$want" RIG_HELPER="$copy" "$root/tools/rig/run.sh" > "$work/$name.txt" 2>&1
	fi
	status=$?
	cp "$root/build/rig/out/record.txt" "$work/$name/record.txt" 2> /dev/null
	if [ "$status" -eq 1 ] && grep -qF -- "$want" "$work/$name.txt"; then
		echo "control $name: caught ($want)"
	else
		echo "control $name: MISSED (exit $status, wanted \"$want\"); see $work/$name.txt" >&2
		missed=$((missed + 1))
	fi
}

# The loader refused the object on the user's desk on 2026-11-17: a copy the compiler made into a call. A copy
# of more than 128 bytes is made one.
plant outlined-copy driver $'hid_set_name(hctx->hid, "Disabled by HID-BPF Xperience");' \
	$'hid_set_name(hctx->hid, "Disabled by HID-BPF Xperience");\n\t\t__builtin_memcpy(data + 1024, data + 2048, 200);' \
	'FAIL install: the helper succeeded'
plant pressure-bit-13 driver 'pressure_high |= (whole[13] & 0x07) << 5;' 'pressure_high |= 0;' \
	'FAIL pen-far-corner-full: hidraw'
plant plain-interfaces-alive driver $'__builtin_memcpy_inline(data, disabled_rdesc, sizeof(disabled_rdesc));\n\t\treturn sizeof(disabled_rdesc);' \
	'(void)disabled_rdesc; return 0;' "FAIL install: only the driver's pen and keypad are left"
# A tablet of 10-byte reports given a 12-byte pen report: the USB transfers never end (found by the rig).
plant short-tablet-long-pen driver 'PUT(raw_length >= 12 ? 24 : 16);' 'PUT(raw_length ? 24 : 24);' \
	'FAIL star03-corner'
# A tablet that did not say what it is must be left as it comes.
plant odd-tablet-driven driver $'if (!tablet_described() || ctx->rdesc_size > sizeof(ctx->rdesc)) {' \
	$'if (ctx->rdesc_size > sizeof(ctx->rdesc)) {' 'FAIL odd tablet no-answer: never asked for raw mode'
# The pen went dead on the user's desk on 2026-11-17: another program had left the vendor interface unbound.
plant interface-not-given-back helper 'basename "$interface" > "$root/sys/bus/usb/drivers/usbhid/bind"' 'true' \
	'FAIL unbound: the helper succeeded'
plant raw-mode-kept helper '> "$node" 2> /dev/null || true' '> /dev/null 2> /dev/null || true' \
	'FAIL remove: the tablet was told to leave raw mode'
# What the tablet says it is must reach udev's record of each HID device, and stay there past the driver's
# own events (the probe once ran on "add" only, and udev kept nothing of it).
plant probe-on-add-only helper 'SUBSYSTEM=="hid", ACTION!="remove"' 'SUBSYSTEM=="hid", ACTION=="add"' \
	'FAIL replug: udev ran the probe for interface'
plant probe-x-loses-high-byte probe '(length > 12 ? data[12] << 16 : 0)' '0' \
	'FAIL probe: interface 0 says what the tablet is'

echo "rig controls: $((count - missed)) of $count planted faults caught"
[ "$missed" -eq 0 ]
