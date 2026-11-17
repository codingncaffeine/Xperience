#!/bin/sh
# Builds 0010-XPPen__Artist16_3rd.bpf.o, the object udev-hid-bpf loads, and xperience-probe, which asks a
# plugged-in tablet what it is, into bin/, or into the folder given. Prints the object's path.
#   tools/build-driver.sh [output folder [source file]]
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
name=0010-XPPen__Artist16_3rd
out=${1:-$root/bin}
source=${2:-$root/driver/$name.bpf.c}
headers=$("$root/tools/fetch-headers.sh")

case "$(uname -m)" in
x86_64) arch=-D__x86_64__ ;;
aarch64) arch=-D__aarch64__ ;;
riscv64) arch="-D__riscv -D__riscv_xlen=64" ;;
*) arch="-D__$(uname -m)__" ;;
esac

# Debian and Ubuntu keep the asm/ headers the BPF target needs in a per-architecture directory.
multiarch=/usr/include/$(uname -m)-linux-gnu
[ -d "$multiarch" ] && arch="$arch -I$multiarch"

mkdir -p "$root/build" "$out"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -fno-stack-protector -O2 -target bpf -g -c \
	-fms-extensions -Wno-microsoft-anon-tag -Wall -Wextra -Wno-unused-parameter -Werror \
	$arch -I "$headers" \
	"$source" -o "$root/build/$name.bpf.unstripped.o"

# The loader needs the BTF sections; the DWARF they were made from is dead weight.
llvm-strip -g "$root/build/$name.bpf.unstripped.o" -o "$out/$name.bpf.o"

# Static where the C library allows it, so the one program serves glibc and musl systems alike; where it does
# not (a from-source build without the static C library), linked against the system's own.
probe_flags="-O2 -Wall -Wextra -Werror"
# shellcheck disable=SC2086
if ! nice -n 10 "${CC:-cc}" $probe_flags -static "$root/driver/xperience-probe.c" -o "$out/xperience-probe" 2> /dev/null; then
	# shellcheck disable=SC2086
	nice -n 10 "${CC:-cc}" $probe_flags "$root/driver/xperience-probe.c" -o "$out/xperience-probe"
fi
echo "$out/$name.bpf.o"
