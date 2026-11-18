#include <immintrin.h>
#include <omp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

static double now_ms(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec * 1e3 + t.tv_nsec / 1e6;
}

static uint64_t rng = 0x9E3779B97F4A7C15ull;
static uint32_t next_random(void)
{
    rng ^= rng << 13;
    rng ^= rng >> 7;
    rng ^= rng << 17;
    return (uint32_t)rng;
}

static int compare_doubles(const void *a, const void *b)
{
    double x = *(const double *)a, y = *(const double *)b;
    return x < y ? -1 : x > y;
}

typedef struct
{
    int entities;
    int cells_per_entity;
    int rows;
    int cell_bytes;
} shape_t;

static double stream_read_gbps(size_t bytes, int threads)
{
    uint64_t *data = aligned_alloc(64, bytes);
    memset(data, 1, bytes);
    size_t words = bytes / 8;
    double samples[7];
    volatile uint64_t sink = 0;
    for (int r = 0; r < 7; r++)
    {
        double start = now_ms();
        uint64_t total = 0;
#pragma omp parallel for num_threads(threads) reduction(+ : total) schedule(static)
        for (size_t i = 0; i < words; i += 4)
        {
            __m256i v = _mm256_load_si256((const __m256i *)(data + i));
            total += (uint64_t)_mm256_extract_epi64(v, 0) ^ (uint64_t)_mm256_extract_epi64(v, 3);
        }
        sink += total;
        samples[r] = now_ms() - start;
    }
    qsort(samples, 7, sizeof(double), compare_doubles);
    free(data);
    return bytes / (samples[3] * 1e6);
}

int main(int argc, char **argv)
{
    shape_t shape = {50000, 100, 0, 2};
    int threads = omp_get_max_threads();
    for (int i = 1; i + 1 < argc; i += 2)
    {
        if (!strcmp(argv[i], "-e")) shape.entities = atoi(argv[i + 1]);
        if (!strcmp(argv[i], "-c")) shape.cells_per_entity = atoi(argv[i + 1]);
        if (!strcmp(argv[i], "-t")) threads = atoi(argv[i + 1]);
        if (!strcmp(argv[i], "-w")) shape.cell_bytes = atoi(argv[i + 1]);
    }

    int timelines = 500;
    int *row_base = malloc(sizeof(int) * (timelines + 1));
    int *duration = malloc(sizeof(int) * timelines);
    int rows = 0;
    for (int t = 0; t < timelines; t++)
    {
        duration[t] = 30 + (t * 7) % 31;
        row_base[t] = rows;
        rows += duration[t];
    }
    row_base[timelines] = rows;
    shape.rows = rows;

    int32_t *score_rows = aligned_alloc(64, (size_t)65536 * 32);
    memset(score_rows, 0, (size_t)65536 * 32);
    for (int r = 0; r < rows; r++)
        for (int lane = 0; lane < 8; lane++)
            score_rows[r * 8 + lane] = (int32_t)(next_random() % 2001) - 1000;

    size_t cell_count = (size_t)shape.entities * shape.cells_per_entity;
    uint16_t *origin16 = aligned_alloc(64, cell_count * 2 + 64);
    uint32_t *origin32 = aligned_alloc(64, cell_count * 4 + 64);
    for (size_t c = 0; c < cell_count; c++)
    {
        int t = next_random() % timelines;
        int start_tick = next_random() % duration[t];
        uint32_t origin = (uint32_t)(row_base[t] + start_tick);
        origin16[c] = (uint16_t)origin;
        origin32[c] = origin;
    }
    int32_t *totals = aligned_alloc(64, (size_t)shape.entities * 32);

    double memory_gbps = stream_read_gbps((size_t)512 << 20, threads);

    double samples[33];
    int64_t checksum = 0;
    for (int tick = 0; tick < 33; tick++)
    {
        uint32_t frame = (uint32_t)tick;
        double start = now_ms();
#pragma omp parallel for num_threads(threads) schedule(static, 256)
        for (int e = 0; e < shape.entities; e++)
        {
            __m256i sum_lanes = _mm256_setzero_si256();
            __m256i max_lanes = _mm256_set1_epi32(INT32_MIN);
            size_t first = (size_t)e * shape.cells_per_entity;
            if (shape.cell_bytes == 2)
            {
                const uint16_t *cells = origin16 + first;
                for (int c = 0; c < shape.cells_per_entity; c++)
                {
                    uint32_t row = (uint16_t)(cells[c] + frame);
                    __m256i value = _mm256_load_si256((const __m256i *)(score_rows + (size_t)row * 8));
                    sum_lanes = _mm256_add_epi32(sum_lanes, value);
                    max_lanes = _mm256_max_epi32(max_lanes, value);
                }
            }
            else
            {
                const uint32_t *cells = origin32 + first;
                for (int c = 0; c < shape.cells_per_entity; c++)
                {
                    uint32_t row = (cells[c] + frame) & 0xFFFF;
                    __m256i value = _mm256_load_si256((const __m256i *)(score_rows + (size_t)row * 8));
                    sum_lanes = _mm256_add_epi32(sum_lanes, value);
                    max_lanes = _mm256_max_epi32(max_lanes, value);
                }
            }
            __m256i packed = _mm256_blend_epi32(sum_lanes, max_lanes, 0xF0);
            _mm256_store_si256((__m256i *)(totals + (size_t)e * 8), packed);
        }
        samples[tick] = now_ms() - start;
        for (int e = 0; e < shape.entities; e += 997) checksum += totals[e * 8] + totals[e * 8 + 7];
    }
    qsort(samples + 1, 32, sizeof(double), compare_doubles);
    double median = samples[17];
    double cell_mb = cell_count * (double)shape.cell_bytes / 1e6;
    printf("floor entities=%d cells=%d cellBytes=%d threads=%d rows=%d scoreKB=%d streamGBps=%.1f tickMs=%.3f nsPerCell=%.4f cellMB=%.1f cellGBps=%.1f checksum=%lld\n",
           shape.entities, shape.cells_per_entity, shape.cell_bytes, threads, rows, rows * 32 / 1024, memory_gbps,
           median, median * 1e6 / cell_count, cell_mb, cell_mb / median, (long long)checksum);
    return 0;
}
