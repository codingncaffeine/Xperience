#!/bin/sh
# Builds bin/0010-XPPen__Artist16_3rd.bpf.o, the object udev-hid-bpf loads.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
name=0010-XPPen__Artist16_3rd
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

mkdir -p "$root/build" "$root/bin"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -fno-stack-protector -O2 -target bpf -g -c \
	-fms-extensions -Wno-microsoft-anon-tag -Wall -Wextra -Wno-unused-parameter -Werror \
	$arch -I "$headers" \
	"$root/driver/$name.bpf.c" -o "$root/build/$name.bpf.unstripped.o"

# The loader needs the BTF sections; the DWARF they were made from is dead weight.
llvm-strip -g "$root/build/$name.bpf.unstripped.o" -o "$root/bin/$name.bpf.o"
echo "$root/bin/$name.bpf.o"
