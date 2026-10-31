/* SPDX-License-Identifier: GPL-2.0-or-later */

#ifndef FAOLAN_CONAN_STREAM_H
#define FAOLAN_CONAN_STREAM_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include <zlib.h>

#include "faolan_conan_framer.h"

/*
 * One direction of a Faolan/AoC TCP stream: Conan packet framing with in-band compression
 * transitions.
 *
 * The stream is a sequence of [u32 BE length][length bytes] packets. At a PACKET BOUNDARY the
 * peer may emit a 9-byte transition header instead: [80 00 00 05][4 bytes][flag]. flag 0x00
 * switches the rest of the stream to one continuous zlib deflate stream (framing continues on
 * the inflated bytes); a non-zero flag (0x01 observed) leaves the stream as plaintext.
 *
 * Observed in the Strie login capture: the GameServer s2c stream transitions at offset 0 with
 * flag 00 (compressed), the c2s stream transitions mid-stream with flag 01 (stays plain).
 * NOTE: the C# ConanMessageFramer only detects the marker at receive-chunk starts and drops the
 * remainder of a chunk with a mid-chunk transition; this module handles it at any packet
 * boundary instead.
 */

typedef enum faolan_stream_phase {
    FAOLAN_PHASE_PLAIN = 0,
    FAOLAN_PHASE_TRANSITION_HEADER,
    FAOLAN_PHASE_COMPRESSED,
    FAOLAN_PHASE_FAILED
} faolan_stream_phase_t;

typedef struct faolan_conan_stream {
    faolan_stream_phase_t phase;

    uint8_t transition_header[9];
    size_t transition_have;

    z_stream zstrm;
    bool zstrm_initialized;

    faolan_conan_framer_t framer;

    size_t total_input_bytes;
    size_t total_inflated_bytes;
} faolan_conan_stream_t;

void faolan_conan_stream_init(faolan_conan_stream_t *stream);
void faolan_conan_stream_destroy(faolan_conan_stream_t *stream);

const char *faolan_stream_phase_to_string(faolan_stream_phase_t phase);

/*
 * Pushes one receive chunk through the stream. Completed packets are appended to *packets
 * (realloc'd array, caller-owned via faolan_framed_packets_free). Bytes inflated by this push
 * are returned through *inflated (malloc-owned by the caller, may be NULL when none).
 * Returns false on zlib/allocation failure (the stream stays FAILED and swallows further input).
 */
bool faolan_conan_stream_push(
    faolan_conan_stream_t *stream,
    const uint8_t *input,
    size_t input_len,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    uint8_t **inflated,
    size_t *inflated_len,
    const char **error_message);

#endif
