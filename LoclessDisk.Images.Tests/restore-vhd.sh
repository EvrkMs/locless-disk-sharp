#!/usr/bin/env bash
set -euo pipefail

if (( $# < 2 )); then
    echo "Usage: bash restore-vhd.sh NEW_OUTPUT.vhd BASE.ldisk [LAYER.ldelta ...]" >&2
    exit 2
fi

vhd=$(realpath -m -- "$1")
shift
[[ ! -e "$vhd" && ! -L "$vhd" ]] || { echo "Destination already exists." >&2; exit 2; }

test_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT
raw="$scratch/disk.raw"

sector=$(dotnet run --file "$test_dir/RestoreVhd.cs" -- "$raw" "$scratch/layout" "$@")
sfdisk --no-reread --no-tell-kernel "$raw" <"$scratch/layout"
sfdisk --verify "$raw"

qemu-img convert -f raw -O vpc -o subformat=dynamic,force_size=on "$raw" "$scratch/disk.vhd"
qemu-img compare -f raw -F vpc "$raw" "$scratch/disk.vhd"

fls -i vhd -f ntfs -o "$sector" "$scratch/disk.vhd" > "$scratch/root.txt"
windows_inode=$(awk '$NF == "Windows" && $1 ~ /^d\/d$/ { sub(/:$/, "", $2); print $2; exit }' "$scratch/root.txt")
[[ -n "$windows_inode" ]]
fls -i vhd -f ntfs -o "$sector" "$scratch/disk.vhd" "$windows_inode" > "$scratch/windows.txt"
grep -E ':([[:space:]]*)System32$' "$scratch/windows.txt"

mv -n -- "$scratch/disk.vhd" "$vhd"
[[ ! -e "$scratch/disk.vhd" ]] || { echo "Destination appeared during the test." >&2; exit 1; }
echo "PASS: GPT, VHD sector comparison, and NTFS Windows/System32. VHD: $vhd"
