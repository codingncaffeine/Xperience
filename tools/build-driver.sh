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

mkdir -p "$root/build/gen" "$out"

# The products from driver/models.tsv: the loader's device list for the driver (written out here, since its
# HID_BPF_CONFIG macro takes 15 at most) and the table the probe reads the dials from.
awk -F '\t' '
	BEGIN {
		print "/* Written by tools/build-driver.sh from driver/models.tsv. */"
		print "#ifdef XPERIENCE_DRIVER"
		print "union {"
	}
	/^#/ || $1 == "product" || NF < 6 { next }
	{
		n++; id[n] = $1; keys[n] = $3; dials[n] = $4; screen[n] = ($5 == "yes")
		printf "\tHID_DEVICE(BUS_USB, HID_GROUP_GENERIC, VID_UGEE, 0x%s);\n", toupper($1)
	}
	END {
		print "} _device_ids SEC(\".hid_bpf_config\");"
		print "#else"
		print "static const struct xperience_model {"
		print "\tunsigned short product;"
		print "\tunsigned char keys, dials, screen;"
		print "} xperience_models[] = {"
		for (i = 1; i <= n; i++)
			printf "\t{ 0x%s, %d, %d, %d },\n", id[i], keys[i], dials[i], screen[i]
		print "};"
		print "#endif"
	}' "$root/driver/models.tsv" > "$root/build/gen/xperience-models.h.new"
mv "$root/build/gen/xperience-models.h.new" "$root/build/gen/xperience-models.h"
# shellcheck disable=SC2086
nice -n 10 clang -std=gnu11 -fno-stack-protector -O2 -target bpf -g -c \
	-fms-extensions -Wno-microsoft-anon-tag -Wall -Wextra -Wno-unused-parameter -Werror \
	$arch -I "$headers" -I "$root/build/gen" \
	"$source" -o "$root/build/$name.bpf.unstripped.o"

# The loader needs the BTF sections; the DWARF they were made from is dead weight.
llvm-strip -g "$root/build/$name.bpf.unstripped.o" -o "$out/$name.bpf.o"

# Static where the C library allows it, so the one program serves glibc and musl systems alike; where it does
# not (a from-source build without the static C library), linked against the system's own.
probe_flags="-O2 -Wall -Wextra -Werror"
# shellcheck disable=SC2086
if ! nice -n 10 "${CC:-cc}" $probe_flags -static -I "$root/build/gen" "$root/driver/xperience-probe.c" -o "$out/xperience-probe" 2> /dev/null; then
	# shellcheck disable=SC2086
	nice -n 10 "${CC:-cc}" $probe_flags -I "$root/build/gen" "$root/driver/xperience-probe.c" -o "$out/xperience-probe"
fi
echo "$out/$name.bpf.o"
