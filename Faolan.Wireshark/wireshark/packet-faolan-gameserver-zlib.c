/*
 * Wireshark dissector for the Faolan / Age of Conan TCP services.
 *
 * Each direction is a Conan packet stream with in-band compression transitions (see
 * faolan_conan_stream.h): [80 00 00 05][4 bytes][flag] at a packet boundary, flag 00 = the
 * rest of the stream is one zlib deflate stream, non-zero = stays plaintext.
 *
 * Packet layout: [u32 BE length][u32 crc32][u32 BE hdrlen][RpcHeader][body]
 *          RpcHeader = 0x0A-tagged sender ProtoEndpoint + 0x12-tagged receiver ProtoEndpoint
 *          + u16 BE method opcode + optional trailing header bytes. A ProtoEndpoint is
 *          [0x0D <class id fixed32 LE>][0x10 <instance varint>]; the class ids are BKDR-31
 *          hashes of the client's agent/interface class names (docs/re/aoc-opcode-ids.md).
 *
 * Applies to UniverseAgent, PlayerAgent, CSPlayerAgent and GameServer connections. The
 * ChatServer chat protocol ([u16 opcode][u16 bodyLen][body] framing) is auto-detected per
 * conversation (first-packet CRC fails Conan + the stream walks as chat) and dissected with
 * its own packet fields.
 *
 * The CRC is a plain CRC-32 (zlib crc32()) over everything after the 8-byte length+crc
 * prefix; verified against capture traffic.
 */

#ifdef HAVE_CONFIG_H
#include "config.h"
#endif

#include <stdbool.h>
#include <stdlib.h>

#include <epan/conversation.h>
#include <epan/expert.h>
#include <epan/frame_data.h>
#include <epan/packet.h>
#include <epan/prefs.h>
#include <epan/proto_data.h>
#include <epan/wmem_scopes.h>
#include <epan/dissectors/packet-tcp.h>

#include <zlib.h>

#include "faolan_conan_stream.h"
#include "faolan_opcode_names.h" /* generated from Opcodes.cs (see scripts/gen_opcode_names.py) */
#include "packet-faolan-gameserver-zlib.h"

static int proto_faolan = -1;

/* Stream diagnostics */
static int hf_faolan_compression_state = -1;
static int hf_faolan_stream_note = -1;
static int hf_faolan_input_bytes = -1;
static int hf_faolan_output_bytes = -1;
static int hf_faolan_output_blob = -1;
static int hf_faolan_error = -1;

/* Conan packet fields */
static int hf_faolan_pkt_len = -1;
static int hf_faolan_pkt_crc = -1;
static int hf_faolan_pkt_unknown_prefix = -1;
static int hf_faolan_pkt_hdr_len = -1;
static int hf_faolan_pkt_sender_class = -1;
static int hf_faolan_pkt_sender_instance = -1;
static int hf_faolan_pkt_receiver_class = -1;
static int hf_faolan_pkt_receiver_instance = -1;
static int hf_faolan_pkt_opcode = -1;
static int hf_faolan_pkt_method = -1;
static int hf_faolan_pkt_iir = -1;
static int hf_faolan_pkt_direction = -1;
static int hf_faolan_pkt_method_id = -1;
static int hf_faolan_pkt_body = -1;

/* ChatServer chat packet fields */
static int hf_faolan_chat_opcode = -1;
static int hf_faolan_chat_method = -1;
static int hf_faolan_chat_direction = -1;
static int hf_faolan_chat_len = -1;
static int hf_faolan_chat_body = -1;

static gint ett_faolan = -1;
static gint ett_faolan_pkt = -1;
static gint ett_faolan_pkt_sender = -1;
static gint ett_faolan_pkt_receiver = -1;
static gint ett_faolan_chat_pkt = -1;

static expert_field ei_faolan_inflate_failed = EI_INIT;
static expert_field ei_faolan_crc_mismatch = EI_INIT;
static expert_field ei_faolan_pkt_malformed = EI_INIT;
static expert_field ei_faolan_pkt_unknown_prefix = EI_INIT;

static dissector_handle_t faolan_handle;
static range_t *global_faolan_tcp_ports;

/* ProtoEndpoint.type -> client class name. BKDR-31 (h = h*31 + c) hashes of the recovered
   class names in docs/aoc-client-rpc-methods.json; verified against 109k captured endpoints.
   "Agent" classes receive client->server calls, "Interface" classes server->client ones. */
static const value_string faolan_class_names[] = {
    {0x0C384054, "UniverseInterface"},
    {0x28377648, "GameCharInterfaceProject"},
    {0x3D744981, "TradepostAgent"},
    {0x5B9756FC, "GameCharAgentProject"},
    {0x604DDBA0, "UniverseAgent"},
    {0x6C67C147, "GameInterface"},
    {0x6DD55614, "CSPlayerAgent"},
    {0x7D155738, "PlayerInterface"},
    {0x82F20484, "PlayerAgent"},
    {0x8B87F791, "GameCharInterface"},
    {0xA9ECB95D, "GameCharAgent"},
    {0xB171CE13, "GameAgent"},
    {0xBBD560C8, "CSPlayerInterface"},
    {0xF2C2D5B5, "TradepostInterface"},
    {0, NULL}
};

/* The connection-level service pair whose 0x2000 (PassBlob/DispatchPassBlob) is the IIR
   carrier: body = [u32 len][u32 IIR type id][record], len covering everything after itself.
   Other classes' 0x2000 are ordinary first-slot methods (Authenticate, GetAllMails, ...). */
#define FAOLAN_CLASS_GAMEAGENT 0xB171CE13u
#define FAOLAN_CLASS_GAMEINTERFACE 0x6C67C147u
#define FAOLAN_OPCODE_PASSBLOB 0x2000u

/* Direction from the receiver endpoint: *Agent classes receive client->server calls,
   *Interface classes server->client dispatches. Falls back to the conversation-initiator
   heuristic (the client sends a followed connection's first bytes) for unknown classes. */
static const char *packet_direction(uint32_t receiver_class, bool from_conv_initiator)
{
    const char *name = try_val_to_str(receiver_class, faolan_class_names);

    if (name != NULL) {
        return strstr(name, "Interface") != NULL ? "s2c" : "c2s";
    }

    return from_conv_initiator ? "c2s" : "s2c";
}

/* An out-of-order TCP segment parked until the sequence gap before it fills. */
typedef struct faolan_stashed_seg
{
    uint32_t seq;
    uint32_t len;
    uint8_t *bytes; /* wmem file scope */
    struct faolan_stashed_seg *next; /* sorted by seq */
} faolan_stashed_seg_t;

#define FAOLAN_STASH_MAX_BYTES (4 * 1024 * 1024)
/* Segments to hold past a gap before declaring the missing bytes lost from the capture
   (gap-filling retransmissions arrive within a handful of frames when they exist at all). */
#define FAOLAN_GAP_GIVEUP_SEGMENTS 16

typedef struct faolan_direction_state
{
    faolan_conan_stream_t stream;
    faolan_conan_framer_t chat_framer; /* used instead of `stream` on chat conversations */

    /* Raw in-order bytes kept while the conversation protocol is undecided, so a connection
       that turns out to be chat can be re-framed from its first byte. */
    uint8_t *replay; /* wmem file scope */
    uint32_t replay_len;

    /* In-order delivery: streams (especially the zlib phase) need the byte stream exactly
       once and in order, so retransmissions are dropped and out-of-order segments stashed. */
    bool seq_initialized;
    uint32_t next_seq;
    faolan_stashed_seg_t *stash;
    uint32_t stash_bytes;
    uint32_t gap_segments;
} faolan_direction_state_t;

typedef enum faolan_conv_validation {
    FAOLAN_CONV_UNVALIDATED = 0,
    FAOLAN_CONV_CONAN,
    FAOLAN_CONV_CHAT, /* ChatServer chat: [u16 opcode][u16 bodyLen][body] framing */
    FAOLAN_CONV_SKIP
} faolan_conv_validation_t;

/* Bytes a direction may accumulate while the conversation protocol is undecided. */
#define FAOLAN_REPLAY_MAX_BYTES (128 * 1024)

typedef struct faolan_conv_state
{
    uint32_t endpoint_a_port;
    faolan_conv_validation_t validation;
    faolan_direction_state_t a_to_b;
    faolan_direction_state_t b_to_a;
} faolan_conv_state_t;

typedef struct faolan_frame_result
{
    bool is_chat; /* packets use ChatServer chat framing, not Conan */
    const char *compression_state;
    const char *stream_note; /* e.g. retransmission/out-of-order diagnostics */
    uint32_t input_bytes;
    uint32_t output_bytes;
    uint8_t *output_blob; /* inflated bytes produced in this frame (NULL when passthrough) */
    bool ok;
    const char *error_message;

    /* Conan packets completed in this frame, concatenated. */
    uint8_t *packets_blob;
    uint32_t packets_blob_len;
    uint32_t *pkt_offsets;
    uint32_t *pkt_lens;
    uint32_t pkt_count;
} faolan_frame_result_t;

static faolan_conv_state_t *get_or_create_conv_state(packet_info *pinfo)
{
    conversation_t *conversation = find_or_create_conversation(pinfo);
    faolan_conv_state_t *state = (faolan_conv_state_t *)conversation_get_proto_data(conversation, proto_faolan);

    if (state != NULL) {
        return state;
    }

    state = wmem_new0(wmem_file_scope(), faolan_conv_state_t);
    state->endpoint_a_port = pinfo->srcport;
    faolan_conan_stream_init(&state->a_to_b.stream);
    faolan_conan_stream_init(&state->b_to_a.stream);
    faolan_conan_framer_init(&state->a_to_b.chat_framer);
    faolan_conan_framer_init(&state->b_to_a.chat_framer);

    conversation_add_proto_data(conversation, proto_faolan, state);
    return state;
}

static const char *class_name(wmem_allocator_t *pool, uint32_t class_id)
{
    const char *name = try_val_to_str(class_id, faolan_class_names);
    return name != NULL ? name : wmem_strdup_printf(pool, "0x%08x", class_id);
}

/* The method the RPC header names. What looks like [u16 BE opcode][trailing header bytes] is ONE
   protobuf field - tag 0x20 (field 4, varint) holding the method's registration constant - and this
   reassembles it: the opcode's low byte is the varint's first group, each trailing byte adds seven
   more bits. On the 15 index-registered classes the constant IS the method index; on the two
   hash-registered ones it is the 32-bit constant. Gitea #610. */
static uint32_t method_constant(tvbuff_t *tvb, uint16_t opcode, uint32_t tail_start, uint32_t tail_end)
{
    uint32_t constant = (uint32_t)(opcode & 0x7F);
    uint32_t shift = 7;
    for (uint32_t p = tail_start; p < tail_end && shift < 32; p++, shift += 7) {
        constant |= (uint32_t)(tvb_get_uint8(tvb, p) & 0x7F) << shift;
    }
    return constant;
}

/* PlayerAgent/PlayerInterface register their methods by 32-bit HASH, so the opcode does NOT
   identify the method: what looks like [u16 BE opcode][trailing header bytes] is one protobuf
   varint (field 4, tag 0x20) holding the method's registration constant, cut after its first
   group. Reassembling it is the only way to tell apart the two methods that share an opcode -
   0x20EC is BOTH ReceivedCharacterSettings and AuthenticateLoggedIn, 0x20D6 BOTH
   VerifyLanguageSetting and UpdateCharacterPreview, and retail sends all four (#608/#610).
   Returns NULL for a class that is not hash-registered, or a constant with no recovered name. */
static const char *method_name_by_constant(uint32_t receiver_class, uint16_t opcode, tvbuff_t *tvb, uint32_t tail_start, uint32_t tail_end)
{
    for (size_t i = 0; i < array_length(faolan_class_constant_tables); i++) {
        if (faolan_class_constant_tables[i].class_id != receiver_class) {
            continue;
        }

        return try_val_to_str(method_constant(tvb, opcode, tail_start, tail_end), faolan_class_constant_tables[i].methods);
    }

    return NULL;
}

/* Resolves a (receiver class id, 16-bit opcode) pair to its recovered method name - the C
   equivalent of the server's GameServerMethodNames.Resolve(). The GameServer connection
   multiplexes several RPC class-pairs, so the SAME opcode means different methods per class;
   the receiver endpoint's class picks the table. Tables are generated from the C# opcode
   enums (faolan_opcode_names.h). Returns NULL when the pair has no recovered name. */
static const char *method_name(uint32_t receiver_class, uint16_t opcode)
{
    for (size_t i = 0; i < array_length(faolan_class_method_tables); i++) {
        if (faolan_class_method_tables[i].class_id == receiver_class) {
            return try_val_to_str(opcode, faolan_class_method_tables[i].methods);
        }
    }

    return NULL;
}

static uint64_t read_varint(tvbuff_t *tvb, uint32_t offset, uint32_t end, uint32_t *length)
{
    uint64_t result = 0;
    int shift = 0;
    uint32_t pos = offset;

    while (pos < end && shift <= 63) {
        const uint8_t b = tvb_get_uint8(tvb, pos);
        pos++;
        result |= (uint64_t)(b & 0x7F) << shift;

        if ((b & 0x80) == 0) {
            *length = pos - offset;
            return result;
        }

        shift += 7;
    }

    *length = 0;
    return 0;
}

/* Dissects one serialized ProtoEndpoint header field: [tag 0x0A/0x12][len][0x0D <fixed32 LE
   class>][0x10 <varint instance>]. Returns the bytes consumed (0 on malformed input). */
static uint32_t dissect_proto_endpoint(
    tvbuff_t *tvb,
    packet_info *pinfo,
    proto_tree *tree,
    uint32_t offset,
    uint32_t end,
    bool is_sender,
    uint32_t *class_id)
{
    *class_id = 0;

    if (offset + 2 > end) {
        return 0;
    }

    const uint8_t field_len = tvb_get_uint8(tvb, offset + 1);
    const uint32_t inner_end = offset + 2 + field_len;

    if (inner_end > end) {
        return 0;
    }

    proto_tree *ep_tree = proto_tree_add_subtree(
        tree, tvb, offset, 2 + field_len,
        is_sender ? ett_faolan_pkt_sender : ett_faolan_pkt_receiver,
        NULL,
        is_sender ? "Sender" : "Receiver");

    uint32_t pos = offset + 2;
    while (pos < inner_end) {
        const uint8_t tag = tvb_get_uint8(tvb, pos);

        if (tag == 0x0D && pos + 5 <= inner_end) {
            *class_id = tvb_get_letohl(tvb, pos + 1);
            proto_tree_add_item(
                ep_tree,
                is_sender ? hf_faolan_pkt_sender_class : hf_faolan_pkt_receiver_class,
                tvb, pos + 1, 4, ENC_LITTLE_ENDIAN);
            pos += 5;
        } else if (tag == 0x10) {
            uint32_t varint_len = 0;
            const uint64_t instance = read_varint(tvb, pos + 1, inner_end, &varint_len);

            if (varint_len == 0) {
                break;
            }

            proto_tree_add_uint64(
                ep_tree,
                is_sender ? hf_faolan_pkt_sender_instance : hf_faolan_pkt_receiver_instance,
                tvb, pos + 1, varint_len, instance);
            pos += 1 + varint_len;
        } else {
            break;
        }
    }

    proto_item_append_text(
        proto_tree_get_parent(ep_tree), ": %s",
        class_name(pinfo->pool, *class_id));

    return 2 + field_len;
}

/* Dissects one framed Conan packet at [offset, offset+len) of the packets tvb. */
static void dissect_conan_packet(
    tvbuff_t *tvb,
    packet_info *pinfo,
    proto_tree *tree,
    uint32_t offset,
    uint32_t len,
    uint32_t index,
    bool from_conv_initiator)
{
    proto_item *pkt_item;
    proto_tree *pkt_tree = proto_tree_add_subtree_format(
        tree, tvb, offset, len, ett_faolan_pkt, &pkt_item,
        "Conan packet %u (%u bytes)", index, len);

    if (len < 14) {
        expert_add_info(pinfo, pkt_item, &ei_faolan_pkt_malformed);
        return;
    }

    const uint32_t end = offset + len;

    proto_tree_add_item(pkt_tree, hf_faolan_pkt_len, tvb, offset, 4, ENC_BIG_ENDIAN);

    const uint32_t crc = tvb_get_ntohl(tvb, offset + 4);
    proto_item *crc_item = proto_tree_add_item(pkt_tree, hf_faolan_pkt_crc, tvb, offset + 4, 4, ENC_BIG_ENDIAN);

    /* Damaged-packet realignment: when decompression ran across a capture hole, a few bytes
       of a packet can be corrupted (the rest of the stream usually survives). If the header
       length at its standard position is implausible, scan a short window for a plausible
       [hdrlen u32][0x0A...] start so the remainder of the packet still dissects. */
    uint32_t hdr_len_offset = offset + 8;
    bool unknown_prefix = false;

    if ((uint64_t)hdr_len_offset + 4 + tvb_get_ntohl(tvb, hdr_len_offset) > end) {
        for (uint32_t skip = 1; skip <= 12 && offset + 8 + skip + 5 <= end; skip++) {
            const uint32_t candidate = offset + 8 + skip;
            const uint32_t hdr_len_alt = tvb_get_ntohl(tvb, candidate);

            if ((uint64_t)candidate + 4 + hdr_len_alt <= end &&
                tvb_get_uint8(tvb, candidate + 4) == 0x0A) {
                proto_item *prefix_item = proto_tree_add_item(pkt_tree, hf_faolan_pkt_unknown_prefix, tvb, offset + 8, skip, ENC_NA);
                expert_add_info(pinfo, prefix_item, &ei_faolan_pkt_unknown_prefix);
                hdr_len_offset = candidate;
                unknown_prefix = true;
                break;
            }
        }
    }

    const uint32_t crc_calculated = (uint32_t)crc32(0L, tvb_get_ptr(tvb, offset + 8, len - 8), len - 8);

    if (crc != crc_calculated) {
        expert_add_info_format(pinfo, crc_item, &ei_faolan_crc_mismatch,
            "CRC mismatch: calculated 0x%08x%s", crc_calculated,
            unknown_prefix ? " (packet damaged, header realigned)" : "");
    }

    const uint32_t hdr_len = tvb_get_ntohl(tvb, hdr_len_offset);
    proto_tree_add_item(pkt_tree, hf_faolan_pkt_hdr_len, tvb, hdr_len_offset, 4, ENC_BIG_ENDIAN);

    uint32_t hdr_end = hdr_len_offset + 4 + hdr_len;
    if (hdr_end > end) {
        expert_add_info(pinfo, pkt_item, &ei_faolan_pkt_malformed);
        hdr_end = end;
    }

    uint32_t pos = hdr_len_offset + 4;
    uint32_t sender_class = 0;
    uint32_t receiver_class = 0;
    bool have_sender = false;
    bool have_receiver = false;

    /* The RpcHeader carries sender_id (protobuf field 1, tag 0x0A) and endpoint_id (field 2, tag 0x12),
       but the two can appear in EITHER order: client->server requests write sender-then-receiver, while
       our server->client replies swap the endpoints (WriteHeader(ReceiverBytes, SenderBytes, ...)) so the
       receiver (0x12) is emitted first. Dispatch each endpoint by its own tag instead of assuming a fixed
       order - otherwise a receiver-first reply skips the sender (class "None") and reads the opcode from
       the sender's 0x0A tag+len bytes (the CSPlayerAgent op=0x0a0a misframe, #108). */
    while (pos < hdr_end) {
        const uint8_t tag = tvb_get_uint8(tvb, pos);
        uint32_t consumed = 0;

        if (tag == 0x0A && !have_sender) {
            consumed = dissect_proto_endpoint(tvb, pinfo, pkt_tree, pos, hdr_end, true, &sender_class);
            have_sender = true;
        } else if (tag == 0x12 && !have_receiver) {
            consumed = dissect_proto_endpoint(tvb, pinfo, pkt_tree, pos, hdr_end, false, &receiver_class);
            have_receiver = true;
        } else {
            break;
        }

        if (consumed == 0) {
            expert_add_info(pinfo, pkt_item, &ei_faolan_pkt_malformed);
            return;
        }
        pos += consumed;
    }

    if (pos + 2 > hdr_end) {
        expert_add_info(pinfo, pkt_item, &ei_faolan_pkt_malformed);
        return;
    }

    const uint16_t opcode = tvb_get_ntohs(tvb, pos);
    proto_item *opcode_item = proto_tree_add_item(pkt_tree, hf_faolan_pkt_opcode, tvb, pos, 2, ENC_BIG_ENDIAN);

    /* Method name resolved by (receiver class, opcode) - the receiver's method table names the call,
       since a connection multiplexes several class-pairs and the same opcode means different things
       on each. On the two hash-registered classes the opcode is only the method varint's first group,
       so the whole varint is reassembled first and that answer wins; unresolved opcodes stay raw hex. */
    const char *method = method_name_by_constant(receiver_class, opcode, tvb, pos + 2, hdr_end);
    if (method == NULL) {
        method = method_name(receiver_class, opcode);
    }
    if (method != NULL) {
        proto_item_append_text(opcode_item, " (%s)", method);
        proto_item *method_item = proto_tree_add_string(pkt_tree, hf_faolan_pkt_method, tvb, pos, 2, method);
        proto_item_set_generated(method_item);
    }

    /* The method id, over the WHOLE field it lives in - the opcode and everything after it up to the end
       of the header. There is no separate trailing "header extra" blob to show: those bytes are the rest
       of this varint (#610), and rendering them as their own field is what made the same value look like
       an opcode plus an unexplained marker byte. */
    proto_item *id_item = proto_tree_add_uint(pkt_tree, hf_faolan_pkt_method_id, tvb, pos, (int)(hdr_end - pos), method_constant(tvb, opcode, pos + 2, hdr_end));
    proto_item_set_generated(id_item);
    pos += 2;
    if (pos < hdr_end) {
        pos = hdr_end;
    }

    const char *direction = packet_direction(receiver_class, from_conv_initiator);
    proto_item *direction_item = proto_tree_add_string(pkt_tree, hf_faolan_pkt_direction, tvb, 0, 0, direction);
    proto_item_set_generated(direction_item);

    const uint32_t body_len = end - hdr_end;
    if (body_len > 0) {
        proto_tree_add_item(pkt_tree, hf_faolan_pkt_body, tvb, hdr_end, body_len, ENC_NA);
    }

    /* IIR carrier: GameAgent/GameInterface 0x2000 (PassBlob) bodies are
       [u32 len][u32 IIR type id][record] with len covering everything after itself; the id
       is the BKDR-31 hash of the client's IIR C++ type name (docs/re/aoc-opcode-ids.md). */
    const char *iir_name = NULL;
    if (opcode == FAOLAN_OPCODE_PASSBLOB &&
        (receiver_class == FAOLAN_CLASS_GAMEAGENT || receiver_class == FAOLAN_CLASS_GAMEINTERFACE) &&
        body_len >= 8 &&
        (uint64_t)tvb_get_ntohl(tvb, hdr_end) + 4 == body_len) {
        const uint32_t iir_id = tvb_get_ntohl(tvb, hdr_end + 4);
        proto_tree_add_item(pkt_tree, hf_faolan_pkt_iir, tvb, hdr_end + 4, 4, ENC_BIG_ENDIAN);
        iir_name = try_val_to_str(iir_id, faolan_iir_names);
        if (iir_name == NULL) {
            iir_name = wmem_strdup_printf(pinfo->pool, "IIR 0x%08x", iir_id);
        }
    }

    /* Call description: method name when recovered (plus the carried IIR type for PassBlob),
       raw opcode hex otherwise. */
    const char *call = method != NULL
        ? (iir_name != NULL
            ? wmem_strdup_printf(pinfo->pool, "%s:%s", method, iir_name)
            : method)
        : wmem_strdup_printf(pinfo->pool, "0x%04x", opcode);

    proto_item_append_text(pkt_item, ": %s -> %s %s, body %u bytes",
        class_name(pinfo->pool, sender_class),
        class_name(pinfo->pool, receiver_class),
        call, body_len);

    col_append_sep_fstr(pinfo->cinfo, COL_INFO, ", ", "%s %s",
        class_name(pinfo->pool, receiver_class), call);
}

/* ---- Auto-follow: register advertised service ports ----
 *
 * The services hand the client the next service's address inside packet bodies: as an ASCII
 * "host:port" string (UniverseAgent realm handoff, GameServer 0x2002 zone-change handoff) or
 * as binary [u32 ip][u16 port BE] (the PlayerAgent 0x1A07 service handoffs). Registering the
 * advertised TCP port makes a whole session dissect from just the fixed UniverseAgent port
 * (chat connections picked up this way are skipped via per-conversation Conan validation). */

static void register_followed_port(uint16_t port)
{
    static dissector_table_t tcp_table;

    if (port == 0) {
        return;
    }

    if (tcp_table == NULL) {
        tcp_table = find_dissector_table("tcp.port");
        if (tcp_table == NULL) {
            return;
        }
    }

    if (dissector_get_uint_handle(tcp_table, port) == faolan_handle) {
        return;
    }

    dissector_add_uint("tcp.port", port, faolan_handle);
}

static bool is_host_char(uint8_t c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-';
}

/* Scans packet bytes for ASCII "host:port" advertisements (host must look DNS-ish: >= 4
   host chars including a dot). */
static void scan_ascii_host_ports(const uint8_t *data, uint32_t len)
{
    for (uint32_t i = 0; i < len; i++) {
        if (data[i] != ':') {
            continue;
        }

        uint32_t host_start = i;
        bool has_dot = false;
        while (host_start > 0 && is_host_char(data[host_start - 1])) {
            host_start--;
            has_dot = has_dot || data[host_start] == '.';
        }

        if (!has_dot || i - host_start < 4) {
            continue;
        }

        uint32_t port_value = 0;
        uint32_t digits = 0;
        while (i + 1 + digits < len && digits < 5 && data[i + 1 + digits] >= '0' && data[i + 1 + digits] <= '9') {
            port_value = port_value * 10 + (data[i + 1 + digits] - '0');
            digits++;
        }

        /* Trailing bytes can be digits too (e.g. "...com:70116" where the port is 7011) -
           shorten from the right until the value fits, like the C# parser. */
        while (digits >= 2 && port_value > 65535) {
            port_value /= 10;
            digits--;
        }

        if (digits >= 2 && port_value > 0 && port_value <= 65535) {
            register_followed_port((uint16_t)port_value);
        }
    }
}

/* Locates the opcode and body of a framed packet blob (mirrors dissect_conan_packet without
   a tvb). Returns false when the header does not parse. */
static bool parse_packet_blob(const uint8_t *pkt, uint32_t len, uint16_t *opcode, uint32_t *body_offset)
{
    if (len < 14) {
        return false;
    }

    const uint32_t hdr_len = ((uint32_t)pkt[8] << 24) | ((uint32_t)pkt[9] << 16) | ((uint32_t)pkt[10] << 8) | pkt[11];
    const uint32_t hdr_end = 12 + hdr_len;

    if (hdr_end > len) {
        return false;
    }

    uint32_t pos = 12;
    while (pos + 2 <= hdr_end && (pkt[pos] == 0x0A || pkt[pos] == 0x12)) {
        pos += 2 + pkt[pos + 1];
    }

    if (pos + 2 > hdr_end) {
        return false;
    }

    *opcode = (uint16_t)((pkt[pos] << 8) | pkt[pos + 1]);
    *body_offset = hdr_end;
    return true;
}

static void follow_advertised_ports(const uint8_t *pkt, uint32_t len)
{
    scan_ascii_host_ports(pkt, len);

    uint16_t opcode = 0;
    uint32_t body_offset = 0;
    if (parse_packet_blob(pkt, len, &opcode, &body_offset) &&
        opcode == 0x1A07 &&
        body_offset + 6 <= len) {
        /* Binary service handoff: [u32 ip][u16 port BE][account id][...] */
        const uint8_t *body = pkt + body_offset;
        const bool ip_nonzero = body[0] != 0 || body[1] != 0 || body[2] != 0 || body[3] != 0;
        if (ip_nonzero) {
            register_followed_port((uint16_t)((body[4] << 8) | body[5]));
        }
    }
}

/* Accumulates the output of (possibly several) stream pushes for one frame. */
typedef struct faolan_push_acc
{
    faolan_conv_state_t *conv;
    faolan_framed_packet_t *packets;
    size_t packet_count;
    uint8_t *inflated; /* malloc, grown by append */
    size_t inflated_len;
    bool ok;
    const char *error_message;
} faolan_push_acc_t;

static void push_bytes(faolan_direction_state_t *dir, const uint8_t *bytes, uint32_t len, faolan_push_acc_t *acc)
{
    if (!acc->ok) {
        return;
    }

    /* While the conversation protocol is undecided, keep the raw bytes so a connection that
       turns out to be chat can be re-framed from its first byte. */
    if (acc->conv != NULL &&
        acc->conv->validation == FAOLAN_CONV_UNVALIDATED &&
        dir->replay_len + len <= FAOLAN_REPLAY_MAX_BYTES) {
        uint8_t *grown = (uint8_t *)wmem_realloc(wmem_file_scope(), dir->replay, dir->replay_len + len);
        memcpy(grown + dir->replay_len, bytes, len);
        dir->replay = grown;
        dir->replay_len += len;
    }

    if (acc->conv != NULL && acc->conv->validation == FAOLAN_CONV_CHAT) {
        acc->ok = faolan_chat_framer_push(&dir->chat_framer, bytes, len, &acc->packets, &acc->packet_count);
        if (!acc->ok) {
            acc->error_message = "chat framer allocation failed";
        }
        return;
    }

    uint8_t *inflated = NULL;
    size_t inflated_len = 0;

    acc->ok = faolan_conan_stream_push(
        &dir->stream, bytes, len,
        &acc->packets, &acc->packet_count, &inflated, &inflated_len, &acc->error_message);

    if (inflated_len > 0) {
        uint8_t *resized = (uint8_t *)realloc(acc->inflated, acc->inflated_len + inflated_len);
        if (resized != NULL) {
            memcpy(resized + acc->inflated_len, inflated, inflated_len);
            acc->inflated = resized;
            acc->inflated_len += inflated_len;
        }
    }

    free(inflated);
}

/* Sanity walk: does this byte stream look like ChatServer chat framing? Every packet is
   [u16 opcode][u16 bodyLen][body] with small opcodes (0x0000..0x00FF observed). */
static bool plausible_chat_stream(const uint8_t *data, uint32_t len)
{
    uint32_t pos = 0;
    uint32_t count = 0;

    while (pos + 4 <= len) {
        const uint16_t opcode = (uint16_t)((data[pos] << 8) | data[pos + 1]);
        if (opcode >= 0x0100) {
            return false;
        }

        pos += 4 + (uint32_t)((data[pos + 2] << 8) | data[pos + 3]);
        count++;
    }

    return count >= 1;
}

/* Feeds this segment - and any stashed segments it makes contiguous - to the stream in
   sequence order. Retransmitted bytes are dropped, future segments stashed. */
static void deliver_in_order(
    faolan_direction_state_t *dir,
    uint32_t seq,
    const uint8_t *bytes,
    uint32_t len,
    faolan_push_acc_t *acc,
    const char **note)
{
    if (!dir->seq_initialized) {
        dir->seq_initialized = true;
        dir->next_seq = seq;
    }

    const int32_t delta = (int32_t)(seq - dir->next_seq);

    if (delta > 0) {
        /* Future segment: park it until the gap fills (a retransmission usually arrives
           within a few frames in a capture). */
        faolan_stashed_seg_t *seg = wmem_new0(wmem_file_scope(), faolan_stashed_seg_t);
        seg->seq = seq;
        seg->len = len;
        seg->bytes = (uint8_t *)wmem_memdup(wmem_file_scope(), bytes, len);

        faolan_stashed_seg_t **insert = &dir->stash;
        while (*insert != NULL && (int32_t)((*insert)->seq - seq) < 0) {
            insert = &(*insert)->next;
        }
        seg->next = *insert;
        *insert = seg;
        dir->stash_bytes += len;
        dir->gap_segments++;

        if (dir->gap_segments < FAOLAN_GAP_GIVEUP_SEGMENTS &&
            dir->stash_bytes < FAOLAN_STASH_MAX_BYTES) {
            *note = "out-of-order segment, deferred until the gap fills";
            return;
        }

        /* The gap never filled: those bytes are missing from the capture. A plaintext
           direction resyncs framing at the next segment. A compressed direction keeps
           inflating across the hole - deflate output downstream of the damage is usually
           intact (verified on capture: most packets after an 8-byte hole CRC-validate),
           and the damaged ones are flagged by their CRC mismatch. */
        if (dir->stream.phase == FAOLAN_PHASE_PLAIN) {
            faolan_conan_framer_reset(&dir->stream.framer);
            *note = "capture gap declared lost; framing resynced at next segment";
        } else {
            *note = "capture gap in the compressed stream; decompressing across the hole - downstream packets may be damaged (check CRCs)";
        }

        dir->next_seq = dir->stash->seq;
        /* Fall through to the drain loop below (nothing new to push directly). */
        len = 0;
        bytes = NULL;
    } else if (delta < 0) {
        /* (Partially) retransmitted bytes: drop what was already processed. */
        const uint32_t skip = (uint32_t)(-delta);
        if (skip >= len) {
            *note = "retransmission, already processed";
            return;
        }

        *note = "partial retransmission, overlap dropped";
        bytes += skip;
        len -= skip;
    }

    if (len > 0) {
        push_bytes(dir, bytes, len, acc);
        dir->next_seq += len;
    }

    /* Drain stashed segments that are now contiguous. */
    while (dir->stash != NULL) {
        faolan_stashed_seg_t *seg = dir->stash;
        const int32_t seg_delta = (int32_t)(seg->seq - dir->next_seq);

        if (seg_delta > 0) {
            break;
        }

        uint32_t skip = seg_delta < 0 ? (uint32_t)(-seg_delta) : 0;
        if (skip < seg->len) {
            push_bytes(dir, seg->bytes + skip, seg->len - skip, acc);
            dir->next_seq += seg->len - skip;
        }

        dir->stash = seg->next;
        dir->stash_bytes -= seg->len;
    }

    if (dir->stash == NULL) {
        dir->gap_segments = 0;
    }
}

/* Dissects one ChatServer chat packet: [u16 opcode][u16 bodyLen][body]. Chat packets carry
   no RpcHeader, so direction comes from the conversation initiator (the client opens the
   connection and speaks first) and picks the per-direction opcode-name table. */
static void dissect_chat_packet(
    tvbuff_t *tvb,
    packet_info *pinfo,
    proto_tree *tree,
    uint32_t offset,
    uint32_t len,
    uint32_t index,
    bool from_conv_initiator)
{
    proto_item *pkt_item;
    proto_tree *pkt_tree = proto_tree_add_subtree_format(
        tree, tvb, offset, len, ett_faolan_chat_pkt, &pkt_item,
        "Chat packet %u (%u bytes)", index, len);

    if (len < 4) {
        expert_add_info(pinfo, pkt_item, &ei_faolan_pkt_malformed);
        return;
    }

    const uint16_t opcode = tvb_get_ntohs(tvb, offset);
    proto_item *opcode_item = proto_tree_add_item(pkt_tree, hf_faolan_chat_opcode, tvb, offset, 2, ENC_BIG_ENDIAN);

    const char *method = try_val_to_str(opcode,
        from_conv_initiator ? faolan_chat_c2s_names : faolan_chat_s2c_names);
    if (method != NULL) {
        proto_item_append_text(opcode_item, " (%s)", method);
        proto_item *method_item = proto_tree_add_string(pkt_tree, hf_faolan_chat_method, tvb, offset, 2, method);
        proto_item_set_generated(method_item);
    }

    proto_item *direction_item = proto_tree_add_string(
        pkt_tree, hf_faolan_chat_direction, tvb, 0, 0, from_conv_initiator ? "c2s" : "s2c");
    proto_item_set_generated(direction_item);

    proto_tree_add_item(pkt_tree, hf_faolan_chat_len, tvb, offset + 2, 2, ENC_BIG_ENDIAN);

    if (len > 4) {
        proto_tree_add_item(pkt_tree, hf_faolan_chat_body, tvb, offset + 4, len - 4, ENC_NA);
    }

    if (method != NULL) {
        proto_item_append_text(pkt_item, ": %s (0x%04x), body %u bytes", method, opcode, len - 4);
        col_append_sep_fstr(pinfo->cinfo, COL_INFO, ", ", "Chat %s", method);
    } else {
        proto_item_append_text(pkt_item, ": opcode 0x%04x, body %u bytes", opcode, len - 4);
        col_append_sep_fstr(pinfo->cinfo, COL_INFO, ", ", "Chat 0x%04x", opcode);
    }
}

static void process_first_pass(tvbuff_t *tvb, packet_info *pinfo, const struct tcpinfo *tcpinfo, faolan_frame_result_t *result)
{
    faolan_conv_state_t *conv_state = get_or_create_conv_state(pinfo);
    faolan_direction_state_t *dir = pinfo->srcport == conv_state->endpoint_a_port
        ? &conv_state->a_to_b
        : &conv_state->b_to_a;

    const int captured_len = tvb_captured_length(tvb);
    const guint8 *payload = captured_len > 0 ? tvb_get_ptr(tvb, 0, captured_len) : NULL;

    faolan_push_acc_t acc = {0};
    acc.conv = conv_state;
    acc.ok = true;
    const char *note = NULL;

    if (conv_state->validation == FAOLAN_CONV_SKIP) {
        result->compression_state = "Skipped";
        result->stream_note = "unrecognized protocol; skipped";
        result->input_bytes = (uint32_t)captured_len;
        result->ok = true;
        return;
    }

    if (payload != NULL) {
        if (tcpinfo != NULL) {
            deliver_in_order(dir, tcpinfo->seq, payload, (uint32_t)captured_len, &acc, &note);
        } else {
            push_bytes(dir, payload, (uint32_t)captured_len, &acc);
        }
    }

    /* The first completed packet decides the conversation protocol: a valid Conan CRC means
       Conan; otherwise an ChatServer chat connection ([u16 opcode][u16 bodyLen] framing -
       its arithmetic matches a Conan misframe for the first packet, so the CRC is the
       discriminator). On chat, re-frame the conversation from its first byte. */
    if (conv_state->validation == FAOLAN_CONV_UNVALIDATED && acc.packet_count > 0) {
        const faolan_framed_packet_t *first = &acc.packets[0];
        const bool conan = first->len >= 12 &&
            (uint32_t)crc32(0L, first->data + 8, first->len - 8) ==
                (((uint32_t)first->data[4] << 24) | ((uint32_t)first->data[5] << 16) |
                 ((uint32_t)first->data[6] << 8) | first->data[7]);

        if (conan) {
            conv_state->validation = FAOLAN_CONV_CONAN;
        } else if (dir->replay != NULL && plausible_chat_stream(dir->replay, dir->replay_len)) {
            conv_state->validation = FAOLAN_CONV_CHAT;

            /* Drop the Conan misframe and replay both directions through the chat framer. */
            faolan_framed_packets_free(acc.packets, acc.packet_count);
            free(acc.inflated);
            memset(&acc, 0, sizeof(acc));
            acc.conv = conv_state;
            acc.ok = true;
            note = "ChatServer chat connection";

            faolan_direction_state_t *dirs[2] = {&conv_state->a_to_b, &conv_state->b_to_a};
            for (int d = 0; d < 2; d++) {
                if (dirs[d]->replay_len > 0) {
                    push_bytes(dirs[d], dirs[d]->replay, dirs[d]->replay_len, &acc);
                }
            }
        } else {
            conv_state->validation = FAOLAN_CONV_SKIP;
            faolan_framed_packets_free(acc.packets, acc.packet_count);
            free(acc.inflated);
            result->compression_state = "Skipped";
            result->stream_note = "unrecognized protocol; skipped";
            result->input_bytes = (uint32_t)captured_len;
            result->ok = true;
            return;
        }

        conv_state->a_to_b.replay = NULL;
        conv_state->a_to_b.replay_len = 0;
        conv_state->b_to_a.replay = NULL;
        conv_state->b_to_a.replay_len = 0;
    }

    result->is_chat = conv_state->validation == FAOLAN_CONV_CHAT;

    if (!result->is_chat) {
        /* Auto-follow: pick up service ports advertised in handoff packets. */
        for (size_t i = 0; i < acc.packet_count; i++) {
            follow_advertised_ports(acc.packets[i].data, (uint32_t)acc.packets[i].len);
        }
    }

    const faolan_framed_packet_t *packets = acc.packets;
    const size_t packet_count = acc.packet_count;

    result->compression_state = result->is_chat ? "Chat" : faolan_stream_phase_to_string(dir->stream.phase);
    result->stream_note = note;
    result->input_bytes = (uint32_t)captured_len;
    result->output_bytes = (uint32_t)acc.inflated_len;
    result->ok = acc.ok;

    if (acc.inflated_len > 0) {
        result->output_blob = (uint8_t *)wmem_memdup(wmem_file_scope(), acc.inflated, acc.inflated_len);
    }

    if (acc.error_message != NULL) {
        result->error_message = wmem_strdup(wmem_file_scope(), acc.error_message);
    }

    if (packet_count > 0) {
        size_t total = 0;
        for (size_t i = 0; i < packet_count; i++) {
            total += packets[i].len;
        }

        result->packets_blob = (uint8_t *)wmem_alloc(wmem_file_scope(), total);
        result->pkt_offsets = (uint32_t *)wmem_alloc(wmem_file_scope(), packet_count * sizeof(uint32_t));
        result->pkt_lens = (uint32_t *)wmem_alloc(wmem_file_scope(), packet_count * sizeof(uint32_t));
        result->pkt_count = (uint32_t)packet_count;

        size_t blob_offset = 0;
        for (size_t i = 0; i < packet_count; i++) {
            memcpy(result->packets_blob + blob_offset, packets[i].data, packets[i].len);
            result->pkt_offsets[i] = (uint32_t)blob_offset;
            result->pkt_lens[i] = (uint32_t)packets[i].len;
            blob_offset += packets[i].len;
        }

        result->packets_blob_len = (uint32_t)total;
    }

    faolan_framed_packets_free(acc.packets, acc.packet_count);
    free(acc.inflated);
}

static int dissect_faolan(tvbuff_t *tvb, packet_info *pinfo, proto_tree *tree, void *data)
{
    col_set_str(pinfo->cinfo, COL_PROTOCOL, "Faolan");
    col_clear(pinfo->cinfo, COL_INFO);

    proto_item *ti = proto_tree_add_item(tree, proto_faolan, tvb, 0, -1, ENC_NA);
    proto_tree *faolan_tree = proto_item_add_subtree(ti, ett_faolan);

    faolan_frame_result_t *frame_result = (faolan_frame_result_t *)p_get_proto_data(wmem_file_scope(), pinfo, proto_faolan, 0);

    if (!PINFO_FD_VISITED(pinfo) || frame_result == NULL) {
        frame_result = wmem_new0(wmem_file_scope(), faolan_frame_result_t);
        process_first_pass(tvb, pinfo, (const struct tcpinfo *)data, frame_result);
        p_add_proto_data(wmem_file_scope(), pinfo, proto_faolan, 0, frame_result);
    }

    proto_tree_add_string(faolan_tree, hf_faolan_compression_state, tvb, 0, 0,
        frame_result->compression_state == NULL ? "Unknown" : frame_result->compression_state);

    if (frame_result->stream_note != NULL) {
        proto_tree_add_string(faolan_tree, hf_faolan_stream_note, tvb, 0, 0, frame_result->stream_note);
    }
    proto_tree_add_uint(faolan_tree, hf_faolan_input_bytes, tvb, 0, 0, frame_result->input_bytes);
    proto_tree_add_uint(faolan_tree, hf_faolan_output_bytes, tvb, 0, 0, frame_result->output_bytes);

    if (frame_result->output_blob != NULL && frame_result->output_bytes > 0) {
        tvbuff_t *decompressed_tvb = tvb_new_child_real_data(
            tvb,
            frame_result->output_blob,
            (int)frame_result->output_bytes,
            (int)frame_result->output_bytes);

        add_new_data_source(pinfo, decompressed_tvb, "Faolan decompressed stream bytes");

        proto_tree_add_item(
            faolan_tree,
            hf_faolan_output_blob,
            decompressed_tvb,
            0,
            (int)frame_result->output_bytes,
            ENC_NA);
    }

    if (!frame_result->ok) {
        proto_tree_add_expert(faolan_tree, pinfo, &ei_faolan_inflate_failed, tvb, 0, 0);
        if (frame_result->error_message != NULL) {
            proto_tree_add_string(faolan_tree, hf_faolan_error, tvb, 0, 0, frame_result->error_message);
        }
    }

    if (frame_result->pkt_count > 0) {
        tvbuff_t *packets_tvb = tvb_new_child_real_data(
            tvb,
            frame_result->packets_blob,
            (int)frame_result->packets_blob_len,
            (int)frame_result->packets_blob_len);

        add_new_data_source(pinfo, packets_tvb,
            frame_result->is_chat ? "Faolan chat packets" : "Faolan Conan packets");

        /* The conversation initiator sent this frame if its source port is endpoint a's (the
           first-seen frame of a followed connection is the client's). Feeds the direction
           field; Conan packets prefer the receiver class's Agent/Interface suffix. */
        const faolan_conv_state_t *conv_state = get_or_create_conv_state(pinfo);
        const bool from_conv_initiator = pinfo->srcport == conv_state->endpoint_a_port;

        for (uint32_t i = 0; i < frame_result->pkt_count; i++) {
            if (frame_result->is_chat) {
                dissect_chat_packet(
                    packets_tvb, pinfo, faolan_tree,
                    frame_result->pkt_offsets[i], frame_result->pkt_lens[i], i + 1,
                    from_conv_initiator);
            } else {
                dissect_conan_packet(
                    packets_tvb, pinfo, faolan_tree,
                    frame_result->pkt_offsets[i], frame_result->pkt_lens[i], i + 1,
                    from_conv_initiator);
            }
        }
    }

    return tvb_captured_length(tvb);
}

void proto_register_faolan_gs_zlib(void)
{
    static hf_register_info hf[] = {
        {&hf_faolan_compression_state, {"Compression State", "faolan.compression_state", FT_STRING, BASE_NONE, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_stream_note, {"Stream Note", "faolan.stream_note", FT_STRING, BASE_NONE, NULL, 0x0, "Retransmission/out-of-order delivery diagnostics", HFILL}},
        {&hf_faolan_input_bytes, {"Stream Input Bytes", "faolan.input_bytes", FT_UINT32, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_output_bytes, {"Stream Output Bytes", "faolan.output_bytes", FT_UINT32, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_output_blob, {"Decompressed Bytes", "faolan.decompressed", FT_BYTES, BASE_NONE, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_error, {"Error", "faolan.error", FT_STRING, BASE_NONE, NULL, 0x0, NULL, HFILL}},

        {&hf_faolan_pkt_len, {"Packet Length", "faolan.pkt.len", FT_UINT32, BASE_DEC, NULL, 0x0, "Bytes following the length field", HFILL}},
        {&hf_faolan_pkt_crc, {"CRC-32", "faolan.pkt.crc", FT_UINT32, BASE_HEX, NULL, 0x0, "CRC-32 over everything after the 8-byte length+crc prefix", HFILL}},
        {&hf_faolan_pkt_unknown_prefix, {"Damaged Bytes", "faolan.pkt.damaged_bytes", FT_BYTES, BASE_NONE, NULL, 0x0, "Corrupted bytes skipped to realign the header (decompression ran across a capture hole)", HFILL}},
        {&hf_faolan_pkt_hdr_len, {"Header Length", "faolan.pkt.header_len", FT_UINT32, BASE_DEC, NULL, 0x0, "RpcHeader length (endpoints + opcode + extra)", HFILL}},
        {&hf_faolan_pkt_sender_class, {"Sender Class", "faolan.pkt.sender_class", FT_UINT32, BASE_HEX, VALS(faolan_class_names), 0x0, "RpcHeader caller_id ProtoEndpoint.type (BKDR-31 class-name hash)", HFILL}},
        {&hf_faolan_pkt_sender_instance, {"Sender Instance", "faolan.pkt.sender_instance", FT_UINT64, BASE_HEX, NULL, 0x0, "RpcHeader caller_id ProtoEndpoint.instance (character/zone id)", HFILL}},
        {&hf_faolan_pkt_receiver_class, {"Receiver Class", "faolan.pkt.receiver_class", FT_UINT32, BASE_HEX, VALS(faolan_class_names), 0x0, "RpcHeader endpoint_id ProtoEndpoint.type (BKDR-31 class-name hash)", HFILL}},
        {&hf_faolan_pkt_receiver_instance, {"Receiver Instance", "faolan.pkt.receiver_instance", FT_UINT64, BASE_HEX, NULL, 0x0, "RpcHeader endpoint_id ProtoEndpoint.instance (character/zone id)", HFILL}},
        {&hf_faolan_pkt_opcode, {"Opcode", "faolan.pkt.opcode", FT_UINT16, BASE_HEX, NULL, 0x0, "Method index (0x2000 + position) into the receiver class's RPC table", HFILL}},
        {&hf_faolan_pkt_method, {"Method", "faolan.pkt.method", FT_STRING, BASE_NONE, NULL, 0x0, "Method name for (receiver class, opcode), from the C# Opcodes.cs catalogs", HFILL}},
        {&hf_faolan_pkt_iir, {"IIR Type", "faolan.pkt.iir", FT_UINT32, BASE_HEX, VALS(faolan_iir_names), 0x0, "IIR type id carried in a PassBlob 0x2000 body (BKDR-31 hash of the client IIR class name)", HFILL}},
        {&hf_faolan_pkt_direction, {"Direction", "faolan.pkt.direction", FT_STRING, BASE_NONE, NULL, 0x0, "c2s (receiver is an *Agent class) or s2c (receiver is an *Interface class)", HFILL}},
        {&hf_faolan_pkt_method_id, {"Method Id", "faolan.pkt.method_id", FT_UINT32, BASE_HEX, NULL, 0x0, "The method's registration constant - the varint the opcode and the bytes after it form", HFILL}},
        {&hf_faolan_pkt_body, {"Body", "faolan.pkt.body", FT_BYTES, BASE_NONE, NULL, 0x0, NULL, HFILL}},

        {&hf_faolan_chat_opcode, {"Chat Opcode", "faolan.chat.opcode", FT_UINT16, BASE_HEX, NULL, 0x0, "ChatServer chat opcode", HFILL}},
        {&hf_faolan_chat_method, {"Chat Method", "faolan.chat.method", FT_STRING, BASE_NONE, NULL, 0x0, "Chat opcode name (per direction), from the C# ChatServer opcode enums", HFILL}},
        {&hf_faolan_chat_direction, {"Chat Direction", "faolan.chat.direction", FT_STRING, BASE_NONE, NULL, 0x0, "c2s/s2c from the conversation initiator (the client connects and speaks first)", HFILL}},
        {&hf_faolan_chat_len, {"Chat Body Length", "faolan.chat.len", FT_UINT16, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_chat_body, {"Chat Body", "faolan.chat.body", FT_BYTES, BASE_NONE, NULL, 0x0, NULL, HFILL}},
    };

    static gint *ett[] = {
        &ett_faolan,
        &ett_faolan_pkt,
        &ett_faolan_pkt_sender,
        &ett_faolan_pkt_receiver,
        &ett_faolan_chat_pkt,
    };

    static ei_register_info ei[] = {
        {&ei_faolan_inflate_failed, {"faolan.inflate_failed", PI_MALFORMED, PI_ERROR, "zlib inflate failed", EXPFILL}},
        {&ei_faolan_crc_mismatch, {"faolan.crc_mismatch", PI_CHECKSUM, PI_WARN, "Packet CRC-32 mismatch", EXPFILL}},
        {&ei_faolan_pkt_malformed, {"faolan.pkt_malformed", PI_MALFORMED, PI_WARN, "Malformed Conan packet", EXPFILL}},
        {&ei_faolan_pkt_unknown_prefix, {"faolan.pkt_damaged", PI_MALFORMED, PI_NOTE, "Packet damaged (capture hole); header realigned past corrupted bytes", EXPFILL}},
    };

    proto_faolan = proto_register_protocol("Faolan Age of Conan", "Faolan", "faolan");
    proto_register_field_array(proto_faolan, hf, array_length(hf));
    proto_register_subtree_array(ett, array_length(ett));

    expert_module_t *expert_faolan = expert_register_protocol(proto_faolan);
    expert_register_field_array(expert_faolan, ei, array_length(ei));

    module_t *faolan_module = prefs_register_protocol(proto_faolan, NULL);

    /* Default to the UniverseAgent port - the only fixed one. The other services' ports are
       advertised in handoff packets and followed automatically (register_followed_port). */
    range_convert_str(wmem_epan_scope(), &global_faolan_tcp_ports, "7010", 65535);

    prefs_register_range_preference(
        faolan_module,
        "tcp_ports",
        "Faolan TCP ports",
        "TCP ports used by the Faolan/AoC services (UniverseAgent, PlayerAgent, CSPlayerAgent, GameServer). "
        "Ports advertised in service handoffs are followed automatically.",
        &global_faolan_tcp_ports,
        65535
    );
}

void proto_reg_handoff_faolan_gs_zlib(void)
{
    static bool initialized = false;
    static range_t *saved_ports;

    if (!initialized) {
        faolan_handle = create_dissector_handle(dissect_faolan, proto_faolan);
        initialized = true;
    } else {
        dissector_delete_uint_range("tcp.port", saved_ports, faolan_handle);
        g_free(saved_ports);
    }

    saved_ports = range_copy(NULL, global_faolan_tcp_ports);
    dissector_add_uint_range("tcp.port", global_faolan_tcp_ports, faolan_handle);
}
