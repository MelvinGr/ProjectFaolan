#!/usr/bin/env bash
set -euo pipefail

for file in Captures/*.pcapng; do
    name="${file%.pcapng}"
    output="${name}.tshark.json"

    if [[ -e "$output" ]]; then
        continue
    fi

    echo "$name"
    ./Captures/tshark_dump.sh "$file" "$output"
done
