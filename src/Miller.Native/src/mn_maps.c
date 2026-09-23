#include "mn_internal.h"

/* The drop cutter and its closing on a grid (HeightMapDilation), and the head limit
 * (HeadClearance). Footprint cells outside the grid or holding NaN never constrain. */

void mn_tip_map_compute(const mn_grid* g, const float* model, const mn_offset* offsets, int count, float* tip)
{
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            float best = NAN;
            for (int o = 0; o < count; o++) {
                int ii = i + offsets[o].dx;
                int jj = j + offsets[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                float z = model[jj * g->width + ii];
                if (mn_isnan(z)) {
                    continue;
                }
                float candidate = z - offsets[o].dz;
                if (mn_isnan(best) || candidate > best) {
                    best = candidate;
                }
            }
            tip[j * g->width + i] = best;
        }
    }
}

MN_API void mn_tip_map(const mn_grid* grid, const float* model, const mn_offset* offsets, int32_t count, float* tip)
{
    mn_tip_map_compute(grid, model, offsets, count, tip);
}

/* Material left once the tip has been everywhere the tip map allows: min over the footprint of
 * tip + dz. */
void mn_remaining_compute(const mn_grid* g, const float* tip, const mn_offset* offsets, int count, float* remaining)
{
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            float best = NAN;
            for (int o = 0; o < count; o++) {
                int ii = i + offsets[o].dx;
                int jj = j + offsets[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                float t = tip[jj * g->width + ii];
                if (mn_isnan(t)) {
                    continue;
                }
                float candidate = t + offsets[o].dz;
                if (mn_isnan(best) || candidate < best) {
                    best = candidate;
                }
            }
            remaining[j * g->width + i] = best;
        }
    }
}

MN_API void mn_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining)
{
    mn_remaining_compute(grid, tip, offsets, count, remaining);
}

/* The head sits cutter_length above the tip and its underside rises dz above that over the ring
 * cell, so the tip cannot go below material - cutter_length - dz: the limit is the highest of
 * those over the ring, NaN where no ring cell holds material. For a flat underside (dz 0) this is
 * the highest material less the cutter length. */
void mn_head_limit_compute(const mn_grid* g, const float* remaining, const mn_offset* annulus, int count, float cutter_length, float* limit)
{
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            float highest = NAN;
            for (int o = 0; o < count; o++) {
                int ii = i + annulus[o].dx;
                int jj = j + annulus[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                float z = remaining[jj * g->width + ii];
                if (mn_isnan(z)) {
                    continue;
                }
                float reach = z - annulus[o].dz;
                if (mn_isnan(highest) || reach > highest) {
                    highest = reach;
                }
            }
            limit[j * g->width + i] = mn_isnan(highest) ? NAN : highest - cutter_length;
        }
    }
}

MN_API void mn_head_limit(const mn_grid* grid, const float* remaining, const mn_offset* annulus, int32_t count, float cutter_length, float* limit)
{
    mn_head_limit_compute(grid, remaining, annulus, count, cutter_length, limit);
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
