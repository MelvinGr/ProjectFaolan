#include "faolan_zlib_stream.h"

#include <stdlib.h>
#include <string.h>

static const uint8_t k_compression_prefix[4] = {0x80, 0x00, 0x00, 0x05};
static const size_t k_header_len = 9;
static const size_t k_chunk_len = 4096;

void faolan_zlib_stream_init(faolan_zlib_stream_t *stream)
{
    memset(stream, 0, sizeof(*stream));
    stream->mode = FAOLAN_STREAM_DISABLED;
}

void faolan_zlib_stream_reset(faolan_zlib_stream_t *stream)
{
    if (stream->zstrm_initialized) {
        inflateEnd(&stream->zstrm);
    }

    faolan_zlib_stream_init(stream);
}

void faolan_zlib_stream_destroy(faolan_zlib_stream_t *stream)
{
    faolan_zlib_stream_reset(stream);
}

const char *faolan_stream_mode_to_string(faolan_stream_mode_t mode)
{
    switch (mode) {
        case FAOLAN_STREAM_DISABLED:
            return "Disabled";
        case FAOLAN_STREAM_HEADER_PENDING:
            return "HeaderPending";
        case FAOLAN_STREAM_ENABLED:
            return "Enabled";
        case FAOLAN_STREAM_FAILED:
            return "Failed";
        case FAOLAN_STREAM_FINISHED:
            return "Finished";
        case FAOLAN_STREAM_NOT_COMPRESSED:
            return "NotCompressed";
        default:
            return "Unknown";
    }
}

static bool ensure_capacity(uint8_t **buffer, size_t required_len, size_t *capacity)
{
    if (*capacity >= required_len) {
        return true;
    }

    size_t next_capacity = *capacity == 0 ? k_chunk_len : *capacity;
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

bool faolan_zlib_stream_process(
    faolan_zlib_stream_t *stream,
    const uint8_t *input,
    size_t input_len,
    uint8_t **output,
    size_t *output_len,
    const char **error_message)
{
    if (stream == NULL || output == NULL || output_len == NULL) {
        set_error(error_message, "invalid arguments");
        return false;
    }

    *output = NULL;
    *output_len = 0;
    set_error(error_message, NULL);

    if (input_len == 0 || input == NULL) {
        return true;
    }

    if (stream->mode == FAOLAN_STREAM_FAILED ||
        stream->mode == FAOLAN_STREAM_FINISHED ||
        stream->mode == FAOLAN_STREAM_NOT_COMPRESSED) {
        return true;
    }

    size_t consumed = 0;
    if (stream->mode == FAOLAN_STREAM_DISABLED || stream->mode == FAOLAN_STREAM_HEADER_PENDING) {
        stream->mode = FAOLAN_STREAM_HEADER_PENDING;

        size_t missing = k_header_len - stream->pending_header_count;
        size_t to_copy = input_len < missing ? input_len : missing;

        memcpy(stream->pending_header + stream->pending_header_count, input, to_copy);
        stream->pending_header_count += to_copy;
        consumed += to_copy;

        if (stream->pending_header_count < k_header_len) {
            return true;
        }

        if (memcmp(stream->pending_header, k_compression_prefix, sizeof(k_compression_prefix)) != 0) {
            stream->mode = FAOLAN_STREAM_NOT_COMPRESSED;
            return true;
        }

        memset(&stream->zstrm, 0, sizeof(stream->zstrm));
        if (inflateInit(&stream->zstrm) != Z_OK) {
            stream->mode = FAOLAN_STREAM_FAILED;
            set_error(error_message, "inflateInit failed");
            return false;
        }

        stream->zstrm_initialized = true;
        stream->mode = FAOLAN_STREAM_ENABLED;
    }

    if (stream->mode != FAOLAN_STREAM_ENABLED) {
        return true;
    }

    const uint8_t *compressed = input + consumed;
    size_t compressed_len = input_len - consumed;

    if (compressed_len == 0) {
        return true;
    }

    stream->zstrm.next_in = (Bytef *)compressed;
    stream->zstrm.avail_in = (uInt)compressed_len;

    uint8_t *local_output = NULL;
    size_t local_output_len = 0;
    size_t local_output_cap = 0;

    while (stream->zstrm.avail_in > 0) {
        if (!ensure_capacity(&local_output, local_output_len + k_chunk_len, &local_output_cap)) {
            free(local_output);
            stream->mode = FAOLAN_STREAM_FAILED;
            set_error(error_message, "output allocation failed");
            return false;
        }

        stream->zstrm.next_out = local_output + local_output_len;
        stream->zstrm.avail_out = (uInt)(local_output_cap - local_output_len);

        int ret = inflate(&stream->zstrm, Z_NO_FLUSH);
        size_t produced = (local_output_cap - local_output_len) - stream->zstrm.avail_out;
        local_output_len += produced;

        if (ret == Z_STREAM_END) {
            stream->mode = FAOLAN_STREAM_FINISHED;
            break;
        }

        if (ret != Z_OK) {
            free(local_output);
            stream->mode = FAOLAN_STREAM_FAILED;
            set_error(error_message, "inflate failed");
            return false;
        }

        if (produced == 0 && stream->zstrm.avail_in > 0) {
            free(local_output);
            stream->mode = FAOLAN_STREAM_FAILED;
            set_error(error_message, "inflate made no progress");
            return false;
        }
    }

    stream->total_input_bytes += compressed_len;
    stream->total_output_bytes += local_output_len;

    *output = local_output;
    *output_len = local_output_len;
    return true;
}
