#!/usr/bin/env bash
# Tests the driver helper without touching the system: it installs into a folder, and the two system tools it
# calls are stand-ins that write down how they were called.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
helper="$root/tools/xperience-driver-helper"
work="$(mktemp -d)"
trap 'rm -rf "${work:?}"' EXIT

name="0010-XPPen__Artist16_3rd.bpf.o"
fake="$work/fake-root"
mkdir -p "$work/bin" "$fake/sys/bus/hid/devices/0003:28BD:097E.0007" "$fake/sys/bus/hid/devices/0003:28BD:097E.0009" \
	"$fake/sys/bus/hid/devices/0003:046D:C52B.0001"
: > "$work/$name"
: > "$work/calls"
# The tablet's first and third interfaces, each with its raw node, and another maker's device with one too.
mkdir -p "$fake/dev" "$fake/sys/bus/hid/devices/0003:28BD:097E.0007/hidraw/hidraw4" \
	"$fake/sys/bus/hid/devices/0003:28BD:097E.0009/hidraw/hidraw6" "$fake/sys/bus/hid/devices/0003:046D:C52B.0001/hidraw/hidraw1"
echo "HID_PHYS=usb-0000:0b:00.3-2/input0" > "$fake/sys/bus/hid/devices/0003:28BD:097E.0007/uevent"
echo "HID_PHYS=usb-0000:0b:00.3-2/input2" > "$fake/sys/bus/hid/devices/0003:28BD:097E.0009/uevent"
echo "HID_PHYS=usb-0000:0b:00.3-1/input2" > "$fake/sys/bus/hid/devices/0003:046D:C52B.0001/uevent"
: > "$fake/dev/hidraw1"; : > "$fake/dev/hidraw4"; : > "$fake/dev/hidraw6"

# Stand-ins: the loader copies the object and writes its rule as the real one does; both write down their arguments.
cat > "$work/bin/udev-hid-bpf" <<STUB
#!/bin/sh
echo "udev-hid-bpf \$*" >> "$work/calls"
if [ "\$1" = install ]; then
	mkdir -p "$fake/etc/udev-hid-bpf" "$fake/etc/udev/rules.d"
	for last; do :; done
	cp "\$last" "$fake/etc/udev-hid-bpf/"
	echo "# loads $name" > "$fake/etc/udev/rules.d/99-hid-bpf-0010-XPPen__Artist16_3rd.rules"
fi
STUB
cat > "$work/bin/udevadm" <<STUB
#!/bin/sh
echo "udevadm \$*" >> "$work/calls"
STUB
chmod +x "$work/bin/udev-hid-bpf" "$work/bin/udevadm"

failures=0
check() {
	if ! eval "$2"; then
		echo "FAIL: $1"
		failures=$((failures + 1))
	fi
}
run() { PATH="$work/bin:$PATH" XPERIENCE_ROOT="$fake" "$helper" "$@"; }
rules="$fake/etc/udev/rules.d/70-xperience.rules"

run install "$work/$name" button off toggle
check "the driver object is installed" '[ -f "$fake/etc/udev-hid-bpf/$name" ]'
check "the rules carry the upper button choice" 'grep -q "ENV{XPERIENCE_PEN_UPPER_BUTTON}=\"button\"" "$rules"'
check "the rules carry the lower button choice" 'grep -q "ENV{XPERIENCE_PEN_LOWER_BUTTON}=\"toggle\"" "$rules"'
check "the rules carry the tilt choice" 'grep -q "ENV{XPERIENCE_PEN_TILT}=\"off\"" "$rules"'
check "the tablet nodes are opened to the person at the seat" 'grep -q "ATTRS{idProduct}==\"097e\", TAG+=\"uaccess\"" "$rules"'
check "the virtual keyboard is opened to the person at the seat" 'grep -q "KERNEL==\"uinput\".*TAG+=\"uaccess\"" "$rules"'
check "udev is told to read its rules again" 'grep -qx "udevadm control --reload" "$work/calls"'
check "the driver is loaded for each of the tablet's interfaces with the options" \
	'[ "$(grep -c "^udev-hid-bpf add --replace --property XPERIENCE_PEN_UPPER_BUTTON=button --property XPERIENCE_PEN_LOWER_BUTTON=toggle --property XPERIENCE_PEN_TILT=off .*097E" "$work/calls")" = 2 ]'
check "another device is left alone" '! grep -q "046D" "$work/calls"'
check "the rules are installed before the driver is loaded" \
	'[ "$(grep -n "control --reload" "$work/calls" | head -1 | cut -d: -f1)" -lt "$(grep -n "add --replace" "$work/calls" | head -1 | cut -d: -f1)" ]'

run install "$work/$name" eraser on button
check "installing again rewrites the options" 'grep -q "UPPER_BUTTON}=\"eraser\"" "$rules" && grep -q "LOWER_BUTTON}=\"button\"" "$rules" && grep -q "PEN_TILT}=\"on\"" "$rules" && ! grep -q "=\"toggle\"" "$rules"'

: > "$work/calls"
run remove
check "remove unloads the driver from each interface" '[ "$(grep -c "^udev-hid-bpf remove .*097E" "$work/calls")" = 2 ]'
check "remove puts the tablet back into its own mode through its vendor interface" \
	'[ "$(od -An -tx1 "$fake/dev/hidraw6" | tr -d " \n")" = "02b00200000000000000" ]'
check "remove writes nothing to the tablet's other interfaces or to other devices" '[ ! -s "$fake/dev/hidraw4" ] && [ ! -s "$fake/dev/hidraw1" ]'
check "the tablet is told only after the driver has let go of it" \
	'[ "$(grep -n "udev-hid-bpf remove" "$work/calls" | tail -1 | cut -d: -f1)" -lt "$(grep -n "udevadm settle" "$work/calls" | head -1 | cut -d: -f1)" ]'
check "remove takes the rules away" '[ ! -e "$rules" ]'
check "remove takes the driver object away" '[ ! -e "$fake/etc/udev-hid-bpf/$name" ]'
check "remove takes the loader rule for the driver away" '[ ! -e "$fake/etc/udev/rules.d/99-hid-bpf-0010-XPPen__Artist16_3rd.rules" ]'

# What must be refused, and that a refusal changes nothing.
: > "$work/calls"
refused() {
	local what="$1"; shift
	if run "$@" > /dev/null 2>&1; then
		echo "FAIL: accepted $what"
		failures=$((failures + 1))
	fi
}
refused "an upper button that is neither" install "$work/$name" both on
refused "a tilt that is neither" install "$work/$name" eraser maybe
: > "$work/other.bpf.o"
refused "an object that is not the driver" install "$work/other.bpf.o" eraser on button
refused "a missing object" install "$work/missing/$name" eraser on button
refused "too few arguments" install "$work/$name"
refused "an unknown verb" purge
refused "no verb"
check "a refusal calls nothing and writes nothing" '[ ! -s "$work/calls" ] && [ ! -e "$rules" ]'

# Without the loader the helper stops with the exit code that says so.
rm "$work/bin/udev-hid-bpf"
set +e
PATH="$work/bin:/usr/bin:/bin" XPERIENCE_ROOT="$fake" "$helper" install "$work/$name" eraser on button > /dev/null 2>&1
code=$?
set -e
if command -v udev-hid-bpf > /dev/null 2>&1; then
	echo "driver helper: the loader is installed on this machine, so its absence could not be played"
else
	check "a missing loader is exit code 3" '[ "$code" = 3 ]'
fi

if [ "$failures" -ne 0 ]; then
	echo "driver helper: $failures checks failed"
	exit 1
fi
echo "driver helper: all checks passed"
