# Converts `tshark -T json` output (with the Faolan dissector plugin loaded) into an
# analyzer-style packet dump: one record per Faolan/chat packet instead of one per frame.
#
#   tshark -r capture.pcapng -o tcp.analyze_sequence_numbers:FALSE -T json \
#     | jq -f Captures/tshark_to_dump.jq > capture.tshark.json
#
# Not byte-identical to the Faolan.PacketAnalyzer schema, but carries the same essentials:
# frame/time/endpoints/direction/opcode/endpoint classes/body. IsDownload uses the 7000-7999
# server-port convention of the retail services.

# Flattens a packet subtree (fields live at several nesting depths) into one lookup dict.
def flat: [.. | objects | to_entries[] | select(.value | type == "string")] | from_entries;

# Strips the colons tshark renders into FT_BYTES fields ("00:0b:.." -> "000b.."). MUST be a literal
# split+join, NOT gsub(":";"") - gsub runs an oniguruma regex per match, which on the big compressed
# bodies (hundreds of KB of hex) dominates the whole run (~30x slower; measured 3018ms -> 220ms).
def dehex: split(":") | join("");

{
  Packets: [
    .[]
    | ._source.layers as $L
    | select($L.faolan != null)
    | ($L.frame["frame.number"] | tonumber) as $frame
    | $L.frame["frame.time_epoch"] as $time   # ISO8601 string in recent tshark builds
    | ($L.ip["ip.src"] // "") as $srcip
    | ($L.ip["ip.dst"] // "") as $dstip
    | ($L.tcp["tcp.srcport"] | tonumber) as $srcport
    | ($L.tcp["tcp.dstport"] | tonumber) as $dstport
    | ($L.faolan | to_entries[]
       | select(.key | startswith("Conan packet") or startswith("Chat packet")))
    | (.key | startswith("Chat packet")) as $chat
    | (.value | flat) as $f
    | {
        Frame: $frame,
        TimeUtc: $time,
        Protocol: (if $chat then "AgentServer" else "Conan" end),
        IsDownload: ($srcport >= 7000 and $srcport <= 7999),
        Source: {IpAddress: $srcip, Port: $srcport},
        Destination: {IpAddress: $dstip, Port: $dstport},
        Opcode: ($f["faolan.pkt.opcode"] // $f["faolan.chat.opcode"]),
        SenderClass: $f["faolan.pkt.sender_class"],
        SenderInstance: $f["faolan.pkt.sender_instance"],
        ReceiverClass: $f["faolan.pkt.receiver_class"],
        ReceiverInstance: $f["faolan.pkt.receiver_instance"],
        Crc32: $f["faolan.pkt.crc"],
        HeaderExtraHex: ($f["faolan.pkt.header_extra"] // "" | dehex),
        BodyHex: ($f["faolan.pkt.body"] // $f["faolan.chat.body"] // "" | dehex)
      }
  ]
}
