#include "mn_internal.h"
#include "mn_thread.h"

/* The drop cutter and its closing on a grid (HeightMapDilation), and the head limit
 * (HeadClearance). Footprint cells outside the grid or holding NaN never constrain. Every cell
 * depends on the input only, so the rows run on the worker pool (T-160) and the result is the same
 * with any number of workers. */

typedef struct map_job {
    const mn_grid* g;
    const float* in;
    const mn_offset* offsets;
    int count;
    float cutter_length;
    float* out;
} map_job;

static void tip_row(void* context, int j, void* scratch)
{
    (void)scratch;
    const map_job* job = (const map_job*)context;
    const mn_grid* g = job->g;
    for (int i = 0; i < g->width; i++) {
        float best = NAN;
        for (int o = 0; o < job->count; o++) {
            int ii = i + job->offsets[o].dx;
            int jj = j + job->offsets[o].dy;
            if (!mn_in_bounds(g, ii, jj)) {
                continue;
            }
            float z = job->in[jj * g->width + ii];
            if (mn_isnan(z)) {
                continue;
            }
            float candidate = z - job->offsets[o].dz;
            if (mn_isnan(best) || candidate > best) {
                best = candidate;
            }
        }
        job->out[j * g->width + i] = best;
    }
}

int mn_tip_map_compute(const mn_grid* g, const float* model, const mn_offset* offsets, int count, float* tip)
{
    map_job job = { g, model, offsets, count, 0.0f, tip };
    return mn_rows_parallel(g->height, tip_row, &job, NULL, mn_worker_count());
}

MN_API int32_t mn_tip_map(const mn_grid* grid, const float* model, const mn_offset* offsets, int32_t count, float* tip)
{
    return mn_tip_map_compute(grid, model, offsets, count, tip);
}

/* Material left once the tip has been everywhere the tip map allows: min over the footprint of
 * tip + dz. */
static void remaining_row(void* context, int j, void* scratch)
{
    (void)scratch;
    const map_job* job = (const map_job*)context;
    const mn_grid* g = job->g;
    for (int i = 0; i < g->width; i++) {
        float best = NAN;
        for (int o = 0; o < job->count; o++) {
            int ii = i + job->offsets[o].dx;
            int jj = j + job->offsets[o].dy;
            if (!mn_in_bounds(g, ii, jj)) {
                continue;
            }
            float t = job->in[jj * g->width + ii];
            if (mn_isnan(t)) {
                continue;
            }
            float candidate = t + job->offsets[o].dz;
            if (mn_isnan(best) || candidate < best) {
                best = candidate;
            }
        }
        job->out[j * g->width + i] = best;
    }
}

int mn_remaining_compute(const mn_grid* g, const float* tip, const mn_offset* offsets, int count, float* remaining)
{
    map_job job = { g, tip, offsets, count, 0.0f, remaining };
    return mn_rows_parallel(g->height, remaining_row, &job, NULL, mn_worker_count());
}

MN_API int32_t mn_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining)
{
    return mn_remaining_compute(grid, tip, offsets, count, remaining);
}

/* The head sits cutter_length above the tip and its underside rises dz above that over the ring
 * cell, so the tip cannot go below material - cutter_length - dz: the limit is the highest of
 * those over the ring, NaN where no ring cell holds material. For a flat underside (dz 0) this is
 * the highest material less the cutter length. */
static void head_limit_row(void* context, int j, void* scratch)
{
    (void)scratch;
    const map_job* job = (const map_job*)context;
    const mn_grid* g = job->g;
    for (int i = 0; i < g->width; i++) {
        float highest = NAN;
        for (int o = 0; o < job->count; o++) {
            int ii = i + job->offsets[o].dx;
            int jj = j + job->offsets[o].dy;
            if (!mn_in_bounds(g, ii, jj)) {
                continue;
            }
            float z = job->in[jj * g->width + ii];
            if (mn_isnan(z)) {
                continue;
            }
            float reach = z - job->offsets[o].dz;
            if (mn_isnan(highest) || reach > highest) {
                highest = reach;
            }
        }
        job->out[j * g->width + i] = mn_isnan(highest) ? NAN : highest - job->cutter_length;
    }
}

int mn_head_limit_compute(const mn_grid* g, const float* remaining, const mn_offset* annulus, int count, float cutter_length, float* limit)
{
    map_job job = { g, remaining, annulus, count, cutter_length, limit };
    return mn_rows_parallel(g->height, head_limit_row, &job, NULL, mn_worker_count());
}

MN_API int32_t mn_head_limit(const mn_grid* grid, const float* remaining, const mn_offset* annulus, int32_t count, float cutter_length, float* limit)
{
    return mn_head_limit_compute(grid, remaining, annulus, count, cutter_length, limit);
}

/* Effective tip = max(tip, limit); a NaN tip stays NaN, a NaN limit does not constrain. */
void mn_apply_limit(const float* tip, const float* limit, int cells, float* effective)
{
    for (int k = 0; k < cells; k++) {
        float t = tip[k];
        float l = limit[k];
        effective[k] = (!mn_isnan(t) && !mn_isnan(l) && l > t) ? l : t;
    }
}

MN_API void mn_apply_head_limit(const float* tip, const float* limit, int32_t cells, float* effective)
{
    mn_apply_limit(tip, limit, cells, effective);
}

MN_API void mn_head_limited_mask(const float* tip, const float* limit, int32_t cells, float tolerance, uint8_t* mask)
{
    for (int k = 0; k < cells; k++) {
        float t = tip[k];
        float l = limit[k];
        mask[k] = (uint8_t)(!mn_isnan(t) && !mn_isnan(l) && l > t + tolerance);
    }
}
