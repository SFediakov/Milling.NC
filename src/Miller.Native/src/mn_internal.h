#ifndef MN_INTERNAL_H
#define MN_INTERNAL_H

/* Declarations shared between the modules of the library. Constants carry the values of the C#
 * implementation they replace. */

#include "mn_core.h"

/* ToolProfile.RadiusTolerance: d == r in float counts as inside. */
#define MN_RADIUS_TOLERANCE 1e-5f

/* Slicer.LevelTolerance: a value this close above a level counts as on it. */
#define MN_LEVEL_TOLERANCE 1e-4f

/* FinalModelAnalyzer.FloorTolerance. */
#define MN_FLOOR_TOLERANCE 1e-4f

/* ---- profile ---- */

typedef struct mn_profile {
    mn_offset* offsets;
    int offset_count;
    mn_offset* annulus;
    int annulus_count;
} mn_profile;

int mn_profile_build(const mn_tool* tool, float cell_size, mn_profile* profile);
void mn_profile_free(mn_profile* profile);
float mn_head_radius(const mn_tool* tool);

/* ---- mesh ---- */

void mn_transform_point(const float* point, const float* matrix, float* result);
int mn_grid_from_bounds(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid);
void mn_rasterize_triangles(const float* triangles, int count, const mn_grid* grid, float* z);
int mn_make_stock(float corner_x, float corner_y, float size_x, float size_y, float top, int cylinder, float diameter, float cell_size, mn_map* stock);

/* ---- heightmap algorithms ---- */

void mn_tip_map_compute(const mn_grid* g, const float* model, const mn_offset* offsets, int count, float* tip);
void mn_remaining_compute(const mn_grid* g, const float* tip, const mn_offset* offsets, int count, float* remaining);
int mn_reach_compute(const mn_grid* g, const float* model, const float* stock, const mn_offset* offsets, int count, float floor, float percent, float tolerance, const mn_monitor* monitor, float* reach);
void mn_head_limit_compute(const mn_grid* g, const float* remaining, const mn_offset* annulus, int count, float cutter_length, float* limit);
void mn_apply_limit(const float* tip, const float* limit, int cells, float* effective);
int mn_mark_should_cut(const mn_grid* g, const float* tip, const float* model, const float* cut_to, const float* remaining, const float* limit, const mn_offset* annulus, int count,
    float cutter_length, float tolerance, uint8_t* should_cut);
int mn_distance_transform(const uint8_t* mask, int width, int height, float cell_size, float* distances);

/* ---- slicing ---- */

struct mn_plan {
    mn_grid grid;
    int count;
    float* levels;
    uint8_t* masks; /* count * cells */
    uint8_t* coverage;
    float lowest;
};

int mn_plan_alloc(const mn_grid* g, int count, mn_plan** plan);
float mn_ceil_level(float z, float stock_top, float stepdown);
int mn_levels_list(float stock_top, float lowest, float stepdown, float** levels, int* count);
int mn_slice_build(const mn_grid* g, const float* effective_tip, const float* stock, float stepdown, mn_plan** plan);
static inline int mn_reachable_at_level(float reach_floor, float level) { return !mn_isnan(reach_floor) && reach_floor <= level + MN_LEVEL_TOLERANCE; }
int mn_separation_build(mn_plan* plan, const float* effective_tip, const float* stock, const mn_tool* tool, float tolerance, float stock_top, float floor, float min_island_volume, float* standing, mn_ints* island_cells, mn_ints* island_offsets, float** island_volumes, int* island_count);
int mn_islands(const mn_grid* g, const float* standing, const float* effective_tip, const float* stock, float tolerance, mn_ints* cells, mn_ints* offsets, float** volumes, int* count);
int mn_components(const uint8_t* mask, int width, int height, int* labels, mn_ints* cells, mn_ints* offsets);
int mn_within_radius(const uint8_t* marked, int width, int height, float cell_size, float radius, uint8_t* result);

/* ---- routing ---- */

int mn_lattice_step(float spacing, float cell_size);
int mn_lattice(const uint8_t* inside, int width, int height, int step, mn_ints* nodes);
int mn_outline(const uint8_t* inside, int width, int height, int i, int j);

typedef struct mn_route_grid {
    mn_grid g;
    const float* floor;
} mn_route_grid;

typedef struct mn_points {
    mn_v3* items;
    int count;
    int capacity;
} mn_points;

int mn_points_push(mn_points* list, mn_v3 point);
void mn_points_free(mn_points* list);

float mn_trace(const mn_route_grid* grid, mn_v3 a, mn_v3 b, mn_points* points, int* status);
float mn_cost_planar(mn_v3 a, mn_v3 b);
float mn_cost_lower_bound(mn_v3 a, mn_v3 b);
float mn_cost_exact(const mn_route_grid* grid, mn_v3 a, mn_v3 b);

#define MN_XY_SPEED_FACTOR 3.0f
#define MN_Z_SPEED_FACTOR 1.0f
#define MN_BUDGET_MAX_EVALUATIONS 40000000LL

typedef struct mn_problem {
    mn_route_grid grid;
    const float* x;
    const float* y;
    const float* z;
    int count;
} mn_problem;

static inline mn_v3 mn_problem_node(const mn_problem* p, int k) { return mn_v3_make(p->x[k], p->y[k], p->z[k]); }

/* TurnFine constants; the cosines are the floats the C# implementation computed in double. */
static inline float mn_float_from_bits(uint32_t bits)
{
    float value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

#define MN_COS_SHARP mn_float_from_bits(0x3F51B3F3u)
#define MN_COS_CIRCULAR_MAX mn_float_from_bits(0x248D3132u)
#define MN_PER_SLOW_MILLIMETRE mn_float_from_bits(0x3F471C71u)
#define MN_SLOW_ZONE 5.0f
#define MN_MIN_CHORD 1e-5f
#define MN_CIRCLE_TOLERANCE_CELLS 0.5f

/* T-139: a circular section exempts its turns only from this path length on; its chain of arcs is
 * followed at most MN_CHAIN_REACH arcs each way. A compound turn is up to MN_TURN_WINDOW soft turns
 * within MN_TURN_REACH positions each way and less than MN_TURN_SPAN of path. Lengths are summed in
 * double, which is exact for chord lengths, so the result does not depend on the direction. */
#define MN_CIRCLE_MIN_LENGTH 10.0
#define MN_CHAIN_REACH 16
#define MN_TURN_WINDOW 4
#define MN_TURN_REACH 8
#define MN_TURN_SPAN 10.0

/* Cosine and side (+1 left, -1 right, 0 straight or reversing) of the XY direction change at b; 0
 * when a chord has no direction. Swapping a and c keeps the cosine and flips the side exactly. */
int mn_turn_between(const float* x, const float* y, int a, int b, int c, float* cosine, int* side);
/* Four consecutive nodes on one circle within the tolerance, turning the same way below 90 degrees
 * at both inner nodes; the same answer for the reversed four. */
int mn_arc_between(const float* x, const float* y, float tolerance, int q0, int q1, int q2, int q3);
float mn_turn_slow(const float* x, const float* y, float cell_size, const int* order, int count);
float mn_turn_overlap_of(int zones_a, int zones_b, float gap);
int mn_solve(const mn_problem* problem, int start, int64_t allowance, const volatile int32_t* cancel, int* order, int64_t* evaluations);
float mn_path_cost(const mn_problem* problem, const int* order, int count);

typedef struct mn_budget {
    int64_t total;
    int64_t used;
} mn_budget;

int64_t mn_share(const mn_budget* budget, int nodes, int64_t nodes_left);

/* ---- writer ---- */

typedef struct mn_segments {
    mn_segment* items;
    int count;
    int capacity;
} mn_segments;

int mn_segments_push(mn_segments* list, mn_segment segment);
void mn_segments_free(mn_segments* list);
float mn_segment_length(const mn_segment* s);

int mn_writer_travel_to(mn_writer* writer, mn_v3 to, const mn_route_grid* grid);
int mn_writer_follow_to(mn_writer* writer, mn_v3 to, const mn_route_grid* grid);
int mn_writer_has_position(const mn_writer* writer, mn_v3* position);
int mn_writer_take(mn_writer* writer, mn_segments* result);

/* ---- strategies and checks ---- */

int mn_z_layer(const mn_context* context, const mn_monitor* monitor, mn_segments* result);
int mn_three_axis_freedom(const mn_context* context, const mn_monitor* monitor, mn_segments* result);
int mn_is_clear(const mn_segment* segment, const mn_grid* g, const float* effective_tip, float tolerance);
int mn_simplify_path(const mn_segments* input, const mn_grid* g, const float* effective_tip, float tolerance, mn_segments* result);
void mn_statistics_of(const mn_segment* segments, int count, float rapid_rate, mn_statistics* statistics);

#endif
