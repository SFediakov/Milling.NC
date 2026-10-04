#include "mn_internal.h"

/* Dynamic collision check (T-147) and its resolution (T-148). The check walks the toolpath over a
 * clone of the stock the way the simulation does (CollisionRecorder): feeds and plunges remove
 * material at CellSize / 2 along the segment, the head ring is tested against the stock as it stands
 * at every segment end and every max(CellSize, cutter radius) along it, the footprint of a rapid at
 * the same spacing. A cell already cut below the tool surface is no collision; the same cell uncut
 * is one. Every entered cell gets COLLISION; the resolution turns entered stock into SHOULD_REMOVE
 * and forbids positions whose head met the model or stock that cannot be removed. */

int mn_collisions_push(mn_collisions* list, mn_collision item)
{
    if (list->count == list->capacity) {
        int capacity = list->capacity == 0 ? 64 : list->capacity * 2;
        mn_collision* items = (mn_collision*)realloc(list->items, (size_t)capacity * sizeof(mn_collision));
        if (items == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for %d collisions.", capacity);
        }
        list->items = items;
        list->capacity = capacity;
    }
    list->items[list->count++] = item;
    return MN_OK;
}

void mn_collisions_free(mn_collisions* list)
{
    free(list->items);
    list->items = NULL;
    list->count = 0;
    list->capacity = 0;
}

int mn_hits_init(mn_hits* hits, int cells)
{
    memset(hits, 0, sizeof(*hits));
    hits->cells = cells;
    hits->position_z = (float*)mn_alloc((size_t)cells, sizeof(float));
    hits->position_flags = (uint8_t*)mn_alloc((size_t)cells, 1);
    hits->cell_height = (float*)mn_alloc((size_t)cells, sizeof(float));
    hits->cell_flags = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (hits->position_z == NULL || hits->position_flags == NULL || hits->cell_height == NULL || hits->cell_flags == NULL) {
        mn_hits_free(hits);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision hits of %d cells.", cells);
    }
    for (int k = 0; k < cells; k++) {
        hits->position_z[k] = NAN;
        hits->cell_height[k] = NAN;
    }
    return MN_OK;
}

void mn_hits_free(mn_hits* hits)
{
    free(hits->position_z);
    free(hits->position_flags);
    free(hits->cell_height);
    free(hits->cell_flags);
    memset(hits, 0, sizeof(*hits));
}

void mn_model_bits(const float* model, int cells, float floor, uint8_t* status)
{
    for (int k = 0; k < cells; k++) {
        if (model[k] > floor + MN_FLOOR_TOLERANCE) {
            status[k] |= MN_CELL_MODEL;
        } else {
            status[k] &= (uint8_t)~MN_CELL_MODEL;
        }
    }
}

/* ---- the walk ---- */

typedef struct mn_walk_state {
    const mn_grid* g;
    float* stock;
    const float* model;
    float floor;
    const mn_profile* profile;
    float cutter_length;
    float tolerance;
    uint8_t* status;
    mn_hits* hits;
    mn_collisions* events;
    int segment;
    int event_of_kind[2]; /* index into events for the current segment, -1 for none */
    int status_code;
} mn_walk_state;

static void record_hit(mn_walk_state* w, int kind, mn_v3 tip, int position, int c, float standing, float surface)
{
    float m = w->model[c];
    int model_hit = m > w->floor + MN_FLOOR_TOLERANCE && m > surface + w->tolerance;
    w->status[c] |= MN_CELL_COLLISION;
    if (w->hits != NULL) {
        mn_hits* h = w->hits;
        float closing = h->closing[c];
        int removable = !mn_isnan(closing) && closing + h->slack <= surface;
        int repeat = removable && (w->status[c] & MN_CELL_SHOULD_REMOVE) != 0;
        uint8_t flag = (uint8_t)(removable ? MN_HIT_STOCK : MN_HIT_MODEL) | (uint8_t)(repeat ? MN_HIT_REPEAT : 0) | (uint8_t)(model_hit ? MN_HIT_SURFACE : 0);
        if (h->cell_flags[c] == 0) {
            h->entered++;
        }
        if (!removable && (h->cell_flags[c] & MN_HIT_MODEL) == 0) {
            h->unremovable++;
        }
        h->cell_flags[c] |= flag;
        if (mn_isnan(h->cell_height[c]) || standing > h->cell_height[c]) {
            h->cell_height[c] = standing;
        }
        if (position >= 0) {
            h->position_flags[position] |= flag;
            if (mn_isnan(h->position_z[position]) || tip.z < h->position_z[position]) {
                h->position_z[position] = tip.z;
            }
        }
    }
    int index = w->event_of_kind[kind];
    if (index < 0) {
        mn_collision e;
        e.segment = w->segment;
        e.kind = kind;
        e.x = tip.x;
        e.y = tip.y;
        e.z = tip.z;
        e.stock_z = standing;
        e.surface = surface;
        e.cell = c;
        e.model = model_hit;
        if (w->status_code == MN_OK) {
            w->status_code = mn_collisions_push(w->events, e);
            if (w->status_code == MN_OK) {
                w->event_of_kind[kind] = w->events->count - 1;
            }
        }
    } else if (model_hit) {
        w->events->items[index].model = 1;
    }
}

/* One tool position: a rapid's footprint against the stock, then the head ring; the position is
 * the cell of the tool axis, -1 outside the grid. */
static void check_position(mn_walk_state* w, mn_v3 tip, int rapid)
{
    const mn_grid* g = w->g;
    int ci = mn_cell_i(g, tip.x);
    int cj = mn_cell_j(g, tip.y);
    int position = mn_in_bounds(g, ci, cj) ? cj * g->width + ci : -1;
    const mn_profile* p = w->profile;
    if (rapid) {
        for (int o = 0; o < p->offset_count; o++) {
            int i = ci + p->offsets[o].dx;
            int j = cj + p->offsets[o].dy;
            if (!mn_in_bounds(g, i, j)) {
                continue;
            }
            int c = j * g->width + i;
            float z = w->stock[c];
            float surface = tip.z + p->offsets[o].dz;
            if (z > surface + w->tolerance) {
                record_hit(w, MN_EVENT_RAPID, tip, position, c, z, surface);
            }
        }
    }
    float head_bottom = tip.z + w->cutter_length;
    for (int o = 0; o < p->annulus_count; o++) {
        int i = ci + p->annulus[o].dx;
        int j = cj + p->annulus[o].dy;
        if (!mn_in_bounds(g, i, j)) {
            continue;
        }
        int c = j * g->width + i;
        float z = w->stock[c];
        float underside = head_bottom + p->annulus[o].dz;
        if (z > underside + w->tolerance) {
            record_hit(w, MN_EVENT_HEAD, tip, position, c, z, underside);
        }
    }
}

static void stamp(mn_walk_state* w, mn_v3 tip)
{
    const mn_grid* g = w->g;
    int ci = mn_cell_i(g, tip.x);
    int cj = mn_cell_j(g, tip.y);
    const mn_profile* p = w->profile;
    for (int o = 0; o < p->offset_count; o++) {
        int i = ci + p->offsets[o].dx;
        int j = cj + p->offsets[o].dy;
        if (!mn_in_bounds(g, i, j)) {
            continue;
        }
        int c = j * g->width + i;
        float current = w->stock[c];
        float cut = tip.z + p->offsets[o].dz;
        if (!mn_isnan(current) && cut < current) {
            w->stock[c] = cut;
        }
    }
}

static mn_v3 point_at(mn_v3 start, mn_v3 end, float length, float distance)
{
    if (length <= 0) {
        return start;
    }
    return mn_v3_lerp(start, end, mn_clamp(distance / length, 0.0f, 1.0f));
}

int mn_check_path(const mn_segment* segments, int count, const mn_grid* g, const float* stock, const float* model, float floor, const mn_profile* profile, float cutter_radius,
    float cutter_length, float tolerance, uint8_t* status, mn_hits* hits, mn_collisions* events, const mn_monitor* monitor)
{
    int cells = mn_cells(g);
    float* clone = (float*)mn_alloc((size_t)(cells > 0 ? cells : 1), sizeof(float));
    if (clone == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the stock clone of the collision check.");
    }
    memcpy(clone, stock, (size_t)cells * sizeof(float));
    for (int k = 0; k < cells; k++) {
        status[k] &= (uint8_t)~MN_CELL_COLLISION;
    }
    mn_walk_state w;
    memset(&w, 0, sizeof(w));
    w.g = g;
    w.stock = clone;
    w.model = model;
    w.floor = floor;
    w.profile = profile;
    w.cutter_length = cutter_length;
    w.tolerance = tolerance;
    w.status = status;
    w.hits = hits;
    w.events = events;
    w.status_code = MN_OK;

    float sweep = g->cell_size / 2.0f;
    /* RapidSampleSpacing: at least once per cutter radius, never coarser than a cell. */
    float head_spacing = mn_max(g->cell_size, cutter_radius);

    for (int k = 0; k < count && w.status_code == MN_OK; k++) {
        if ((k & 255) == 0 && mn_cancelled(monitor)) {
            free(clone);
            return mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        }
        const mn_segment* s = &segments[k];
        mn_v3 start = mn_v3_make(s->start_x, s->start_y, s->start_z);
        mn_v3 end = mn_v3_make(s->end_x, s->end_y, s->end_z);
        float length = mn_v3_distance(start, end);
        w.segment = k;
        w.event_of_kind[0] = -1;
        w.event_of_kind[1] = -1;
        if (s->kind == MN_MOVE_RAPID) {
            check_position(&w, start, 1);
            for (float d = head_spacing; d < length; d += head_spacing) {
                check_position(&w, point_at(start, end, length, d), 1);
            }
            check_position(&w, end, 1);
            continue;
        }
        int steps = length > 0 ? mn_f2i(ceilf(length / sweep)) : 0;
        float since_head = 0.0f;
        for (int n = 0; n <= steps; n++) {
            float t = steps == 0 ? 0.0f : (float)n / (float)steps;
            mn_v3 tip = mn_v3_lerp(start, end, t);
            stamp(&w, tip);
            since_head += steps == 0 ? 0.0f : length / (float)steps;
            if (n == steps || since_head >= head_spacing) {
                check_position(&w, tip, 0);
                since_head = 0.0f;
            }
        }
        mn_report(monitor, k + 1, count, (float)(k + 1) / (float)count);
    }
    free(clone);
    return w.status_code;
}

MN_API int32_t mn_collision_check(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* stock, const float* model, float floor, const mn_tool* tool, float tolerance, uint8_t* status, mn_collision** events, int32_t* event_count)
{
    if (grid == NULL || stock == NULL || model == NULL || tool == NULL || status == NULL || events == NULL || event_count == NULL || (count > 0 && segments == NULL)) {
        return mn_fail(MN_ERR_ARGUMENT, "The collision check needs the segments, the grid, the stock, the model, the tool and the status.");
    }
    if (!(grid->width > 0 && grid->height > 0 && grid->cell_size > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The collision check needs a grid with positive size.");
    }
    if (!(tool->cutter_length > 0) || !(tolerance >= 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The collision check needs a positive cutter length and a tolerance of zero or more.");
    }
    mn_profile profile;
    MN_CHECK(mn_profile_build(tool, grid->cell_size, &profile));
    mn_model_bits(model, mn_cells(grid), floor, status);
    mn_collisions list = { 0 };
    int result = mn_check_path(segments, count, grid, stock, model, floor, &profile, tool->cutter_diameter / 2.0f, tool->cutter_length, tolerance, status, NULL, &list, NULL);
    mn_profile_free(&profile);
    if (result != MN_OK) {
        mn_collisions_free(&list);
        return result;
    }
    *events = list.items != NULL ? list.items : (mn_collision*)mn_alloc(1, sizeof(mn_collision));
    *event_count = list.count;
    return MN_OK;
}

/* ---- resolution ---- */

/* The closing of the effective tip per cell with the position that attains it and the next lowest
 * value from another position, so that raising one position gives the closing without it. */
static int closing_with_runner_up(const mn_grid* g, const float* effective, const mn_profile* profile, float* lowest, int* lowest_by, float* runner_up)
{
    int cells = mn_cells(g);
    for (int k = 0; k < cells; k++) {
        lowest[k] = NAN;
        lowest_by[k] = -1;
        runner_up[k] = NAN;
    }
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            int c = j * g->width + i;
            for (int o = 0; o < profile->offset_count; o++) {
                int qi = i - profile->offsets[o].dx;
                int qj = j - profile->offsets[o].dy;
                if (!mn_in_bounds(g, qi, qj)) {
                    continue;
                }
                int q = qj * g->width + qi;
                float t = effective[q];
                if (mn_isnan(t)) {
                    continue;
                }
                float value = t + profile->offsets[o].dz;
                if (mn_isnan(lowest[c]) || value < lowest[c]) {
                    runner_up[c] = lowest[c];
                    lowest[c] = value;
                    lowest_by[c] = q;
                } else if (mn_isnan(runner_up[c]) || value < runner_up[c]) {
                    runner_up[c] = value;
                }
            }
        }
    }
    return MN_OK;
}

/* The X rule for one position over the material that stays (`lowest`, the closing of the tip map
 * with the raised positions): the unremovable cells its head meets at tip `z` against `heights`
 * (the standing heights the check saw, or the closing itself), the model cells its cutter finishes
 * there, and the tip that clears. Returns 1 when the position is forbidden, filling `need`. */
static int forbid_position(const mn_grid* g, const mn_profile* profile, float cutter_length, const float* model, float floor, float tolerance, float ratio, int p, float z,
    const float* heights, const uint8_t* cell_flags, const float* lowest, float* need)
{
    int i = p % g->width;
    int j = p / g->width;
    float raise = NAN;
    int damaged = 0;
    int repeat = 0;
    for (int o = 0; o < profile->annulus_count; o++) {
        int ii = i + profile->annulus[o].dx;
        int jj = j + profile->annulus[o].dy;
        if (!mn_in_bounds(g, ii, jj)) {
            continue;
        }
        int c = jj * g->width + ii;
        float h = heights[c];
        float surface = z + cutter_length + profile->annulus[o].dz;
        if (mn_isnan(h) || !(h > surface + tolerance)) {
            continue;
        }
        float m = model[c];
        float here;
        int unremovable = cell_flags == NULL ? 1 : (cell_flags[c] & MN_HIT_MODEL) != 0;
        if (unremovable) {
            damaged++;
            /* What stays: the closing at best, never below the model, the standing height where no
             * position reaches the cell. */
            float stays = mn_isnan(lowest[c]) ? h : mn_max(m, lowest[c]);
            here = stays - profile->annulus[o].dz - cutter_length;
        } else if ((cell_flags[c] & MN_HIT_REPEAT) != 0) {
            repeat = 1;
            here = h - profile->annulus[o].dz - cutter_length;
        } else {
            continue;
        }
        if (mn_isnan(raise) || here > raise) {
            raise = here;
        }
    }
    if ((damaged == 0 && !repeat) || mn_isnan(raise)) {
        return 0;
    }
    int forbid = repeat;
    if (damaged > 0 && !forbid) {
        /* The model cells the cutter finishes at this position: its bottom reaches their surface. */
        int finished = 0;
        for (int o = 0; o < profile->offset_count; o++) {
            int ii = i + profile->offsets[o].dx;
            int jj = j + profile->offsets[o].dy;
            if (!mn_in_bounds(g, ii, jj)) {
                continue;
            }
            int c = jj * g->width + ii;
            float m = model[c];
            if (m > floor + MN_FLOOR_TOLERANCE && z + profile->offsets[o].dz <= m + tolerance) {
                finished++;
            }
        }
        forbid = !((float)finished > ratio * (float)damaged);
    }
    /* The next pass's path may run `tolerance` above its planned tip (simplifier) and the check
     * allows MN_COLLISION_TOLERANCE, so the head clears what stays by both. */
    *need = raise + tolerance + MN_COLLISION_TOLERANCE;
    return forbid;
}

/* Every position whose head meets the model that stays at best under the raised tips (the closing
 * of the tip map without the standing stock, whose terraces the cut scope draws again every pass)
 * is a collision the next pass cannot avoid; the X rule decides it now, round by round, so a
 * forbidden wall is cleared in one pass instead of one ring of positions per pass. */
#define MN_PROPAGATION_ROUNDS 8

static int propagate(const mn_grid* g, const mn_profile* profile, float cutter_length, const float* model, const float* effective, float floor, float tolerance, float ratio,
    uint8_t* status, float* raised, float* tip, float* lowest, int* lowest_by, float* runner_up, float* limit)
{
    int cells = mn_cells(g);
    int changed = 0;
    for (int round = 0; round < MN_PROPAGATION_ROUNDS; round++) {
        mn_apply_limit(effective, raised, cells, tip);
        closing_with_runner_up(g, tip, profile, lowest, lowest_by, runner_up);
        mn_head_limit_compute(g, lowest, profile->annulus, profile->annulus_count, cutter_length, limit);
        int added = 0;
        for (int p = 0; p < cells; p++) {
            float z = tip[p];
            if (mn_isnan(z) || mn_isnan(limit[p]) || !(limit[p] > z + MN_COLLISION_TOLERANCE)) {
                continue;
            }
            float need;
            if (!forbid_position(g, profile, cutter_length, model, floor, tolerance, ratio, p, z, lowest, NULL, lowest, &need)) {
                continue;
            }
            if (mn_isnan(raised[p]) || need > raised[p]) {
                raised[p] = need;
                status[p] |= MN_CELL_FORBIDDEN;
                added++;
            }
        }
        changed += added;
        if (added == 0) {
            break;
        }
    }
    return changed;
}

int mn_resolve(const mn_grid* g, const mn_profile* profile, float cutter_length, const float* model, const float* effective, const float* material, float floor, float tolerance,
    float ratio, const mn_hits* hits, uint8_t* status, float* raised)
{
    int cells = mn_cells(g);
    int changed = 0;
    for (int c = 0; c < cells; c++) {
        uint8_t flags = hits->cell_flags[c];
        if (flags == 0 || (flags & MN_HIT_MODEL) != 0 || (flags & MN_HIT_REPEAT) != 0) {
            continue;
        }
        /* Entered removable stock: should be removed, and no longer a collision. */
        if ((status[c] & MN_CELL_SHOULD_REMOVE) == 0) {
            changed++;
        }
        status[c] |= MN_CELL_SHOULD_REMOVE;
        status[c] &= (uint8_t)~MN_CELL_COLLISION;
    }

    float* lowest = (float*)mn_alloc((size_t)cells, sizeof(float));
    int* lowest_by = (int*)mn_alloc((size_t)cells, sizeof(int));
    float* runner_up = (float*)mn_alloc((size_t)cells, sizeof(float));
    float* tip = (float*)mn_alloc((size_t)cells, sizeof(float));
    float* limit = (float*)mn_alloc((size_t)cells, sizeof(float));
    if (lowest == NULL || lowest_by == NULL || runner_up == NULL || tip == NULL || limit == NULL) {
        free(lowest);
        free(lowest_by);
        free(runner_up);
        free(tip);
        free(limit);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision resolution.");
    }
    closing_with_runner_up(g, material, profile, lowest, lowest_by, runner_up);

    /* The positions the check caught: forbidden by the X rule over the heights they met. */
    for (int p = 0; p < cells; p++) {
        uint8_t flags = hits->position_flags[p];
        if ((flags & (MN_HIT_MODEL | MN_HIT_REPEAT)) == 0 || mn_isnan(effective[p])) {
            continue;
        }
        float need;
        if (!forbid_position(g, profile, cutter_length, model, floor, tolerance, ratio, p, hits->position_z[p], hits->cell_height, hits->cell_flags, lowest, &need)) {
            continue;
        }
        if (!(need > effective[p] + MN_FLOOR_TOLERANCE)) {
            continue;
        }
        if (mn_isnan(raised[p]) || need > raised[p]) {
            raised[p] = need;
            status[p] |= MN_CELL_FORBIDDEN;
            changed++;
        }
    }

    int propagated = propagate(g, profile, cutter_length, model, effective, floor, tolerance, ratio, status, raised, tip, lowest, lowest_by, runner_up, limit);
    free(lowest);
    free(lowest_by);
    free(runner_up);
    free(tip);
    free(limit);
    if (propagated < 0) {
        return propagated;
    }
    return changed + propagated;
}
