/* SPDX-License-Identifier: GPL-2.0-or-later */

#include "faolan_conan_framer.h"
#include "faolan_conan_stream.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Builds one framed packet [u32 BE len][payload] into dst, returns total size. */
static size_t build_packet(uint8_t *dst, const uint8_t *payload, size_t payload_len)
{
    dst[0] = (uint8_t)(payload_len >> 24);
    dst[1] = (uint8_t)(payload_len >> 16);
    dst[2] = (uint8_t)(payload_len >> 8);
    dst[3] = (uint8_t)payload_len;
    memcpy(dst + 4, payload, payload_len);
    return 4 + payload_len;
}

static int push_chunked(
    faolan_conan_stream_t *stream,
    const uint8_t *bytes,
    size_t len,
    const size_t *chunks,
    size_t chunk_count,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    const char **last_error)
{
    size_t offset = 0;
    size_t chunk_index = 0;

    while (offset < len) {
        const size_t want = chunks[chunk_index % chunk_count];
        const size_t remaining = len - offset;
        const size_t take = want < remaining ? want : remaining;

        uint8_t *inflated = NULL;
        size_t inflated_len = 0;

        if (!faolan_conan_stream_push(stream, bytes + offset, take, packets, packet_count, &inflated, &inflated_len, last_error)) {
            free(inflated);
            return 0;
        }

        free(inflated);
        offset += take;
        chunk_index++;
    }

    return 1;
}

/* s2c pattern: transition header (flag 00) at stream start, framed packets inside the zlib stream. */
static int test_compressed_from_start(void)
{
    const uint8_t payload_a[] = {0x11, 0x22, 0x33, 0x44, 0x55};
    const uint8_t payload_b[] = {0xDE, 0xAD, 0xBE, 0xEF};

    uint8_t plain[64];
    size_t plain_len = build_packet(plain, payload_a, sizeof(payload_a));
    plain_len += build_packet(plain + plain_len, payload_b, sizeof(payload_b));

    uLongf compressed_len = compressBound((uLong)plain_len);
    uint8_t compressed[128];
    if (compress2(compressed, &compressed_len, plain, (uLong)plain_len, Z_BEST_SPEED) != Z_OK) {
        return 0;
    }

    uint8_t stream_bytes[160] = {0x80, 0x00, 0x00, 0x05, 0xD2, 0x02, 0xEF, 0x8D, 0x00};
    memcpy(stream_bytes + 9, compressed, compressed_len);
    const size_t stream_len = 9 + compressed_len;
    const size_t chunks[] = {2, 1, 3, 2, 4, 5, 7, 9, 13};

    faolan_conan_stream_t state;
    faolan_conan_stream_init(&state);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;
    const char *last_error = NULL;

    const int ok = push_chunked(&state, stream_bytes, stream_len, chunks, sizeof(chunks) / sizeof(chunks[0]), &packets, &packet_count, &last_error) &&
                   packet_count == 2 &&
                   packets[0].len == 4 + sizeof(payload_a) &&
                   memcmp(packets[0].data + 4, payload_a, sizeof(payload_a)) == 0 &&
                   packets[1].len == 4 + sizeof(payload_b) &&
                   memcmp(packets[1].data + 4, payload_b, sizeof(payload_b)) == 0 &&
                   state.phase == FAOLAN_PHASE_COMPRESSED;

    if (!ok) {
        fprintf(stderr, "test_compressed_from_start failed (phase=%s, count=%zu, err=%s)\n",
                faolan_stream_phase_to_string(state.phase), packet_count, last_error == NULL ? "none" : last_error);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_stream_destroy(&state);
    return ok;
}

/* c2s pattern from the Strie login capture: plaintext packets, then a MID-STREAM transition
   header with flag 01, then more plaintext packets (the regression that dropped a ping). */
static int test_midstream_plaintext_transition(void)
{
    const uint8_t payload_a[] = {0xAA, 0xBB, 0xCC};
    const uint8_t payload_b[] = {0x01, 0x02, 0x03, 0x04, 0x05, 0x06};

    uint8_t stream_bytes[64];
    size_t len = build_packet(stream_bytes, payload_a, sizeof(payload_a));

    const uint8_t transition[9] = {0x80, 0x00, 0x00, 0x05, 0xA5, 0x05, 0xDF, 0x1B, 0x01};
    memcpy(stream_bytes + len, transition, sizeof(transition));
    len += sizeof(transition);

    len += build_packet(stream_bytes + len, payload_b, sizeof(payload_b));

    const size_t chunks[] = {3, 1, 5, 2, 4};

    faolan_conan_stream_t state;
    faolan_conan_stream_init(&state);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;
    const char *last_error = NULL;

    const int ok = push_chunked(&state, stream_bytes, len, chunks, sizeof(chunks) / sizeof(chunks[0]), &packets, &packet_count, &last_error) &&
                   packet_count == 2 &&
                   packets[0].len == 4 + sizeof(payload_a) &&
                   memcmp(packets[0].data + 4, payload_a, sizeof(payload_a)) == 0 &&
                   packets[1].len == 4 + sizeof(payload_b) &&
                   memcmp(packets[1].data + 4, payload_b, sizeof(payload_b)) == 0 &&
                   state.phase == FAOLAN_PHASE_PLAIN;

    if (!ok) {
        fprintf(stderr, "test_midstream_plaintext_transition failed (phase=%s, count=%zu, err=%s)\n",
                faolan_stream_phase_to_string(state.phase), packet_count, last_error == NULL ? "none" : last_error);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_stream_destroy(&state);
    return ok;
}

static int test_invalid_compressed_data(void)
{
    const uint8_t stream_bytes[] = {
        0x80, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, /* transition, flag 00 */
        0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 /* not a zlib stream */
    };
    const size_t chunks[] = {3, 3, 3, 4, 4};

    faolan_conan_stream_t state;
    faolan_conan_stream_init(&state);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;
    const char *last_error = NULL;

    const int processed = push_chunked(&state, stream_bytes, sizeof(stream_bytes), chunks, sizeof(chunks) / sizeof(chunks[0]), &packets, &packet_count, &last_error);
    const int ok = !processed && state.phase == FAOLAN_PHASE_FAILED && packet_count == 0;

    if (!ok) {
        fprintf(stderr, "test_invalid_compressed_data failed (processed=%d, phase=%s, err=%s)\n",
                processed, faolan_stream_phase_to_string(state.phase), last_error == NULL ? "none" : last_error);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_stream_destroy(&state);
    return ok;
}

static int test_framer_chunked_packets(void)
{
    /* Two framed packets plus a zero-length frame, fed in awkward chunks. */
    const uint8_t stream_bytes[] = {
        0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05,
    };
    const size_t chunks[] = {1, 3, 2, 6, 1};

    faolan_conan_framer_t framer;
    faolan_conan_framer_init(&framer);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;

    size_t offset = 0;
    size_t chunk_index = 0;
    int ok = 1;
    while (offset < sizeof(stream_bytes) && ok) {
        const size_t want = chunks[chunk_index % (sizeof(chunks) / sizeof(chunks[0]))];
        const size_t remaining = sizeof(stream_bytes) - offset;
        const size_t take = want < remaining ? want : remaining;

        ok = faolan_conan_framer_push(&framer, stream_bytes + offset, take, &packets, &packet_count);
        offset += take;
        chunk_index++;
    }

    const uint8_t expected_first[] = {0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC};
    const uint8_t expected_second[] = {0x00, 0x00, 0x00, 0x05, 0x01, 0x02, 0x03, 0x04, 0x05};

    ok = ok &&
         packet_count == 2 &&
         packets[0].len == sizeof(expected_first) &&
         memcmp(packets[0].data, expected_first, sizeof(expected_first)) == 0 &&
         packets[1].len == sizeof(expected_second) &&
         memcmp(packets[1].data, expected_second, sizeof(expected_second)) == 0;

    if (!ok) {
        fprintf(stderr, "test_framer_chunked_packets failed (count=%zu)\n", packet_count);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_framer_reset(&framer);
    return ok;
}

static int test_framer_oversize_resets(void)
{
    /* A length above the 16 MiB cap resets the framer and discards the rest of the chunk. */
    const uint8_t bad[] = {0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x02, 0x03};
    const uint8_t good[] = {0x00, 0x00, 0x00, 0x02, 0x11, 0x22};

    faolan_conan_framer_t framer;
    faolan_conan_framer_init(&framer);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;

    int ok = faolan_conan_framer_push(&framer, bad, sizeof(bad), &packets, &packet_count) &&
             packet_count == 0 &&
             faolan_conan_framer_push(&framer, good, sizeof(good), &packets, &packet_count) &&
             packet_count == 1 &&
             packets[0].len == sizeof(good) &&
             memcmp(packets[0].data, good, sizeof(good)) == 0;

    if (!ok) {
        fprintf(stderr, "test_framer_oversize_resets failed (count=%zu)\n", packet_count);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_framer_reset(&framer);
    return ok;
}

static int test_chat_framer(void)
{
    /* ChatServer framing: [u16 opcode][u16 bodyLen][body]; a hello, a body-less packet and a
       chat message, fed in awkward chunks. */
    const uint8_t stream_bytes[] = {
        0x00, 0x00, 0x00, 0x03, 0x01, 0x02, 0x03,
        0x00, 0x64, 0x00, 0x00,
        0x00, 0x41, 0x00, 0x05, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE,
    };
    const size_t chunks[] = {1, 4, 2, 5, 3};

    faolan_conan_framer_t framer;
    faolan_conan_framer_init(&framer);

    faolan_framed_packet_t *packets = NULL;
    size_t packet_count = 0;

    size_t offset = 0;
    size_t chunk_index = 0;
    int ok = 1;
    while (offset < sizeof(stream_bytes) && ok) {
        const size_t want = chunks[chunk_index % (sizeof(chunks) / sizeof(chunks[0]))];
        const size_t remaining = sizeof(stream_bytes) - offset;
        const size_t take = want < remaining ? want : remaining;

        ok = faolan_chat_framer_push(&framer, stream_bytes + offset, take, &packets, &packet_count);
        offset += take;
        chunk_index++;
    }

    ok = ok &&
         packet_count == 3 &&
         packets[0].len == 7 && memcmp(packets[0].data, stream_bytes, 7) == 0 &&
         packets[1].len == 4 && memcmp(packets[1].data, stream_bytes + 7, 4) == 0 &&
         packets[2].len == 9 && memcmp(packets[2].data, stream_bytes + 11, 9) == 0;

    if (!ok) {
        fprintf(stderr, "test_chat_framer failed (count=%zu)\n", packet_count);
    }

    faolan_framed_packets_free(packets, packet_count);
    faolan_conan_framer_reset(&framer);
    return ok;
}

int main(void)
{
    const int ok1 = test_compressed_from_start();
    const int ok2 = test_midstream_plaintext_transition();
    const int ok3 = test_invalid_compressed_data();
    const int ok4 = test_framer_chunked_packets();
    const int ok5 = test_framer_oversize_resets();
    const int ok6 = test_chat_framer();

    if (!ok1 || !ok2 || !ok3 || !ok4 || !ok5 || !ok6) {
        return 1;
    }

    printf("OK: compressed-from-start + midstream-plaintext-transition + invalid-data + framer + chat-framer tests passed\n");
    return 0;
}
