/* SPDX-License-Identifier: GPL-2.0-or-later */

#ifndef FAOLAN_CONAN_FRAMER_H
#define FAOLAN_CONAN_FRAMER_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * Conan packet framer - mirrors Faolan.Core ConanMessageFramer.
 *
 * The (already decompressed) byte stream is a sequence of [u32 BE length][length bytes];
 * the length field excludes itself. Emitted packets KEEP the 4-byte length prefix so the
 * packet layout matches what ConanPacket parses: [len][crc][hdrlen][RpcHeader][body].
 */

typedef struct faolan_framed_packet {
    uint8_t *data; /* malloc-owned, freed via faolan_framed_packets_free */
    size_t len;
} faolan_framed_packet_t;

typedef struct faolan_conan_framer {
    uint8_t len_buf[4];
    size_t len_buf_count;

    uint8_t *pkt_buf; /* malloc-owned while a packet body is being collected */
    size_t pkt_len;
    size_t pkt_read;
} faolan_conan_framer_t;

void faolan_conan_framer_init(faolan_conan_framer_t *framer);
void faolan_conan_framer_reset(faolan_conan_framer_t *framer);

/*
 * Feeds stream bytes into the framer. Packets completed by this push are appended to
 * *packets (a realloc'd array of *packet_count entries, caller-owned).
 * Returns false only on allocation failure.
 */
bool faolan_conan_framer_push(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count);

/*
 * Like faolan_conan_framer_push, but stops when a "length" of 80 00 00 05 is collected at a
 * packet boundary - that is the in-band compression-transition marker, not a real length.
 * *consumed is the number of input bytes used (including the 4 marker bytes when *hit_marker
 * is set); the caller handles the remaining 5 transition-header bytes.
 */
bool faolan_conan_framer_push_scan(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    size_t *consumed,
    bool *hit_marker);

/*
 * ChatServer (chat) framing: [u16 BE opcode][u16 BE bodyLen][body] (mirrors
 * ChatServerMessageFramer). Reuses the same state struct - len_buf doubles as the 4-byte
 * header buffer. Emitted packets keep the 4-byte header.
 */
bool faolan_chat_framer_push(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count);

void faolan_framed_packets_free(faolan_framed_packet_t *packets, size_t count);

#endif
