#include <stdio.h>

#include "mn_gpu.h"
#include "mn_thread.h"

/* Backward check of the Z layer (T-170, collision mode "Backwards"). It runs on a thread of its own
 * and sweeps the levels from the lowest up. A position of a level's mask is reached when the head at
 * that level clears the material standing there at that moment, and that material is what the reached
 * positions of the levels above leave: a cell drops to the deepest level any reached position covering
 * it reaches (the closing of those levels), never below the level above the current one before it is
 * cut, never above the stock. The test is the one of the far block (far_steps_of): material standing h
 * above the head bottom forbids the positions within the meet radius at h plus the head margin
 * (power-distance envelope). A position whose head would meet that material is unreached: only cutting
 * the model would free it. A lower level is swept before the levels above it are known, so the first
 * sweep counts every position of the plan's masks as reached; every sweep then starts from the deepest
 * level each position reached in the sweep before, until a sweep changes nothing (a position only ever
 * loses levels, so this ends; for a flat tip the result equals the cut from the top down). The rows of
 * every level run on the checker's share of the workers.
 * Once settled the check gives the Z layer its input (T-193): the reached plan, each level's mask without
 * the positions that do not reach it, and the strategy tip with those positions raised to the deepest
 * level they reach. Both are written in one pass over the masks, which also counts the unreached
 * positions; the Z layer then routes them by its own rules, as it routes a pass of the recursion. */

#define MN_BACKWARDS_ERROR 512

struct mn_backwards {
    const mn_context* context;
    const volatile int32_t* cancel;
    int workers;
    int gpu;
    mn_profile profile;
    int* plan_deepest; /* per position the deepest level index of the plan's masks, -1 for none */
    int* deepest;      /* per position the deepest level index it reaches, -1 for none */
    int* next;         /* the same as the current sweep finds it */
    float* closing;
    float* field;
    int* row_counts;
    mn_plan plan; /* the reached plan: the plan's levels, its own masks */
    float* tip;   /* the strategy tip with the positions that lose a level raised */
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

typedef struct row_job {
    mn_backwards* check;
    int level;
    float clamp;
    const uint8_t* mask;
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
        check->plan_deepest[c] = deepest;
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

/* The positions of the mask outside every disk: the deepest level each one reaches in this sweep. */
static void verdict_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    int width = check->context->grid.width;
    for (int i = 0; i < width; i++) {
        int c = j * width + i;
        if (job->mask[c] && (job->sources == 0 || check->field[c] > 0.0f) && check->next[c] < 0) {
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
static int clear_at(mn_backwards* check, int level, float clamp, const uint8_t* mask)
{
    const mn_grid* g = &check->context->grid;
    row_job job = { check, level, clamp, mask, 0 };
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
    row_job job = { check, 0, 0.0f, NULL, 0 };
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
        MN_CHECK(clear_at(check, k, level_above(ctx, k), ctx->plan->masks + (size_t)k * (size_t)cells));
    }
    return MN_OK;
}

/* One row of the Z layer's input (T-193): per level the positions of the mask reached that deep, the
 * unreached ones counted, then the strategy tip of every position that loses a level raised to the
 * deepest level it reaches (the stock top where it reaches none); a position that keeps every level of
 * the plan keeps its tip bit for bit. The levels are read row by row, so every pass over a mask reads
 * consecutive bytes. */
static void input_row(void* context, int j, void* scratch)
{
    (void)scratch;
    row_job* job = (row_job*)context;
    mn_backwards* check = job->check;
    const mn_context* ctx = check->context;
    const mn_plan* plan = ctx->plan;
    int width = ctx->grid.width;
    int cells = mn_cells(&ctx->grid);
    size_t row = (size_t)j * (size_t)width;
    int unreached = 0;
    for (int k = 0; k < plan->count; k++) {
        const uint8_t* mask = plan->masks + (size_t)k * (size_t)cells + row;
        uint8_t* reached = check->plan.masks + (size_t)k * (size_t)cells + row;
        const int* deepest = check->deepest + row;
        for (int i = 0; i < width; i++) {
            uint8_t r = (uint8_t)(mask[i] && deepest[i] >= k);
            reached[i] = r;
            unreached += mask[i] && !r ? 1 : 0;
        }
    }
    for (int i = 0; i < width; i++) {
        size_t c = row + (size_t)i;
        int d = check->deepest[c];
        float tip = ctx->effective_tip[c];
        check->tip[c] = check->plan_deepest[c] > d ? mn_max(tip, d >= 0 ? plan->levels[d] : ctx->stock_top) : tip;
    }
    check->row_counts[j] = unreached;
}

static int check_all(mn_backwards* check)
{
    const mn_context* ctx = check->context;
    const mn_grid* g = &ctx->grid;
    int cells = mn_cells(g);
    row_job job = { check, 0, 0.0f, NULL, 0 };
    MN_CHECK(mn_rows_parallel(g->height, start_row, &job, NULL, check->workers));
    check->model_cells = row_sum(check);
    if (check->gpu) {
        MN_CHECK(mn_gpu_backward_settle(ctx->gpu, ctx, check->deepest, &check->sweeps));
    } else {
        memcpy(check->next, check->deepest, (size_t)cells * sizeof(int));
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
    if (stopped(check)) {
        return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
    }
    double started = mn_seconds();
    MN_CHECK(mn_rows_parallel(g->height, input_row, &job, NULL, check->workers));
    check->unreached = row_sum(check);
    mn_time_add(MN_TIME_BACK_INPUT, mn_seconds() - started);
    mn_signal_raise(check->signal, 0, 1);
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
    mn_profile_free(&check->profile);
    free(check->plan_deepest);
    free(check->deepest);
    free(check->next);
    free(check->closing);
    free(check->field);
    free(check->row_counts);
    free(check->plan.masks);
    free(check->tip);
    mn_signal_destroy(check->signal);
    free(check);
}

int mn_backwards_start(const mn_context* context, int workers, const volatile int32_t* cancel, mn_backwards** result)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    int cells = mn_cells(g);
    mn_backwards* check = (mn_backwards*)mn_alloc(1, sizeof(mn_backwards));
    if (check == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the backward check.");
    }
    check->context = context;
    check->cancel = cancel;
    check->workers = workers;
    check->gpu = context->gpu != NULL;
    check->plan = *plan;
    check->plan.masks = (uint8_t*)mn_alloc((size_t)(plan->count > 0 ? plan->count : 1) * (size_t)cells, 1);
    check->plan_deepest = (int*)mn_alloc((size_t)cells, sizeof(int));
    check->deepest = (int*)mn_alloc((size_t)cells, sizeof(int));
    check->tip = (float*)mn_alloc((size_t)cells, sizeof(float));
    check->row_counts = (int*)mn_alloc((size_t)g->height, sizeof(int));
    int status = check->plan.masks == NULL || check->plan_deepest == NULL || check->deepest == NULL || check->tip == NULL || check->row_counts == NULL
        ? mn_fail(MN_ERR_MEMORY, "Out of memory for the backward check of %d levels.", plan->count)
        : MN_OK;
    /* The sweeps' own maps live on the GPU in Backwards_GPU. */
    if (status == MN_OK && !check->gpu) {
        check->next = (int*)mn_alloc((size_t)cells, sizeof(int));
        check->closing = (float*)mn_alloc((size_t)cells, sizeof(float));
        check->field = (float*)mn_alloc((size_t)cells, sizeof(float));
        status = check->next == NULL || check->closing == NULL || check->field == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the backward check.")
                                                                                       : mn_profile_build(&context->tool, g->cell_size, &check->profile);
    }
    if (status == MN_OK) {
        status = mn_signal_create(&check->signal);
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

int mn_backwards_settled(mn_backwards* check, const mn_plan** reached, const float** tip)
{
    int produced = mn_signal_wait(check->signal, 0, 1);
    if (produced == MN_SIGNAL_DONE && check->status != MN_OK) {
        return mn_fail(check->status, "%s", check->error);
    }
    *reached = &check->plan;
    *tip = check->tip;
    return MN_OK;
}

int mn_backwards_finish(mn_backwards* check, int abort, int64_t* unreached, int64_t* model_cells, int* sweeps)
{
    if (abort) {
        check->abort = 1;
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
