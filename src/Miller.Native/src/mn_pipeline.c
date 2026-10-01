#include "mn_internal.h"

/* The whole generation after the project is validated (PipelineService): transform, stock, model
 * map and reach map once, then one or more passes of slicing and cut scope, routing, simplification,
 * statistics and the dynamic collision check (T-147). The first pass runs without any head
 * consideration. In recursion mode (T-148) the check's result is resolved into the cell status and
 * the raised tips, and the pass is repeated while the number of entered cells decreases, at most
 * MN_MAX_PASSES times; the pass with the fewest entered cells is kept. In one run mode (T-150) the
 * strategy evaluates its nodes against the standing material and one pass is checked for the
 * report. Progress goes to the caller per pass and stage; cancellation is honoured between the
 * stages and inside the long ones. */

/* Everything one pass produced. */
typedef struct mn_pass {
    mn_map effective;
    mn_map limit;
    mn_map standing;
    mn_map strategy_tip;
    uint8_t* head_limited;
    uint8_t* status;
    uint8_t* contacts;
    mn_plan* plan;
    mn_segments toolpath;
    mn_statistics statistics;
    mn_collisions events;
    mn_bridge_counts bridges;
    int entered;
    int valid;
} mn_pass;

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
    mn_profile profile;
    mn_pass best;
    int passes;
    int pass_entered[MN_MAX_PASSES];
    int pass_events[MN_MAX_PASSES];
};

typedef struct stage_monitor {
    mn_stage_fn progress;
    void* context;
    int pass;
    int stage;
} stage_monitor;

static void forward_progress(void* context, int32_t step, int32_t steps, float fraction)
{
    stage_monitor* m = (stage_monitor*)context;
    if (m->progress != NULL) {
        m->progress(m->context, m->pass, m->stage, step, steps, fraction);
    }
}

static void begin(stage_monitor* m, int stage)
{
    m->stage = stage;
    forward_progress(m, 0, 0, 0.0f);
}

static int cancelled(const volatile int32_t* cancel) { return cancel != NULL && *cancel != 0; }

static void pass_free(mn_pass* pass)
{
    mn_map_free(&pass->effective);
    mn_map_free(&pass->limit);
    mn_map_free(&pass->standing);
    mn_map_free(&pass->strategy_tip);
    free(pass->head_limited);
    free(pass->status);
    free(pass->contacts);
    mn_plan_free(pass->plan);
    mn_segments_free(&pass->toolpath);
    mn_collisions_free(&pass->events);
    memset(pass, 0, sizeof(*pass));
}

MN_API void mn_result_free(mn_result* result)
{
    if (result == NULL) {
        return;
    }
    free(result->triangles);
    mn_map_free(&result->stock);
    mn_map_free(&result->model);
    mn_map_free(&result->tip);
    mn_profile_free(&result->profile);
    pass_free(&result->best);
    free(result);
}

#define MN_STOP_IF_CANCELLED()                                                                     \
    do {                                                                                           \
        if (cancelled(cancel)) {                                                                   \
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");                                        \
        }                                                                                          \
    } while (0)

/* Transform, stock, model map and reach map: the part of the generation every pass shares. */
static int prepare(const mn_job* job, stage_monitor* stage, const volatile int32_t* cancel, mn_result* r)
{
    const mn_parameters* p = &job->parameters;
    mn_monitor monitor;
    monitor.progress = forward_progress;
    monitor.context = stage;
    monitor.cancel = cancel;

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
    return MN_OK;
}

/* One pass: the tip under the raised map, slicing and cut scope, holding bridges, routing,
 * simplification, statistics and the check. `status` holds the MODEL and SHOULD_REMOVE bits the pass
 * runs with and receives the BRIDGE bits of its bridges and the COLLISION bits of its check; `hits`
 * receives the check's aggregation when given. */
static int run_pass(const mn_job* job, stage_monitor* stage, const volatile int32_t* cancel, const mn_result* r, const float* raised, uint8_t* status, mn_hits* hits, mn_pass* pass)
{
    const mn_parameters* p = &job->parameters;
    const mn_grid* g = &r->grid;
    int cells = mn_cells(g);
    mn_monitor monitor;
    monitor.progress = forward_progress;
    monitor.context = stage;
    monitor.cancel = cancel;
    memset(pass, 0, sizeof(*pass));

    /* head clearance: only the positions the earlier passes forbade lift the tip. */
    begin(stage, MN_STAGE_HEAD);
    MN_CHECK(mn_map_create(&pass->effective, g, NAN));
    MN_CHECK(mn_map_create(&pass->limit, g, NAN));
    memcpy(pass->limit.z, raised, (size_t)cells * sizeof(float));
    mn_apply_limit(r->tip.z, pass->limit.z, cells, pass->effective.z);
    pass->head_limited = (uint8_t*)mn_alloc((size_t)cells, 1);
    pass->status = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (pass->head_limited == NULL || pass->status == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the pass masks.");
    }
    MN_STOP_IF_CANCELLED();
    uint8_t* should_cut = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (should_cut == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the should-cut mask.");
    }
    for (int k = 0; k < cells; k++) {
        should_cut[k] = (uint8_t)((status[k] & MN_CELL_SHOULD_REMOVE) != 0);
    }

    /* slice and cut scope; strategies stay above the standing stock as well as above the model. */
    begin(stage, MN_STAGE_SLICE);
    int status_code = mn_slice_build(g, pass->effective.z, r->stock.z, p->stepdown, &pass->plan);
    if (status_code == MN_OK) {
        status_code = mn_map_create(&pass->standing, g, NAN);
    }
    if (status_code == MN_OK && job->cut_scope == MN_SCOPE_SEPARATION) {
        mn_ints island_cells = { 0 };
        mn_ints island_offsets = { 0 };
        float* island_volumes = NULL;
        int island_count = 0;
        /* The region and its obstacles follow the raised tip, as they followed the head-limited tip
         * before: the trench lies beyond the collar the head keeps beside a tall part. */
        status_code = mn_separation_build(pass->plan, pass->effective.z, r->stock.z, &job->tool, p->tolerance, r->stock_top, r->floor, job->min_island_volume, pass->standing.z,
            &island_cells, &island_offsets, &island_volumes, &island_count);
        mn_ints_free(&island_cells);
        mn_ints_free(&island_offsets);
        free(island_volumes);
    } else if (status_code == MN_OK && job->cut_scope != MN_SCOPE_EVERYTHING) {
        status_code = mn_fail(MN_ERR_ARGUMENT, "Unknown cut scope %d.", job->cut_scope);
    }
    if (status_code == MN_OK) {
        status_code = mn_map_create(&pass->strategy_tip, g, NAN);
    }
    if (status_code != MN_OK) {
        free(should_cut);
        return status_code;
    }
    mn_apply_limit(pass->effective.z, pass->standing.z, cells, pass->strategy_tip.z);
    /* Holding bridges belong to the trench of the separation scope; they lift the strategy tip only,
     * so the next pass builds its trench without them. */
    if (job->cut_scope == MN_SCOPE_SEPARATION) {
        status_code = mn_bridges_place(g, r->stock.z, r->model.z, &r->profile, job->tool.cutter_diameter, r->floor, job->bridge_width, job->bridge_height, job->bridge_count,
            pass->strategy_tip.z, &pass->plan, status, &pass->bridges);
        if (status_code != MN_OK) {
            free(should_cut);
            return status_code;
        }
    }
    if (cancelled(cancel)) {
        free(should_cut);
        return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
    }

    /* route */
    begin(stage, MN_STAGE_ROUTE);
    mn_context context;
    context.grid = *g;
    context.model = r->model.z;
    context.tip = r->tip.z;
    context.effective_tip = pass->strategy_tip.z;
    context.head_limit = pass->limit.z;
    context.stock = r->stock.z;
    context.should_cut = should_cut;
    context.plan = pass->plan;
    context.tool = job->tool;
    context.parameters = *p;
    context.stock_top = r->stock_top;
    context.floor = r->floor;
    context.collision_mode = job->collision_mode;
    context.ratio = job->collision_mode == MN_COLLISION_ONE_RUN ? job->one_run_ratio : job->recursion_ratio;
    context.raised = job->collision_mode == MN_COLLISION_ONE_RUN ? pass->limit.z : NULL;
    mn_segments routed = { 0 };
    if (job->strategy == MN_STRATEGY_Z_LAYER) {
        status_code = mn_z_layer(&context, &monitor, &routed);
    } else if (job->strategy == MN_STRATEGY_THREE_AXIS_FREEDOM) {
        status_code = mn_three_axis_freedom(&context, &monitor, &routed);
    } else {
        status_code = mn_fail(MN_ERR_ARGUMENT, "Unknown strategy %d.", job->strategy);
    }
    free(should_cut);
    if (status_code == MN_OK && cancelled(cancel)) {
        status_code = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
    }
    if (status_code != MN_OK) {
        mn_segments_free(&routed);
        return status_code;
    }
    mn_head_limited_mask(r->tip.z, pass->limit.z, cells, p->tolerance, pass->head_limited);

    /* simplify */
    begin(stage, MN_STAGE_SIMPLIFY);
    status_code = mn_simplify_path(&routed, g, pass->strategy_tip.z, p->tolerance, &pass->toolpath);
    mn_segments_free(&routed);
    MN_CHECK(status_code);
    MN_STOP_IF_CANCELLED();

    /* statistics */
    begin(stage, MN_STAGE_STATISTICS);
    mn_statistics_of(pass->toolpath.items, pass->toolpath.count, p->rapid_rate, &pass->statistics);

    /* check: the head and the rapids against the stock as the pass leaves it, order included. A hit
     * cell is removable when its closing under the strategy tip (standing stock included) lies
     * below the tool surface. */
    begin(stage, MN_STAGE_CHECK);
    mn_map closing = { 0 };
    if (hits != NULL) {
        MN_CHECK(mn_map_create(&closing, g, NAN));
        mn_remaining_compute(g, pass->strategy_tip.z, r->profile.offsets, r->profile.offset_count, closing.z);
        hits->closing = closing.z;
        hits->slack = p->tolerance;
    }
    status_code = mn_check_path(pass->toolpath.items, pass->toolpath.count, g, r->stock.z, r->model.z, r->floor, &r->profile, job->tool.cutter_diameter / 2.0f, job->tool.cutter_length,
        MN_COLLISION_TOLERANCE, status, hits, &pass->events, &monitor);
    if (status_code != MN_OK) {
        mn_map_free(&closing);
        if (hits != NULL) {
            hits->closing = NULL;
        }
        return status_code;
    }
    memcpy(pass->status, status, (size_t)cells);
    mn_map_free(&closing);
    if (hits != NULL) {
        hits->closing = NULL;
    }
    pass->contacts = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (pass->contacts == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the contact mask.");
    }
    if (hits != NULL) {
        for (int k = 0; k < cells; k++) {
            uint8_t flags = hits->cell_flags[k];
            pass->contacts[k] = (uint8_t)((flags & MN_HIT_SURFACE) != 0 ? MN_CONTACT_MODEL : flags != 0 ? MN_CONTACT_STOCK : MN_CONTACT_NONE);
        }
    }
    pass->entered = hits != NULL ? hits->entered : pass->events.count;
    pass->valid = 1;
    return MN_OK;
}

static int generate(const mn_job* job, stage_monitor* stage, const volatile int32_t* cancel, mn_result* r)
{
    if (job->collision_mode != MN_COLLISION_RECURSION && job->collision_mode != MN_COLLISION_ONE_RUN) {
        return mn_fail(MN_ERR_ARGUMENT, "Unknown collision mode %d.", job->collision_mode);
    }
    if (!(job->recursion_ratio >= 0) || !(job->one_run_ratio >= 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The collision ratios must be zero or positive.");
    }
    if (job->bridge_count < 0 || job->bridge_count > MN_MAX_BRIDGES) {
        return mn_fail(MN_ERR_ARGUMENT, "The bridge count must be 0 to %d, got %d.", MN_MAX_BRIDGES, job->bridge_count);
    }
    if (job->bridge_count > 0 && !(job->bridge_width > 0 && job->bridge_width < INFINITY && job->bridge_height > 0 && job->bridge_height < INFINITY)) {
        return mn_fail(MN_ERR_ARGUMENT, "The bridge width and height must be finite and greater than 0.");
    }
    stage->pass = 1;
    MN_CHECK(prepare(job, stage, cancel, r));

    const mn_grid* g = &r->grid;
    int cells = mn_cells(g);
    uint8_t* status = (uint8_t*)mn_alloc((size_t)cells, 1);
    float* raised = (float*)mn_alloc((size_t)cells, sizeof(float));
    mn_hits hits;
    int hits_ready = 0;
    int result = MN_OK;
    if (status == NULL || raised == NULL) {
        result = mn_fail(MN_ERR_MEMORY, "Out of memory for the cell status.");
        goto done;
    }
    for (int k = 0; k < cells; k++) {
        raised[k] = NAN;
    }
    mn_model_bits(r->model.z, cells, r->floor, status);
    result = mn_hits_init(&hits, cells);
    if (result != MN_OK) {
        goto done;
    }
    hits_ready = 1;

    /* The pass repeats while the entered cells or the unremovable ones among them still decrease
     * from one pass to the next and the resolution added a mark; the pass with the fewest entered
     * cells is kept. */
    int previous_entered = -1;
    int previous_unremovable = -1;
    for (int pass_index = 1; pass_index <= MN_MAX_PASSES; pass_index++) {
        stage->pass = pass_index;
        mn_pass pass;
        memset(hits.position_flags, 0, (size_t)cells);
        memset(hits.cell_flags, 0, (size_t)cells);
        for (int k = 0; k < cells; k++) {
            hits.position_z[k] = NAN;
            hits.cell_height[k] = NAN;
        }
        hits.entered = 0;
        hits.unremovable = 0;
        result = run_pass(job, stage, cancel, r, raised, status, &hits, &pass);
        if (result != MN_OK) {
            pass_free(&pass);
            goto done;
        }
        r->passes = pass_index;
        r->pass_entered[pass_index - 1] = pass.entered;
        r->pass_events[pass_index - 1] = pass.events.count;
        int improved = !r->best.valid || pass.entered < r->best.entered;
        int decreasing = previous_entered < 0 || pass.entered < previous_entered || hits.unremovable < previous_unremovable;
        previous_entered = pass.entered;
        previous_unremovable = hits.unremovable;
        int last = job->collision_mode == MN_COLLISION_ONE_RUN || pass.entered == 0 || !decreasing;
        int changed = 0;
        if (!last) {
            changed = mn_resolve(g, &r->profile, job->tool.cutter_length, r->model.z, pass.effective.z, pass.strategy_tip.z, r->floor, job->parameters.tolerance, job->recursion_ratio, &hits, status, raised);
        }
        if (improved) {
            pass_free(&r->best);
            r->best = pass;
        } else {
            pass_free(&pass);
        }
        if (last) {
            break;
        }
        if (changed < 0) {
            result = changed;
            goto done;
        }
        if (changed == 0) {
            break;
        }
        if (cancelled(cancel)) {
            result = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            goto done;
        }
    }

done:
    if (hits_ready) {
        mn_hits_free(&hits);
    }
    free(status);
    free(raised);
    return result;
}

#undef MN_STOP_IF_CANCELLED

MN_API int32_t mn_generate(const mn_job* job, mn_stage_fn progress, void* context, const volatile int32_t* cancel, mn_result** result)
{
    mn_result* r = (mn_result*)mn_alloc(1, sizeof(mn_result));
    if (r == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the generation result.");
    }
    stage_monitor stage;
    stage.progress = progress;
    stage.context = context;
    stage.pass = 1;
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
    const mn_pass* b = &result->best;
    const mn_map* maps[] = { &result->stock, &result->model, &result->tip, &b->effective, &b->limit, &b->standing, &b->strategy_tip };
    if (which < 0 || which > MN_MAP_STRATEGY_TIP) {
        return;
    }
    memcpy(z, maps[which]->z, (size_t)mn_cells(&result->grid) * sizeof(float));
}

MN_API void mn_result_mask(const mn_result* result, int32_t which, uint8_t* mask)
{
    int cells = mn_cells(&result->grid);
    const mn_pass* b = &result->best;
    if (which == MN_MASK_STATUS) {
        memcpy(mask, b->status, (size_t)cells);
        return;
    }
    if (which == MN_MASK_CONTACTS) {
        memcpy(mask, b->contacts, (size_t)cells);
        return;
    }
    if (which == MN_MASK_SHOULD_CUT) {
        for (int k = 0; k < cells; k++) {
            mask[k] = (uint8_t)((b->status[k] & MN_CELL_SHOULD_REMOVE) != 0);
        }
        return;
    }
    memcpy(mask, b->head_limited, (size_t)cells);
}

MN_API const mn_plan* mn_result_plan(const mn_result* result) { return result->best.plan; }

MN_API int32_t mn_result_triangle_count(const mn_result* result) { return result->triangle_count; }

MN_API void mn_result_triangles(const mn_result* result, float* triangles) { memcpy(triangles, result->triangles, (size_t)result->triangle_count * 9 * sizeof(float)); }

MN_API int32_t mn_result_segment_count(const mn_result* result) { return result->best.toolpath.count; }

MN_API void mn_result_segments(const mn_result* result, mn_segment* segments)
{
    memcpy(segments, result->best.toolpath.items, (size_t)result->best.toolpath.count * sizeof(mn_segment));
}

MN_API void mn_result_statistics(const mn_result* result, mn_statistics* statistics) { *statistics = result->best.statistics; }

MN_API int32_t mn_result_passes(const mn_result* result) { return result->passes; }

MN_API void mn_result_pass_counts(const mn_result* result, int32_t* entered, int32_t* events)
{
    for (int k = 0; k < result->passes; k++) {
        entered[k] = result->pass_entered[k];
        events[k] = result->pass_events[k];
    }
}

MN_API void mn_result_bridges(const mn_result* result, int32_t* parts, int32_t* wanted, int32_t* placed)
{
    *parts = result->best.bridges.parts;
    *wanted = result->best.bridges.wanted;
    *placed = result->best.bridges.placed;
}

MN_API int32_t mn_result_collision_count(const mn_result* result) { return result->best.events.count; }

MN_API void mn_result_collisions(const mn_result* result, mn_collision* collisions)
{
    memcpy(collisions, result->best.events.items, (size_t)result->best.events.count * sizeof(mn_collision));
}

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
