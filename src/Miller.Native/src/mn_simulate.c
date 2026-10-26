#include "mn_internal.h"

/* The simulation of the toolpath on the stock height map (MaterialRemover, CollisionDetector in C#
 * before T-159): the sweep of one segment part, the stamp of one tool position and the check of one
 * tip against the standing stock. The rules are those of the C# classes: the tip is sampled at most
 * cell / 2 apart along the part with both ends included, the tip sits on the centre of the cell it
 * falls in, every footprint cell of a sample takes min(stock, z + dz), a cell without material stays
 * so. Two things make it fast without changing a bit of the result: consecutive samples that fall in
 * one cell stamp once with their lowest z, which gives the same minimum over the same cells, and the
 * footprint is walked as row spans clipped to the grid, with one constant cut for a flat tip. */

/* Consecutive offsets of one dy with increasing dx: one row of the footprint. */
typedef struct mn_span {
    int dy;
    int dx0;
    int count;
    const mn_offset* first;
} mn_span;

typedef struct mn_footprint {
    mn_span* spans;
    int span_count;
    int flat;
} mn_footprint;

static int footprint_build(const mn_offset* offsets, int count, mn_footprint* fp)
{
    fp->spans = (mn_span*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(mn_span));
    fp->span_count = 0;
    fp->flat = 1;
    if (fp->spans == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a footprint of %d cells.", count);
    }
    for (int o = 0; o < count; o++) {
        if (offsets[o].dz != 0.0f) {
            fp->flat = 0;
        }
        mn_span* last = fp->span_count > 0 ? &fp->spans[fp->span_count - 1] : NULL;
        if (last != NULL && last->dy == offsets[o].dy && offsets[o].dx == last->dx0 + last->count) {
            last->count++;
            continue;
        }
        mn_span* span = &fp->spans[fp->span_count++];
        span->dy = offsets[o].dy;
        span->dx0 = offsets[o].dx;
        span->count = 1;
        span->first = &offsets[o];
    }
    return MN_OK;
}

static void dirty_clear(mn_dirty* d)
{
    d->i0 = INT_MAX;
    d->j0 = INT_MAX;
    d->i1 = INT_MIN;
    d->j1 = INT_MIN;
}

static void dirty_include_row(mn_dirty* d, int i0, int i1, int j)
{
    if (i0 < d->i0) {
        d->i0 = i0;
    }
    if (i1 > d->i1) {
        d->i1 = i1;
    }
    if (j < d->j0) {
        d->j0 = j;
    }
    if (j > d->j1) {
        d->j1 = j;
    }
}

/* One tool position at cell (ci, cj) with its tip at z; the cells that got lower join `dirty`. */
static void stamp_spans(float* stock, const mn_grid* g, const mn_footprint* fp, int ci, int cj, float z, mn_dirty* dirty)
{
    int width = g->width;
    for (int s = 0; s < fp->span_count; s++) {
        const mn_span* sp = &fp->spans[s];
        int j = cj + sp->dy;
        if (j < 0 || j >= g->height) {
            continue;
        }
        int i0 = ci + sp->dx0;
        int skip = 0;
        if (i0 < 0) {
            skip = -i0;
            i0 = 0;
        }
        int i1 = ci + sp->dx0 + sp->count - 1;
        if (i1 > width - 1) {
            i1 = width - 1;
        }
        if (i1 < i0) {
            continue;
        }
        float* row = stock + (size_t)j * (size_t)width;
        int first_changed = -1;
        int last_changed = -1;
        if (fp->flat) {
            for (int i = i0; i <= i1; i++) {
                if (z < row[i]) {
                    row[i] = z;
                    if (first_changed < 0) {
                        first_changed = i;
                    }
                    last_changed = i;
                }
            }
        } else {
            const mn_offset* o = sp->first + skip;
            for (int i = i0; i <= i1; i++, o++) {
                float cut = z + o->dz;
                if (cut < row[i]) {
                    row[i] = cut;
                    if (first_changed < 0) {
                        first_changed = i;
                    }
                    last_changed = i;
                }
            }
        }
        if (first_changed >= 0) {
            dirty_include_row(dirty, first_changed, last_changed, j);
        }
    }
}

static int check_arguments(const float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* point)
{
    if (stock == NULL || grid == NULL || point == NULL || (count > 0 && offsets == NULL) || count < 0) {
        return mn_fail(MN_ERR_ARGUMENT, "The simulation needs a stock map, a grid, a footprint and a tool position.");
    }
    if (!(grid->cell_size > 0) || grid->width <= 0 || grid->height <= 0) {
        return mn_fail(MN_ERR_ARGUMENT, "The grid must have a positive cell size and extent.");
    }
    return MN_OK;
}

MN_API int32_t mn_stamp(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* tip, mn_dirty* dirty)
{
    MN_CHECK(check_arguments(stock, grid, offsets, count, tip));
    dirty_clear(dirty);
    mn_footprint fp;
    MN_CHECK(footprint_build(offsets, count, &fp));
    stamp_spans(stock, grid, &fp, mn_cell_i(grid, tip[0]), mn_cell_j(grid, tip[1]), tip[2], dirty);
    free(fp.spans);
    return MN_OK;
}

static int segment_steps(mn_v3 a, mn_v3 b, float cell_size, float* length)
{
    *length = mn_v3_distance(a, b);
    return *length > 0 ? mn_f2i(ceilf(*length / (cell_size / 2.0f))) : 0;
}

MN_API int32_t mn_segment_steps(const float* start, const float* end, float cell_size, float* length)
{
    if (start == NULL || end == NULL || length == NULL || !(cell_size > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The sample count needs both ends, a positive cell size and a length output.");
    }
    return segment_steps(mn_v3_make(start[0], start[1], start[2]), mn_v3_make(end[0], end[1], end[2]), cell_size, length);
}

/* Samples first..last (clamped to 0..steps) of the segment; returns the step count. The samples are
 * those of the whole segment, so a segment swept in parts gets the same stamps as one swept at once. */
static int sweep_samples(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* start, const float* end, int first, int last, mn_dirty* dirty, float* last_tip)
{
    MN_CHECK(check_arguments(stock, grid, offsets, count, start));
    if (end == NULL) {
        return mn_fail(MN_ERR_ARGUMENT, "The sweep needs an end point.");
    }
    dirty_clear(dirty);
    mn_footprint fp;
    MN_CHECK(footprint_build(offsets, count, &fp));
    mn_v3 a = mn_v3_make(start[0], start[1], start[2]);
    mn_v3 b = mn_v3_make(end[0], end[1], end[2]);
    float length;
    int steps = segment_steps(a, b, grid->cell_size, &length);
    if (first < 0) {
        first = 0;
    }
    if (last > steps) {
        last = steps;
    }
    int pending = 0;
    int pci = 0;
    int pcj = 0;
    float pz = 0.0f;
    for (int s = first; s <= last; s++) {
        float t = steps == 0 ? 0.0f : (float)s / (float)steps;
        mn_v3 tip = mn_v3_lerp(a, b, t);
        int ci = mn_cell_i(grid, tip.x);
        int cj = mn_cell_j(grid, tip.y);
        if (pending && ci == pci && cj == pcj) {
            if (tip.z < pz) {
                pz = tip.z;
            }
            continue;
        }
        if (pending) {
            stamp_spans(stock, grid, &fp, pci, pcj, pz, dirty);
        }
        pending = 1;
        pci = ci;
        pcj = cj;
        pz = tip.z;
    }
    if (pending) {
        stamp_spans(stock, grid, &fp, pci, pcj, pz, dirty);
    }
    free(fp.spans);
    if (last_tip != NULL) {
        int at = last < 0 ? 0 : last;
        mn_v3 tip = steps == 0 ? a : mn_v3_lerp(a, b, (float)at / (float)steps);
        last_tip[0] = tip.x;
        last_tip[1] = tip.y;
        last_tip[2] = tip.z;
    }
    return steps;
}

MN_API int32_t mn_sweep_samples(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* start, const float* end, int32_t first, int32_t last, mn_dirty* dirty, float* last_tip)
{
    return sweep_samples(stock, grid, offsets, count, start, end, first, last, dirty, last_tip);
}

MN_API int32_t mn_sweep(float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t count, const float* from, const float* to, mn_dirty* dirty)
{
    int steps = sweep_samples(stock, grid, offsets, count, from, to, 0, INT32_MAX, dirty, NULL);
    return steps < 0 ? steps : MN_OK;
}

/* Entered cells of one tip in offset order: the footprint of a rapid (the tool bottom below the
 * stock), then the head annulus (the underside below the stock); returns their number, at most
 * `capacity`. The first entered cell gives the kind and the heights of the event. */
MN_API int32_t mn_check_tip(const float* stock, const mn_grid* grid, const mn_offset* offsets, int32_t offset_count, const mn_offset* annulus, int32_t annulus_count,
    float cutter_length, float tolerance, const float* tip, int32_t rapid, int32_t* cells, float* surfaces, int32_t capacity, int32_t* first_kind, float* first_stock,
    float* first_surface)
{
    MN_CHECK(check_arguments(stock, grid, offsets, offset_count, tip));
    if ((annulus_count > 0 && annulus == NULL) || annulus_count < 0 || cells == NULL || surfaces == NULL) {
        return mn_fail(MN_ERR_ARGUMENT, "The check needs the annulus and the output arrays.");
    }
    int ci = mn_cell_i(grid, tip[0]);
    int cj = mn_cell_j(grid, tip[1]);
    int entered = 0;
    *first_kind = -1;
    *first_stock = NAN;
    *first_surface = NAN;
    if (rapid) {
        for (int o = 0; o < offset_count; o++) {
            int i = ci + offsets[o].dx;
            int j = cj + offsets[o].dy;
            if (!mn_in_bounds(grid, i, j)) {
                continue;
            }
            int c = j * grid->width + i;
            float z = stock[c];
            float surface = tip[2] + offsets[o].dz;
            if (z > surface + tolerance) {
                if (entered == capacity) {
                    return mn_fail(MN_ERR_ARGUMENT, "The check found more than %d entered cells.", capacity);
                }
                if (entered == 0) {
                    *first_kind = MN_EVENT_RAPID;
                    *first_stock = z;
                    *first_surface = surface;
                }
                cells[entered] = c;
                surfaces[entered] = surface;
                entered++;
            }
        }
    }
    float head_bottom = tip[2] + cutter_length;
    for (int o = 0; o < annulus_count; o++) {
        int i = ci + annulus[o].dx;
        int j = cj + annulus[o].dy;
        if (!mn_in_bounds(grid, i, j)) {
            continue;
        }
        int c = j * grid->width + i;
        float z = stock[c];
        float underside = head_bottom + annulus[o].dz;
        if (z > underside + tolerance) {
            if (entered == capacity) {
                return mn_fail(MN_ERR_ARGUMENT, "The check found more than %d entered cells.", capacity);
            }
            if (entered == 0) {
                *first_kind = MN_EVENT_HEAD;
                *first_stock = z;
                *first_surface = underside;
            }
            cells[entered] = c;
            surfaces[entered] = underside;
            entered++;
        }
    }
    return entered;
}

/* ---- the simulation session (T-207) ---- */

struct mn_sim {
    mn_grid grid;
    mn_offset* offsets;
    mn_offset* annulus;
    mn_segment* segments;
    int segment_count;
    int min_dx;
    int max_dx;
    int min_dy;
    int max_dy;
    mn_walker* walker;
};

MN_API void mn_sim_close(mn_sim* session)
{
    if (session == NULL) {
        return;
    }
    mn_walker_close(session->walker);
    free(session->offsets);
    free(session->annulus);
    free(session->segments);
    free(session);
}

MN_API int32_t mn_sim_open(const mn_grid* grid, const mn_offset* offsets, int32_t offset_count, const mn_offset* annulus, int32_t annulus_count, float cutter_radius,
    float cutter_length, float tolerance, const mn_segment* segments, int32_t segment_count, mn_sim** session)
{
    if (grid == NULL || session == NULL || offset_count < 0 || annulus_count < 0 || segment_count < 0 || (offset_count > 0 && offsets == NULL)
        || (annulus_count > 0 && annulus == NULL) || (segment_count > 0 && segments == NULL)) {
        return mn_fail(MN_ERR_ARGUMENT, "The simulation session needs a grid, the footprint, the annulus, the segments and an output.");
    }
    if (!(grid->cell_size > 0) || grid->width <= 0 || grid->height <= 0) {
        return mn_fail(MN_ERR_ARGUMENT, "The grid must have a positive cell size and extent.");
    }
    if (!(cutter_radius > 0) || !(cutter_length > 0) || !(tolerance >= 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The simulation session needs a positive cutter radius and length and a tolerance of zero or more.");
    }
    mn_sim* s = (mn_sim*)calloc(1, sizeof(mn_sim));
    if (s == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the simulation session.");
    }
    s->grid = *grid;
    s->offsets = (mn_offset*)mn_alloc((size_t)(offset_count > 0 ? offset_count : 1), sizeof(mn_offset));
    s->annulus = (mn_offset*)mn_alloc((size_t)(annulus_count > 0 ? annulus_count : 1), sizeof(mn_offset));
    s->segments = (mn_segment*)mn_alloc((size_t)(segment_count > 0 ? segment_count : 1), sizeof(mn_segment));
    if (s->offsets == NULL || s->annulus == NULL || s->segments == NULL) {
        mn_sim_close(s);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the simulation session.");
    }
    if (offset_count > 0) {
        memcpy(s->offsets, offsets, (size_t)offset_count * sizeof(mn_offset));
    }
    if (annulus_count > 0) {
        memcpy(s->annulus, annulus, (size_t)annulus_count * sizeof(mn_offset));
    }
    if (segment_count > 0) {
        memcpy(s->segments, segments, (size_t)segment_count * sizeof(mn_segment));
    }
    s->segment_count = segment_count;
    s->min_dx = INT_MAX;
    s->max_dx = INT_MIN;
    s->min_dy = INT_MAX;
    s->max_dy = INT_MIN;
    for (int o = 0; o < offset_count; o++) {
        s->min_dx = mn_mini(s->min_dx, offsets[o].dx);
        s->max_dx = mn_maxi(s->max_dx, offsets[o].dx);
        s->min_dy = mn_mini(s->min_dy, offsets[o].dy);
        s->max_dy = mn_maxi(s->max_dy, offsets[o].dy);
    }
    mn_profile profile;
    profile.offsets = s->offsets;
    profile.offset_count = offset_count;
    profile.annulus = s->annulus;
    profile.annulus_count = annulus_count;
    int code = mn_walker_open(&s->grid, &profile, cutter_radius, cutter_length, tolerance, 1, &s->walker);
    if (code != MN_OK) {
        mn_sim_close(s);
        return code;
    }
    *session = s;
    return MN_OK;
}

MN_API int32_t mn_sim_walk(mn_sim* session, int32_t first, int32_t last, float* stock, const float* model, float floor, mn_collision** events, int32_t* event_count, mn_dirty* dirty)
{
    if (session == NULL || stock == NULL || model == NULL || events == NULL || event_count == NULL || dirty == NULL) {
        return mn_fail(MN_ERR_ARGUMENT, "The simulation walk needs a session, the stock, the model and the outputs.");
    }
    if (first < 0 || last > session->segment_count || first > last) {
        return mn_fail(MN_ERR_ARGUMENT, "The simulation walk covers segments 0 to %d, got %d to %d.", session->segment_count, (int)first, (int)last);
    }
    const mn_grid* g = &session->grid;
    dirty_clear(dirty);
    if (session->min_dx <= session->max_dx) {
        for (int k = first; k < last; k++) {
            const mn_segment* sg = &session->segments[k];
            if (sg->kind == MN_MOVE_RAPID) {
                continue;
            }
            int a = mn_cell_i(g, sg->start_x);
            int b = mn_cell_i(g, sg->end_x);
            int c = mn_cell_j(g, sg->start_y);
            int d = mn_cell_j(g, sg->end_y);
            int i0 = mn_maxi(mn_mini(a, b) + session->min_dx, 0);
            int i1 = mn_mini(mn_maxi(a, b) + session->max_dx, g->width - 1);
            int j0 = mn_maxi(mn_mini(c, d) + session->min_dy, 0);
            int j1 = mn_mini(mn_maxi(c, d) + session->max_dy, g->height - 1);
            if (i0 <= i1 && j0 <= j1) {
                dirty_include_row(dirty, i0, i1, j0);
                dirty_include_row(dirty, i0, i1, j1);
            }
        }
    }
    mn_collisions list = { 0 };
    double probing = 0.0;
    double walking = 0.0;
    int code = mn_walker_run(session->walker, session->segments, first, last, stock, model, floor, NULL, NULL, &list, NULL, &probing, &walking);
    if (code != MN_OK) {
        mn_collisions_free(&list);
        return code;
    }
    *events = list.items != NULL ? list.items : (mn_collision*)mn_alloc(1, sizeof(mn_collision));
    *event_count = list.count;
    return MN_OK;
}
