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

/* CollisionDetector.Tolerance: float slack so a tip resting exactly on a surface is no collision. */
#define MN_COLLISION_TOLERANCE 1e-4f

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
float mn_head_widening(const mn_tool* tool);
float mn_head_radius_at(const mn_tool* tool, float h);
float mn_meet_radius_at(const mn_tool* tool, float h);

/* ---- mesh ---- */

void mn_transform_point(const float* point, const float* matrix, float* result);
int mn_grid_from_bounds(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid);
int mn_rasterize_triangles(const float* triangles, int count, const mn_grid* grid, float* z);
int mn_make_stock(float corner_x, float corner_y, float size_x, float size_y, float top, int cylinder, float diameter, float cell_size, mn_map* stock);

/* ---- heightmap algorithms ---- */

int mn_tip_map_compute(const mn_grid* g, const float* model, const mn_offset* offsets, int count, float* tip);
int mn_remaining_compute(const mn_grid* g, const float* tip, const mn_offset* offsets, int count, float* remaining);
/* The reach map's vote (mn_reach.c), shared with its GPU port: the rounds, the percent at which a
 * round takes the highest top, the arguments' check and the tops a vote of n needs. */
#define MN_REACH_ROUNDS 3
#define MN_REACH_MAX_PERCENT 100.0f
int mn_reach_check(float percent, float tolerance);
int mn_reach_needed(int n, double percent);
int mn_reach_compute(const mn_grid* g, const float* model, const float* stock, const mn_offset* offsets, int count, float floor, float percent, float tolerance, const mn_monitor* monitor, float* reach);
int mn_head_limit_compute(const mn_grid* g, const float* remaining, const mn_offset* annulus, int count, float cutter_length, float* limit);
void mn_apply_limit(const float* tip, const float* limit, int cells, float* effective);
int mn_mark_should_cut(const mn_grid* g, const float* tip, const float* model, const float* cut_to, const float* remaining, const float* limit, const mn_offset* annulus, int count,
    float cutter_length, float tolerance, uint8_t* should_cut);
int mn_distance_transform(const uint8_t* mask, int width, int height, float cell_size, float* distances);
/* In place: values[c] becomes min over q of values[q] + |c - q|^2 in cells (INFINITY where no source);
 * a source value may be negative (a power distance). */
int mn_lower_envelope(float* values, int width, int height);

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
/* The two halves of mn_separation_build (T-184, so the GPU can take the first): the levels from the
 * bottom up write the separation's masks into `masks` (count * cells), the model region and the
 * deepest region level of every cell; the finish turns them into the standing map, the coverage and
 * the islands, puts the plan's own masks back over islands below the volume to keep, and moves `masks`
 * into the plan. */
int mn_separation_levels(const mn_plan* plan, const float* effective_tip, const mn_tool* tool, float tolerance, float stock_top, float floor, uint8_t* masks, uint8_t* model_region, float* deepest);
int mn_separation_finish(mn_plan* plan, uint8_t* masks, const float* effective_tip, const float* stock, const uint8_t* model_region, const float* deepest, float tolerance, float min_island_volume,
    float* standing, mn_ints* island_cells, mn_ints* island_offsets, float** island_volumes, int* island_count);
int mn_islands(const mn_grid* g, const float* standing, const float* effective_tip, const float* stock, float tolerance, mn_ints* cells, mn_ints* offsets, float** volumes, int* count);
int mn_components(const uint8_t* mask, int width, int height, int* labels, mn_ints* cells, mn_ints* offsets);
/* Whether a cell of the list borders the grid edge or a cell without stock (8-neighbourhood). */
int mn_touches_outside(const int* cells, int count, const mn_grid* g, const float* stock);
int mn_within_radius(const uint8_t* marked, int width, int height, float cell_size, float radius, uint8_t* result);

/* ---- routing ---- */

int mn_lattice_step(float spacing, float cell_size);

/* A rectangle of cells, columns i0..i1 and rows j0..j1 inclusive; empty when i0 > i1 or j0 > j1
 * (T-185: the cells one cave or one route touches, so its work does not scan the whole grid). */
typedef struct mn_box {
    int i0;
    int j0;
    int i1;
    int j1;
} mn_box;

static inline mn_box mn_box_grid(const mn_grid* g) { mn_box box = { 0, 0, g->width - 1, g->height - 1 }; return box; }
static inline mn_box mn_box_empty(void) { mn_box box = { 0, 0, -1, -1 }; return box; }
static inline void mn_box_include(mn_box* box, int i, int j)
{
    if (box->i0 > box->i1 || box->j0 > box->j1) {
        box->i0 = box->i1 = i;
        box->j0 = box->j1 = j;
        return;
    }
    box->i0 = mn_mini(box->i0, i);
    box->i1 = mn_maxi(box->i1, i);
    box->j0 = mn_mini(box->j0, j);
    box->j1 = mn_maxi(box->j1, j);
}
/* The box grown by `margin` cells on every side and cut to the grid. */
static inline mn_box mn_box_grow(mn_box box, int margin, const mn_grid* g)
{
    if (box.i0 > box.i1 || box.j0 > box.j1) {
        return box;
    }
    mn_box grown = { mn_maxi(0, box.i0 - margin), mn_maxi(0, box.j0 - margin), mn_mini(g->width - 1, box.i1 + margin), mn_mini(g->height - 1, box.j1 + margin) };
    return grown;
}
mn_box mn_box_of_cells(const mn_grid* g, const int* cells, int count);

int mn_lattice(const uint8_t* inside, int width, int height, int step, mn_ints* nodes);
/* mn_lattice over the cells of `box` only: the same nodes in the same order when every cell of the
 * region lies in the box and `inside` is zero around it. */
int mn_lattice_in(const uint8_t* inside, int width, int height, int step, mn_box box, mn_ints* nodes);
int mn_outline(const uint8_t* inside, int width, int height, int i, int j);

/* `floor` is what a move through a cell stays above, `touch` what a point on a cell's edge or corner
 * stays above: over uncut stock the strategies put `floor` on the safe plane and `touch` on what must
 * remain there, so a diagonal step between neighbouring nodes past an uncut corner does not rise. */
typedef struct mn_route_grid {
    mn_grid g;
    const float* floor;
    const float* touch;
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

/* Precedence pairs (T-150): before[k] is visited ahead of after[k]. pair_count 0 means none. */
typedef struct mn_problem {
    mn_route_grid grid;
    const float* x;
    const float* y;
    const float* z;
    int count;
    const int* before;
    const int* after;
    int pair_count;
    /* Backwards_GPU (T-177): the start walks are joined by tours built on the GPU. */
    int gpu_tours;
} mn_problem;

/* Whether a collision mode is Backwards on the CPU or on the GPU. */
static inline int mn_backwards_mode(int collision_mode) { return collision_mode == MN_COLLISION_BACKWARDS || collision_mode == MN_COLLISION_BACKWARDS_GPU; }

/* The levels a far stepdown spans (T-156, T-196): k >= 2 when it is on, 1 when it is off (0, or the
 * stepdown itself: nothing then goes deeper than one stepdown), 0 when it is no whole multiple of the
 * stepdown, which the caller reports. */
static inline int mn_far_levels(const mn_parameters* p)
{
    if (p->far_stepdown == 0.0f) {
        return 1;
    }
    if (!(p->far_stepdown > 0.0f && p->stepdown > 0.0f)) {
        return 0;
    }
    int k = mn_f2i(rintf(p->far_stepdown / p->stepdown));
    return k >= 1 && fabsf(p->far_stepdown - (float)k * p->stepdown) <= MN_LEVEL_TOLERANCE ? k : 0;
}

static inline mn_v3 mn_problem_node(const mn_problem* p, int k) { return mn_v3_make(p->x[k], p->y[k], p->z[k]); }

/* TurnFine constants: the slow zones of a movement run at MN_SLOW_SPEED_FACTOR of the speed (three
 * times slower); the fine per slow millimetre is computed in double from the float factor and rounded
 * once, exactly as TurnFine.PerSlowMillimetre is. */
#define MN_SLOW_SPEED_FACTOR (1.0f / 3.0f)
#define MN_PER_SLOW_MILLIMETRE ((float)((1.0 / (double)MN_SLOW_SPEED_FACTOR - 1.0) / (double)MN_XY_SPEED_FACTOR))
#define MN_SLOW_ZONE 5.0f
#define MN_MIN_CHORD 1e-5f
/* A direction change whose sine lies within this is straight on: float noise of the node coordinates,
 * and a node that far off the line of its neighbours stays within 0.05 mm over a 50 mm chord, which
 * the simplifier joins into one move. */
#define MN_STRAIGHT_SINE 1e-3f

/* Cosine and side (+1 left, -1 right, 0 straight on or reversing) of the XY direction change at b; 0
 * when a chord has no direction. Swapping a and c keeps the cosine and flips the side exactly. */
int mn_turn_between(const float* x, const float* y, int a, int b, int c, float* cosine, int* side);
/* The turn fine (T-153): every new direction at a node is fined, straight on is not. */
static inline int mn_turn_is_fined(int defined, float cosine, int side) { return defined && (side != 0 || cosine < 0.0f); }
int mn_turn_fined(const float* x, const float* y, int a, int b, int c);
float mn_turn_slow(const float* x, const float* y, const int* order, int count);
float mn_turn_overlap_of(int zones_a, int zones_b, float gap);
/* An open path from `start` through every node; with `end` >= 0 (T-166) the path finishes at that node,
 * which must differ from the start unless the problem has one node and must have no successor. */
int mn_solve(const mn_problem* problem, int start, int end, int64_t allowance, const volatile int32_t* cancel, int* order, int64_t* evaluations);
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

/* Layer marks: the index of the first segment of every run of consecutive routes at one plan level,
 * with that level. The simplifier and the slow zones rewrite the indices for their output. */
typedef struct mn_marks {
    int* segment;
    float* level;
    int count;
    int capacity;
} mn_marks;

int mn_marks_push(mn_marks* list, int segment, float level);
void mn_marks_free(mn_marks* list);

/* Records a mark at the next segment the writer produces. A mark at the level of the previous one
 * joins it (the same layer), a mark at the index of the previous one replaces it (an empty route). */
int mn_writer_mark(mn_writer* writer, float level);
int mn_writer_travel_to(mn_writer* writer, mn_v3 to, const mn_route_grid* grid);
/* One run (T-150): a travel along the surface polyline is taken only when the head clears
 * `material` along it (sampled every max(cell, cutter radius)); otherwise the writer retracts.
 * The pointers stay owned by the caller. NULL material switches the rule off. */
void mn_writer_guard(mn_writer* writer, const float* material, const mn_profile* profile, float cutter_radius, float cutter_length);
int mn_writer_follow_to(mn_writer* writer, mn_v3 to, const mn_route_grid* grid);
int mn_writer_has_position(const mn_writer* writer, mn_v3* position);
/* Finish: the retract to safe Z, then the segments move to `result` and the marks to `marks` (NULL
 * drops them). */
int mn_writer_take(mn_writer* writer, mn_segments* result, mn_marks* marks);
/* Route units (T-166): a unit's writer starts where the tool already is, without a move; its path and
 * marks are released without the final retract; the merging writer appends them in unit order, the
 * marks shifted by its segment count and joined like mn_writer_mark joins them. */
void mn_writer_place(mn_writer* writer, mn_v3 position);
int mn_writer_release(mn_writer* writer, mn_segments* result, mn_marks* marks);
int mn_writer_append(mn_writer* writer, const mn_segments* path, const mn_marks* marks);

/* ---- collision check and resolution (T-147, T-148) ---- */

typedef struct mn_collisions {
    mn_collision* items;
    int count;
    int capacity;
} mn_collisions;

int mn_collisions_push(mn_collisions* list, mn_collision item);
void mn_collisions_free(mn_collisions* list);

/* What a check found, aggregated per tool position (the cell of the tool axis) and per entered
 * cell. STOCK: removable, the cell's closing (the lowest height any position brings it to, the
 * standing stock of the cut scope included) plus the tolerance lies below the tool surface that
 * hit it. MODEL: not removable, the model or kept stock stops the removal short of the surface.
 * REPEAT: removable stock that was already SHOULD_REMOVE and still stood in the way. SURFACE: the
 * tool surface lay below the model surface (the report's Model contact, the simulation's rule). */
#define MN_HIT_STOCK 1
#define MN_HIT_MODEL 2
#define MN_HIT_REPEAT 4
#define MN_HIT_SURFACE 8

typedef struct mn_hits {
    int cells;
    const float* closing;   /* per cell, NaN where no position reaches it */
    float slack;            /* the project tolerance: the simplified path may run this much above the planned tip */
    float* position_z;      /* lowest tip z of the position when it hit; NaN without a hit */
    uint8_t* position_flags;
    float* cell_height;     /* highest material an entered cell stood at when hit; NaN without a hit */
    uint8_t* cell_flags;
    int entered;            /* distinct entered cells */
    int unremovable;        /* entered cells flagged MODEL */
} mn_hits;

int mn_hits_init(mn_hits* hits, int cells);
void mn_hits_free(mn_hits* hits);

/* Sets MN_CELL_MODEL where the model stands above the floor; other bits are kept. */
void mn_model_bits(const float* model, int cells, float floor, uint8_t* status);

/* Clears MN_CELL_COLLISION, walks the segments over a clone of `stock` and sets it again on every
 * entered cell; `hits` may be NULL, otherwise its closing map must be set. Events hold one collision
 * per segment and kind. `final_stock` (cells, may be NULL) receives the clone as the walk leaves it. */
int mn_check_path(const mn_segment* segments, int count, const mn_grid* g, const float* stock, const float* model, float floor, const mn_profile* profile, float cutter_radius,
    float cutter_length, float tolerance, uint8_t* status, mn_hits* hits, mn_collisions* events, const mn_monitor* monitor, float* final_stock);

/* The check's walk with its inputs kept (T-207): opened once, run over any segment range in place on a
 * stock, closed when done. The grid and the profile's offsets must outlive the walker. */
typedef struct mn_walker mn_walker;
int mn_walker_open(const mn_grid* g, const mn_profile* profile, float cutter_radius, float cutter_length, float tolerance, int first_hit_only, mn_walker** walker);
int mn_walker_run(mn_walker* walker, const mn_segment* segments, int first_segment, int count, float* stock, const float* model, float floor, uint8_t* status,
    mn_hits* hits, mn_collisions* events, const mn_monitor* monitor, double* probing_seconds, double* walking_seconds);
void mn_walker_close(mn_walker* walker);

/* Not cut (T-173): the cells whose material after the toolpath stands more than the tolerance above
 * what the plan intended, their volume above it and the highest of them. */
typedef struct mn_uncut {
    int cells;
    double volume;
    float max_height;
} mn_uncut;

/* What the plan intends to leave: the closing of the deepest level of every position in its masks
 * (NaN where no position covers a cell); with a GPU session from the masks it holds. */
int mn_intended_compute(const mn_plan* plan, const mn_profile* profile, struct mn_gpu_session* gpu, float* deepest, float* intended);
void mn_uncut_of(const mn_grid* g, const float* final_stock, const float* intended, float tolerance, mn_uncut* uncut);

/* Resolution of one check (recursion mode): entered removable stock becomes SHOULD_REMOVE without
 * COLLISION; a position whose head hit unremovable cells is forbidden (its tip in `raised` lifted
 * until the head clears what stays, FORBIDDEN set) unless the model cells only it finishes outnumber
 * the hit ones by more than `ratio` times; a position blocked by stock that was already
 * SHOULD_REMOVE is forbidden as well. `effective` is the tip map of the positions (the reach tip under
 * the raises), `material` the same with the standing stock of the cut scope, which is what stays.
 * Returns how many marks were added. */
int mn_resolve(const mn_grid* g, const mn_profile* profile, float cutter_length, const float* model, const float* effective, const float* material, float floor, float tolerance,
    float ratio, const mn_hits* hits, uint8_t* status, float* raised);

/* ---- holding bridges (separation scope) ---- */

typedef struct mn_bridge_counts {
    int parts;
    int wanted;
    int placed;
} mn_bridge_counts;

/* Clears MN_CELL_BRIDGE, places up to `count` bridges per part the cut frees (see mn_bridges.c), sets
 * MN_CELL_BRIDGE on their cells, lifts `tip` (the strategy tip, in place) over them and replaces the
 * plan with one whose levels below the bridge top skip the lifted positions. */
int mn_bridges_place(const mn_grid* g, const float* stock, const float* model, const mn_profile* profile, float cutter_diameter, float floor, float width, float height, int count,
    float* tip, mn_plan** plan, uint8_t* status, mn_bridge_counts* counts);

/* ---- strategies and checks ---- */

float mn_distance_to(mn_v3 p, mn_v3 a, mn_v3 b);

/* `marks` (NULL allowed) receives the layer marks of the routes. */
/* `entries` (may be NULL) receives the entry of every route unit after the first (T-166). */
/* `unreached` (may be NULL) receives the counts of the backward check when the collision mode is
 * MN_COLLISION_BACKWARDS (T-170). */
typedef struct mn_unreached {
    int checked;
    int64_t unreached;
    int64_t model_cells;
    int sweeps;
} mn_unreached;

int mn_z_layer(const mn_context* context, const mn_monitor* monitor, mn_segments* result, mn_marks* marks, mn_points* entries, mn_unreached* unreached);

/* ---- backward check (T-170, mn_backwards.c) ---- */

typedef struct mn_backwards mn_backwards;

/* Starts the check on a thread of its own with `workers` workers. With the context's GPU session the
 * sweeps run on the GPU (mn_gpu_backward_settle). */
int mn_backwards_start(const mn_context* context, int workers, const volatile int32_t* cancel, mn_backwards** check);
/* Waits until the check has settled (T-193) and gives the reached plan, the plan's levels with per level
 * the positions of its mask reached that deep, and the strategy tip with every position that loses a
 * level raised to the deepest level it reaches (the stock top where it reaches none), so the Z layer
 * routes them as it routes a raised position; both stay valid until mn_backwards_finish. */
int mn_backwards_settled(mn_backwards* check, const mn_plan** reached, const float** tip);
/* Joins the check (stopping it first when `abort`) and frees it; returns the check's status unless it
 * was aborted, the unreached positions and the model cells summed over the levels, and the sweeps it
 * took to settle. */
int mn_backwards_finish(mn_backwards* check, int abort, int64_t* unreached, int64_t* model_cells, int* sweeps);
int mn_three_axis_freedom(const mn_context* context, const mn_monitor* monitor, mn_segments* result, mn_marks* marks);
int mn_is_clear(const mn_segment* segment, const mn_grid* g, const float* effective_tip, float tolerance);
/* `marks` (NULL allowed) ends a feed run at every marked segment and gets its indices rewritten for
 * the result. */
int mn_simplify_path(const mn_segments* input, const mn_grid* g, const float* effective_tip, float tolerance, mn_marks* marks, mn_segments* result);
/* Slow zones (mn_slow.c): the first and last MN_SLOW_ZONE of every movement at MN_SLOW_SPEED_FACTOR
 * of the rate, a movement being a run of feeds straight on in XY (mn_turn_between); `marks` as above. */
int mn_slow_zones(const mn_segments* input, mn_marks* marks, mn_segments* result);
void mn_statistics_of(const mn_segment* segments, int count, float rapid_rate, mn_statistics* statistics);

#endif
