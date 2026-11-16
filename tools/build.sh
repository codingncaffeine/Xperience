#!/usr/bin/env bash
# Builds everything and refreshes bin/: bin/xperience starts the settings app, and the driver object sits beside it.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
if [ -f "$root/scratch/env.sh" ]; then . "$root/scratch/env.sh"; fi
out="$root/bin"

# A build is background work: it gets a few cores at low priority, not the whole machine.
nice -n 10 dotnet publish "$root/src/Xperience.App/Xperience.App.csproj" -c Release -v q --nologo -maxcpucount:4 -o "$out/app"

cat > "$out/xperience" <<'LAUNCHER'
#!/bin/sh
# Starts the Xperience settings window from this dev build.
exec "$(dirname -- "$(readlink -f -- "$0")")/app/xperience" "$@"
LAUNCHER
chmod +x "$out/xperience"

"$root/tools/build-driver.sh" > /dev/null
echo "$out/xperience"
