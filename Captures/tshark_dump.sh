#!/usr/bin/env bash
# Dissect an AoC capture with the Faolan Wireshark plugin and write an analyzer-style JSON
# dump: one record per Conan/chat packet (frame, time, endpoints, direction, opcode,
# sender/receiver classes, body hex). Includes the compressed-stream packets the
# Faolan.PacketAnalyzer dumps drop. See Faolan.Wireshark/README.md.
#
# Requires the plugin to be installed (cmake --build Faolan.Wireshark/build --target
# install-faolan-plugin) and jq.
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <capture.pcapng> [output.json]" >&2
    exit 1
fi

capture=$1
output=${2:-${capture%.pcapng}.tshark.json}
filter="$(dirname "$0")/tshark_to_dump.jq"

if ! tshark -G plugins | grep -q faolan; then
    echo "error: the Faolan Wireshark plugin is not installed (see Faolan.Wireshark/README.md)" >&2
    exit 1
fi

# tcp.analyze_sequence_numbers:FALSE - Wireshark otherwise hides retransmitted frames from
# the plugin, stalling compressed streams at gaps that a late retransmission would fill.
tshark -r "$capture" -o tcp.analyze_sequence_numbers:FALSE -T json \
    | jq -f "$filter" > "$output"

echo "$(jq '.Packets | length' "$output") packets -> $output"
