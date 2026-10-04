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

/* Collision feedback (T-136). Every cell is shouldn't be cut (the model), might be cut (stock above
 * the model) or should be cut. Where the head keeps the tip above its reach floor, a ring cell that
 * blocks it with stock above the model, and that the cutter can take lower than the level the stock
 * stands at (`cut_to`, the height a should-cut cell counts with, below its rounded-up value), becomes
 * should be cut, provided that marking the blocking cells of the position lowers its limit at all.
 * Marks only grow. Returns how many cells were marked. */
int mn_mark_should_cut(const mn_grid* g, const float* tip, const float* model, const float* cut_to, const float* remaining, const float* limit, const mn_offset* annulus, int count,
    float cutter_length, float tolerance, uint8_t* should_cut)
{
    int marked = 0;
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            int p = j * g->width + i;
            float t = tip[p];
            float l = limit[p];
            if (mn_isnan(t) || mn_isnan(l) || !(l > t + tolerance)) {
                continue;
            }
            float lowered = NAN;
            int candidates = 0;
            for (int o = 0; o < count; o++) {
                int ii = i + annulus[o].dx;
                int jj = j + annulus[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                int c = jj * g->width + ii;
                float z = remaining[c];
                if (mn_isnan(z)) {
                    continue;
                }
                int candidate = !should_cut[c] && z - annulus[o].dz - cutter_length > t + tolerance && z > model[c] + tolerance && cut_to[c] < z - tolerance;
                float stays = candidate ? cut_to[c] : z;
                float reach = stays - annulus[o].dz;
                candidates += candidate;
                if (mn_isnan(lowered) || reach > lowered) {
                    lowered = reach;
                }
            }
            if (candidates == 0 || !(lowered - cutter_length < l - tolerance)) {
                continue;
            }
            for (int o = 0; o < count; o++) {
                int ii = i + annulus[o].dx;
                int jj = j + annulus[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                int c = jj * g->width + ii;
                float z = remaining[c];
                if (!mn_isnan(z) && !should_cut[c] && z - annulus[o].dz - cutter_length > t + tolerance && z > model[c] + tolerance && cut_to[c] < z - tolerance) {
                    should_cut[c] = 1;
                    marked++;
                }
            }
        }
    }
    return marked;
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
