#!/usr/bin/env bash
# Builds Xperience from source on Alpine (musl) in a throwaway container, the way an Alpine user would: Alpine's
# own .NET SDK and clang, a clean copy of the tracked files, warnings as errors, then every test (the driver's,
# the helper's and the app's) and the program's own version line.
#   tools/alpine-build.sh [alpine tag, default latest]
set -uo pipefail
cd "$(dirname "$0")/.."
tag="${1:-latest}"
out="scratch/alpine"
rm -rf "$out/src"; mkdir -p "$out/src"
git archive HEAD | tar -x -C "$out/src"
# Uncommitted tracked changes too, so the check covers what is about to be committed.
git diff HEAD --binary | (cd "$out/src" && git apply --allow-empty 2>/dev/null || true)
# Fetching needs the network; building and testing do not, and run without any. Rootless podman's own network
# needs the tun device; where that is missing the host's network serves the fetching step instead.
net=()
if ! podman run --rm "docker.io/library/alpine:$tag" true >/dev/null 2>&1; then
    net=(--network=host)
fi
id="xperience-alpine-build-$$"
podman rm -f "$id" >/dev/null 2>&1
{
    podman run --name "$id" "${net[@]}" -v "$PWD/$out/src:/src:Z" -w /src "docker.io/library/alpine:$tag" sh -euc '
        apk add --no-cache -q dotnet10-sdk bash icu-libs fontconfig ttf-dejavu dbus coreutils python3 \
            clang llvm libbpf-dev linux-headers build-base curl py3-pip > /dev/null
        echo "── $(cat /etc/alpine-release), dotnet $(dotnet --version), RID $(dotnet --info | sed -n "s/^ *RID: *//p" | head -1)"
        tools/fetch-headers.sh > /dev/null
        python3 -m venv build/venv && build/venv/bin/pip install --quiet --disable-pip-version-check hid-tools==0.12
        dotnet restore Xperience.slnx --nologo -v q 2>&1 | grep -E "error|warn" | sort -u || true
    ' && podman commit -q "$id" "localhost/$id" >/dev/null && podman run --rm --network=none -v "$PWD/$out/src:/src:Z" -w /src "localhost/$id" sh -euc '
        tools/build-driver.sh
        dotnet build Xperience.slnx -c Release -warnaserror --no-restore --nologo -v q 2>&1 | grep -E "error|warn|Build succeeded" | sort -u
        tools/test.sh > /tmp/test.log 2>&1 || { tail -40 /tmp/test.log; exit 1; }
        grep -E "reports judged|driver helper:|^(Passed|Failed)!" /tmp/test.log
        src/Xperience.App/bin/Release/net10.0/xperience --version
    '
} > "$out/build.log" 2>&1
rc=$?
podman rm -f "$id" >/dev/null 2>&1
podman rmi -f "localhost/$id" >/dev/null 2>&1
cat "$out/build.log"
if [ $rc -eq 0 ] && grep -q "^Passed!" "$out/build.log" && grep -q "0 faults" "$out/build.log" && grep -q "all checks passed" "$out/build.log" && grep -q "^Xperience " "$out/build.log"; then
    echo "ALPINE BUILD PASSED"
else
    echo "ALPINE BUILD FAILED"; exit 1
fi
