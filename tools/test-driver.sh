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
headers=$("$root/tools/fetch-headers.sh")

quiet="-fms-extensions -Wno-microsoft-anon-tag -Wno-unknown-attributes"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -O1 -g $quiet -Wno-unknown-pragmas -Wno-attributes -DUDEV_HID_BPF_TEST_SUITE \
	-I "$headers" -c "$root/driver/$name.bpf.c" -o "$build/test-driver.o"
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
