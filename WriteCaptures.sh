#!/usr/bin/env bash
set -euo pipefail

for file in Captures/*.pcapng; do
    name="${file%.pcapng}"
    output="${name}.json"

    if [[ -e "$output" ]]; then
        continue
    fi

    echo "$name"
    ./Faolan.PacketAnalyzer/bin/Debug/Faolan.PacketAnalyzer --dump "$file" > "$output"
done
