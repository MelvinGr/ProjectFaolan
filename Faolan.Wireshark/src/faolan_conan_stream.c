#include "faolan_conan_stream.h"

#include <stdlib.h>
#include <string.h>

static const size_t k_transition_header_len = 9;
static const size_t k_inflate_chunk = 4096;

void faolan_conan_stream_init(faolan_conan_stream_t *stream)
{
    memset(stream, 0, sizeof(*stream));
    stream->phase = FAOLAN_PHASE_PLAIN;
    faolan_conan_framer_init(&stream->framer);
}

void faolan_conan_stream_destroy(faolan_conan_stream_t *stream)
{
    if (stream->zstrm_initialized) {
        inflateEnd(&stream->zstrm);
    }

    faolan_conan_framer_reset(&stream->framer);
    faolan_conan_stream_init(stream);
}

const char *faolan_stream_phase_to_string(faolan_stream_phase_t phase)
{
    switch (phase) {
        case FAOLAN_PHASE_PLAIN:
            return "Plain";
        case FAOLAN_PHASE_TRANSITION_HEADER:
            return "TransitionHeader";
        case FAOLAN_PHASE_COMPRESSED:
            return "Compressed";
        case FAOLAN_PHASE_FAILED:
            return "Failed";
        default:
            return "Unknown";
    }
}

static bool ensure_capacity(uint8_t **buffer, size_t required_len, size_t *capacity)
{
    if (*capacity >= required_len) {
        return true;
    }

    size_t next_capacity = *capacity == 0 ? k_inflate_chunk : *capacity;
    while (next_capacity < required_len) {
        if (next_capacity > ((size_t)-1) / 2) {
            return false;
        }

        next_capacity *= 2;
    }

    uint8_t *resized = (uint8_t *)realloc(*buffer, next_capacity);
    if (resized == NULL) {
        return false;
    }

    *buffer = resized;
    *capacity = next_capacity;
    return true;
}

static void set_error(const char **error_message, const char *message)
{
    if (error_message != NULL) {
        *error_message = message;
    }
}

bool faolan_conan_stream_push(
    faolan_conan_stream_t *stream,
    const uint8_t *input,
    size_t input_len,
    faolan_framed_packet_t **packets,
    size_t *packet_count,
    uint8_t **inflated,
    size_t *inflated_len,
    const char **error_message)
{
    *inflated = NULL;
    *inflated_len = 0;
    set_error(error_message, NULL);

    if (input == NULL || input_len == 0) {
        return true;
    }

    stream->total_input_bytes += input_len;

    if (stream->phase == FAOLAN_PHASE_FAILED) {
        return true;
    }

    size_t pos = 0;
    uint8_t *inflate_buf = NULL;
    size_t inflate_buf_len = 0;
    size_t inflate_buf_cap = 0;

    while (pos < input_len) {
        if (stream->phase == FAOLAN_PHASE_PLAIN) {
            size_t consumed = 0;
            bool hit_marker = false;

            if (!faolan_conan_framer_push_scan(
                    &stream->framer, input + pos, input_len - pos,
                    packets, packet_count, &consumed, &hit_marker)) {
                stream->phase = FAOLAN_PHASE_FAILED;
                set_error(error_message, "framer allocation failed");
                free(inflate_buf);
                return false;
            }

            pos += consumed;

            if (hit_marker) {
                /* The 4 marker bytes count toward the 9-byte transition header. */
                memcpy(stream->transition_header, "\x80\x00\x00\x05", 4);
                stream->transition_have = 4;
                stream->phase = FAOLAN_PHASE_TRANSITION_HEADER;
            } else if (pos < input_len) {
                /* The framer reset on an oversize length and discards the rest of the chunk
                   (mirrors ConanMessageFramer). */
                break;
            }
        } else if (stream->phase == FAOLAN_PHASE_TRANSITION_HEADER) {
            const size_t missing = k_transition_header_len - stream->transition_have;
            const size_t available = input_len - pos;
            const size_t take = missing < available ? missing : available;

            memcpy(stream->transition_header + stream->transition_have, input + pos, take);
            stream->transition_have += take;
            pos += take;

            if (stream->transition_have < k_transition_header_len) {
                break;
            }

            /* Header byte 8 is the compression flag: 00 = zlib stream follows, non-zero
               (01 observed) = the stream stays plaintext. */
            if (stream->transition_header[8] == 0x00) {
                if (stream->zstrm_initialized) {
                    inflateEnd(&stream->zstrm);
                    stream->zstrm_initialized = false;
                }

                memset(&stream->zstrm, 0, sizeof(stream->zstrm));
                if (inflateInit(&stream->zstrm) != Z_OK) {
                    stream->phase = FAOLAN_PHASE_FAILED;
                    set_error(error_message, "inflateInit failed");
                    free(inflate_buf);
                    return false;
                }

                stream->zstrm_initialized = true;
                stream->phase = FAOLAN_PHASE_COMPRESSED;
            } else {
                stream->phase = FAOLAN_PHASE_PLAIN;
            }

            stream->transition_have = 0;
        } else { /* FAOLAN_PHASE_COMPRESSED */
            stream->zstrm.next_in = (Bytef *)(input + pos);
            stream->zstrm.avail_in = (uInt)(input_len - pos);

            const size_t inflated_before = inflate_buf_len;

            while (stream->zstrm.avail_in > 0) {
                if (!ensure_capacity(&inflate_buf, inflate_buf_len + k_inflate_chunk, &inflate_buf_cap)) {
                    stream->phase = FAOLAN_PHASE_FAILED;
                    set_error(error_message, "inflate output allocation failed");
                    free(inflate_buf);
                    return false;
                }

                stream->zstrm.next_out = inflate_buf + inflate_buf_len;
                stream->zstrm.avail_out = (uInt)(inflate_buf_cap - inflate_buf_len);

                const int ret = inflate(&stream->zstrm, Z_NO_FLUSH);
                const size_t produced = (inflate_buf_cap - inflate_buf_len) - stream->zstrm.avail_out;
                inflate_buf_len += produced;

                if (ret == Z_STREAM_END) {
                    break;
                }

                if (ret != Z_OK || (produced == 0 && stream->zstrm.avail_in > 0)) {
                    stream->phase = FAOLAN_PHASE_FAILED;
                    set_error(error_message, ret != Z_OK ? "inflate failed" : "inflate made no progress");
                    free(inflate_buf);
                    return false;
                }
            }

            pos = input_len;

            const size_t produced_now = inflate_buf_len - inflated_before;
            if (produced_now > 0 &&
                !faolan_conan_framer_push(
                    &stream->framer, inflate_buf + inflated_before, produced_now,
                    packets, packet_count)) {
                stream->phase = FAOLAN_PHASE_FAILED;
                set_error(error_message, "framer allocation failed");
                free(inflate_buf);
                return false;
            }
        }
    }

    stream->total_inflated_bytes += inflate_buf_len;

    if (inflate_buf_len > 0) {
        *inflated = inflate_buf;
        *inflated_len = inflate_buf_len;
    } else {
        free(inflate_buf);
    }

    return true;
}
