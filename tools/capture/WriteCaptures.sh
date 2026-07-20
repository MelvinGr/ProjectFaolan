#!/usr/bin/env bash
# Regenerate the canonical *.tshark.json next to every *.pcapng in the capture corpus.
#
# The corpus is NOT in this repo - it lives in the private ProjectFaolan-Data repo. Point at it with
# FAOLAN_CAPTURES, the same variable the Python dissectors use:
#
#   FAOLAN_CAPTURES=<data-repo>/Captures tools/capture/WriteCaptures.sh
set -euo pipefail

captures="${FAOLAN_CAPTURES:-}"
if [[ -z "$captures" ]]; then
    repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    captures="$repo_root/Captures"
fi

if [[ ! -d "$captures" ]]; then
    echo "Capture corpus not found: $captures" >&2
    echo "  Set FAOLAN_CAPTURES=<data-repo>/Captures (see CLAUDE.md \"Capture data\")." >&2
    exit 1
fi

dump="$(dirname "${BASH_SOURCE[0]}")/tshark_dump.sh"

for file in "$captures"/*.pcapng; do
    name="${file%.pcapng}"
    output="${name}.tshark.json"

    if [[ -e "$output" ]]; then
        continue
    fi

    echo "$name"
    "$dump" "$file" "$output"
done
