#ifndef MILLER_NATIVE_H
#define MILLER_NATIVE_H

/* The whole toolpath generation of Miller: from the model triangles and the project settings to
 * the simplified toolpath and every map the viewport, the simulation and the analysis read. The C#
 * side validates the project, calls mn_generate once and wraps the results; the per-algorithm
 * functions below serve the C# classes that keep their public signatures for the tests and for the
 * simulation and analysis, which are not part of the generation.
 *
 * Conventions: maps are row-major float arrays (index j * width + i) over an mn_grid, NaN meaning
 * no material; masks are byte arrays in the same order (0 or 1). A function returns MN_OK or a
 * negative status and mn_last_error() then describes the failure. Arrays a function allocates are
 * released with mn_free. */

#include <stdint.h>

#if defined(_WIN32)
#if defined(MN_BUILDING)
#define MN_API __declspec(dllexport)
#else
#define MN_API __declspec(dllimport)
#endif
#else
#define MN_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define MN_OK 0
#define MN_ERR_ARGUMENT (-1)
#define MN_ERR_OUT_OF_RANGE (-2)
#define MN_ERR_STATE (-3)
#define MN_ERR_MEMORY (-4)
#define MN_ERR_CANCELLED (-5)

typedef struct mn_grid {
    float origin_x;
    float origin_y;
    float cell_size;
    int32_t width;
    int32_t height;
} mn_grid;

#define MN_TIP_FLAT 0
#define MN_TIP_BALL 1

#define MN_HEAD_CYLINDER 0
#define MN_HEAD_FRUSTUM 1

/* Head: a cylinder of head_diameter above the cutter, or a frustum from head_diameter at its bottom
 * to head_top_diameter after head_length, continuing upward with the top diameter. */
typedef struct mn_tool {
    float cutter_diameter;
    float cutter_length;
    float head_diameter;
    float head_top_diameter;
    float head_length;
    int32_t tip_type;
    int32_t head_shape;
} mn_tool;

/* Grid offset from the tool axis and the height above the tip (cutter footprint) or above the head
 * bottom (head ring). */
typedef struct mn_offset {
    int32_t dx;
    int32_t dy;
    float dz;
} mn_offset;

#define MN_MOVE_RAPID 0
#define MN_MOVE_FEED 1
#define MN_MOVE_PLUNGE 2

typedef struct mn_segment {
    float start_x;
    float start_y;
    float start_z;
    float end_x;
    float end_y;
    float end_z;
    int32_t kind;
    float rate;
} mn_segment;

typedef struct mn_parameters {
    float feed_rate;
    float rapid_rate;
    float stepover;
    float stepdown;
    float safe_height;
    float cell_size;
    float tolerance;
    /* Z layer by layer: the far region of every group of far_stepdown / stepdown levels is cut this
     * much deeper in one step (T-156); 0 or the stepdown itself for none (T-196). */
    float far_stepdown;
} mn_parameters;

/* Inclusive cell rectangle a sweep lowered; empty when i1 < i0 (DirtyRect). */
typedef struct mn_dirty {
    int32_t i0;
    int32_t j0;
    int32_t i1;
    int32_t j1;
} mn_dirty;

typedef struct mn_statistics {
    float rapid_length;
    float feed_length;
    float plunge_length;
    int32_t segment_count;
    float estimated_minutes;
    int32_t retract_count;
} mn_statistics;

/* Progress of one stage or one algorithm: step of steps (0 steps: no inner loop) and the share of the
 * work done. Called on the calling thread only. */
typedef void (*mn_progress_fn)(void* context, int32_t step, int32_t steps, float fraction);

MN_API const char* mn_last_error(void);
MN_API void mn_free(void* pointer);

/* Worker threads of the parallel stages: the processor count, or the limit set here (0 for none);
 * every stage gives the same result with any count. */
MN_API void mn_set_worker_limit(int32_t limit);
MN_API int32_t mn_workers(void);

/* ---- simulation (T-159): the stock as a float map the caller owns ---- */
/* Sweeps one segment part: the tip sampled at most cell / 2 apart from `from` to `to`, both ends
 * included, every footprint cell taking min(stock, z + dz); `dirty` receives the cells that got lower.
 * One tool position: mn_stamp. */
MN_API int32_t mn_sweep(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* from, const float* to, mn_dirty* dirty);
/* The sample count of a segment (cell / 2 apart, both ends included) and its length. */
MN_API int32_t mn_segment_steps(const float* start, const float* end, float cell_size, float* length);
/* Samples first..last of the whole segment's samples (clamped); the tip of the last one in last_tip
 * (may be NULL); returns the segment's sample count or a negative status. */
MN_API int32_t mn_sweep_samples(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* start, const float* end, int32_t first, int32_t last, mn_dirty* dirty, float* last_tip);
MN_API int32_t mn_stamp(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* tip, mn_dirty* dirty);
/* The cells one tip enters, in offset order (the footprint of a rapid first, then the head annulus),
 * with the tool surface height there; returns their number (at most `capacity`, else an error) and
 * the kind (MN_EVENT_*) and heights of the first one, or -1 when none. */
MN_API int32_t mn_check_tip(const float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t offset_count, const mn_offset* annulus, int32_t annulus_count,
    float cutter_length, float tolerance, const float* tip, int32_t rapid, int32_t* cells, float* surfaces, int32_t capacity, int32_t* first_kind, float* first_stock,
    float* first_surface);

/* ---- tool profile ---- */
MN_API int32_t mn_profile_create(const mn_tool* tool, float cell_size, mn_offset** offsets, int32_t* offset_count, mn_offset** annulus, int32_t* annulus_count);
MN_API float mn_bottom_height(int32_t tip_type, float radius, float distance);

/* ---- mesh ---- */
MN_API void mn_transform_points(const float* points, int32_t point_count, const float* matrix, float* result);
MN_API int32_t mn_grid_for(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid);
MN_API int32_t mn_rasterize(const float* triangles, int32_t triangle_count, const mn_grid* grid, float* z);
MN_API int32_t mn_stock_map(float corner_x, float corner_y, float size_x, float size_y, float top, int32_t cylinder, float diameter, float cell_size, mn_grid* grid, float** z);

/* ---- heightmap algorithms ---- */
MN_API int32_t mn_tip_map(const mn_grid* grid, const float* model, const mn_offset* offsets, int32_t count, float* tip);
MN_API int32_t mn_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining);
MN_API int32_t mn_reach_map(const mn_grid* grid, const float* model, const float* stock, const mn_offset* offsets, int32_t count, float floor, float percent, float tolerance, mn_progress_fn progress, void* context, float* reach);
MN_API double mn_reach_round_percent(float percent, int32_t round);
MN_API int32_t mn_reach_rank(int32_t n, float percent);
MN_API float mn_reach_select(float* values, int32_t n, int32_t rank);
MN_API float mn_reach_select_threshold(float* values, uint8_t* excluded, int32_t n, double percent);
MN_API int32_t mn_head_limit(const mn_grid* grid, const float* remaining, const mn_offset* annulus, int32_t count, float cutter_length, float* limit);
MN_API void mn_apply_head_limit(const float* tip, const float* limit, int32_t cells, float* effective);
MN_API void mn_head_limited_mask(const float* tip, const float* limit, int32_t cells, float tolerance, uint8_t* mask);
MN_API int32_t mn_distances(const uint8_t* mask, int32_t width, int32_t height, float cell_size, float* distances);

/* ---- slicing and cut scope ---- */
typedef struct mn_plan mn_plan;
MN_API float mn_ceil_to_level(float z, float stock_top, float stepdown);
MN_API int32_t mn_levels(float stock_top, float lowest, float stepdown, float** levels, int32_t* count);
MN_API int32_t mn_slice(const mn_grid* grid, const float* effective_tip, const float* stock, float stepdown, mn_plan** plan);
MN_API int32_t mn_plan_create(const mn_grid* grid, int32_t level_count, const float* levels, const uint8_t* masks, const uint8_t* coverage, float lowest, mn_plan** plan);
MN_API int32_t mn_plan_level_count(const mn_plan* plan);
MN_API float mn_plan_lowest(const mn_plan* plan);
MN_API void mn_plan_read(const mn_plan* plan, float* levels, uint8_t* masks, uint8_t* coverage);
MN_API void mn_plan_free(mn_plan* plan);
MN_API void mn_model_region(const float* effective_tip, int32_t cells, float floor, uint8_t* region);
MN_API int32_t mn_head_reach(const float* levels, int32_t count, int32_t k, float stock_top, float cutter_length);
MN_API void mn_obstacles(const float* effective_tip, int32_t cells, float level, uint8_t* obstacles);
MN_API int32_t mn_within(const uint8_t* marked, int32_t width, int32_t height, float cell_size, float radius, uint8_t* result);
MN_API float mn_adjacency_margin(float cell_size);
MN_API float mn_head_margin(float cell_size, float tolerance);
MN_API float mn_head_overhang(const mn_tool* tool);
/* Separation scope: replaces the plan, fills standing, and returns the milled islands as cell lists
 * (island_offsets has island_count + 1 entries into island_cells) with their volumes. */
MN_API int32_t mn_separation(mn_plan* plan, const float* effective_tip, const float* stock, const mn_tool* tool, float tolerance, float stock_top, float floor, float min_island_volume, float* standing, int32_t** island_cells, int32_t** island_offsets, float** island_volumes, int32_t* island_count);
MN_API int32_t mn_islands_find(const mn_grid* grid, const float* standing, const float* effective_tip, const float* stock, float tolerance, int32_t** island_cells, int32_t** island_offsets, float** island_volumes, int32_t* island_count);
MN_API int32_t mn_label(const uint8_t* mask, int32_t width, int32_t height, int32_t* labels, int32_t** component_cells, int32_t** component_offsets, int32_t* component_count);
MN_API int32_t mn_reachable_at(float reach_floor, float level);
MN_API void mn_ceil_to_levels(const float* z, int32_t cells, float stock_top, float stepdown, float* result);
/* Gives the cells of every island below min_volume back to the level masks, the coverage and the
 * standing map; `removed` receives the indices of the islands milled out. */
MN_API int32_t mn_islands_remove_below(const int32_t* island_cells, const int32_t* island_offsets, const float* island_volumes, int32_t island_count, float min_volume, int32_t level_count, int32_t cells, const uint8_t* allowed, uint8_t* masks, const uint8_t* full_coverage, uint8_t* coverage, float* standing, int32_t* removed, int32_t* removed_count);
/* The level masks as a forest of caves: labels (level_count * cells, -1 outside), and per cave its
 * level, its label, its cells (cave_cell_offsets has cave_count + 1 entries) and its children
 * (cave_child_offsets likewise); roots lists the caves without a parent. */
MN_API int32_t mn_caves(const uint8_t* masks, int32_t level_count, int32_t width, int32_t height, int32_t* labels, int32_t** cave_levels, int32_t** cave_ids, int32_t** cave_cells, int32_t** cave_cell_offsets, int32_t** cave_children, int32_t** cave_child_offsets, int32_t** roots, int32_t* cave_count, int32_t* root_count);

/* ---- routing ---- */
/* The node step along a row of a Z layer lattice (T-210): the largest step up to the row step whose node
 * footprints alone cover the cells between two rows. */
MN_API int32_t mn_column_step(int32_t row_step, float cutter_radius, float cell_size);
MN_API int32_t mn_step_cells(float spacing, float cell_size);
MN_API int32_t mn_on_lattice(int32_t index, int32_t count, int32_t step);
MN_API int32_t mn_lattice_nodes(const uint8_t* inside, int32_t width, int32_t height, int32_t step, int32_t** nodes, int32_t* count);
MN_API int32_t mn_is_outline(const uint8_t* inside, int32_t width, int32_t height, int32_t i, int32_t j);
MN_API float mn_surface_trace(const mn_grid* grid, const float* floor, const float* a, const float* b, float** points, int32_t* point_count);
MN_API float mn_route_exact(const mn_grid* grid, const float* floor, const float* a, const float* b);
MN_API float mn_route_lower_bound(const float* a, const float* b);
MN_API float mn_route_planar(const float* a, const float* b);
/* Whether the node at `position` of the route `order` is fined (TurnFine): the direction changes there. */
MN_API int32_t mn_turn_fined_at(const float* x, const float* y, const int32_t* order, int32_t count, int32_t position);
MN_API float mn_turn_slow_length(const float* x, const float* y, const int32_t* order, int32_t count);
MN_API float mn_turn_overlap(int32_t zones_a, int32_t zones_b, float gap);
MN_API float mn_turn_fine(const float* x, const float* y, const int32_t* order, int32_t count);
/* The slow factor of the zones (a third: three times slower) and the fine per slow millimetre in
 * route cost units, as the solver prices them (T-215: the zones price the routes only, the toolpath
 * runs at the feed rate). */
MN_API float mn_turn_slow_speed_factor(void);
MN_API float mn_turn_per_slow_millimetre(void);
MN_API int64_t mn_budget_share(int64_t remaining, int32_t nodes, int64_t nodes_left);
/* `end` (T-166) is -1 for a path that ends anywhere, or the node it must finish at (not the start). */
MN_API int32_t mn_route_solve(const mn_grid* grid, const float* floor, const float* x, const float* y, const float* z, int32_t count, int32_t start, int32_t end, int64_t allowance, const volatile int32_t* cancel, int32_t* order, int64_t* evaluations);
MN_API float mn_route_path_cost(const mn_grid* grid, const float* floor, const float* x, const float* y, const float* z, const int32_t* order, int32_t count);

/* ---- writer ---- */
typedef struct mn_writer mn_writer;
MN_API int32_t mn_writer_create(const mn_parameters* parameters, float safe_z, mn_writer** writer);
MN_API int32_t mn_writer_travel(mn_writer* writer, const float* to, const mn_grid* grid, const float* floor);
MN_API int32_t mn_writer_follow(mn_writer* writer, const float* to, const mn_grid* grid, const float* floor);
MN_API int32_t mn_writer_position(const mn_writer* writer, float* position);
MN_API int32_t mn_writer_count(const mn_writer* writer);
MN_API int32_t mn_writer_finish(mn_writer* writer, mn_segment** segments, int32_t* count);
MN_API void mn_writer_free(mn_writer* writer);

/* ---- strategies, checks, simplification ---- */
#define MN_STRATEGY_Z_LAYER 0

/* ---- cell status (collision handling, T-147) ---- */
/* One byte per cell. MODEL: shouldn't be removed (the model stands above the floor). COLLISION: the
 * head or a rapid entered the cell in the last check. BRIDGE: a cut-through cell of a holding bridge,
 * shouldn't be cut below the bridge top (separation scope). */
#define MN_CELL_MODEL 1
#define MN_CELL_COLLISION 4
#define MN_CELL_BRIDGE 16

/* Backwards (T-169 to T-172, T-193): a check from the lowest level up excludes the positions whose head
 * would meet material only cutting the model frees, and the Z layer routes the plan it reaches. With its
 * GPU copy the only collision handling (T-214). */
#define MN_COLLISION_BACKWARDS 0
/* Backwards_GPU (T-174, user request): Backwards with its data-parallel processes on the discrete GPU
 * (the backward check's sweeps, the intended surface, extra start tours of the route solver); it fails
 * when no OpenCL GPU is found. */
#define MN_COLLISION_BACKWARDS_GPU 1

/* The OpenCL GPU the Backwards_GPU mode uses: MN_OK and its name, or a status and mn_last_error. */
MN_API int32_t mn_gpu_device(char* name, int32_t capacity);
/* The GPU processes on their own, for the comparisons with the CPU (T-177): the closing of a map
 * (mn_remaining on the GPU) and the start tours of the route solver (orders keep * count, kept their
 * number). */
MN_API int32_t mn_gpu_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining);
MN_API int32_t mn_gpu_start_tours(const float* x, const float* y, int32_t count, int32_t start, int32_t end, int32_t tours, int32_t keep, int32_t* orders, int32_t* kept);

#define MN_EVENT_HEAD 0
#define MN_EVENT_RAPID 1

/* One collision per segment and kind (the rule of the simulation panel): the tool tip, the first
 * entered cell, the material height there and the tool surface (head underside, rapid footprint
 * bottom) that lay below it; model is 1 when the segment entered the model. */
typedef struct mn_collision {
    int32_t segment;
    int32_t kind;
    float x;
    float y;
    float z;
    float stock_z;
    float surface;
    int32_t cell;
    int32_t model;
} mn_collision;

/* Dynamic collision check over a stock clone: feeds and plunges remove material at CellSize / 2 along
 * the segment; the head ring is tested at every segment end and every max(CellSize, cutter radius)
 * along it, the footprint of a rapid at the same spacing, against the stock as it stands. status
 * (cells) receives MODEL and, per entered cell, COLLISION. */
/* The simulation session (T-207): the simulation's whole segments on the collision check's banded walk
 * over the CPU workers. Open takes the footprint, the annulus and the segments once (copied) and keeps
 * a worker pool; walk sweeps segments [first, last) over `stock` in place, with one event per check
 * position from its first entered cell (the per-sample simulation's rule), and returns the events
 * (free with mn_free) and a rectangle holding every changed cell. */
typedef struct mn_sim mn_sim;
MN_API int32_t mn_sim_open(const mn_grid* grid, const mn_offset* offsets, int32_t offset_count, const mn_offset* annulus, int32_t annulus_count, float cutter_radius,
    float cutter_length, float tolerance, const mn_segment* segments, int32_t segment_count, mn_sim** session);
MN_API int32_t mn_sim_walk(mn_sim* session, int32_t first, int32_t last, float* stock, const float* model, float floor, mn_collision** events, int32_t* event_count, mn_dirty* dirty);
MN_API void mn_sim_close(mn_sim* session);

MN_API int32_t mn_collision_check(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* stock, const float* model, float floor, const mn_tool* tool, float tolerance, uint8_t* status, mn_collision** events, int32_t* event_count);

/* Far stepdown withdrawal (T-164): a collision's place (a cell the head or a rapid entered, and the
 * tool axis cell that entered it) and everything within this many millimetres of it in XY are
 * withdrawn from the far stepdown for every later pass and milled with the normal stepdown. */
#define MN_FAR_WITHDRAW_RADIUS 1.0f

/* Clears `allowed` within `radius` of every marked place; returns how many allowed cells it cleared
 * or a negative status. */
MN_API int32_t mn_far_withdraw(const uint8_t* places, int32_t width, int32_t height, float cell_size, float radius, uint8_t* allowed);

/* Everything a strategy reads. Masks of the plan come from mn_plan. */
typedef struct mn_context {
    mn_grid grid;
    const float* model;
    const float* tip;
    const float* effective_tip;
    const float* head_limit;
    const float* stock;
    const mn_plan* plan;
    mn_tool tool;
    mn_parameters parameters;
    float stock_top;
    float floor;
    int32_t collision_mode;
    /* Z layer with a far stepdown (T-164): the positions the far block may take (NULL: every far
     * position), and, when not NULL, 1 per position it milled with the far stepdown, 0 elsewhere. */
    const uint8_t* far_allowed;
    uint8_t* far_area;
    /* Backwards_GPU (T-183): the generation's GPU session, which holds the stock, the profile and the
     * plan's masks; NULL in every other mode. */
    struct mn_gpu_session* gpu;
} mn_context;

MN_API int32_t mn_strategy_generate(int32_t strategy, const mn_context* context, mn_progress_fn progress, void* progress_context, const volatile int32_t* cancel, mn_segment** segments, int32_t* count);
MN_API int32_t mn_gouge_verify(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, int32_t** segment_index, float** positions, float** depths, int32_t* violation_count);
MN_API int32_t mn_gouge_is_clear(const mn_segment* segment, const mn_grid* grid, const float* effective_tip, float tolerance);
MN_API int32_t mn_simplify(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, mn_segment** result, int32_t* result_count);
MN_API int32_t mn_kept_indices(const float* points, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, float feed_rate, int32_t** kept, int32_t* kept_count);
MN_API float mn_distance_to_segment(const float* p, const float* a, const float* b);
MN_API int32_t mn_statistics_compute(const mn_segment* segments, int32_t count, float rapid_rate, mn_statistics* statistics);

/* ---- the whole generation ---- */
#define MN_SCOPE_EVERYTHING 0
#define MN_SCOPE_SEPARATION 1

typedef struct mn_job {
    /* Model triangles in model space (9 floats each) per mesh, and one row-major 4 x 4 matrix
     * (System.Numerics layout, row vectors) per mesh to machine space. */
    const float* triangles;
    const int32_t* mesh_triangle_counts;
    const float* matrices;
    /* 1 where the matrix mirrors (negative determinant): B and C are swapped to keep the winding. */
    const int32_t* mirrored;
    int32_t mesh_count;
    /* Stock box in machine space. */
    float stock_min_x;
    float stock_min_y;
    float stock_min_z;
    float stock_size_x;
    float stock_size_y;
    float stock_size_z;
    int32_t stock_cylinder;
    float stock_diameter;
    mn_tool tool;
    mn_parameters parameters;
    int32_t strategy;
    int32_t cut_scope;
    float min_island_volume;
    float reach_percent;
    int32_t collision_mode;
    /* Separation scope: holding bridges per part the trench frees (0 to MN_MAX_BRIDGES, 0 for none),
     * their width and the material they keep above the floor. */
    int32_t bridge_count;
    float bridge_width;
    float bridge_height;
} mn_job;

#define MN_MAX_BRIDGES 32

typedef struct mn_result mn_result;

#define MN_STAGE_TRANSFORM 1
#define MN_STAGE_STOCK 2
#define MN_STAGE_MODEL 3
#define MN_STAGE_REACH 4
#define MN_STAGE_HEAD 5
#define MN_STAGE_SLICE 6
#define MN_STAGE_ROUTE 7
#define MN_STAGE_SIMPLIFY 8
#define MN_STAGE_STATISTICS 9
#define MN_STAGE_CHECK 10

/* Generation passes (the far withdrawal's repeats) never exceed this number. */
#define MN_MAX_PASSES 8

/* Progress: context, pass (from 1), stage, step, steps, fraction. */
typedef void (*mn_stage_fn)(void* context, int32_t pass, int32_t stage, int32_t step, int32_t steps, float fraction);

MN_API int32_t mn_generate(const mn_job* job, mn_stage_fn progress, void* context, const volatile int32_t* cancel, mn_result** result);

#define MN_MAP_STOCK 0
#define MN_MAP_MODEL 1
#define MN_MAP_TIP 2
#define MN_MAP_EFFECTIVE_TIP 3
#define MN_MAP_HEAD_LIMIT 4
#define MN_MAP_STANDING 5
#define MN_MAP_STRATEGY_TIP 6

#define MN_MASK_HEAD_LIMITED 0
#define MN_MASK_STATUS 2
/* Per cell what the kept pass's check entered: nothing, stock only, or the model (outranks stock). */
#define MN_MASK_CONTACTS 3
#define MN_CONTACT_NONE 0
#define MN_CONTACT_STOCK 1
#define MN_CONTACT_MODEL 2
/* Far stepdown (T-164): the positions the first pass milled with it, those the kept pass milled with
 * it, and those of the first pass the kept pass was no longer allowed (withdrawn around collisions). */
#define MN_MASK_FAR_FIRST 4
#define MN_MASK_FAR_MILLED 5
#define MN_MASK_FAR_WITHDRAWN 6

MN_API void mn_result_grid(const mn_result* result, mn_grid* grid);
MN_API void mn_result_numbers(const mn_result* result, float* stock_top, float* stock_bottom, float* floor);
MN_API void mn_result_map(const mn_result* result, int32_t which, float* z);
MN_API void mn_result_mask(const mn_result* result, int32_t which, uint8_t* mask);
MN_API const mn_plan* mn_result_plan(const mn_result* result);
MN_API int32_t mn_result_triangle_count(const mn_result* result);
MN_API void mn_result_triangles(const mn_result* result, float* triangles);
MN_API int32_t mn_result_segment_count(const mn_result* result);
MN_API void mn_result_segments(const mn_result* result, mn_segment* segments);
MN_API void mn_result_statistics(const mn_result* result, mn_statistics* statistics);
/* Layers of the kept pass's toolpath: one per run of consecutive routes at one plan level, in path
 * order (the Z layer strategy visits caves depth first, so a level can return later); per layer the
 * index of its first segment (the first layer starts at 0) and its level. */
MN_API int32_t mn_result_layer_count(const mn_result* result);
MN_API void mn_result_layers(const mn_result* result, int32_t* segment, float* level);
MN_API int32_t mn_result_passes(const mn_result* result);
/* Per pass (mn_result_passes entries): the distinct cells its check entered and its events. */
MN_API void mn_result_pass_counts(const mn_result* result, int32_t* entered, int32_t* events);
/* Route units of the kept pass (T-166): the entry of every unit after the first (x, y, z each), where
 * the unit before it ends and it begins. */
MN_API int32_t mn_result_unit_count(const mn_result* result);
MN_API void mn_result_units(const mn_result* result, float* points);
/* Per pass: the positions it milled with the far stepdown and those withdrawn after its check. */
MN_API void mn_result_pass_far(const mn_result* result, int32_t* milled, int32_t* withdrawn);
/* Holding bridges of the kept pass: the parts the trench frees, the bridges wanted (parts times the
 * count) and the bridges placed. */
MN_API void mn_result_bridges(const mn_result* result, int32_t* parts, int32_t* wanted, int32_t* placed);
/* Backwards (T-172): 1 when the kept pass ran the backward check, then the positions it left unreached
 * and the model cells, both summed over the levels (a cell counts on every level the model stands above),
 * and the sweeps from the lowest level up the check took to settle. */
MN_API int32_t mn_result_unreached(const mn_result* result, int64_t* unreached, int64_t* model_cells, int32_t* sweeps);
/* Not cut (T-173): the cells the kept pass's toolpath leaves more than the tolerance above what the
 * first pass's plan intended (the closing of the deepest level of every position in its masks), the
 * volume of material above that surface there and the highest of them. */
MN_API void mn_result_uncut(const mn_result* result, int32_t* cells, double* volume, float* max_height);

/* Process times of the whole generation in seconds (T-176), indexed as below: the wall time of every
 * stage over all passes, then the parts inside them; the backward check's input is the reached plan and
 * the raised tip it gives the Z layer (T-193), the level groups the Z layer's preparation of its groups;
 * the route units and the solver parts (candidate lists, start walks, local search, route floors,
 * writing, GPU start tours) are summed over the units, which run on several threads at once; the check's bands are the
 * batches the workers walked at once, its walk the batches with a hit that ran as one walk (T-187); GPU
 * transfers include the kernels a read waits for. Returns how many values it wrote (at most `capacity`). */
#define MN_TIME_INTENDED 10
#define MN_TIME_BACK_SETTLE 11
#define MN_TIME_BACK_LEVELS 12
#define MN_TIME_BACK_INPUT 13
#define MN_TIME_BACK_WAIT 14
#define MN_TIME_GROUPS 15
#define MN_TIME_UNITS 16
#define MN_TIME_CANDIDATES 17
#define MN_TIME_WALKS 18
#define MN_TIME_SEARCH 19
#define MN_TIME_FLOORS 20
#define MN_TIME_WRITE 21
#define MN_TIME_GPU_SETUP 22
#define MN_TIME_GPU_TRANSFER 23
#define MN_TIME_GPU_TOURS 24
#define MN_TIME_CHECK_BANDS 25
#define MN_TIME_CHECK_WALK 26
#define MN_TIME_COUNT 27
MN_API int32_t mn_result_timings(const mn_result* result, double* seconds, int32_t capacity);
MN_API int32_t mn_result_collision_count(const mn_result* result);
MN_API void mn_result_collisions(const mn_result* result, mn_collision* collisions);
MN_API void mn_result_profile(const mn_result* result, int32_t* offset_count, int32_t* annulus_count);
MN_API void mn_result_profile_read(const mn_result* result, mn_offset* offsets, mn_offset* annulus);
MN_API void mn_result_free(mn_result* result);

#ifdef __cplusplus
}
#endif

#endif
