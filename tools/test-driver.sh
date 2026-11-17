#!/bin/sh
# Builds the driver for the kernel, then runs the same source in user space over
# the test vectors and has hid-tools judge what it produced. Needs no root and
# no tablet. The exit code is the verdict.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
name=0010-XPPen__Artist16_3rd
build="$root/build"
tests="$root/tests"

# Built for the kernel too, to see that it compiles for it. Into the test's own folder: bin/ holds the build
# that gets installed, and a test run must not replace it.
"$root/tools/build-driver.sh" "$build" >/dev/null
object="$build/$name.bpf.o"

# A program in the kernel can call nothing but the kernel's own helpers, which the object names as weak
# references. Anything else left undefined, such as a memcpy the compiler chose not to make in place, has the
# loader refuse the whole object, and nothing short of the loader says so.
undefined=$(llvm-nm -u "$object" | awk '$1 != "w" { print $NF }' | tr '\n' ' ')
if [ -n "$undefined" ]; then
	echo "driver: the object calls what a program in the kernel does not have: $undefined" >&2
	exit 1
fi

# Where the loader is installed it is asked too: it has to read the object and find the tablet, the three
# programs and the options in it.
if command -v udev-hid-bpf >/dev/null 2>&1; then
	if ! seen=$(udev-hid-bpf inspect "$object" 2>&1); then
		echo "$seen" | tail -n 5 >&2
		echo "driver: udev-hid-bpf cannot read the object" >&2
		exit 1
	fi
	for want in '"pid": "0x097E"' hid_rdesc_fixup hid_device_event '"section": "syscall"' \
		XPERIENCE_PEN_UPPER_BUTTON XPERIENCE_PEN_LOWER_BUTTON XPERIENCE_PEN_SWITCH_KEYS XPERIENCE_PEN_SMOOTHING XPERIENCE_PEN_TILT; do
		if ! echo "$seen" | grep -q -- "$want"; then
			echo "driver: udev-hid-bpf does not find $want in the object" >&2
			exit 1
		fi
	done
fi
headers=$("$root/tools/fetch-headers.sh")

quiet="-fms-extensions -Wno-microsoft-anon-tag -Wno-unknown-attributes"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -O1 -g $quiet -Wno-unknown-pragmas -Wno-attributes -DUDEV_HID_BPF_TEST_SUITE \
	-I "$headers" -I "$root/build/gen" -c "$root/driver/$name.bpf.c" -o "$build/test-driver.o"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -O1 -g $quiet -Wall -Wno-unused-parameter -Werror \
	-I "$headers" -c "$tests/kernel_side.c" -o "$build/test-kernel-side.o"
nice -n 10 clang -std=gnu11 -O1 -g -Wall -Wextra -Werror -c "$tests/harness.c" -o "$build/test-harness.o"
clang "$build/test-driver.o" "$build/test-kernel-side.o" "$build/test-harness.o" -o "$build/driver-harness"

if [ ! -x "$build/venv/bin/python" ] || ! "$build/venv/bin/python" -c 'import hidtools' 2>/dev/null; then
	python3 -m venv "$build/venv"
	"$build/venv/bin/pip" install --quiet --disable-pip-version-check hid-tools==0.12
fi

fixtures="$tests/fixtures/artist16-3rd"
"$build/driver-harness" "$fixtures-if0.rdesc.hex" "$fixtures-if1.rdesc.hex" "$fixtures-if2.rdesc.hex" \
	"$tests/artist16-3rd.vectors" > "$build/harness.out"
"$build/venv/bin/python" "$tests/check_driver.py" "$build/harness.out" "$tests/artist16-3rd.vectors" \
	"$fixtures-if2.rdesc.hex"

# The same program on tablets of other shapes: what each one answered decides its descriptor.
"$build/driver-harness" "$fixtures-if0.rdesc.hex" "$fixtures-if1.rdesc.hex" "$fixtures-if2.rdesc.hex" \
	"$tests/shapes.vectors" > "$build/harness-shapes.out"
"$build/venv/bin/python" "$tests/check_shapes.py" "$build/harness-shapes.out" "$tests/shapes.vectors"
