#!/usr/bin/env bash
# Runs every test: the driver's, the driver helper's, both on a stand-in tablet in a virtual machine
# (tools/rig), then the app's. The app's tests run with no display, on a
# session bus of their own that starts nothing, with their own XDG folders, so nothing of the desktop they run
# on can be reached.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
if [ -f "$root/scratch/env.sh" ]; then . "$root/scratch/env.sh"; fi

"$root/tools/test-driver.sh"
"$root/tools/test-helper.sh"

# The driver and the helper on a stand-in tablet, in a virtual machine running this machine's own kernel. Its
# exit code 2 means this machine cannot run it (no KVM, no loader); that is said, and the rest goes on.
rig=0
"$root/tools/rig/run.sh" || rig=$?
case "$rig" in
0) ;;
2) echo "rig: NOT RUN on this machine (the reason is above)" >&2 ;;
*) echo "rig: failed; the guest's record is in build/rig/out/record.txt" >&2; exit 1 ;;
esac

sandbox="$(mktemp -d)"
trap 'rm -rf "${sandbox:?}"' EXIT
mkdir -p "$sandbox"/{config,data,state,cache,runtime}
chmod 700 "$sandbox/runtime"

# The kernel's own numbers for its input layer, printed from its headers, for the tests to hold ours against.
cc -Wall -Werror -o "$sandbox/kernel_facts" "$root/tests/kernel_facts.c"
"$sandbox/kernel_facts" > "$sandbox/kernel-facts.txt"

nice -n 10 dotnet build "$root/tests/Xperience.Tests/Xperience.Tests.csproj" -c Release -v q --nologo -maxcpucount:4
env -u DISPLAY -u WAYLAND_DISPLAY -u DBUS_SESSION_BUS_ADDRESS \
	XDG_CONFIG_HOME="$sandbox/config" XDG_DATA_HOME="$sandbox/data" XDG_STATE_HOME="$sandbox/state" \
	XDG_CACHE_HOME="$sandbox/cache" XDG_RUNTIME_DIR="$sandbox/runtime" TMPDIR="$sandbox" \
	XPERIENCE_KERNEL_FACTS="$sandbox/kernel-facts.txt" XPERIENCE_TEST_BUS=1 \
	dbus-run-session --config-file "$root/tools/isolated-bus.conf" -- \
	nice -n 10 dotnet test "$root/tests/Xperience.Tests/Xperience.Tests.csproj" -c Release --no-build --nologo -v q
