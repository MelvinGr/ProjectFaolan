#include "faolan_conan_framer.h"

#include <stdlib.h>
#include <string.h>

/* Same cap as ConanMessageFramer.MaxPacketSize. */
static const size_t k_max_packet_size = 16 * 1024 * 1024;

void faolan_conan_framer_init(faolan_conan_framer_t *framer)
{
    memset(framer, 0, sizeof(*framer));
}

void faolan_conan_framer_reset(faolan_conan_framer_t *framer)
{
    free(framer->pkt_buf);
    faolan_conan_framer_init(framer);
}

static bool emit_packet(
    const faolan_conan_framer_t *framer,
    faolan_framed_packet_t **packets,
    size_t *packet_count)
{
    faolan_framed_packet_t *resized = (faolan_framed_packet_t *)realloc(
        *packets, (*packet_count + 1) * sizeof(faolan_framed_packet_t));
    if (resized == NULL) {
        return false;
    }

    *packets = resized;

    const size_t total = sizeof(framer->len_buf) + framer->pkt_len;
    uint8_t *data = (uint8_t *)malloc(total);
    if (data == NULL) {
        return false;
    }

    memcpy(data, framer->len_buf, sizeof(framer->len_buf));
    if (framer->pkt_len > 0) {
        memcpy(data + sizeof(framer->len_buf), framer->pkt_buf, framer->pkt_len);
    }

    resized[*packet_count].data = data;
    resized[*packet_count].len = total;
    (*packet_count)++;
    return true;
}

static const uint8_t k_transition_marker[4] = {0x80, 0x00, 0x00, 0x05};

static bool framer_push_internal(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    size_t *consumed,
    bool *hit_marker)
{
    size_t offset = 0;

    while (offset < len) {
        const size_t available = len - offset;

        if (framer->pkt_buf != NULL) {
            const size_t requested = framer->pkt_len - framer->pkt_read;
            const size_t transferred = requested < available ? requested : available;

            memcpy(framer->pkt_buf + framer->pkt_read, bytes + offset, transferred);
            offset += transferred;
            framer->pkt_read += transferred;

            if (framer->pkt_read == framer->pkt_len) {
                if (!emit_packet(framer, packets, packet_count)) {
                    return false;
                }

                free(framer->pkt_buf);
                framer->pkt_buf = NULL;
                framer->pkt_len = 0;
                framer->pkt_read = 0;
            }
        } else {
            const size_t requested = sizeof(framer->len_buf) - framer->len_buf_count;
            const size_t transferred = requested < available ? requested : available;

            memcpy(framer->len_buf + framer->len_buf_count, bytes + offset, transferred);
            offset += transferred;
            framer->len_buf_count += transferred;

            if (framer->len_buf_count == sizeof(framer->len_buf)) {
                if (hit_marker != NULL &&
                    memcmp(framer->len_buf, k_transition_marker, sizeof(k_transition_marker)) == 0) {
                    framer->len_buf_count = 0;
                    *hit_marker = true;
                    *consumed = offset;
                    return true;
                }

                const size_t packet_len =
                    ((size_t)framer->len_buf[0] << 24) |
                    ((size_t)framer->len_buf[1] << 16) |
                    ((size_t)framer->len_buf[2] << 8) |
                    (size_t)framer->len_buf[3];

                if (packet_len > k_max_packet_size) {
                    /* ConanMessageFramer resets and discards the rest of this chunk. */
                    faolan_conan_framer_reset(framer);
                    break;
                }

                if (packet_len == 0) {
                    framer->len_buf_count = 0;
                    continue;
                }

                framer->pkt_buf = (uint8_t *)malloc(packet_len);
                if (framer->pkt_buf == NULL) {
                    return false;
                }

                framer->pkt_len = packet_len;
                framer->pkt_read = 0;
                framer->len_buf_count = 0;
            }
        }
    }

    if (consumed != NULL) {
        *consumed = offset;
    }

    return true;
}

bool faolan_conan_framer_push(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count)
{
    return framer_push_internal(framer, bytes, len, packets, packet_count, NULL, NULL);
}

bool faolan_conan_framer_push_scan(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    size_t *consumed,
    bool *hit_marker)
{
    *hit_marker = false;
    *consumed = 0;
    return framer_push_internal(framer, bytes, len, packets, packet_count, consumed, hit_marker);
}

bool faolan_chat_framer_push(
    faolan_conan_framer_t *framer,
    const uint8_t *bytes,
    size_t len,
    faolan_framed_packet_t **packets,
    size_t *packet_count)
{
    size_t offset = 0;

    while (offset < len) {
        const size_t available = len - offset;

        if (framer->pkt_buf != NULL) {
            const size_t requested = framer->pkt_len - framer->pkt_read;
            const size_t transferred = requested < available ? requested : available;

            memcpy(framer->pkt_buf + framer->pkt_read, bytes + offset, transferred);
            offset += transferred;
            framer->pkt_read += transferred;

            if (framer->pkt_read == framer->pkt_len) {
                if (!emit_packet(framer, packets, packet_count)) {
                    return false;
                }

                free(framer->pkt_buf);
                framer->pkt_buf = NULL;
                framer->pkt_len = 0;
                framer->pkt_read = 0;
            }
        } else {
            const size_t requested = sizeof(framer->len_buf) - framer->len_buf_count;
            const size_t transferred = requested < available ? requested : available;

            memcpy(framer->len_buf + framer->len_buf_count, bytes + offset, transferred);
            offset += transferred;
            framer->len_buf_count += transferred;

            if (framer->len_buf_count == sizeof(framer->len_buf)) {
                /* len_buf = [u16 opcode][u16 bodyLen]; the body follows. */
                const size_t body_len = ((size_t)framer->len_buf[2] << 8) | framer->len_buf[3];

                if (body_len == 0) {
                    if (!emit_packet(framer, packets, packet_count)) {
                        return false;
                    }

                    framer->len_buf_count = 0;
                    continue;
                }

                framer->pkt_buf = (uint8_t *)malloc(body_len);
                if (framer->pkt_buf == NULL) {
                    return false;
                }

                framer->pkt_len = body_len;
                framer->pkt_read = 0;
                framer->len_buf_count = 0;
            }
        }
    }

    return true;
}

void faolan_framed_packets_free(faolan_framed_packet_t *packets, size_t count)
{
    for (size_t i = 0; i < count; i++) {
        free(packets[i].data);
    }

    free(packets);
}
