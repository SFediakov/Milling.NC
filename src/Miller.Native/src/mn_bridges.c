#include "mn_internal.h"

/* Holding bridges (tabs) of the separation scope. The trench cuts every part free at the floor; a
 * bridge is a strip of cut-through cells, `width` wide, where `height` of material stays above the
 * floor between the part and the stock that stands around it (the frame). Every freed part gets up to
 * `count` bridges, one per direction sector around its centroid (sector 0 centred on +X), each a
 * straight crossing of the cut band to the nearest frame cell from the part cell nearest the sector
 * centre among those whose crossing is at most one cutter diameter longer than the shortest in the
 * sector (the 50 percent reach rule narrows the band at convex corners, and shortest-first put the
 * bridges of two sectors into one corner). Tool positions whose footprint holds a bridge cell are
 * lifted to the bridge top (drop cutter), so the strategies, the simplifier and the check, which all
 * read the strategy tip, leave the bridge; the plan drops the lifted positions from the levels below
 * the bridge top and gains a level at the top, so a layer strategy cuts the bridge zone to it. */

#define MN_BRIDGE_TWO_PI 6.28318530717958647692f
#define MN_BRIDGE_PI 3.14159265358979323846f
/* Path samples per cell along a bridge centreline. */
#define MN_BRIDGE_SAMPLES_PER_CELL 4

typedef struct bridge_candidate {
    int cell;
    int sector;
    float deviation;
    float distance;
    int rejected;
} bridge_candidate;

/* The bridge workspace of one placement. */
typedef struct bridge_work {
    const mn_grid* g;
    const float* stock;
    const uint8_t* cut;
    const uint8_t* frame;
    const int* labels;
    const float* distance;
    float half_width;
    float longer_allowed;
    uint8_t* status;
    mn_ints* bridge;
} bridge_work;

static int sector_of(float angle, int count, float* deviation)
{
    float width = MN_BRIDGE_TWO_PI / (float)count;
    float turned = angle < 0.0f ? angle + MN_BRIDGE_TWO_PI : angle;
    int sector = mn_f2i(floorf((turned + width * 0.5f) / width)) % count;
    float off = fabsf(turned - (float)sector * width);
    *deviation = off > MN_BRIDGE_PI ? MN_BRIDGE_TWO_PI - off : off;
    return sector;
}

/* A cut-through 4-neighbour: the cell borders the band the trench leaves at the floor. */
static int borders_cut(const mn_grid* g, const uint8_t* cut, int cell)
{
    static const int di[4] = { 1, -1, 0, 0 };
    static const int dj[4] = { 0, 0, 1, -1 };
    int i = cell % g->width;
    int j = cell / g->width;
    for (int n = 0; n < 4; n++) {
        int ii = i + di[n];
        int jj = j + dj[n];
        if (mn_in_bounds(g, ii, jj) && cut[jj * g->width + ii]) {
            return 1;
        }
    }
    return 0;
}

/* The frame cell nearest to `cell` by centre distance (the first in row-major order on a tie), or -1
 * when none lies within `distance`. */
static int nearest_frame(const bridge_work* w, int cell, float distance)
{
    const mn_grid* g = w->g;
    int reach = mn_f2i(ceilf(distance / g->cell_size)) + 1;
    int ci = cell % g->width;
    int cj = cell / g->width;
    int best = -1;
    int best_squared = INT_MAX;
    for (int jj = mn_maxi(0, cj - reach); jj <= mn_mini(g->height - 1, cj + reach); jj++) {
        for (int ii = mn_maxi(0, ci - reach); ii <= mn_mini(g->width - 1, ci + reach); ii++) {
            int c = jj * g->width + ii;
            if (!w->frame[c]) {
                continue;
            }
            int squared = (ii - ci) * (ii - ci) + (jj - cj) * (jj - cj);
            if (squared < best_squared) {
                best_squared = squared;
                best = c;
            }
        }
    }
    return best;
}

static mn_v3 center_of(const mn_grid* g, int cell) { return mn_v3_make(mn_center_x(g, cell % g->width), mn_center_y(g, cell / g->width), 0.0f); }

/* The centreline from the part cell to the frame cell runs through the part, then through cut cells
 * only, and ends in the frame: no other standing piece, no cell without stock, no return into the part. */
static int path_clear(const bridge_work* w, int part, int from, int to)
{
    const mn_grid* g = w->g;
    mn_v3 a = center_of(g, from);
    mn_v3 b = center_of(g, to);
    float length = mn_v3_distance(a, b);
    int samples = mn_maxi(1, mn_f2i(ceilf(length / g->cell_size * (float)MN_BRIDGE_SAMPLES_PER_CELL)));
    int crossed = 0;
    for (int k = 1; k <= samples; k++) {
        mn_v3 p = mn_v3_lerp(a, b, (float)k / (float)samples);
        int i = mn_cell_i(g, p.x);
        int j = mn_cell_j(g, p.y);
        if (!mn_in_bounds(g, i, j)) {
            return 0;
        }
        int c = j * g->width + i;
        if (mn_isnan(w->stock[c])) {
            return 0;
        }
        if (w->frame[c]) {
            return 1;
        }
        if (w->labels[c] == part) {
            if (crossed) {
                return 0;
            }
            continue;
        }
        if (!w->cut[c]) {
            return 0;
        }
        crossed = 1;
    }
    return 0;
}

/* Whether the segment touches the closed square of a cell (Liang-Barsky clipping). A segment through a
 * cell corner touches all four cells there, so the marked centreline is 4-connected. */
static int segment_touches(mn_v3 a, mn_v3 b, float x0, float y0, float x1, float y1)
{
    float dx = b.x - a.x;
    float dy = b.y - a.y;
    float p[4] = { -dx, dx, -dy, dy };
    float q[4] = { a.x - x0, x1 - a.x, a.y - y0, y1 - a.y };
    float t0 = 0.0f;
    float t1 = 1.0f;
    for (int k = 0; k < 4; k++) {
        if (p[k] == 0.0f) {
            if (q[k] < 0.0f) {
                return 0;
            }
            continue;
        }
        float r = q[k] / p[k];
        if (p[k] < 0.0f) {
            if (r > t1) {
                return 0;
            }
            if (r > t0) {
                t0 = r;
            }
        } else {
            if (r < t0) {
                return 0;
            }
            if (r < t1) {
                t1 = r;
            }
        }
    }
    return 1;
}

/* Marks the cut cells of one bridge: centre within half the width of the centreline, or touched by it. */
static int mark_bridge(bridge_work* w, int from, int to)
{
    const mn_grid* g = w->g;
    mn_v3 a = center_of(g, from);
    mn_v3 b = center_of(g, to);
    int margin = mn_f2i(ceilf(w->half_width / g->cell_size)) + 1;
    int i0 = mn_maxi(0, mn_mini(from % g->width, to % g->width) - margin);
    int i1 = mn_mini(g->width - 1, mn_maxi(from % g->width, to % g->width) + margin);
    int j0 = mn_maxi(0, mn_mini(from / g->width, to / g->width) - margin);
    int j1 = mn_mini(g->height - 1, mn_maxi(from / g->width, to / g->width) + margin);
    float half = g->cell_size * 0.5f;
    int status = MN_OK;
    for (int j = j0; j <= j1 && status == MN_OK; j++) {
        for (int i = i0; i <= i1 && status == MN_OK; i++) {
            int c = j * g->width + i;
            if (!w->cut[c] || (w->status[c] & MN_CELL_BRIDGE) != 0) {
                continue;
            }
            mn_v3 p = center_of(g, c);
            if (mn_distance_to(p, a, b) <= w->half_width || segment_touches(a, b, p.x - half, p.y - half, p.x + half, p.y + half)) {
                w->status[c] |= MN_CELL_BRIDGE;
                status = mn_ints_push(w->bridge, c);
            }
        }
    }
    return status;
}

/* One bridge in one sector of a part; 1 when placed. Candidates that fail are rejected for good. */
static int place_in_sector(bridge_work* w, int part, bridge_candidate* candidates, int candidate_count, int sector, int* status)
{
    for (;;) {
        float shortest = INFINITY;
        for (int k = 0; k < candidate_count; k++) {
            const bridge_candidate* c = &candidates[k];
            if (c->sector == sector && !c->rejected && c->distance < shortest) {
                shortest = c->distance;
            }
        }
        if (!(shortest < INFINITY)) {
            return 0;
        }
        int best = -1;
        for (int k = 0; k < candidate_count; k++) {
            const bridge_candidate* c = &candidates[k];
            if (c->sector != sector || c->rejected || c->distance > shortest + w->longer_allowed) {
                continue;
            }
            if (best < 0 || c->deviation < candidates[best].deviation || (c->deviation == candidates[best].deviation && c->distance < candidates[best].distance)) {
                best = k;
            }
        }
        bridge_candidate* chosen = &candidates[best];
        int target = nearest_frame(w, chosen->cell, chosen->distance);
        if (target < 0 || !path_clear(w, part, chosen->cell, target)) {
            chosen->rejected = 1;
            continue;
        }
        *status = mark_bridge(w, chosen->cell, target);
        return *status == MN_OK;
    }
}

static int bridges_of_part(bridge_work* w, int part, const int* list, int size, int count, int* placed)
{
    const mn_grid* g = w->g;
    double sum_x = 0.0;
    double sum_y = 0.0;
    for (int m = 0; m < size; m++) {
        sum_x += (double)mn_center_x(g, list[m] % g->width);
        sum_y += (double)mn_center_y(g, list[m] / g->width);
    }
    float cx = (float)(sum_x / (double)size);
    float cy = (float)(sum_y / (double)size);

    bridge_candidate* candidates = (bridge_candidate*)mn_alloc((size_t)size, sizeof(bridge_candidate));
    if (candidates == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the bridge candidates of %d cells.", size);
    }
    int candidate_count = 0;
    for (int m = 0; m < size; m++) {
        int c = list[m];
        if (!borders_cut(g, w->cut, c)) {
            continue;
        }
        bridge_candidate* b = &candidates[candidate_count++];
        b->cell = c;
        b->distance = w->distance[c];
        b->sector = sector_of(atan2f(mn_center_y(g, c / g->width) - cy, mn_center_x(g, c % g->width) - cx), count, &b->deviation);
        b->rejected = 0;
    }
    int status = MN_OK;
    for (int s = 0; s < count && status == MN_OK; s++) {
        *placed += place_in_sector(w, part, candidates, candidate_count, s, &status);
    }
    free(candidates);
    return status;
}

/* The plan rule "a mask holds the positions whose tip reaches its level" over the lifted tip: below the
 * bridge top the lifted positions leave the masks, and unless a level lies at the top, one is inserted
 * there with the mask of the first level below it, so the bridge zone is cut to the top and no lower. */
static int relevel(mn_plan** plan, const float* stock, const float* lift, float top)
{
    mn_plan* old = *plan;
    int cells = mn_cells(&old->grid);
    int below = -1;
    int at = -1;
    for (int k = 0; k < old->count; k++) {
        if (fabsf(old->levels[k] - top) <= MN_LEVEL_TOLERANCE) {
            at = k;
        } else if (old->levels[k] < top && below < 0) {
            below = k;
        }
    }
    if (below < 0) {
        return MN_OK;
    }
    mn_plan* p;
    MN_CHECK(mn_plan_alloc(&old->grid, old->count + (at < 0 ? 1 : 0), &p));
    int n = 0;
    for (int k = 0; k < old->count; k++) {
        const uint8_t* source = old->masks + (size_t)k * (size_t)cells;
        if (at < 0 && k == below) {
            uint8_t* inserted = p->masks + (size_t)n * (size_t)cells;
            for (int c = 0; c < cells; c++) {
                inserted[c] = (uint8_t)(source[c] && !mn_isnan(stock[c]) && stock[c] > top);
            }
            p->levels[n++] = top;
        }
        uint8_t* mask = p->masks + (size_t)n * (size_t)cells;
        memcpy(mask, source, (size_t)cells);
        if (old->levels[k] < top - MN_LEVEL_TOLERANCE) {
            for (int c = 0; c < cells; c++) {
                if (!mn_isnan(lift[c]) && !mn_reachable_at_level(lift[c], old->levels[k])) {
                    mask[c] = 0;
                }
            }
        }
        p->levels[n++] = old->levels[k];
    }
    memcpy(p->coverage, old->coverage, (size_t)cells);
    p->lowest = old->lowest;
    mn_plan_free(old);
    *plan = p;
    return MN_OK;
}

int mn_bridges_place(const mn_grid* g, const float* stock, const float* model, const mn_profile* profile, float cutter_diameter, float floor, float width, float height, int count,
    float* tip, mn_plan** plan, uint8_t* status, mn_bridge_counts* counts)
{
    int cells = mn_cells(g);
    memset(counts, 0, sizeof(*counts));
    for (int k = 0; k < cells; k++) {
        status[k] &= (uint8_t)~MN_CELL_BRIDGE;
    }
    if (count <= 0) {
        return MN_OK;
    }

    float top = floor + height;
    uint8_t* cut = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* stands = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* frame = (uint8_t*)mn_alloc((size_t)cells, 1);
    int* labels = (int*)mn_alloc((size_t)cells, sizeof(int));
    float* distance = (float*)mn_alloc((size_t)cells, sizeof(float));
    float* lift = (float*)mn_alloc((size_t)cells, sizeof(float));
    mn_ints component_cells = { 0 };
    mn_ints component_offsets = { 0 };
    mn_ints bridge = { 0 };
    bridge_work work = { g, stock, cut, frame, labels, distance, width * 0.5f, cutter_diameter, status, &bridge };
    int pieces = 0;
    int result = MN_OK;
    if (cut == NULL || stands == NULL || frame == NULL || labels == NULL || distance == NULL || lift == NULL) {
        result = mn_fail(MN_ERR_MEMORY, "Out of memory for the bridges.");
        goto done;
    }

    /* Cut through: some position takes the cell down to the bridge top or below. */
    for (int p = 0; p < cells; p++) {
        float t = tip[p];
        if (mn_isnan(t) || t > top) {
            continue;
        }
        int pi = p % g->width;
        int pj = p / g->width;
        for (int o = 0; o < profile->offset_count; o++) {
            int ii = pi + profile->offsets[o].dx;
            int jj = pj + profile->offsets[o].dy;
            if (!mn_in_bounds(g, ii, jj)) {
                continue;
            }
            int c = jj * g->width + ii;
            if (!mn_isnan(stock[c]) && t + profile->offsets[o].dz <= top) {
                cut[c] = 1;
            }
        }
    }
    for (int k = 0; k < cells; k++) {
        stands[k] = (uint8_t)(!mn_isnan(stock[k]) && !cut[k]);
    }

    /* The standing pieces; a piece touching the grid border or a cell without stock is the frame. */
    result = mn_components(stands, g->width, g->height, labels, &component_cells, &component_offsets);
    if (result != MN_OK) {
        goto done;
    }
    pieces = component_offsets.count - 1;
    for (int c = 0; c < pieces; c++) {
        const int* list = component_cells.items + component_offsets.items[c];
        int size = component_offsets.items[c + 1] - component_offsets.items[c];
        if (mn_touches_outside(list, size, g, stock)) {
            for (int m = 0; m < size; m++) {
                frame[list[m]] = 1;
            }
        }
    }
    result = mn_distance_transform(frame, g->width, g->height, g->cell_size, distance);
    if (result != MN_OK) {
        goto done;
    }

    for (int c = 0; c < pieces && result == MN_OK; c++) {
        const int* list = component_cells.items + component_offsets.items[c];
        int size = component_offsets.items[c + 1] - component_offsets.items[c];
        if (frame[list[0]]) {
            continue;
        }
        int has_model = 0;
        for (int m = 0; m < size && !has_model; m++) {
            has_model = model[list[m]] > floor + MN_FLOOR_TOLERANCE;
        }
        if (!has_model) {
            continue;
        }
        counts->parts++;
        counts->wanted += count;
        result = bridges_of_part(&work, labels[list[0]], list, size, count, &counts->placed);
    }
    if (result != MN_OK) {
        goto done;
    }

    /* Drop cutter over the bridge top: no position lets its cutter below it over a bridge cell. */
    for (int k = 0; k < cells; k++) {
        lift[k] = NAN;
    }
    for (int b = 0; b < bridge.count; b++) {
        int q = bridge.items[b];
        int qi = q % g->width;
        int qj = q / g->width;
        for (int o = 0; o < profile->offset_count; o++) {
            int pi = qi - profile->offsets[o].dx;
            int pj = qj - profile->offsets[o].dy;
            if (!mn_in_bounds(g, pi, pj)) {
                continue;
            }
            int p = pj * g->width + pi;
            float z = top - profile->offsets[o].dz;
            if (mn_isnan(lift[p]) || z > lift[p]) {
                lift[p] = z;
            }
        }
    }
    mn_apply_limit(tip, lift, cells, tip);
    if (bridge.count > 0) {
        result = relevel(plan, stock, lift, top);
    }

done:
    free(cut);
    free(stands);
    free(frame);
    free(labels);
    free(distance);
    free(lift);
    mn_ints_free(&component_cells);
    mn_ints_free(&component_offsets);
    mn_ints_free(&bridge);
    return result;
}
