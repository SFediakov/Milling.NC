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
    float plunge_rate;
    float rapid_rate;
    float stepover;
    float finishing_stepover;
    float stepdown;
    float safe_height;
    float cell_size;
    float tolerance;
} mn_parameters;

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

/* ---- tool profile ---- */
MN_API int32_t mn_profile_create(const mn_tool* tool, float cell_size, mn_offset** offsets, int32_t* offset_count, mn_offset** annulus, int32_t* annulus_count);
MN_API float mn_bottom_height(int32_t tip_type, float radius, float distance);

/* ---- mesh ---- */
MN_API void mn_transform_points(const float* points, int32_t point_count, const float* matrix, float* result);
MN_API int32_t mn_grid_for(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid);
MN_API void mn_rasterize(const float* triangles, int32_t triangle_count, const mn_grid* grid, float* z);
MN_API int32_t mn_stock_map(float corner_x, float corner_y, float size_x, float size_y, float top, int32_t cylinder, float diameter, float cell_size, mn_grid* grid, float** z);

/* ---- heightmap algorithms ---- */
MN_API void mn_tip_map(const mn_grid* grid, const float* model, const mn_offset* offsets, int32_t count, float* tip);
MN_API void mn_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining);
MN_API int32_t mn_reach_map(const mn_grid* grid, const float* model, const float* stock, const mn_offset* offsets, int32_t count, float floor, float percent, float tolerance, mn_progress_fn progress, void* context, float* reach);
MN_API double mn_reach_round_percent(float percent, int32_t round);
MN_API int32_t mn_reach_rank(int32_t n, float percent);
MN_API float mn_reach_select(float* values, int32_t n, int32_t rank);
MN_API float mn_reach_select_threshold(float* values, uint8_t* excluded, int32_t n, double percent);
MN_API void mn_head_limit(const mn_grid* grid, const float* remaining, const mn_offset* annulus, int32_t count, float cutter_length, float* limit);
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
MN_API int32_t mn_step_cells(float spacing, float cell_size);
MN_API int32_t mn_on_lattice(int32_t index, int32_t count, int32_t step);
MN_API int32_t mn_lattice_nodes(const uint8_t* inside, int32_t width, int32_t height, int32_t step, int32_t** nodes, int32_t* count);
MN_API int32_t mn_is_outline(const uint8_t* inside, int32_t width, int32_t height, int32_t i, int32_t j);
MN_API float mn_surface_trace(const mn_grid* grid, const float* floor, const float* a, const float* b, float** points, int32_t* point_count);
MN_API float mn_route_exact(const mn_grid* grid, const float* floor, const float* a, const float* b);
MN_API float mn_route_lower_bound(const float* a, const float* b);
MN_API float mn_route_planar(const float* a, const float* b);
MN_API int32_t mn_turn_is_fined(const float* x, const float* y, float cell_size, int32_t p, int32_t a, int32_t b, int32_t c, int32_t d);
MN_API float mn_turn_slow_length(const float* x, const float* y, float cell_size, const int32_t* order, int32_t count);
MN_API float mn_turn_overlap(int32_t zones_a, int32_t zones_b, float gap);
MN_API float mn_turn_fine(const float* x, const float* y, float cell_size, const int32_t* order, int32_t count);
MN_API int64_t mn_budget_share(int64_t remaining, int32_t nodes, int64_t nodes_left);
MN_API int32_t mn_route_solve(const mn_grid* grid, const float* floor, const float* x, const float* y, const float* z, int32_t count, int32_t start, int64_t allowance, const volatile int32_t* cancel, int32_t* order, int64_t* evaluations);
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
#define MN_STRATEGY_THREE_AXIS_FREEDOM 1

/* Everything a strategy reads. Masks of the plan come from mn_plan; should_cut may be NULL. */
typedef struct mn_context {
    mn_grid grid;
    const float* model;
    const float* tip;
    const float* effective_tip;
    const float* head_limit;
    const float* stock;
    const uint8_t* should_cut;
    const mn_plan* plan;
    mn_tool tool;
    mn_parameters parameters;
    float stock_top;
} mn_context;

/* 3 axis freedom helpers: the level map max(tip, level) with NaN kept, and whether a cell of a map
 * steps by more than the tolerance to a neighbour or borders a cell without material. */
MN_API void mn_level_map(const float* tip, int32_t cells, float level, float* result);
MN_API int32_t mn_is_step(const mn_grid* grid, const float* map, int32_t i, int32_t j, float tolerance);
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
} mn_job;

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

/* Progress: context, stage, step, steps, fraction. */
typedef void (*mn_stage_fn)(void* context, int32_t stage, int32_t step, int32_t steps, float fraction);

MN_API int32_t mn_generate(const mn_job* job, mn_stage_fn progress, void* context, const volatile int32_t* cancel, mn_result** result);

#define MN_MAP_STOCK 0
#define MN_MAP_MODEL 1
#define MN_MAP_TIP 2
#define MN_MAP_EFFECTIVE_TIP 3
#define MN_MAP_HEAD_LIMIT 4
#define MN_MAP_STANDING 5
#define MN_MAP_STRATEGY_TIP 6

#define MN_MASK_HEAD_LIMITED 0
#define MN_MASK_SHOULD_CUT 1

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
MN_API void mn_result_profile(const mn_result* result, int32_t* offset_count, int32_t* annulus_count);
MN_API void mn_result_profile_read(const mn_result* result, mn_offset* offsets, mn_offset* annulus);
MN_API void mn_result_free(mn_result* result);

#ifdef __cplusplus
}
#endif

#endif
