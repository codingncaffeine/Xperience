#!/usr/bin/env bash
# Tests the driver helper without touching the system: it installs into a folder, and the two system tools it
# calls are stand-ins that write down how they were called.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "${work:?}"' EXIT
# The helper runs from a folder of its own with a stand-in probe beside it, as it comes in a bundle. The probe
# answers as the Artist 16 3rd does, with one line of a form the helper must not pass on.
mkdir -p "$work/bundle"
helper="$work/bundle/xperience-driver-helper"
cp "$root/tools/xperience-driver-helper" "$helper"
cat > "$work/bundle/xperience-probe" <<'STUB'
#!/bin/sh
echo XPERIENCE_TABLET_X_MAX=68199
echo XPERIENCE_TABLET_PRESSURE_MAX=16383
echo 'XPERIENCE_TABLET_KEYS=8 --property OTHER=1'
echo XPERIENCE_TABLET_PROBE=ok
STUB
chmod +x "$work/bundle/xperience-probe"

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
# The tablet as the kernel lists it on USB: three HID interfaces, the third one held by no driver, the way the
# maker's own program leaves it. And a mouse of another maker, with an interface held by nothing too.
usb="$fake/sys/bus/usb/devices"
mkdir -p "$usb/1-2/1-2:1.0/driver" "$usb/1-2/1-2:1.1/driver" "$usb/1-2/1-2:1.2" "$usb/3-1/3-1:1.0" "$fake/sys/bus/usb/drivers/usbhid"
echo 28bd > "$usb/1-2/idVendor"; echo 097e > "$usb/1-2/idProduct"
echo 046d > "$usb/3-1/idVendor"; echo c52b > "$usb/3-1/idProduct"
for interface in "$usb/1-2/1-2:1.0" "$usb/1-2/1-2:1.1" "$usb/1-2/1-2:1.2" "$usb/3-1/3-1:1.0"; do echo 03 > "$interface/bInterfaceClass"; done
export XPERIENCE_SETTLE_SECONDS=1
: > "$fake/dev/hidraw1"; : > "$fake/dev/hidraw4"; : > "$fake/dev/hidraw6"

# Stand-ins: the loader copies the object and writes its rule as the real one does; both write down their arguments.
cat > "$work/bin/udev-hid-bpf" <<STUB
#!/bin/sh
echo "udev-hid-bpf \$*" >> "$work/calls"
# Marker files make the stand-in refuse, the way the real loader and the kernel can.
if [ "\$1" = install ] && [ -e "$work/refuse-install" ]; then
	echo "Error: failed to open object" >&2
	exit 101
fi
if [ "\$1" = add ] && [ -e "$work/refuse-add" ]; then
	echo "libbpf: prog 'device_event': BPF program load failed: Permission denied" >&2
	exit 1
fi
if [ "\$1" = add ]; then
	# The device is the argument before the last. Loaded onto the tablet's vendor interface the driver makes
	# the pen appear there, unless a marker file says this tablet stays silent.
	device=""; previous=""
	for argument; do device="\$previous"; previous="\$argument"; done
	if grep -q "input2\\$" "\$device/uevent" 2> /dev/null && [ ! -e "$work/no-pen" ]; then
		mkdir -p "\$device/input/input7"
	fi
fi
if [ "\$1" = remove ]; then
	for device; do :; done
	rm -rf "\$device/input"
fi
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

run install "$work/$name" button off toggle 36 4
check "the driver object is installed" '[ -f "$fake/etc/udev-hid-bpf/$name" ]'
check "the rules carry the upper button choice" 'grep -q "ENV{XPERIENCE_PEN_UPPER_BUTTON}=\"button\"" "$rules"'
check "the rules carry the lower button choice" 'grep -q "ENV{XPERIENCE_PEN_LOWER_BUTTON}=\"toggle\"" "$rules"'
check "the rules carry the switch keys" 'grep -q "ENV{XPERIENCE_PEN_SWITCH_KEYS}=\"36\"" "$rules"'
check "the rules carry the smoothing" 'grep -q "ENV{XPERIENCE_PEN_SMOOTHING}=\"4\"" "$rules"'
check "the rules carry the tilt choice" 'grep -q "ENV{XPERIENCE_PEN_TILT}=\"off\"" "$rules"'
check "the tablet nodes are opened to the person at the seat" 'grep -q "ATTRS{idProduct}==\"097e\", TAG+=\"uaccess\"" "$rules"'
check "the tablet's USB node is opened to the person at the seat" 'grep -q "SUBSYSTEM==\"usb\".*ATTR{idProduct}==\"097e\", TAG+=\"uaccess\"" "$rules"'
check "and looked at again, the tablet's maker only" 'grep -qx "udevadm trigger --action=change --subsystem-match=usb --attr-match=idVendor=28bd" "$work/calls"'
check "the virtual keyboard is opened to the person at the seat" 'grep -q "KERNEL==\"uinput\".*TAG+=\"uaccess\"" "$rules"'
check "udev is told to read its rules again" 'grep -qx "udevadm control --reload" "$work/calls"'
check "the driver is loaded for each of the tablet's interfaces with the options" \
	'[ "$(grep -c "^udev-hid-bpf add --replace --property XPERIENCE_PEN_UPPER_BUTTON=button --property XPERIENCE_PEN_LOWER_BUTTON=toggle --property XPERIENCE_PEN_TILT=off --property XPERIENCE_PEN_SWITCH_KEYS=36 --property XPERIENCE_PEN_SMOOTHING=4 .*097E" "$work/calls")" = 2 ]'
check "the interface no driver held is given back to the kernel, and only that one" '[ "$(cat "$fake/sys/bus/usb/drivers/usbhid/bind")" = "1-2:1.2" ]'
check "another device is left alone" '! grep -q "046D" "$work/calls"'
check "the probe is installed where only root can change it" 'cmp -s "$fake/usr/local/libexec/xperience-probe" "$work/bundle/xperience-probe" && [ -x "$fake/usr/local/libexec/xperience-probe" ]'
check "the rules run the installed probe when a HID device of the maker appears" \
	'grep -qx "SUBSYSTEM==\"hid\", ACTION!=\"remove\", ATTRS{idVendor}==\"28bd\", IMPORT{program}=\"/usr/local/libexec/xperience-probe \\\$sys\\\$devpath\"" "$rules"'
check "the driver is loaded with what the tablet said it is" \
	'[ "$(grep -c -- "--property XPERIENCE_PEN_SMOOTHING=4 --property XPERIENCE_TABLET_X_MAX=68199 --property XPERIENCE_TABLET_PRESSURE_MAX=16383 --property XPERIENCE_TABLET_PROBE=ok .*097E" "$work/calls")" = 2 ]'
check "a probe line of another form is not passed on" '! grep -q "OTHER=1\|TABLET_KEYS" "$work/calls"'
check "the rules are installed before the driver is loaded" \
	'[ "$(grep -n "control --reload" "$work/calls" | head -1 | cut -d: -f1)" -lt "$(grep -n "add --replace" "$work/calls" | head -1 | cut -d: -f1)" ]'

run install "$work/$name" eraser on button none 1
check "installing again rewrites the options" 'grep -q "UPPER_BUTTON}=\"eraser\"" "$rules" && grep -q "LOWER_BUTTON}=\"button\"" "$rules" && grep -q "PEN_TILT}=\"on\"" "$rules" && ! grep -q "=\"toggle\"" "$rules" && grep -q "SWITCH_KEYS}=\"\"" "$rules"'

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
check "remove takes the probe away" '[ ! -e "$fake/usr/local/libexec/xperience-probe" ]'

# What must be refused, and that a refusal changes nothing.
: > "$work/calls"
refused() {
	local what="$1"; shift
	if run "$@" > /dev/null 2>&1; then
		echo "FAIL: accepted $what"
		failures=$((failures + 1))
	fi
}
refused "an upper button that is neither" install "$work/$name" both on button none 1
refused "a lower button that is neither" install "$work/$name" eraser on both none 1
refused "a tilt that is neither" install "$work/$name" eraser maybe button none 1
: > "$work/other.bpf.o"
refused "an object that is not the driver" install "$work/other.bpf.o" eraser on button none 1
refused "a missing object" install "$work/missing/$name" eraser on button none 1
refused "switch keys that are not keys" install "$work/$name" eraser on button 39 1
refused "an old call without the smoothing" install "$work/$name" eraser on button none
refused "a smoothing that is none" install "$work/$name" eraser on button none 0
refused "a smoothing too wide" install "$work/$name" eraser on button none 9
refused "too few arguments" install "$work/$name"
refused "an unknown verb" purge
refused "no verb"
mv "$work/bundle/xperience-probe" "$work/probe-aside"
refused "an install without the probe beside the helper" install "$work/$name" eraser on button none 1
mv "$work/probe-aside" "$work/bundle/xperience-probe"
check "a refusal calls nothing and writes nothing" '[ ! -s "$work/calls" ] && [ ! -e "$rules" ]'

# The loader will not take the object: nothing is changed, and what it said is passed on.
rm -f "$rules"
touch "$work/refuse-install"
set +e
said=$(run install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
rm "$work/refuse-install"
check "a loader that will not take the object is exit code 4" '[ "$code" = 4 ]'
check "what the loader said is passed on" 'echo "$said" | grep -q "failed to open object"'
check "and no rules were written" '[ ! -e "$rules" ]'

# The kernel will not load the program: the driver is taken out again, so the tablet is left as it was.
touch "$work/refuse-add"
: > "$fake/dev/hidraw6"
set +e
said=$(run install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
rm "$work/refuse-add"
check "a kernel that will not load the driver is exit code 5" '[ "$code" = 5 ]'
check "what the kernel said is passed on" 'echo "$said" | grep -q "BPF program load failed"'
check "and the driver is taken out again" '[ ! -e "$rules" ] && [ ! -e "$fake/etc/udev-hid-bpf/$name" ] && [ ! -e "$fake/etc/udev/rules.d/99-hid-bpf-0010-XPPen__Artist16_3rd.rules" ]'
check "with the tablet put back into its own mode" '[ "$(od -An -tx1 "$fake/dev/hidraw6" | tr -d " \n")" = "02b00200000000000000" ]'

# The driver loads but the tablet does not come back as a pen: the driver is taken out again and it says so.
touch "$work/no-pen"
rm -rf "$fake/sys/bus/hid/devices/0003:28BD:097E.0009/input"
: > "$fake/dev/hidraw6"
set +e
said=$(run install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
rm "$work/no-pen"
check "a tablet that does not come back as a pen is exit code 6" '[ "$code" = 6 ]'
check "it says the driver was taken out" 'echo "$said" | grep -q "did not come back as a pen"'
check "and it was: no rules, no driver" '[ ! -e "$rules" ] && [ ! -e "$fake/etc/udev-hid-bpf/$name" ]'

# With no tablet plugged in, installing sets everything up for when one is, and checks no pen.
mkdir -p "$work/unplugged"
mv "$fake/sys/bus/hid/devices/0003:28BD:097E.0007" "$fake/sys/bus/hid/devices/0003:28BD:097E.0009" "$work/unplugged/"
: > "$work/calls"
set +e
said=$(run install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
mv "$work/unplugged/"* "$fake/sys/bus/hid/devices/"
check "an install with no tablet plugged in succeeds" '[ "$code" = 0 ]'
check "and leaves the driver, its rules and the probe in place" '[ -e "$rules" ] && [ -f "$fake/etc/udev-hid-bpf/$name" ] && [ -x "$fake/usr/local/libexec/xperience-probe" ]'
check "and loads nothing" '! grep -q "^udev-hid-bpf add" "$work/calls"'

# Without the loader. These runs see nothing of this machine's own programs but the few plain tools the helper
# uses, so that no real package manager can be reached from a test. The stand-in loader is kept aside for the
# stand-in package manager to install.
mv "$work/bin/udev-hid-bpf" "$work/loader-stand-in"
mkdir -p "$work/tools"
for tool in sh basename dirname mkdir cat mv rm grep mktemp tail cp chmod sleep; do
	ln -sf "$(command -v "$tool")" "$work/tools/$tool"
done
bare() { PATH="$work/bin:$work/tools" XPERIENCE_ROOT="$fake" "$helper" "$@"; }

# No package manager it knows: it stops with the exit code that says the loader is missing, and says which package.
: > "$work/calls"
set +e
said=$(bare install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
check "a missing loader with no known package manager is exit code 3" '[ "$code" = 3 ]'
check "it names the package" 'echo "$said" | grep -q "udev-hid-bpf"'
check "and nothing was installed or loaded" '[ ! -s "$work/calls" ]'

# With the distribution's package manager at hand it installs the loader itself and goes on.
cat > "$work/bin/pacman" <<STUB
#!/bin/sh
echo "pacman \$*" >> "$work/calls"
cp "$work/loader-stand-in" "$work/bin/udev-hid-bpf"
STUB
chmod +x "$work/bin/pacman"
: > "$work/calls"
bare install "$work/$name" eraser on button none 1
check "a missing loader is installed with the package manager" 'grep -qx "pacman -S --needed --noconfirm udev-hid-bpf" "$work/calls"'
check "and the driver is then handed to it" 'grep -q "^udev-hid-bpf install --force" "$work/calls"'
check "in that order" '[ "$(grep -n "^pacman" "$work/calls" | cut -d: -f1)" -lt "$(grep -n "^udev-hid-bpf install" "$work/calls" | cut -d: -f1)" ]'

# A package manager that fails leaves the driver uninstalled and says so.
rm "$work/bin/udev-hid-bpf" "$rules"
printf '#!/bin/sh\necho "error: failed retrieving file" >&2\nexit 1\n' > "$work/bin/pacman"
: > "$work/calls"
set +e
said=$(bare install "$work/$name" eraser on button none 1 2>&1)
code=$?
set -e
check "a package manager that fails is exit code 3" '[ "$code" = 3 ]'
check "what it said is passed on" 'echo "$said" | grep -q "failed retrieving file"'
check "and no rules were written" '[ ! -e "$rules" ]'

# Removing still takes the rules away when the loader is gone.
mkdir -p "$(dirname "$rules")" && : > "$rules"
bare remove
check "remove works without the loader" '[ ! -e "$rules" ]'

if [ "$failures" -ne 0 ]; then
	echo "driver helper: $failures checks failed"
	exit 1
fi
echo "driver helper: all checks passed"
