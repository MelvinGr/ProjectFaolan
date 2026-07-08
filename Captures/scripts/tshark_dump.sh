#!/usr/bin/env bash
# Dissect an AoC capture with the Faolan Wireshark plugin and write an analyzer-style JSON
# dump: one record per Conan/chat packet (frame, time, endpoints, direction, opcode,
# sender/receiver classes, body hex). Includes the compressed-stream packets the
# Faolan.PacketAnalyzer dumps drop. See Faolan.Wireshark/README.md.
#
# Requires the plugin to be installed (cmake --build Faolan.Wireshark/build --target
# install-faolan-plugin) and python3.
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <capture.pcapng> [output.json]" >&2
    exit 1
fi

capture=$1
output=${2:-${capture%.pcapng}.tshark.json}
reshaper="$(dirname "$0")/tshark_to_dump.py"

if ! tshark -G plugins | grep -q faolan; then
    echo "error: the Faolan Wireshark plugin is not installed (see Faolan.Wireshark/README.md)" >&2
    exit 1
fi

# tcp.analyze_sequence_numbers:FALSE - Wireshark otherwise hides retransmitted frames from
# the plugin, stalling compressed streams at gaps that a late retransmission would fill.
# -Y faolan drops the non-game frames (pure ACKs etc.) and -J trims the emitted protocol tree
# to just what the reshaper reads, shrinking tshark's JSON ~3.7x (484MB -> 131MB on the longload
# capture). The reshape pass is python3 (tshark_to_dump.py) - same dump as the old jq filter, but
# ~10x faster on large captures and no jq dependency (#106).
tshark -r "$capture" -o tcp.analyze_sequence_numbers:FALSE -Y faolan -J "frame ip tcp faolan" -T json \
    | python3 "$reshaper" > "$output"

echo "$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["Packets"]))' "$output") packets -> $output"
