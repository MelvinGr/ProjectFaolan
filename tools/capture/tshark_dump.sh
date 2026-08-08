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

# ⚠️ A PLAYFIELD TRANSITION OPENS A NEW CONNECTION, and the plugin's auto-follow does not always learn
# its port - so the frames are dropped by `-Y faolan` and the whole zone is silently missing from the
# dump. Not a small loss: entering the Necromancer armoury in
# NecroL80_mage-necromancer_login_gameplay_annotated_7 moved the session to port 7028 for 55 s, and that
# segment alone held 7 of the capture's 8 shop windows. Nothing in the traffic advertises 7028 in a form
# the plugin recognises - it is not in the plaintext frames and not in any decompressed body - so the
# dump has to bind the port itself.
#
# So: bind every TCP port a Faolan HOST serves. The host set comes from the frames the plugin already
# claims, which keeps unrelated services out (the local CDN mirror on :7081 is HTTP and must not be
# dissected as Conan). A server port is the source port of a SYN-ACK, which is exact and needs no
# guessing about ranges.
faolan_hosts=$(tshark -r "$capture" -o tcp.analyze_sequence_numbers:FALSE -Y faolan -T fields -e ip.src -e ip.dst 2>/dev/null \
    | tr '\t' '\n' | sort -u | grep -E '^[0-9]' || true)

decode_as=()
if [[ -n "$faolan_hosts" ]]; then
    host_re=$(echo "$faolan_hosts" | paste -sd'|' -)
    for port in $(tshark -r "$capture" -o tcp.analyze_sequence_numbers:FALSE \
                      -Y 'tcp.flags.syn==1 && tcp.flags.ack==1' -T fields -e ip.src -e tcp.srcport 2>/dev/null \
                  | grep -E "^($host_re)\b" | cut -f2 | sort -un); do
        decode_as+=(-d "tcp.port==${port},faolan")
    done
fi

# tcp.analyze_sequence_numbers:FALSE - Wireshark otherwise hides retransmitted frames from
# the plugin, stalling compressed streams at gaps that a late retransmission would fill.
# -Y faolan drops the non-game frames (pure ACKs etc.) and -J trims the emitted protocol tree
# to just what the reshaper reads, shrinking tshark's JSON ~3.7x (484MB -> 131MB on the longload
# capture). The reshape pass is python3 (tshark_to_dump.py) - same dump as the old jq filter, but
# ~10x faster on large captures and no jq dependency (#106).
tshark -r "$capture" -o tcp.analyze_sequence_numbers:FALSE "${decode_as[@]}" -Y faolan -J "frame ip tcp faolan" -T json \
    | python3 "$reshaper" > "$output"

echo "$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["Packets"]))' "$output") packets -> $output"
