#!/bin/sh
# Fetches the pinned udev-hid-bpf headers into build/udev-hid-bpf and checks each
# one against its recorded sha256. Prints the directory on success.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
pin="$root/tools/udev-hid-bpf.pin"
dest="$root/build/udev-hid-bpf"
commit=$(sed -n 's/^commit //p' "$pin")
base="https://gitlab.freedesktop.org/libevdev/udev-hid-bpf/-/raw/$commit/src/bpf"

mkdir -p "$dest"
grep -E '^[0-9a-f]{64}  ' "$pin" | while read -r sum name; do
	file="$dest/$name"
	if [ ! -f "$file" ] || [ "$(sha256sum "$file" | cut -d' ' -f1)" != "$sum" ]; then
		curl -sSfL --max-time 120 -A curl -o "$file.part" "$base/$name"
		if [ "$(sha256sum "$file.part" | cut -d' ' -f1)" != "$sum" ]; then
			echo "fetch-headers: $name does not match its pinned sha256" >&2
			rm -f "$file.part"
			exit 1
		fi
		mv "$file.part" "$file"
	fi
done
echo "$dest"
