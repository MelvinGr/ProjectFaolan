#include "faolan_zlib_stream.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int append_bytes(uint8_t **dst, size_t *dst_len, const uint8_t *src, size_t src_len)
{
    uint8_t *resized = (uint8_t *)realloc(*dst, *dst_len + src_len);
    if (resized == NULL) {
        return 0;
    }

    memcpy(resized + *dst_len, src, src_len);
    *dst = resized;
    *dst_len += src_len;
    return 1;
}

static int run_chunked_stream(
    const uint8_t *stream,
    size_t stream_len,
    const size_t *chunks,
    size_t chunk_count,
    faolan_zlib_stream_t *state,
    uint8_t **collected,
    size_t *collected_len,
    const char **last_error)
{
    size_t offset = 0;
    size_t chunk_index = 0;
    *collected = NULL;
    *collected_len = 0;
    *last_error = NULL;

    while (offset < stream_len) {
        size_t want = chunks[chunk_index % chunk_count];
        size_t remaining = stream_len - offset;
        size_t take = want < remaining ? want : remaining;

        uint8_t *out = NULL;
        size_t out_len = 0;
        const char *err = NULL;

        if (!faolan_zlib_stream_process(state, stream + offset, take, &out, &out_len, &err)) {
            *last_error = err;
            free(out);
            return 0;
        }

        if (out_len > 0) {
            if (!append_bytes(collected, collected_len, out, out_len)) {
                free(out);
                *last_error = "append failure";
                return 0;
            }
        }

        free(out);
        offset += take;
        chunk_index++;
    }

    return 1;
}

static int test_chunked_success(void)
{
    const uint8_t marker_header[9] = {0x80, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00};
    const char *plaintext = "Faolan GameServer zlib stream smoke test";
    const size_t plaintext_len = strlen(plaintext);
    const size_t chunks[] = {2, 1, 3, 2, 4, 5, 7, 9, 13, 32, 64};

    uLongf compressed_len = compressBound((uLong)plaintext_len);
    uint8_t *compressed = (uint8_t *)malloc(compressed_len);
    if (compressed == NULL) {
        return 0;
    }

    int zret = compress2(compressed, &compressed_len, (const Bytef *)plaintext, (uLong)plaintext_len, Z_BEST_SPEED);
    if (zret != Z_OK) {
        free(compressed);
        return 0;
    }

    const size_t stream_len = sizeof(marker_header) + (size_t)compressed_len;
    uint8_t *stream = (uint8_t *)malloc(stream_len);
    if (stream == NULL) {
        free(compressed);
        return 0;
    }

    memcpy(stream, marker_header, sizeof(marker_header));
    memcpy(stream + sizeof(marker_header), compressed, (size_t)compressed_len);

    faolan_zlib_stream_t state;
    faolan_zlib_stream_init(&state);

    uint8_t *collected = NULL;
    size_t collected_len = 0;
    const char *last_error = NULL;
    const int ok = run_chunked_stream(stream, stream_len, chunks, sizeof(chunks) / sizeof(chunks[0]), &state, &collected, &collected_len, &last_error) &&
                   collected_len == plaintext_len &&
                   memcmp(collected, plaintext, plaintext_len) == 0 &&
                   state.mode == FAOLAN_STREAM_FINISHED;

    if (!ok) {
        fprintf(stderr, "test_chunked_success failed (mode=%s, err=%s)\n", faolan_stream_mode_to_string(state.mode), last_error == NULL ? "none" : last_error);
    }

    free(collected);
    faolan_zlib_stream_destroy(&state);
    free(stream);
    free(compressed);
    return ok;
}

static int test_not_compressed(void)
{
    const uint8_t payload[] = {0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99};
    const size_t chunks[] = {1, 2, 1, 1, 4};

    faolan_zlib_stream_t state;
    faolan_zlib_stream_init(&state);

    uint8_t *collected = NULL;
    size_t collected_len = 0;
    const char *last_error = NULL;

    const int ok = run_chunked_stream(payload, sizeof(payload), chunks, sizeof(chunks) / sizeof(chunks[0]), &state, &collected, &collected_len, &last_error) &&
                   collected_len == 0 &&
                   state.mode == FAOLAN_STREAM_NOT_COMPRESSED;

    if (!ok) {
        fprintf(stderr, "test_not_compressed failed (mode=%s, err=%s)\n", faolan_stream_mode_to_string(state.mode), last_error == NULL ? "none" : last_error);
    }

    free(collected);
    faolan_zlib_stream_destroy(&state);
    return ok;
}

static int test_invalid_compressed_data(void)
{
    const uint8_t marker_header[9] = {0x80, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00};
    const uint8_t bad_payload[] = {0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04};
    const size_t chunks[] = {3, 3, 3, 4, 4};

    uint8_t stream[sizeof(marker_header) + sizeof(bad_payload)];
    memcpy(stream, marker_header, sizeof(marker_header));
    memcpy(stream + sizeof(marker_header), bad_payload, sizeof(bad_payload));

    faolan_zlib_stream_t state;
    faolan_zlib_stream_init(&state);

    uint8_t *collected = NULL;
    size_t collected_len = 0;
    const char *last_error = NULL;

    const int processed = run_chunked_stream(stream, sizeof(stream), chunks, sizeof(chunks) / sizeof(chunks[0]), &state, &collected, &collected_len, &last_error);
    const int ok = !processed && state.mode == FAOLAN_STREAM_FAILED;

    if (!ok) {
        fprintf(stderr, "test_invalid_compressed_data failed (processed=%d, mode=%s, err=%s)\n",
                processed,
                faolan_stream_mode_to_string(state.mode),
                last_error == NULL ? "none" : last_error);
    }

    free(collected);
    faolan_zlib_stream_destroy(&state);
    return ok;
}

int main(void)
{
    const int ok1 = test_chunked_success();
    const int ok2 = test_not_compressed();
    const int ok3 = test_invalid_compressed_data();

    if (!ok1 || !ok2 || !ok3) {
        return 1;
    }

    printf("OK: chunked success + non-compressed + invalid-data tests passed\n");
    return 0;
}
