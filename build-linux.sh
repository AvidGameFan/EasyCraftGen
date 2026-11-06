#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
version=${1:-$(cat "$root/version.txt")}
runtime=${2:-linux-x64}
case "$runtime" in linux-x64|linux-arm64) ;; *) echo "Use linux-x64 or linux-arm64" >&2; exit 1 ;; esac
case "$version" in ''|*[!0-9A-Za-z.-]*|.*|-*) echo "Invalid version" >&2; exit 1 ;; esac
mkdir -p "$root/dist"
publish=$(mktemp -d "$root/dist/publish-$runtime-XXXXXX")
trap 'rm -rf -- "$publish"' EXIT
dotnet publish "$root/EasyCraftGen/EasyCraftGen.csproj" -c Release -r "$runtime" --self-contained true "-p:Version=$version" -o "$publish"
tar -czf "$root/dist/EasyCraftGen-$runtime-v$version.tar.gz" -C "$publish" .
echo "Created dist/EasyCraftGen-$runtime-v$version.tar.gz"
