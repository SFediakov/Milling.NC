#include <stdio.h>

#include "mn_gpu.h"
#include "mn_thread.h"

/* Backward check of the Z layer (T-170, collision mode "Backwards"). It runs on a thread of its own
 * beside the route solver and sweeps the levels from the lowest up. A position of a level's mask is
 * reached when the head at that level clears the material standing there at that moment, and that
 * material is what the reached positions of the levels above leave: a cell drops to the deepest level
 * any reached position covering it reaches (the closing of those levels), never below the level above
 * the current one before it is cut, never above the stock. The test is the one of the far block
 * (far_steps_of): material standing h above the head bottom forbids the positions within the meet
 * radius at h plus the head margin (power-distance envelope). A position whose head would meet that
 * material is unreached: only cutting the model would free it. A lower level is swept before the levels
 * above it are known, so the first sweep counts every position of the plan's masks as reached; every
 * sweep then starts from the deepest level each position reached in the sweep before, until a sweep
 * changes nothing (a position only ever loses levels, so this ends; for a flat tip the result equals
 * the cut from the top down). Then the batches of `group_size` levels (one far stepdown slab) go to the
 * solver from the lowest, each with the positions reached on every one of its levels as its far go.
 * The rows of every level run on the checker's share of the workers. */

/* The checker runs at most this many batches ahead of the solver. */
#define MN_BACKWARDS_AHEAD 4

#define MN_BACKWARDS_ERROR 512

struct mn_backwards {
    const mn_context* context;
    const volatile int32_t* cancel;
    int group_size;
    int batch_count;
    int workers;
    int gpu;
    mn_backwards_batch* batches; /* from the top: batch b holds the levels from b * group_size */
    mn_profile profile;
    int* deepest;      /* per position the deepest level index it reaches, -1 for none */
    int* next;         /* the same as the current sweep finds it */
    float* closing;
    float* field;
    uint8_t* reached;
    int* row_counts;
    int sweeps;
    int64_t unreached;
    int64_t model_cells;
    volatile int abort;
    int status;
    double times[MN_TIME_COUNT];
    char error[MN_BACKWARDS_ERROR];
    mn_signal* signal;
    mn_task* task;
};

int mn_backwards_batch_count(const mn_backwards* check) { return check->batch_count; }

const int* mn_backwards_deepest(const mn_backwards* check) { return check->deepest; }

typedef struct row_job {
    mn_backwards* check;
    int level;
    float clamp;
    const uint8_t* mask;
    uint8_t* reached;
    int sources;
} row_job;

static int stopped(const mn_backwards* check) { return check->abort || (check->cancel != NULL && *check->cancel != 0); }

/* The deepest level of every position in the plan's masks, and the model cells of every level. */
static void start_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    const mn_context* ctx = check->context;
    const mn_plan* plan = ctx->plan;
    int width = ctx->grid.width;
    int cells = mn_cells(&ctx->grid);
    int counted = 0;
    for (int i = 0; i < width; i++) {
        int c = j * width + i;
        int deepest = -1;
        for (int k = plan->count - 1; k >= 0 && deepest < 0; k--) {
            if (plan->masks[(size_t)k * (size_t)cells + (size_t)c]) {
                deepest = k;
            }
        }
        check->deepest[c] = deepest;
        float m = ctx->model[c];
        if (!(m > ctx->floor + MN_FLOOR_TOLERANCE)) {
            continue;
        }
        for (int k = 0; k < plan->count; k++) {
            counted += m > plan->levels[k] + MN_LEVEL_TOLERANCE ? 1 : 0;
        }
    }
    check->row_counts[j] = counted;
}

/* The deepest level of every position as a height (NaN for none), for the closing. */
static void height_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    const mn_context* ctx = check->context;
    int width = ctx->grid.width;
    int changed = 0;
    for (int i = 0; i < width; i++) {
        int c = j * width + i;
        changed += check->next[c] != check->deepest[c] ? 1 : 0;
        check->deepest[c] = check->next[c];
        check->field[c] = check->deepest[c] >= 0 ? ctx->plan->levels[check->deepest[c]] : NAN;
        check->next[c] = -1;
    }
    check->row_counts[j] = changed;
}

/* The disks of the material standing above the head bottom at the job's level (field), counted per row. */
static void source_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    const mn_context* ctx = check->context;
    const mn_grid* g = &ctx->grid;
    float head_bottom = ctx->plan->levels[job->level] + ctx->tool.cutter_length;
    float margin = mn_head_margin(g->cell_size, ctx->parameters.tolerance);
    int sources = 0;
    for (int i = 0; i < g->width; i++) {
        int c = j * g->width + i;
        float stock = ctx->stock[c];
        float a = check->closing[c];
        check->field[c] = INFINITY;
        if (mn_isnan(stock)) {
            continue;
        }
        float top = mn_isnan(a) ? stock : mn_min(stock, mn_max(a, job->clamp));
        float h = top - head_bottom;
        if (!(h > MN_LEVEL_TOLERANCE)) {
            continue;
        }
        float radius = (mn_meet_radius_at(&ctx->tool, h) + margin) / g->cell_size;
        check->field[c] = -radius * radius;
        sources++;
    }
    check->row_counts[j] = sources;
}

/* The positions of the mask outside every disk; with a sweep, the deepest level each one reaches. */
static void verdict_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    int width = check->context->grid.width;
    for (int i = 0; i < width; i++) {
        int c = j * width + i;
        uint8_t clear = (uint8_t)(job->mask[c] && (job->sources == 0 || check->field[c] > 0.0f));
        job->reached[c] = clear;
        if (clear && job->reached == check->reached && check->next[c] < 0) {
            check->next[c] = job->level;
        }
    }
}

static int64_t row_sum(const mn_backwards* check)
{
    int64_t sum = 0;
    for (int j = 0; j < check->context->grid.height; j++) {
        sum += check->row_counts[j];
    }
    return sum;
}

/* The positions of `mask` the head clears at plan level `level` with nothing cut below `clamp` yet. */
static int clear_at(mn_backwards* check, int level, float clamp, const uint8_t* mask, uint8_t* reached)
{
    const mn_grid* g = &check->context->grid;
    row_job job = { check, level, clamp, mask, reached, 0 };
    MN_CHECK(mn_rows_parallel(g->height, source_row, &job, NULL, check->workers));
    job.sources = row_sum(check) > 0;
    if (job.sources) {
        MN_CHECK(mn_lower_envelope(check->field, g->width, g->height));
    }
    return mn_rows_parallel(g->height, verdict_row, &job, NULL, check->workers);
}

static float level_above(const mn_context* context, int k) { return k > 0 ? context->plan->levels[k - 1] : context->stock_top; }

/* The closing of the deepest levels the sweep before left (the first: those of the masks); returns how
 * many positions changed their deepest level, or a negative status. */
static int64_t settle(mn_backwards* check)
{
    const mn_grid* g = &check->context->grid;
    row_job job = { check, 0, 0.0f, NULL, NULL, 0 };
    MN_CHECK(mn_rows_parallel(g->height, height_row, &job, NULL, check->workers));
    int64_t changed = row_sum(check);
    MN_CHECK(mn_remaining_compute(g, check->field, check->profile.offsets, check->profile.offset_count, check->closing));
    return changed;
}

/* One sweep from the lowest level up: the deepest level every position reaches under the closing. */
static int sweep(mn_backwards* check)
{
    const mn_context* ctx = check->context;
    int cells = mn_cells(&ctx->grid);
    for (int k = ctx->plan->count - 1; k >= 0; k--) {
        if (stopped(check)) {
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        }
        MN_CHECK(clear_at(check, k, level_above(ctx, k), ctx->plan->masks + (size_t)k * (size_t)cells, check->reached));
    }
    return MN_OK;
}

/* One batch from the converged levels: per level the positions of its mask reached that deep, then the
 * far go (positions reached on every level, never a withdrawn one, T-164); the far go cuts from the
 * batch top in one step, so where that is deeper than the head allows it is tested against the
 * material at the batch top. */
static int fill_batch(mn_backwards* check, mn_backwards_batch* batch)
{
    const mn_context* ctx = check->context;
    int cells = mn_cells(&ctx->grid);
    int levels = batch->last - batch->first + 1;
    batch->reached = (uint8_t*)mn_alloc((size_t)levels * (size_t)cells, 1);
    if (batch->reached == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a batch of %d levels.", levels);
    }
    for (int k = batch->first; k <= batch->last; k++) {
        const uint8_t* mask = ctx->plan->masks + (size_t)k * (size_t)cells;
        uint8_t* reached = batch->reached + (size_t)(k - batch->first) * (size_t)cells;
        for (int c = 0; c < cells; c++) {
            reached[c] = (uint8_t)(mask[c] && check->deepest[c] >= k);
            check->unreached += mask[c] && !reached[c] ? 1 : 0;
        }
    }
    if (levels < 2 || !(ctx->parameters.far_stepdown > 0)) {
        return MN_OK;
    }
    batch->far = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (batch->far == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a far go.");
    }
    for (int c = 0; c < cells; c++) {
        uint8_t all = (uint8_t)(ctx->far_allowed == NULL || ctx->far_allowed[c]);
        for (int n = 0; n < levels && all; n++) {
            all = batch->reached[(size_t)n * (size_t)cells + (size_t)c];
        }
        batch->far[c] = all;
    }
    float top = level_above(ctx, batch->first);
    if (top - (ctx->plan->levels[batch->last] + ctx->tool.cutter_length) > MN_LEVEL_TOLERANCE) {
        MN_CHECK(clear_at(check, batch->last, top, batch->far, batch->far));
    }
    return MN_OK;
}

static void free_batch(mn_backwards_batch* batch)
{
    free(batch->reached);
    free(batch->far);
    batch->reached = NULL;
    batch->far = NULL;
}

static int check_all(mn_backwards* check)
{
    const mn_context* ctx = check->context;
    const mn_grid* g = &ctx->grid;
    int cells = mn_cells(g);
    row_job job = { check, 0, 0.0f, NULL, NULL, 0 };
    MN_CHECK(mn_rows_parallel(g->height, start_row, &job, NULL, check->workers));
    check->model_cells = row_sum(check);
    memcpy(check->next, check->deepest, (size_t)cells * sizeof(int));
    if (check->gpu) {
        MN_CHECK(mn_gpu_backward_settle(ctx, &check->profile, check->deepest, &check->sweeps));
    }
    for (int round = 0; !check->gpu && round <= ctx->plan->count; round++) {
        double started = mn_seconds();
        int64_t changed = settle(check);
        mn_time_add(MN_TIME_BACK_SETTLE, mn_seconds() - started);
        if (changed < 0) {
            return (int)changed;
        }
        if (round > 0 && changed == 0) {
            break;
        }
        started = mn_seconds();
        MN_CHECK(sweep(check));
        mn_time_add(MN_TIME_BACK_LEVELS, mn_seconds() - started);
        check->sweeps++;
    }
    for (int n = 0; n < check->batch_count; n++) {
        if (n >= MN_BACKWARDS_AHEAD) {
            int consumed = mn_signal_wait(check->signal, 1, n - MN_BACKWARDS_AHEAD + 1);
            if (consumed == MN_SIGNAL_DONE) {
                return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            }
        }
        if (stopped(check)) {
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        }
        double started = mn_seconds();
        MN_CHECK(fill_batch(check, &check->batches[check->batch_count - 1 - n]));
        mn_time_add(MN_TIME_BACK_FILL, mn_seconds() - started);
        mn_signal_raise(check->signal, 0, n + 1);
    }
    return MN_OK;
}

static void check_main(void* argument)
{
    mn_backwards* check = (mn_backwards*)argument;
    mn_set_thread_cap(check->workers);
    check->status = check_all(check);
    mn_time_take(check->times);
    if (check->status != MN_OK) {
        snprintf(check->error, MN_BACKWARDS_ERROR, "%s", mn_last_error());
        mn_signal_raise(check->signal, 0, MN_SIGNAL_DONE);
    }
}

static void release_all(mn_backwards* check)
{
    for (int b = 0; b < check->batch_count && check->batches != NULL; b++) {
        free_batch(&check->batches[b]);
    }
    free(check->batches);
    mn_profile_free(&check->profile);
    free(check->deepest);
    free(check->next);
    free(check->closing);
    free(check->field);
    free(check->reached);
    free(check->row_counts);
    mn_signal_destroy(check->signal);
    free(check);
}

int mn_backwards_start(const mn_context* context, int group_size, int workers, int gpu, const volatile int32_t* cancel, mn_backwards** result)
{
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    int count = context->plan->count;
    mn_backwards* check = (mn_backwards*)mn_alloc(1, sizeof(mn_backwards));
    if (check == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the backward check.");
    }
    check->context = context;
    check->cancel = cancel;
    check->group_size = group_size;
    check->batch_count = count > 0 ? (count + group_size - 1) / group_size : 0;
    check->workers = workers;
    check->gpu = gpu;
    check->batches = (mn_backwards_batch*)mn_alloc((size_t)(check->batch_count > 0 ? check->batch_count : 1), sizeof(mn_backwards_batch));
    check->deepest = (int*)mn_alloc((size_t)cells, sizeof(int));
    check->next = (int*)mn_alloc((size_t)cells, sizeof(int));
    check->closing = (float*)mn_alloc((size_t)cells, sizeof(float));
    check->field = (float*)mn_alloc((size_t)cells, sizeof(float));
    check->reached = (uint8_t*)mn_alloc((size_t)cells, 1);
    check->row_counts = (int*)mn_alloc((size_t)g->height, sizeof(int));
    int status = check->batches == NULL || check->deepest == NULL || check->next == NULL || check->closing == NULL || check->field == NULL || check->reached == NULL
            || check->row_counts == NULL
        ? mn_fail(MN_ERR_MEMORY, "Out of memory for the backward check.")
        : MN_OK;
    if (status == MN_OK) {
        status = mn_profile_build(&context->tool, g->cell_size, &check->profile);
    }
    if (status == MN_OK) {
        status = mn_signal_create(&check->signal);
    }
    for (int b = 0; b < check->batch_count && status == MN_OK; b++) {
        check->batches[b].first = b * group_size;
        check->batches[b].last = mn_mini(b * group_size + group_size, count) - 1;
    }
    if (status == MN_OK) {
        status = mn_task_start(check_main, check, &check->task);
    }
    if (status != MN_OK) {
        release_all(check);
        return status;
    }
    *result = check;
    return MN_OK;
}

int mn_backwards_take(mn_backwards* check, int taken, const mn_backwards_batch** batch)
{
    int produced = mn_signal_wait(check->signal, 0, taken + 1);
    if (produced == MN_SIGNAL_DONE && check->status != MN_OK) {
        return mn_fail(check->status, "%s", check->error);
    }
    *batch = &check->batches[check->batch_count - 1 - taken];
    return MN_OK;
}

void mn_backwards_release(mn_backwards* check, int taken)
{
    free_batch(&check->batches[check->batch_count - 1 - taken]);
    mn_signal_raise(check->signal, 1, taken + 1);
}

int mn_backwards_finish(mn_backwards* check, int abort, int64_t* unreached, int64_t* model_cells, int* sweeps)
{
    if (abort) {
        check->abort = 1;
        mn_signal_raise(check->signal, 1, MN_SIGNAL_DONE);
    }
    mn_task_join(check->task);
    for (int k = 0; k < MN_TIME_COUNT; k++) {
        mn_time_add(k, check->times[k]);
    }
    int status = check->status;
    if (!abort && status != MN_OK) {
        status = mn_fail(status, "%s", check->error);
    }
    if (unreached != NULL) {
        *unreached = check->unreached;
    }
    if (model_cells != NULL) {
        *model_cells = check->model_cells;
    }
    if (sweeps != NULL) {
        *sweeps = check->sweeps;
    }
    release_all(check);
    return abort ? MN_OK : status;
}
