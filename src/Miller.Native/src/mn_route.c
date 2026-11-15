#include "mn_window.h"

/* Moves between route nodes: the surface polyline (SurfacePath), its cost (RouteCost) and the turn
 * fine (TurnFine). */

/* SurfacePath.LineEpsilon: a coordinate this close to a grid line (in cells) counts as on it. */
#define MN_LINE_EPSILON 1e-4f

int mn_points_push(mn_points* list, mn_v3 point)
{
    if (list->count == list->capacity) {
        int capacity = list->capacity == 0 ? 16 : list->capacity * 2;
        mn_v3* items = (mn_v3*)realloc(list->items, (size_t)capacity * sizeof(mn_v3));
        if (items == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for a polyline of %d points.", capacity);
        }
        list->items = items;
        list->capacity = capacity;
    }
    list->items[list->count++] = point;
    return MN_OK;
}

void mn_points_free(mn_points* list)
{
    free(list->items);
    list->items = NULL;
    list->count = 0;
    list->capacity = 0;
}

/* ---- surface polyline ---- */

static float plateau(const mn_route_grid* grid, int i, int j)
{
    return mn_in_bounds(&grid->g, i, j) ? grid->floor[j * grid->g.width + i] : NAN;
}

static float plateau_touched(const mn_route_grid* grid, int i, int j)
{
    return mn_in_bounds(&grid->g, i, j) ? grid->touch[j * grid->g.width + i] : NAN;
}

/* `first` must be a number; NaN candidates never win. */
static float max2(float first, float second) { return second > first ? second : first; }

static float max3(float first, float second, float third) { return max2(max2(first, second), third); }

/* Highest touch floor among the cells touching the point: its own cell and the cell on the other
 * side of every grid line the point lies on. */
static float touching(const mn_route_grid* grid, float x, float y)
{
    float fi = (x - grid->g.origin_x) / grid->g.cell_size;
    float fj = (y - grid->g.origin_y) / grid->g.cell_size;
    int i = mn_f2i(floorf(fi));
    int j = mn_f2i(floorf(fj));
    int on_vertical = fabsf(fi - rintf(fi)) < MN_LINE_EPSILON;
    int on_horizontal = fabsf(fj - rintf(fj)) < MN_LINE_EPSILON;
    int i0 = on_vertical ? mn_f2i(rintf(fi)) - 1 : i;
    int i1 = on_vertical ? mn_f2i(rintf(fi)) : i;
    int j0 = on_horizontal ? mn_f2i(rintf(fj)) - 1 : j;
    int j1 = on_horizontal ? mn_f2i(rintf(fj)) : j;
    float best = NAN;
    for (int jj = j0; jj <= j1; jj++) {
        for (int ii = i0; ii <= i1; ii++) {
            float value = plateau_touched(grid, ii, jj);
            if (mn_isnan(best) || value > best) {
                best = value;
            }
        }
    }
    return best;
}

/* The vertices of the polyline from a to b in order, `t` being the share of the move's XY length behind
 * them: the rise in place over a, every cell-edge crossing lifted to the highest plateau touching it and
 * never below the straight line between the ends, the descent in place over b, then b. The walk starts at
 * a, which is no vertex. */
enum { WALK_START, WALK_CROSSINGS, WALK_ABOVE, WALK_END, WALK_DONE };

typedef struct trace_vertex {
    float t;
    mn_v3 p;
} trace_vertex;

typedef struct trace_walk {
    const mn_route_grid* grid;
    mn_v3 a;
    mn_v3 b;
    float dx;
    float dy;
    int i;
    int j;
    int i_end;
    int j_end;
    int step_i;
    int step_j;
    float t_delta_x;
    float t_delta_y;
    float t_max_x;
    float t_max_y;
    int remaining;
    float current;
    float z;
    int stage;
} trace_walk;

static void walk_init(trace_walk* w, const mn_route_grid* grid, mn_v3 a, mn_v3 b)
{
    const mn_grid* g = &grid->g;
    float cell = g->cell_size;
    w->grid = grid;
    w->a = a;
    w->b = b;
    w->i = mn_cell_i(g, a.x);
    w->j = mn_cell_j(g, a.y);
    w->i_end = mn_cell_i(g, b.x);
    w->j_end = mn_cell_j(g, b.y);
    w->dx = b.x - a.x;
    w->dy = b.y - a.y;
    w->step_i = w->dx > 0 ? 1 : w->dx < 0 ? -1 : 0;
    w->step_j = w->dy > 0 ? 1 : w->dy < 0 ? -1 : 0;
    w->t_delta_x = w->step_i == 0 ? INFINITY : cell / fabsf(w->dx);
    w->t_delta_y = w->step_j == 0 ? INFINITY : cell / fabsf(w->dy);
    w->t_max_x = w->step_i == 0 ? INFINITY : (g->origin_x + (float)(w->step_i > 0 ? w->i + 1 : w->i) * cell - a.x) / w->dx;
    w->t_max_y = w->step_j == 0 ? INFINITY : (g->origin_y + (float)(w->step_j > 0 ? w->j + 1 : w->j) * cell - a.y) / w->dy;
    w->remaining = abs(w->i_end - w->i) + abs(w->j_end - w->j) + 2;
    w->current = plateau(grid, w->i, w->j);
    w->z = a.z;
    w->stage = WALK_START;
}

static int walk_vertex(trace_walk* w, float t, mn_v3 p, trace_vertex* v)
{
    w->z = p.z;
    v->t = t;
    v->p = p;
    return 1;
}

/* The next vertex in v; 0 after b. */
static int walk_next(trace_walk* w, trace_vertex* v)
{
    const mn_route_grid* grid = w->grid;
    if (w->stage == WALK_START) {
        w->stage = WALK_CROSSINGS;
        float start = max3(w->z, w->current, touching(grid, w->a.x, w->a.y));
        if (start > w->z) {
            return walk_vertex(w, 0.0f, mn_v3_make(w->a.x, w->a.y, start), v);
        }
    }
    if (w->stage == WALK_CROSSINGS) {
        if ((w->i != w->i_end || w->j != w->j_end) && w->remaining-- > 0) {
            float t;
            if (w->t_max_x < w->t_max_y) {
                t = w->t_max_x;
                w->i += w->step_i;
                w->t_max_x += w->t_delta_x;
            } else if (w->t_max_y < w->t_max_x) {
                t = w->t_max_y;
                w->j += w->step_j;
                w->t_max_y += w->t_delta_y;
            } else {
                t = w->t_max_x;
                w->i += w->step_i;
                w->j += w->step_j;
                w->t_max_x += w->t_delta_x;
                w->t_max_y += w->t_delta_y;
            }
            if (!(t > 1.0f)) {
                float px = w->a.x + w->dx * t;
                float py = w->a.y + w->dy * t;
                float next = plateau(grid, w->i, w->j);
                float lifted = max2(max3(w->a.z + (w->b.z - w->a.z) * t, w->current, next), touching(grid, px, py));
                w->current = next;
                return walk_vertex(w, t, mn_v3_make(px, py, lifted), v);
            }
        }
        w->stage = WALK_ABOVE;
    }
    if (w->stage == WALK_ABOVE) {
        w->stage = WALK_END;
        float above = max2(max3(w->b.z, w->current, plateau(grid, w->i_end, w->j_end)), touching(grid, w->b.x, w->b.y));
        if (above > w->b.z) {
            return walk_vertex(w, 1.0f, mn_v3_make(w->b.x, w->b.y, above), v);
        }
    }
    if (w->stage == WALK_END) {
        w->stage = WALK_DONE;
        return walk_vertex(w, 1.0f, w->b, v);
    }
    return 0;
}

static mn_v3 point_on(const trace_walk* w, float t, float z)
{
    if (t <= 0.0f) {
        return mn_v3_make(w->a.x, w->a.y, z);
    }
    if (t >= 1.0f) {
        return mn_v3_make(w->b.x, w->b.y, z);
    }
    return mn_v3_make(w->a.x + w->dx * t, w->a.y + w->dy * t, z);
}

/* What a trace gives: the vertical travel from a and, when wanted, the points after a. */
typedef struct trace_out {
    mn_points* points;
    int* status;
    float z;
    float climb;
} trace_out;

static void out_emit(trace_out* o, mn_v3 p)
{
    o->climb += fabsf(p.z - o->z);
    o->z = p.z;
    if (o->points != NULL && *o->status == MN_OK) {
        *o->status = mn_points_push(o->points, p);
    }
}

/* The vertices of a walk one after another: `prev` the last one taken (a at t = 0 before any), `next` the
 * one after it. */
typedef struct trace_feed {
    trace_walk walk;
    trace_vertex prev;
    trace_vertex next;
    int has_next;
} trace_feed;

static void feed_init(trace_feed* f, const mn_route_grid* grid, mn_v3 a, mn_v3 b)
{
    walk_init(&f->walk, grid, a, b);
    f->prev.t = 0.0f;
    f->prev.p = a;
    f->has_next = walk_next(&f->walk, &f->next);
}

static void feed_take(trace_feed* f)
{
    f->prev = f->next;
    f->has_next = walk_next(&f->walk, &f->next);
}

static void feed_emit_before(trace_feed* f, trace_out* o, float t)
{
    while (f->has_next && f->next.t < t) {
        out_emit(o, f->next.p);
        feed_take(f);
    }
}

static void feed_skip_through(trace_feed* f, float t)
{
    while (f->has_next && f->next.t <= t) {
        feed_take(f);
    }
}

/* The polyline's height at t, which lies from prev to next. */
static float feed_height(const trace_feed* f, float t)
{
    if (f->has_next && f->next.t == t) {
        return f->next.p.z;
    }
    if (!f->has_next || f->prev.t == t) {
        return f->prev.p.z;
    }
    return f->prev.p.z + (f->next.p.z - f->prev.p.z) * ((t - f->prev.t) / (f->next.t - f->prev.t));
}

/* The end of a stretch on the safe plane: the polyline up to `land` is replaced, the tool lands in place
 * there on the polyline (b itself at the move's end). */
static void feed_land(trace_feed* f, trace_out* o, float land, float safe)
{
    feed_skip_through(f, land);
    out_emit(o, point_on(&f->walk, land, safe));
    out_emit(o, land >= 1.0f ? f->walk.b : point_on(&f->walk, land, feed_height(f, land)));
}

/* T-216: every stretch of the polyline on the safe plane (consecutive vertices on it) widened by `margin`
 * (a share of the move) on both sides. A lead walk finds the vertices on the plane; the feed walk, at
 * least the margin behind it, emits the polyline up to the rise, the stretch on the plane and the
 * polyline after the landing. A rise or landing beyond the move's own ends lies on them, in place;
 * stretches whose widened ends meet join. */
static float trace_widened(const mn_route_grid* grid, mn_v3 a, mn_v3 b, float margin, mn_points* points, int* status)
{
    float safe = grid->safe_z;
    trace_walk lead;
    walk_init(&lead, grid, a, b);
    trace_feed feed;
    feed_init(&feed, grid, a, b);
    trace_out out = { points, status, a.z, 0.0f };
    int on_plane = 0;
    int previous_on_plane = 0;
    float land = 0.0f;
    trace_vertex v;
    while (walk_next(&lead, &v)) {
        int here = v.p.z >= safe;
        if (here) {
            float rise = mn_max(0.0f, v.t - margin);
            if (on_plane && !previous_on_plane && rise > land) {
                feed_land(&feed, &out, land, safe);
                on_plane = 0;
            }
            if (!on_plane) {
                feed_emit_before(&feed, &out, rise);
                if (rise > 0.0f) {
                    out_emit(&out, point_on(&feed.walk, rise, feed_height(&feed, rise)));
                }
                out_emit(&out, point_on(&feed.walk, rise, safe));
                on_plane = 1;
            }
            land = mn_min(1.0f, v.t + margin);
        } else if (on_plane && v.t > land + margin) {
            feed_land(&feed, &out, land, safe);
            on_plane = 0;
        }
        previous_on_plane = here;
        if (!on_plane) {
            feed_emit_before(&feed, &out, v.t - margin);
        }
    }
    if (on_plane) {
        feed_land(&feed, &out, land, safe);
    }
    feed_emit_before(&feed, &out, INFINITY);
    return out.climb;
}

/* The polyline after `a` (excluded) up to `b` (included), appended to `points` when given, and the
 * vertical travel along it. A move whose polyline reaches the safe plane (cells lifted over uncut stock)
 * is on the plane from MN_SAFE_MARGIN of its XY travel before the first such vertex to MN_SAFE_MARGIN
 * after the last (T-216); every other move keeps the plain polyline. */
float mn_trace(const mn_route_grid* grid, mn_v3 a, mn_v3 b, mn_points* points, int* status)
{
    *status = MN_OK;
    int base = points != NULL ? points->count : 0;
    trace_walk walk;
    walk_init(&walk, grid, a, b);
    trace_out out = { points, status, a.z, 0.0f };
    int on_plane = 0;
    trace_vertex v;
    while (walk_next(&walk, &v)) {
        out_emit(&out, v.p);
        on_plane |= v.p.z >= grid->safe_z;
    }
    float planar = sqrtf(walk.dx * walk.dx + walk.dy * walk.dy);
    if (!on_plane || !(planar > 0.0f)) {
        return out.climb;
    }
    if (points != NULL) {
        points->count = base;
    }
    *status = MN_OK;
    return trace_widened(grid, a, b, MN_SAFE_MARGIN / planar, points, status);
}

MN_API float mn_route_safe_margin(void) { return MN_SAFE_MARGIN; }

MN_API float mn_surface_trace(const mn_grid* grid, const float* floor, const float* a, const float* b, float** points, int32_t* point_count)
{
    mn_route_grid route = { *grid, floor, floor, INFINITY };
    mn_points list = { 0 };
    int status;
    float climb = mn_trace(&route, mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2]), points != NULL ? &list : NULL, &status);
    if (points != NULL) {
        if (status != MN_OK) {
            mn_points_free(&list);
            *points = NULL;
            *point_count = -1;
            return NAN;
        }
        *points = (float*)list.items;
        *point_count = list.count;
    }
    return climb;
}

/* ---- costs: travel time in units of Z travel, XY three times faster ---- */

float mn_cost_planar(mn_v3 a, mn_v3 b)
{
    float dx = a.x - b.x;
    float dy = a.y - b.y;
    return sqrtf(dx * dx + dy * dy);
}

float mn_cost_lower_bound(mn_v3 a, mn_v3 b) { return mn_cost_planar(a, b) / MN_XY_SPEED_FACTOR + fabsf(a.z - b.z) / MN_Z_SPEED_FACTOR; }

float mn_cost_exact(const mn_route_grid* grid, mn_v3 a, mn_v3 b)
{
    int status;
    return mn_cost_planar(a, b) / MN_XY_SPEED_FACTOR + mn_trace(grid, a, b, NULL, &status) / MN_Z_SPEED_FACTOR;
}

MN_API float mn_route_exact(const mn_grid* grid, const float* floor, const float* a, const float* b)
{
    mn_route_grid route = { *grid, floor, floor, INFINITY };
    return mn_cost_exact(&route, mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2]));
}

MN_API float mn_route_lower_bound(const float* a, const float* b) { return mn_cost_lower_bound(mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2])); }

MN_API float mn_route_planar(const float* a, const float* b) { return mn_cost_planar(mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2])); }

/* ---- turn fine (T-153) ---- */

int mn_turn_between(const float* x, const float* y, int a, int b, int c, float* cosine, int* side)
{
    float ux = x[b] - x[a];
    float uy = y[b] - y[a];
    float wx = x[c] - x[b];
    float wy = y[c] - y[b];
    float lu = sqrtf(ux * ux + uy * uy);
    float lw = sqrtf(wx * wx + wy * wy);
    if (lu < MN_MIN_CHORD || lw < MN_MIN_CHORD) {
        *cosine = 1.0f;
        *side = 0;
        return 0;
    }
    *cosine = (ux * wx + uy * wy) / (lu * lw);
    float cross = ux * wy - uy * wx;
    *side = fabsf(cross) <= MN_STRAIGHT_SINE * (lu * lw) ? 0 : cross > 0 ? 1 : -1;
    return 1;
}

int mn_turn_fined(const float* x, const float* y, int a, int b, int c)
{
    float cosine;
    int side;
    int defined = mn_turn_between(x, y, a, b, c, &cosine, &side);
    return mn_turn_is_fined(defined, cosine, side);
}

MN_API int32_t mn_turn_fined_at(const float* x, const float* y, const int32_t* order, int32_t count, int32_t position)
{
    if (position < 1 || position >= count - 1) {
        return 0;
    }
    return mn_turn_fined(x, y, order[position - 1], order[position], order[position + 1]);
}

float mn_turn_overlap_of(int zones_a, int zones_b, float gap) { return mn_max(0.0f, MN_SLOW_ZONE * (float)(zones_a + zones_b) - gap); }

MN_API float mn_turn_overlap(int32_t zones_a, int32_t zones_b, float gap) { return mn_turn_overlap_of(zones_a, zones_b, gap); }

/* Slow XY length of a route: every node where the direction changes starts a movement whose first
 * and last SlowZone are slow; 2 x SlowZone per fined node less the overlap of every two consecutive
 * events, the route start and end being events without a zone, so a millimetre is slow once. */
float mn_turn_slow(const float* x, const float* y, const int* order, int n)
{
    if (n < 3) {
        return 0.0f;
    }
    float s = 0.0f;
    float last_s = 0.0f;
    int last_zones = 0;
    float total = 0.0f;
    for (int k = 1; k < n; k++) {
        float dx = x[order[k - 1]] - x[order[k]];
        float dy = y[order[k - 1]] - y[order[k]];
        s += sqrtf(dx * dx + dy * dy);
        int zones;
        if (k == n - 1) {
            zones = 0;
        } else if (mn_turn_fined(x, y, order[k - 1], order[k], order[k + 1])) {
            zones = 1;
        } else {
            continue;
        }
        total += (float)zones * 2.0f * MN_SLOW_ZONE - mn_turn_overlap_of(last_zones, zones, s - last_s);
        last_s = s;
        last_zones = zones;
    }
    return total;
}

MN_API float mn_turn_slow_length(const float* x, const float* y, const int32_t* order, int32_t count) { return mn_turn_slow(x, y, order, count); }

float mn_per_slow_millimetre(void) { return MN_PER_SLOW_MILLIMETRE; }

MN_API float mn_turn_fine(const float* x, const float* y, const int32_t* order, int32_t count) { return mn_turn_slow(x, y, order, count) * MN_PER_SLOW_MILLIMETRE; }
