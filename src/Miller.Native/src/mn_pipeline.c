#include "mn_internal.h"

/* The whole generation after the project is validated (PipelineService): transform, stock, model
 * map, reach map, head clearance, slicing and cut scope, routing, simplification and statistics.
 * Progress goes to the caller per stage; cancellation is honoured between the stages and inside the
 * long ones. */

#define MN_MAX_HEAD_ITERATIONS 8

struct mn_result {
    mn_grid grid;
    float stock_top;
    float stock_bottom;
    float floor;
    float* triangles;
    int triangle_count;
    mn_map stock;
    mn_map model;
    mn_map tip;
    mn_map effective;
    mn_map limit;
    mn_map standing;
    mn_map strategy_tip;
    uint8_t* head_limited;
    uint8_t* should_cut;
    mn_plan* plan;
    mn_segments toolpath;
    mn_statistics statistics;
    mn_profile profile;
};

typedef struct stage_monitor {
    mn_stage_fn progress;
    void* context;
    int stage;
} stage_monitor;

static void forward_progress(void* context, int32_t step, int32_t steps, float fraction)
{
    stage_monitor* m = (stage_monitor*)context;
    if (m->progress != NULL) {
        m->progress(m->context, m->stage, step, steps, fraction);
    }
}

static void begin(stage_monitor* m, int stage)
{
    m->stage = stage;
    forward_progress(m, 0, 0, 0.0f);
}

static int cancelled(const volatile int32_t* cancel) { return cancel != NULL && *cancel != 0; }

MN_API void mn_result_free(mn_result* result)
{
    if (result == NULL) {
        return;
    }
    free(result->triangles);
    mn_map_free(&result->stock);
    mn_map_free(&result->model);
    mn_map_free(&result->tip);
    mn_map_free(&result->effective);
    mn_map_free(&result->limit);
    mn_map_free(&result->standing);
    mn_map_free(&result->strategy_tip);
    free(result->head_limited);
    free(result->should_cut);
    mn_plan_free(result->plan);
    mn_segments_free(&result->toolpath);
    mn_profile_free(&result->profile);
    free(result);
}

static int same_within(const float* a, const float* b, int cells, float tolerance)
{
    for (int k = 0; k < cells; k++) {
        float x = a[k];
        float y = b[k];
        if (mn_isnan(x) != mn_isnan(y) || (!mn_isnan(x) && fabsf(x - y) > tolerance)) {
            return 0;
        }
    }
    return 1;
}

static int generate(const mn_job* job, stage_monitor* stage, const volatile int32_t* cancel, mn_result* r)
{
    const mn_parameters* p = &job->parameters;
    mn_monitor monitor;
    monitor.progress = forward_progress;
    monitor.context = stage;
    monitor.cancel = cancel;

#define MN_STOP_IF_CANCELLED()                                                                     \
    do {                                                                                           \
        if (cancelled(cancel)) {                                                                   \
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");                                        \
        }                                                                                          \
    } while (0)

    /* transform: every model's triangles in machine space as one mesh; a mirroring matrix swaps B
     * and C so the winding stays outward. */
    begin(stage, MN_STAGE_TRANSFORM);
    int total = 0;
    for (int m = 0; m < job->mesh_count; m++) {
        total += job->mesh_triangle_counts[m];
    }
    r->triangles = (float*)mn_alloc((size_t)(total > 0 ? total : 1) * 9, sizeof(float));
    if (r->triangles == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for %d triangles.", total);
    }
    r->triangle_count = total;
    const float* source = job->triangles;
    float* target = r->triangles;
    for (int m = 0; m < job->mesh_count; m++) {
        const float* matrix = job->matrices + 16 * m;
        for (int t = 0; t < job->mesh_triangle_counts[m]; t++) {
            float a[3], b[3], c[3];
            mn_transform_point(source, matrix, a);
            mn_transform_point(source + 3, matrix, b);
            mn_transform_point(source + 6, matrix, c);
            const float* second = job->mirrored[m] ? c : b;
            const float* third = job->mirrored[m] ? b : c;
            memcpy(target, a, sizeof(a));
            memcpy(target + 3, second, sizeof(a));
            memcpy(target + 6, third, sizeof(a));
            source += 9;
            target += 9;
        }
    }
    MN_STOP_IF_CANCELLED();

    /* stock */
    begin(stage, MN_STAGE_STOCK);
    float max_z = job->stock_min_z + job->stock_size_z;
    MN_CHECK(mn_make_stock(job->stock_min_x, job->stock_min_y, job->stock_size_x, job->stock_size_y, max_z, job->stock_cylinder, job->stock_diameter, p->cell_size, &r->stock));
    r->grid = r->stock.g;
    r->stock_top = max_z;
    r->stock_bottom = job->stock_min_z;
    MN_STOP_IF_CANCELLED();

    /* model map */
    begin(stage, MN_STAGE_MODEL);
    const mn_grid* g = &r->grid;
    int cells = mn_cells(g);
    r->floor = r->stock_bottom;
    MN_CHECK(mn_map_create(&r->model, g, r->floor));
    mn_rasterize_triangles(r->triangles, total, g, r->model.z);
    MN_STOP_IF_CANCELLED();

    /* reach map */
    begin(stage, MN_STAGE_REACH);
    MN_CHECK(mn_profile_build(&job->tool, p->cell_size, &r->profile));
    MN_CHECK(mn_map_create(&r->tip, g, NAN));
    MN_CHECK(mn_reach_compute(g, r->model.z, r->stock.z, r->profile.offsets, r->profile.offset_count, r->floor, job->reach_percent, p->tolerance, &monitor, r->tip.z));
    MN_STOP_IF_CANCELLED();

    /* head clearance: the head must clear what the cutter leaves, rounded up to the level it stands at
     * until the pass that reaches it; limits only rise, so the loop is bounded. */
    begin(stage, MN_STAGE_HEAD);
    MN_CHECK(mn_map_clone(&r->effective, &r->tip));
    MN_CHECK(mn_map_create(&r->limit, g, NAN));
    mn_map remaining;
    mn_map next;
    MN_CHECK(mn_map_create(&remaining, g, NAN));
    int status = mn_map_create(&next, g, NAN);
    if (status != MN_OK) {
        mn_map_free(&remaining);
        return status;
    }
    for (int iteration = 1;; iteration++) {
        mn_remaining_compute(g, r->effective.z, r->profile.offsets, r->profile.offset_count, remaining.z);
        for (int k = 0; k < cells; k++) {
            remaining.z[k] = mn_ceil_level(remaining.z[k], r->stock_top, p->stepdown);
        }
        mn_head_limit_compute(g, remaining.z, r->profile.annulus, r->profile.annulus_count, job->tool.cutter_length, r->limit.z);
        mn_apply_limit(r->tip.z, r->limit.z, cells, next.z);
        int settled = same_within(next.z, r->effective.z, cells, p->tolerance);
        memcpy(r->effective.z, next.z, (size_t)cells * sizeof(float));
        if (cancelled(cancel)) {
            mn_map_free(&remaining);
            mn_map_free(&next);
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        }
        forward_progress(stage, iteration, MN_MAX_HEAD_ITERATIONS, (float)iteration / (float)MN_MAX_HEAD_ITERATIONS);
        if (settled || iteration >= MN_MAX_HEAD_ITERATIONS) {
            break;
        }
    }
    mn_map_free(&remaining);
    mn_map_free(&next);
    r->head_limited = (uint8_t*)mn_alloc((size_t)cells, 1);
    r->should_cut = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (r->head_limited == NULL || r->should_cut == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the head-limited mask.");
    }
    mn_head_limited_mask(r->tip.z, r->limit.z, cells, p->tolerance, r->head_limited);

    /* slice and cut scope; strategies stay above the standing stock as well as above the model. */
    begin(stage, MN_STAGE_SLICE);
    MN_CHECK(mn_slice_build(g, r->effective.z, r->stock.z, p->stepdown, &r->plan));
    MN_CHECK(mn_map_create(&r->standing, g, NAN));
    if (job->cut_scope == MN_SCOPE_SEPARATION) {
        mn_ints island_cells = { 0 };
        mn_ints island_offsets = { 0 };
        float* island_volumes = NULL;
        int island_count = 0;
        status = mn_separation_build(r->plan, r->effective.z, r->stock.z, &job->tool, p->tolerance, r->stock_top, r->floor, job->min_island_volume, r->standing.z, &island_cells,
            &island_offsets, &island_volumes, &island_count);
        mn_ints_free(&island_cells);
        mn_ints_free(&island_offsets);
        free(island_volumes);
        MN_CHECK(status);
    } else if (job->cut_scope != MN_SCOPE_EVERYTHING) {
        return mn_fail(MN_ERR_ARGUMENT, "Unknown cut scope %d.", job->cut_scope);
    }
    MN_CHECK(mn_map_create(&r->strategy_tip, g, NAN));
    mn_apply_limit(r->effective.z, r->standing.z, cells, r->strategy_tip.z);
    MN_STOP_IF_CANCELLED();

    /* route */
    begin(stage, MN_STAGE_ROUTE);
    mn_context context;
    context.grid = *g;
    context.model = r->model.z;
    context.tip = r->tip.z;
    context.effective_tip = r->strategy_tip.z;
    context.head_limit = r->limit.z;
    context.stock = r->stock.z;
    context.should_cut = r->should_cut;
    context.plan = r->plan;
    context.tool = job->tool;
    context.parameters = *p;
    context.stock_top = r->stock_top;
    mn_segments routed = { 0 };
    if (job->strategy == MN_STRATEGY_Z_LAYER) {
        status = mn_z_layer(&context, &monitor, &routed);
    } else if (job->strategy == MN_STRATEGY_THREE_AXIS_FREEDOM) {
        status = mn_three_axis_freedom(&context, &monitor, &routed);
    } else {
        status = mn_fail(MN_ERR_ARGUMENT, "Unknown strategy %d.", job->strategy);
    }
    if (status != MN_OK) {
        mn_segments_free(&routed);
        return status;
    }
    if (cancelled(cancel)) {
        mn_segments_free(&routed);
        return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
    }

    /* simplify */
    begin(stage, MN_STAGE_SIMPLIFY);
    status = mn_simplify_path(&routed, g, r->strategy_tip.z, p->tolerance, &r->toolpath);
    mn_segments_free(&routed);
    MN_CHECK(status);
    MN_STOP_IF_CANCELLED();

    /* statistics */
    begin(stage, MN_STAGE_STATISTICS);
    mn_statistics_of(r->toolpath.items, r->toolpath.count, p->rapid_rate, &r->statistics);
    return MN_OK;
#undef MN_STOP_IF_CANCELLED
}

MN_API int32_t mn_generate(const mn_job* job, mn_stage_fn progress, void* context, const volatile int32_t* cancel, mn_result** result)
{
    mn_result* r = (mn_result*)mn_alloc(1, sizeof(mn_result));
    if (r == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the generation result.");
    }
    stage_monitor stage;
    stage.progress = progress;
    stage.context = context;
    stage.stage = 0;
    int status = generate(job, &stage, cancel, r);
    if (status != MN_OK) {
        mn_result_free(r);
        return status;
    }
    *result = r;
    return MN_OK;
}

MN_API void mn_result_grid(const mn_result* result, mn_grid* grid) { *grid = result->grid; }

MN_API void mn_result_numbers(const mn_result* result, float* stock_top, float* stock_bottom, float* floor)
{
    *stock_top = result->stock_top;
    *stock_bottom = result->stock_bottom;
    *floor = result->floor;
}

MN_API void mn_result_map(const mn_result* result, int32_t which, float* z)
{
    const mn_map* maps[] = { &result->stock, &result->model, &result->tip, &result->effective, &result->limit, &result->standing, &result->strategy_tip };
    if (which < 0 || which > MN_MAP_STRATEGY_TIP) {
        return;
    }
    memcpy(z, maps[which]->z, (size_t)mn_cells(&result->grid) * sizeof(float));
}

MN_API void mn_result_mask(const mn_result* result, int32_t which, uint8_t* mask)
{
    const uint8_t* source = which == MN_MASK_SHOULD_CUT ? result->should_cut : result->head_limited;
    memcpy(mask, source, (size_t)mn_cells(&result->grid));
}

MN_API const mn_plan* mn_result_plan(const mn_result* result) { return result->plan; }

MN_API int32_t mn_result_triangle_count(const mn_result* result) { return result->triangle_count; }

MN_API void mn_result_triangles(const mn_result* result, float* triangles) { memcpy(triangles, result->triangles, (size_t)result->triangle_count * 9 * sizeof(float)); }

MN_API int32_t mn_result_segment_count(const mn_result* result) { return result->toolpath.count; }

MN_API void mn_result_segments(const mn_result* result, mn_segment* segments)
{
    memcpy(segments, result->toolpath.items, (size_t)result->toolpath.count * sizeof(mn_segment));
}

MN_API void mn_result_statistics(const mn_result* result, mn_statistics* statistics) { *statistics = result->statistics; }

MN_API void mn_result_profile(const mn_result* result, int32_t* offset_count, int32_t* annulus_count)
{
    *offset_count = result->profile.offset_count;
    *annulus_count = result->profile.annulus_count;
}

MN_API void mn_result_profile_read(const mn_result* result, mn_offset* offsets, mn_offset* annulus)
{
    memcpy(offsets, result->profile.offsets, (size_t)result->profile.offset_count * sizeof(mn_offset));
    memcpy(annulus, result->profile.annulus, (size_t)result->profile.annulus_count * sizeof(mn_offset));
}
