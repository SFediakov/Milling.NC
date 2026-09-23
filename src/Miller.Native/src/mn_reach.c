#include "mn_internal.h"
#include "mn_thread.h"

/* Where the tool axis may go, decided in rounds by a vote over its footprint (ReachMap, guide 6.3).
 * At a level z a footprint cell that holds stock is unintended when the model stands above the tool
 * bottom there and intended otherwise; a position is reachable at z when the intended cells that
 * still carry material are at least `percent` of those cells plus the unintended ones. Three rounds
 * run at 100, percent + (100 - percent) / 2 and percent; after a round every footprint cell an
 * accepted position brings to the model top within the tolerance is marked reached and stops counting
 * as intended later. The map keeps the lowest floor over the rounds, never below the stock floor.
 * Rows of a block run in parallel; a row writes its own cells and only stores true into the marks of
 * the running round, so the split does not change the result. */

#define MN_ROUNDS 3
#define MN_ROWS_PER_BLOCK 16
#define MN_MAX_PERCENT 100.0f
#define MN_RANK_SLACK 1e-9

MN_API double mn_reach_round_percent(float percent, int32_t round)
{
    switch (round) {
    case 0:
        return (double)MN_MAX_PERCENT;
    case 1:
        return (double)percent + ((double)MN_MAX_PERCENT - (double)percent) / 2.0;
    default:
        return (double)percent;
    }
}

static int needed(int n, double percent) { return mn_d2i(ceil((double)n * percent / 100.0 - MN_RANK_SLACK)); }

MN_API int32_t mn_reach_rank(int32_t n, float percent) { return mn_clampi(needed(n, (double)percent) - 1, 0, n - 1); }

/* The value sorting ascending would put at `rank` (quickselect; the values are reordered). */
MN_API float mn_reach_select(float* values, int32_t n, int32_t rank)
{
    int lo = 0;
    int hi = n - 1;
    while (lo < hi) {
        float pivot = values[(lo + hi) / 2];
        int l = lo;
        int h = hi;
        while (l <= h) {
            while (values[l] < pivot) {
                l++;
            }
            while (values[h] > pivot) {
                h--;
            }
            if (l <= h) {
                float swap = values[l];
                values[l] = values[h];
                values[h] = swap;
                l++;
                h--;
            }
        }
        if (rank <= h) {
            hi = h;
        } else if (rank >= l) {
            lo = l;
        } else {
            break;
        }
    }
    return values[rank];
}

static int passes(int rank, int excluded_up_to, int n, double percent) { return rank + 1 - excluded_up_to >= needed(n - excluded_up_to, percent); }

/* The lowest value at which the vote passes: the values at or below it that are not excluded are at
 * least `percent` of them plus every value above it. Values and flags are reordered together. */
MN_API float mn_reach_select_threshold(float* values, uint8_t* excluded, int32_t n, double percent)
{
    int lo = 0;
    int hi = n - 1;
    int excluded_below = 0;
    while (lo < hi) {
        float pivot = values[(lo + hi) / 2];
        int l = lo;
        int h = hi;
        int e = excluded_below;
        while (l <= h) {
            while (values[l] < pivot) {
                e += excluded[l] ? 1 : 0;
                l++;
            }
            while (values[h] > pivot) {
                h--;
            }
            if (l <= h) {
                float swap = values[l];
                values[l] = values[h];
                values[h] = swap;
                uint8_t flag = excluded[l];
                excluded[l] = excluded[h];
                excluded[h] = flag;
                e += excluded[l] ? 1 : 0;
                l++;
                h--;
            }
        }

        int middle = l == h + 2;
        int e_left = middle ? e - (excluded[h + 1] ? 1 : 0) : e;
        if (h >= lo && passes(h, e_left, n, percent)) {
            hi = h;
            continue;
        }

        excluded_below = e_left;
        if (middle) {
            if (passes(h + 1, e, n, percent)) {
                return values[h + 1];
            }
            excluded_below = e;
        }
        lo = l;
    }
    return values[lo];
}

typedef struct reach_scratch {
    float* tops;
    int* cells;
    uint8_t* flags;
    float* work;
} reach_scratch;

typedef struct reach_job {
    const mn_grid* g;
    const mn_offset* offsets;
    int count;
    const int* span;
    const float* dz;
    int margin;
    const float* value;
    const uint8_t* reached;
    uint8_t* reached_next;
    float* reach;
    float floor;
    float tolerance;
    double percent;
    int last_round;
} reach_job;

static float threshold(float* values, uint8_t* flags, int n, int excluded, double percent, float* work)
{
    if (percent >= (double)MN_MAX_PERCENT) {
        float max = values[0];
        for (int m = 1; m < n; m++) {
            max = mn_max(max, values[m]);
        }
        return max;
    }
    memcpy(work, values, (size_t)n * sizeof(float));
    return excluded == 0 ? mn_reach_select(work, n, mn_clampi(needed(n, percent) - 1, 0, n - 1)) : mn_reach_select_threshold(work, flags, n, percent);
}

static void reach_row(void* context, int j, void* scratch_pointer)
{
    const reach_job* job = (const reach_job*)context;
    reach_scratch* scratch = (reach_scratch*)scratch_pointer;
    const mn_grid* g = job->g;
    int width = g->width;
    int height = g->height;
    int interior_row = j >= job->margin && j < height - job->margin;
    for (int i = 0; i < width; i++) {
        int n = 0;
        int excluded = 0;
        int center = j * width + i;
        if (interior_row && i >= job->margin && i < width - job->margin) {
            for (int o = 0; o < job->count; o++) {
                int k = center + job->span[o];
                float top = job->value[k];
                if (mn_isnan(top)) {
                    continue;
                }
                uint8_t done = job->reached[k];
                scratch->tops[n] = top - job->dz[o];
                scratch->cells[n] = k;
                scratch->flags[n] = done;
                excluded += done ? 1 : 0;
                n++;
            }
        } else {
            for (int o = 0; o < job->count; o++) {
                int ii = i + job->offsets[o].dx;
                int jj = j + job->offsets[o].dy;
                if (!mn_in_bounds(g, ii, jj)) {
                    continue;
                }
                int k = jj * width + ii;
                float top = job->value[k];
                if (mn_isnan(top)) {
                    continue;
                }
                uint8_t done = job->reached[k];
                scratch->tops[n] = top - job->offsets[o].dz;
                scratch->cells[n] = k;
                scratch->flags[n] = done;
                excluded += done ? 1 : 0;
                n++;
            }
        }

        if (n == 0 || excluded == n) {
            continue;
        }

        float z = mn_max(threshold(scratch->tops, scratch->flags, n, excluded, job->percent, scratch->work), job->floor);
        float previous = job->reach[center];
        if (!(mn_isnan(previous) || z < previous)) {
            continue;
        }

        job->reach[center] = z;
        if (job->last_round) {
            continue;
        }

        float touched = z - job->tolerance;
        for (int m = 0; m < n; m++) {
            if (scratch->tops[m] >= touched) {
                job->reached_next[scratch->cells[m]] = 1;
            }
        }
    }
}

int mn_reach_compute(const mn_grid* g, const float* model, const float* stock, const mn_offset* offsets, int count, float floor, float percent, float tolerance, const mn_monitor* monitor, float* reach)
{
    if (!(percent > 0.0f && percent <= MN_MAX_PERCENT)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Reach percent must lie in (0, 100].");
    }
    if (!(tolerance >= 0.0f)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Tolerance must be zero or positive.");
    }

    int cells = mn_cells(g);
    int width = g->width;
    int height = g->height;
    int workers = mn_worker_count();
    int status = MN_OK;
    float* value = (float*)mn_alloc((size_t)cells, sizeof(float));
    uint8_t* reached = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* reached_next = (uint8_t*)mn_alloc((size_t)cells, 1);
    int* span = (int*)mn_alloc((size_t)count, sizeof(int));
    float* dz = (float*)mn_alloc((size_t)count, sizeof(float));
    reach_scratch* scratch = (reach_scratch*)mn_alloc((size_t)workers, sizeof(reach_scratch));
    void* scratch_pointers[MN_MAX_WORKERS];
    mn_pool* pool = NULL;
    if (value == NULL || reached == NULL || reached_next == NULL || span == NULL || dz == NULL || scratch == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the reach map.");
        goto done;
    }
    for (int w = 0; w < workers; w++) {
        scratch[w].tops = (float*)mn_alloc((size_t)count, sizeof(float));
        scratch[w].cells = (int*)mn_alloc((size_t)count, sizeof(int));
        scratch[w].flags = (uint8_t*)mn_alloc((size_t)count, 1);
        scratch[w].work = (float*)mn_alloc((size_t)count, sizeof(float));
        scratch_pointers[w] = &scratch[w];
        if (scratch[w].tops == NULL || scratch[w].cells == NULL || scratch[w].flags == NULL || scratch[w].work == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for the reach map.");
            goto done;
        }
    }

    status = mn_pool_create(workers, scratch_pointers, &pool);
    if (status != MN_OK) {
        goto done;
    }

    for (int k = 0; k < cells; k++) {
        reach[k] = NAN;
        float top = model[k];
        value[k] = mn_isnan(stock[k]) ? NAN : mn_isnan(top) ? floor : top;
    }

    int margin = 0;
    for (int o = 0; o < count; o++) {
        span[o] = offsets[o].dy * width + offsets[o].dx;
        dz[o] = offsets[o].dz;
        margin = mn_maxi(margin, mn_maxi(abs(offsets[o].dx), abs(offsets[o].dy)));
    }

    reach_job job;
    job.g = g;
    job.offsets = offsets;
    job.count = count;
    job.span = span;
    job.dz = dz;
    job.margin = margin;
    job.value = value;
    job.reached = reached;
    job.reached_next = reached_next;
    job.reach = reach;
    job.floor = floor;
    job.tolerance = tolerance;

    for (int round = 0; round < MN_ROUNDS; round++) {
        double round_percent = mn_reach_round_percent(percent, round);
        int last_round = round == MN_ROUNDS - 1;
        if (round > 0 && round_percent == mn_reach_round_percent(percent, round - 1)) {
            mn_report(monitor, round + 1, MN_ROUNDS, ((float)round + 1.0f) / (float)MN_ROUNDS);
            continue;
        }

        job.percent = round_percent;
        job.last_round = last_round;
        for (int first = 0; first < height; first += MN_ROWS_PER_BLOCK) {
            if (mn_cancelled(monitor)) {
                status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
                goto done;
            }
            int last = mn_mini(first + MN_ROWS_PER_BLOCK, height);
            mn_pool_run(pool, first, last, reach_row, &job);
            mn_report(monitor, round + 1, MN_ROUNDS, ((float)round + (float)last / (float)height) / (float)MN_ROUNDS);
        }

        if (!last_round) {
            memcpy(reached, reached_next, (size_t)cells);
        }
    }

done:
    mn_pool_destroy(pool);
    if (scratch != NULL) {
        for (int w = 0; w < workers; w++) {
            free(scratch[w].tops);
            free(scratch[w].cells);
            free(scratch[w].flags);
            free(scratch[w].work);
        }
    }
    free(scratch);
    free(value);
    free(reached);
    free(reached_next);
    free(span);
    free(dz);
    return status;
}

MN_API int32_t mn_reach_map(const mn_grid* grid, const float* model, const float* stock, const mn_offset* offsets, int32_t count, float floor, float percent, float tolerance, mn_progress_fn progress, void* context, float* reach)
{
    mn_monitor monitor;
    monitor.progress = progress;
    monitor.context = context;
    monitor.cancel = NULL;
    return mn_reach_compute(grid, model, stock, offsets, count, floor, percent, tolerance, &monitor, reach);
}
