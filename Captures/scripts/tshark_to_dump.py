#!/usr/bin/env python3
# Converts `tshark -T json` output (with the Faolan dissector plugin loaded) into an
# analyzer-style packet dump: one record per Faolan/chat packet instead of one per frame.
# Reads the tshark JSON from stdin (or a file argument), writes the dump JSON to stdout.
#
#   tshark -r capture.pcapng -o tcp.analyze_sequence_numbers:FALSE -T json \
#     | Captures/scripts/tshark_to_dump.py > capture.tshark.json
#
# Drop-in replacement for the old tshark_to_dump.jq (#106): byte-for-byte the same dump, but
# Python's C-accelerated json parser is ~10x faster than the jq pass on large captures
# (131 MB tshark JSON: ~11.7 s jq -> ~1.1 s here), and it drops the jq dependency.
#
# Not byte-identical to the Faolan.PacketAnalyzer schema, but carries the same essentials:
# frame/time/endpoints/direction/opcode/endpoint classes/body. IsDownload uses the 7000-7999
# server-port convention of the retail services. Method/Iir/Direction are the plugin's
# resolved names (generated from Opcodes.cs) - additive keys, absent in older dumps.
import json
import sys


def flat(node, out):
    # Flattens a packet subtree (fields live at several nesting depths) into one lookup dict,
    # keeping only string-valued leaves (mirrors the jq `flat` def / from_entries: last wins).
    if isinstance(node, dict):
        for key, value in node.items():
            if isinstance(value, str):
                out[key] = value
            else:
                flat(value, out)
    elif isinstance(node, list):
        for value in node:
            flat(value, out)
    return out


def dehex(value):
    # Strips the colons tshark renders into FT_BYTES fields ("00:0b:.." -> "000b..").
    return value.replace(":", "")


def reshape(frames):
    packets = []
    for frame in frames:
        layers = frame.get("_source", {}).get("layers", {})
        faolan = layers.get("faolan")
        if faolan is None:
            continue

        frame_no = int(layers["frame"]["frame.number"])
        time = layers["frame"]["frame.time_epoch"]
        ip = layers.get("ip", {})
        src_ip = ip.get("ip.src", "")
        dst_ip = ip.get("ip.dst", "")
        tcp = layers["tcp"]
        src_port = int(tcp["tcp.srcport"])
        dst_port = int(tcp["tcp.dstport"])

        for key, value in faolan.items():
            if not (key.startswith("Conan packet") or key.startswith("Chat packet")):
                continue

            chat = key.startswith("Chat packet")
            fields = flat(value, {})
            packets.append({
                "Frame": frame_no,
                "TimeUtc": time,
                "Protocol": "AgentServer" if chat else "Conan",
                "IsDownload": 7000 <= src_port <= 7999,
                "Source": {"IpAddress": src_ip, "Port": src_port},
                "Destination": {"IpAddress": dst_ip, "Port": dst_port},
                "Opcode": fields.get("faolan.pkt.opcode") or fields.get("faolan.chat.opcode"),
                "Method": fields.get("faolan.pkt.method") or fields.get("faolan.chat.method"),
                "Iir": fields.get("faolan.pkt.iir"),
                "Direction": fields.get("faolan.pkt.direction") or fields.get("faolan.chat.direction"),
                "SenderClass": fields.get("faolan.pkt.sender_class"),
                "SenderInstance": fields.get("faolan.pkt.sender_instance"),
                "ReceiverClass": fields.get("faolan.pkt.receiver_class"),
                "ReceiverInstance": fields.get("faolan.pkt.receiver_instance"),
                "Crc32": fields.get("faolan.pkt.crc"),
                "HeaderExtraHex": dehex(fields.get("faolan.pkt.header_extra") or ""),
                "BodyHex": dehex(fields.get("faolan.pkt.body") or fields.get("faolan.chat.body") or ""),
            })
    return {"Packets": packets}


def main():
    source = open(sys.argv[1]) if len(sys.argv) > 1 else sys.stdin
    json.dump(reshape(json.load(source)), sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
