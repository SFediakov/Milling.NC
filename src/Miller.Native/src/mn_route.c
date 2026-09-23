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

/* `first` must be a number; NaN candidates never win. */
static float max2(float first, float second) { return second > first ? second : first; }

static float max3(float first, float second, float third) { return max2(max2(first, second), third); }

/* Highest plateau among the cells touching the point: its own cell and the cell on the other side
 * of every grid line the point lies on. */
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
            float value = plateau(grid, ii, jj);
            if (mn_isnan(best) || value > best) {
                best = value;
            }
        }
    }
    return best;
}

/* The polyline after `a` (excluded) up to `b` (included), appended to `points` when given, and the
 * vertical travel along it: rise in place, every cell-edge crossing lifted to the highest plateau
 * touching it and never below the straight line, descent in place over b. */
float mn_trace(const mn_route_grid* grid, mn_v3 a, mn_v3 b, mn_points* points, int* status)
{
    const mn_grid* g = &grid->g;
    float cell = g->cell_size;
    int i = mn_cell_i(g, a.x);
    int j = mn_cell_j(g, a.y);
    int i_end = mn_cell_i(g, b.x);
    int j_end = mn_cell_j(g, b.y);
    float z = a.z;
    float climb = 0.0f;
    float current = plateau(grid, i, j);
    float start = max3(z, current, touching(grid, a.x, a.y));
    *status = MN_OK;
    if (start > z) {
        climb += start - z;
        z = start;
        if (points != NULL && *status == MN_OK) {
            *status = mn_points_push(points, mn_v3_make(a.x, a.y, z));
        }
    }

    if (i != i_end || j != j_end) {
        float dx = b.x - a.x;
        float dy = b.y - a.y;
        int step_i = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        int step_j = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        float t_delta_x = step_i == 0 ? INFINITY : cell / fabsf(dx);
        float t_delta_y = step_j == 0 ? INFINITY : cell / fabsf(dy);
        float t_max_x = step_i == 0 ? INFINITY : (g->origin_x + (float)(step_i > 0 ? i + 1 : i) * cell - a.x) / dx;
        float t_max_y = step_j == 0 ? INFINITY : (g->origin_y + (float)(step_j > 0 ? j + 1 : j) * cell - a.y) / dy;
        int remaining = abs(i_end - i) + abs(j_end - j) + 2;
        while ((i != i_end || j != j_end) && remaining-- > 0) {
            float t;
            if (t_max_x < t_max_y) {
                t = t_max_x;
                i += step_i;
                t_max_x += t_delta_x;
            } else if (t_max_y < t_max_x) {
                t = t_max_y;
                j += step_j;
                t_max_y += t_delta_y;
            } else {
                t = t_max_x;
                i += step_i;
                j += step_j;
                t_max_x += t_delta_x;
                t_max_y += t_delta_y;
            }

            if (t > 1.0f) {
                break;
            }

            float px = a.x + dx * t;
            float py = a.y + dy * t;
            float next = plateau(grid, i, j);
            float lifted = max2(max3(a.z + (b.z - a.z) * t, current, next), touching(grid, px, py));
            climb += fabsf(lifted - z);
            z = lifted;
            if (points != NULL && *status == MN_OK) {
                *status = mn_points_push(points, mn_v3_make(px, py, z));
            }
            current = next;
        }
    }

    float above = max2(max3(b.z, current, plateau(grid, i_end, j_end)), touching(grid, b.x, b.y));
    climb += fabsf(above - z);
    z = above;
    if (z > b.z) {
        if (points != NULL && *status == MN_OK) {
            *status = mn_points_push(points, mn_v3_make(b.x, b.y, z));
        }
        climb += z - b.z;
    }
    if (points != NULL && *status == MN_OK) {
        *status = mn_points_push(points, b);
    }
    return climb;
}

MN_API float mn_surface_trace(const mn_grid* grid, const float* floor, const float* a, const float* b, float** points, int32_t* point_count)
{
    mn_route_grid route = { *grid, floor };
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
    mn_route_grid route = { *grid, floor };
    return mn_cost_exact(&route, mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2]));
}

MN_API float mn_route_lower_bound(const float* a, const float* b) { return mn_cost_lower_bound(mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2])); }

MN_API float mn_route_planar(const float* a, const float* b) { return mn_cost_planar(mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2])); }

/* ---- turn fine ---- */

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
    *side = cross > 0 ? 1 : cross < 0 ? -1 : 0;
    return 1;
}

/* Whether `test` lies within the tolerance of the circle through i, j and k, taken in index order. */
static int on_circle(const float* x, const float* y, int i, int j, int k, int test, float tolerance)
{
    int swap;
    if (i > j) {
        swap = i;
        i = j;
        j = swap;
    }
    if (j > k) {
        swap = j;
        j = k;
        k = swap;
    }
    if (i > j) {
        swap = i;
        i = j;
        j = swap;
    }
    double bx = (double)(x[j] - x[i]);
    double by = (double)(y[j] - y[i]);
    double cx = (double)(x[k] - x[i]);
    double cy = (double)(y[k] - y[i]);
    double d = 2.0 * (bx * cy - by * cx);
    if (d == 0.0) {
        return 0;
    }
    double b2 = bx * bx + by * by;
    double c2 = cx * cx + cy * cy;
    double ox = (cy * b2 - by * c2) / d;
    double oy = (bx * c2 - cx * b2) / d;
    double radius = sqrt(ox * ox + oy * oy);
    double tx = (double)(x[test] - x[i]) - ox;
    double ty = (double)(y[test] - y[i]) - oy;
    return fabs(sqrt(tx * tx + ty * ty) - radius) <= (double)tolerance;
}

int mn_arc_between(const float* x, const float* y, float tolerance, int q0, int q1, int q2, int q3)
{
    float cos1, cos2;
    int side1, side2;
    if (!mn_turn_between(x, y, q0, q1, q2, &cos1, &side1) || !mn_turn_between(x, y, q1, q2, q3, &cos2, &side2)) {
        return 0;
    }
    if (side1 == 0 || side1 != side2 || !(cos1 > MN_COS_CIRCULAR_MAX) || !(cos2 > MN_COS_CIRCULAR_MAX)) {
        return 0;
    }
    return on_circle(x, y, q0, q1, q2, q3, tolerance) && on_circle(x, y, q1, q2, q3, q0, tolerance);
}

/* A route order read as one forward piece, without a cache. */
static void order_view(mn_view* view, const float* x, const float* y, float cell_size, const int* order, int count)
{
    mn_view_reset(view, order, NULL, x, y, cell_size, NULL);
    mn_view_add(view, 0, count - 1, 0);
}

MN_API int32_t mn_turn_fined_at(const float* x, const float* y, float cell_size, const int32_t* order, int32_t count, int32_t position)
{
    if (count < 3 || position < 0 || position >= count) {
        return 0;
    }
    mn_view view;
    order_view(&view, x, y, cell_size, order, count);
    return mn_view_fined(&view, position);
}

float mn_turn_overlap_of(int zones_a, int zones_b, float gap) { return mn_max(0.0f, MN_SLOW_ZONE * (float)(zones_a + zones_b) - gap); }

MN_API float mn_turn_overlap(int32_t zones_a, int32_t zones_b, float gap) { return mn_turn_overlap_of(zones_a, zones_b, gap); }

/* Slow XY length of a route: 2 x SlowZone per fined turn less the overlap of every two consecutive
 * events, the route start and end being events without a zone. */
float mn_turn_slow(const float* x, const float* y, float cell_size, const int* order, int n)
{
    if (n < 3) {
        return 0.0f;
    }
    mn_view view;
    order_view(&view, x, y, cell_size, order, n);
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
        } else if (mn_view_fined(&view, k)) {
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

MN_API float mn_turn_slow_length(const float* x, const float* y, float cell_size, const int32_t* order, int32_t count)
{
    return mn_turn_slow(x, y, cell_size, order, count);
}

float mn_per_slow_millimetre(void) { return MN_PER_SLOW_MILLIMETRE; }

MN_API float mn_turn_fine(const float* x, const float* y, float cell_size, const int32_t* order, int32_t count)
{
    return mn_turn_slow(x, y, cell_size, order, count) * MN_PER_SLOW_MILLIMETRE;
}
