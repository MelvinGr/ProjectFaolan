#ifndef FAOLAN_ZLIB_STREAM_H
#define FAOLAN_ZLIB_STREAM_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include <zlib.h>

typedef enum faolan_stream_mode {
    FAOLAN_STREAM_DISABLED = 0,
    FAOLAN_STREAM_HEADER_PENDING,
    FAOLAN_STREAM_ENABLED,
    FAOLAN_STREAM_FAILED,
    FAOLAN_STREAM_FINISHED,
    FAOLAN_STREAM_NOT_COMPRESSED
} faolan_stream_mode_t;

typedef struct faolan_zlib_stream {
    uint8_t pending_header[9];
    size_t pending_header_count;

    z_stream zstrm;
    bool zstrm_initialized;

    faolan_stream_mode_t mode;
    size_t total_input_bytes;
    size_t total_output_bytes;
} faolan_zlib_stream_t;

void faolan_zlib_stream_init(faolan_zlib_stream_t *stream);
void faolan_zlib_stream_reset(faolan_zlib_stream_t *stream);
void faolan_zlib_stream_destroy(faolan_zlib_stream_t *stream);

const char *faolan_stream_mode_to_string(faolan_stream_mode_t mode);

/*
 * Processes one direction of a TCP byte stream.
 *
 * The output buffer is allocated with malloc/realloc and owned by the caller.
 * Returns true on success, false on a zlib/internal error.
 */
bool faolan_zlib_stream_process(
    faolan_zlib_stream_t *stream,
    const uint8_t *input,
    size_t input_len,
    uint8_t **output,
    size_t *output_len,
    const char **error_message
);

#endif
