/*
 * Initial Wireshark dissector skeleton for Faolan GameServer zlib transition.
 *
 * This file is intentionally minimal for v1 startup. It focuses on field
 * registration and where per-conversation decompression hooks will go.
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

#include "faolan_zlib_stream.h"
#include "packet-faolan-gameserver-zlib.h"

static int proto_faolan_gs_zlib = -1;

static int hf_faolan_direction = -1;
static int hf_faolan_compression_state = -1;
static int hf_faolan_header_progress = -1;
static int hf_faolan_input_bytes = -1;
static int hf_faolan_output_bytes = -1;
static int hf_faolan_output_blob = -1;
static int hf_faolan_error = -1;

static gint ett_faolan = -1;

static expert_field ei_faolan_inflate_failed = EI_INIT;

static dissector_handle_t faolan_handle;
static range_t *global_faolan_tcp_ports;

typedef struct faolan_conv_state
{
    bool endpoints_initialized;
    uint32_t endpoint_a_port;
    uint32_t endpoint_b_port;
    faolan_zlib_stream_t a_to_b;
    faolan_zlib_stream_t b_to_a;
} faolan_conv_state_t;

typedef struct faolan_frame_result
{
    const char *direction;
    const char *compression_state;
    uint32_t header_progress;
    uint32_t input_bytes;
    uint32_t output_bytes;
    uint8_t *output_blob;
    bool ok;
    const char *error_message;
} faolan_frame_result_t;

static faolan_conv_state_t *get_or_create_conv_state(packet_info *pinfo)
{
    conversation_t *conversation = find_or_create_conversation(pinfo);
    faolan_conv_state_t *state = (faolan_conv_state_t *)conversation_get_proto_data(conversation, proto_faolan_gs_zlib);

    if (state != NULL) {
        return state;
    }

    state = wmem_new0(wmem_file_scope(), faolan_conv_state_t);
    state->endpoints_initialized = true;
    state->endpoint_a_port = pinfo->srcport;
    state->endpoint_b_port = pinfo->destport;
    faolan_zlib_stream_init(&state->a_to_b);
    faolan_zlib_stream_init(&state->b_to_a);

    conversation_add_proto_data(conversation, proto_faolan_gs_zlib, state);
    return state;
}

static void resolve_direction(
    faolan_conv_state_t *state,
    packet_info *pinfo,
    faolan_zlib_stream_t **stream,
    const char **direction)
{
    if (state->endpoints_initialized &&
        pinfo->srcport == state->endpoint_a_port &&
        pinfo->destport == state->endpoint_b_port) {
        *stream = &state->a_to_b;
        *direction = "A -> B";
        return;
    }

    if (state->endpoints_initialized &&
        pinfo->srcport == state->endpoint_b_port &&
        pinfo->destport == state->endpoint_a_port) {
        *stream = &state->b_to_a;
        *direction = "B -> A";
        return;
    }

    *stream = &state->a_to_b;
    *direction = "A -> B (fallback)";
}

static void add_result_to_tree(proto_tree *faolan_tree, tvbuff_t *tvb, packet_info *pinfo, const faolan_frame_result_t *result)
{
    //proto_tree_add_string(faolan_tree, hf_faolan_direction, tvb, 0, 0, result->direction == NULL ? "Unknown" : result->direction);
    proto_tree_add_string(faolan_tree, hf_faolan_compression_state, tvb, 0, 0, result->compression_state == NULL ? "Unknown" : result->compression_state);
    //proto_tree_add_uint(faolan_tree, hf_faolan_header_progress, tvb, 0, 0, result->header_progress);
    proto_tree_add_uint(faolan_tree, hf_faolan_input_bytes, tvb, 0, 0, result->input_bytes);
    proto_tree_add_uint(faolan_tree, hf_faolan_output_bytes, tvb, 0, 0, result->output_bytes);

    if (result->output_blob != NULL && result->output_bytes > 0) {
        tvbuff_t *decompressed_tvb = tvb_new_child_real_data(
            tvb,
            result->output_blob,
            (int)result->output_bytes,
            (int)result->output_bytes);

        add_new_data_source(pinfo, decompressed_tvb, "Faolan decompressed stream bytes");

        proto_tree_add_item(
            faolan_tree,
            hf_faolan_output_blob,
            decompressed_tvb,
            0,
            (int)result->output_bytes,
            ENC_NA);
    }

    if (!result->ok) {
        proto_tree_add_expert(faolan_tree, pinfo, &ei_faolan_inflate_failed, tvb, 0, 0);
        if (result->error_message != NULL) {
            proto_tree_add_string(faolan_tree, hf_faolan_error, tvb, 0, 0, result->error_message);
        }
    }
}

static int dissect_faolan_gs_zlib(tvbuff_t *tvb, packet_info *pinfo, proto_tree *tree, void *data _U_)
{
    col_set_str(pinfo->cinfo, COL_PROTOCOL, "FaolanGS");
    col_clear(pinfo->cinfo, COL_INFO);

    proto_item *ti = proto_tree_add_item(tree, proto_faolan_gs_zlib, tvb, 0, -1, ENC_NA);
    proto_tree *faolan_tree = proto_item_add_subtree(ti, ett_faolan);

    faolan_frame_result_t *frame_result = (faolan_frame_result_t *)p_get_proto_data(wmem_file_scope(), pinfo, proto_faolan_gs_zlib, 0);

    if (!PINFO_FD_VISITED(pinfo) || frame_result == NULL) {
        faolan_conv_state_t *conv_state = get_or_create_conv_state(pinfo);
        faolan_zlib_stream_t *stream = NULL;
        const char *direction = NULL;
        resolve_direction(conv_state, pinfo, &stream, &direction);

        const int captured_len = tvb_captured_length(tvb);
        const guint8 *payload = captured_len > 0
            ? tvb_get_ptr(tvb, 0, captured_len)
            : NULL;

        guint8 *output = NULL;
        size_t output_len = 0;
        const char *error_message = NULL;
        const bool ok = faolan_zlib_stream_process(
            stream,
            payload,
            (size_t)captured_len,
            &output,
            &output_len,
            &error_message);

        frame_result = wmem_new0(wmem_file_scope(), faolan_frame_result_t);
        frame_result->direction = direction;
        frame_result->compression_state = faolan_stream_mode_to_string(stream->mode);
        frame_result->header_progress = (uint32_t)stream->pending_header_count;
        frame_result->input_bytes = (uint32_t)captured_len;
        frame_result->output_bytes = (uint32_t)output_len;
        frame_result->ok = ok;

        if (output_len > 0) {
            frame_result->output_blob = (uint8_t *)wmem_memdup(wmem_file_scope(), output, output_len);
        }

        if (error_message != NULL) {
            frame_result->error_message = wmem_strdup(wmem_file_scope(), error_message);
        }

        p_add_proto_data(wmem_file_scope(), pinfo, proto_faolan_gs_zlib, 0, frame_result);
        free(output);
    }

    add_result_to_tree(faolan_tree, tvb, pinfo, frame_result);

    return tvb_captured_length(tvb);
}

void proto_register_faolan_gs_zlib(void)
{
    static hf_register_info hf[] = {
        //{&hf_faolan_direction, {"Direction", "faolan_gs.direction", FT_STRING, BASE_NONE, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_compression_state, {"Compression State", "faolan_gs.compression_state", FT_STRING, BASE_NONE, NULL, 0x0, NULL, HFILL}},
        //{&hf_faolan_header_progress, {"Header Progress", "faolan_gs.header_progress", FT_UINT32, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_input_bytes, {"Compressed Input Bytes", "faolan_gs.input_bytes", FT_UINT32, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_output_bytes, {"Decompressed Output Bytes", "faolan_gs.output_bytes", FT_UINT32, BASE_DEC, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_output_blob, {"Decompressed Bytes", "faolan_gs.output_blob", FT_BYTES, BASE_NONE, NULL, 0x0, NULL, HFILL}},
        {&hf_faolan_error, {"Error", "faolan_gs.error", FT_STRING, BASE_NONE, NULL, 0x0, NULL, HFILL}},
    };

    static gint *ett[] = {
        &ett_faolan,
    };

    static ei_register_info ei[] = {
        {&ei_faolan_inflate_failed, {"faolan_gs.inflate_failed", PI_MALFORMED, PI_ERROR, "zlib inflate failed", EXPFILL}},
    };

    proto_faolan_gs_zlib = proto_register_protocol("Faolan GameServer zlib", "FaolanGS", "faolan_gs");
    proto_register_field_array(proto_faolan_gs_zlib, hf, array_length(hf));
    proto_register_subtree_array(ett, array_length(ett));

    expert_module_t *expert_faolan = expert_register_protocol(proto_faolan_gs_zlib);
    expert_register_field_array(expert_faolan, ei, array_length(ei));

    module_t *faolan_module = prefs_register_protocol(proto_faolan_gs_zlib, NULL);
    prefs_register_range_preference(
        faolan_module,
        "tcp_ports",
        "Faolan GameServer TCP ports",
        "TCP ports used for Faolan GameServer stream decompression.",
        &global_faolan_tcp_ports,
        65535
    );
}

void proto_reg_handoff_faolan_gs_zlib(void)
{
    static bool initialized = false;
    static range_t *saved_ports;

    if (!initialized) {
        faolan_handle = create_dissector_handle(dissect_faolan_gs_zlib, proto_faolan_gs_zlib);
        initialized = true;
    } else {
        dissector_delete_uint_range("tcp.port", saved_ports, faolan_handle);
        g_free(saved_ports);
    }

    saved_ports = range_copy(NULL, global_faolan_tcp_ports);
    dissector_add_uint_range("tcp.port", global_faolan_tcp_ports, faolan_handle);
}
