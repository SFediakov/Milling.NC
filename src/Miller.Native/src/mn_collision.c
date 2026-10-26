#include "mn_gpu.h"
#include "mn_thread.h"

/* Dynamic collision check (T-147). The check walks the toolpath over a clone of the stock the way the
 * simulation does (CollisionRecorder): feeds and plunges remove material at CellSize / 2 along the
 * segment, the head ring is tested against the stock as it stands at every segment end and every
 * max(CellSize, cutter radius) along it, the footprint of a rapid at the same spacing. A cell already
 * cut below the tool surface is no collision; the same cell uncut is one. Every entered cell gets
 * COLLISION.
 *
 * The walk runs in batches of segments on the workers (T-187), with the same result as one walk: every
 * worker walks the whole batch in order over its own grid rows (every `bands`-th row), so each cell
 * meets the same removals and tests in the same order as in one walk. A batch in which no worker finds
 * a hit has recorded nothing and is done; a batch with a hit gets its stock back and runs as one walk,
 * which records the hits in their order.
 *
 * The walker (T-207) holds what the walk needs besides the stock: the footprint and annulus rows, the
 * worker pool and the backup of the rows a batch reaches. A generation pass opens one per check; the
 * simulation opens one per loaded toolpath (mn_sim_open) and walks with it until it is unloaded. */

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

/* The offsets of a profile list grouped by row: the profile lists them row by row (dy ascending), so
 * the offsets of row dy are first[dy - min_dy] .. first[dy - min_dy + 1] - 1. */
typedef struct mn_offset_rows {
    int min_dy;
    int max_dy;
    int* first;
} mn_offset_rows;

static int offset_rows_build(const mn_offset* offsets, int count, mn_offset_rows* rows)
{
    rows->min_dy = count > 0 ? offsets[0].dy : 0;
    rows->max_dy = count > 0 ? offsets[count - 1].dy : -1;
    int span = rows->max_dy - rows->min_dy + 1;
    rows->first = (int*)mn_alloc((size_t)(span > 0 ? span : 0) + 1, sizeof(int));
    if (rows->first == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision check.");
    }
    int o = 0;
    for (int r = 0; r <= span; r++) {
        while (o < count && offsets[o].dy < rows->min_dy + r) {
            o++;
        }
        rows->first[r] = o;
    }
    for (int k = 1; k < count; k++) {
        if (offsets[k].dy < offsets[k - 1].dy) {
            free(rows->first);
            rows->first = NULL;
            return mn_fail(MN_ERR_STATE, "The tool profile is not listed row by row.");
        }
    }
    return MN_OK;
}

typedef struct mn_walk_state {
    const mn_grid* g;
    float* stock;
    const float* model;
    float floor;
    const mn_profile* profile;
    const mn_offset_rows* offset_rows;
    const mn_offset_rows* annulus_rows;
    float cutter_length;
    float tolerance;
    uint8_t* status;
    mn_hits* hits;
    mn_collisions* events;
    int segment;
    int event_of_kind[2]; /* index into events for the current segment, -1 for none */
    int status_code;
    /* A band (T-187): the walk stamps and tests the grid rows j with j % bands == band only, and only
     * finds whether a cell is hit, recording nothing; one band of one is the whole walk. */
    int band;
    int bands;
    int probed;
    /* The simulation's rule (T-207): a check position records its first entered cell only, which
     * gives the kind and the heights of its event; the generation records every entered cell. */
    int first_hit_only;
} mn_walk_state;

static void record_hit(mn_walk_state* w, int kind, mn_v3 tip, int position, int c, float standing, float surface)
{
    float m = w->model[c];
    int model_hit = m > w->floor + MN_FLOOR_TOLERANCE && m > surface + w->tolerance;
    if (w->status != NULL) {
        w->status[c] |= MN_CELL_COLLISION;
    }
    if (w->hits != NULL) {
        mn_hits* h = w->hits;
        float closing = h->closing[c];
        int removable = !mn_isnan(closing) && closing + h->slack <= surface;
        uint8_t flag = (uint8_t)(removable ? MN_HIT_STOCK : MN_HIT_MODEL) | (uint8_t)(model_hit ? MN_HIT_SURFACE : 0);
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

/* The first offset row of the walker's band for a tool axis in grid row cj; the rows follow every
 * `bands`-th. */
static int band_first_row(const mn_walk_state* w, const mn_offset_rows* rows, int cj)
{
    if (w->bands <= 1) {
        return rows->min_dy;
    }
    int residue = ((cj + rows->min_dy) % w->bands + w->bands) % w->bands;
    return rows->min_dy + (w->band - residue + w->bands) % w->bands;
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
    int step = w->bands > 1 ? w->bands : 1;
    if (rapid) {
        const mn_offset_rows* rows = w->offset_rows;
        for (int dy = band_first_row(w, rows, cj); dy <= rows->max_dy; dy += step) {
            for (int o = rows->first[dy - rows->min_dy]; o < rows->first[dy - rows->min_dy + 1]; o++) {
                int i = ci + p->offsets[o].dx;
                int j = cj + p->offsets[o].dy;
                if (!mn_in_bounds(g, i, j)) {
                    continue;
                }
                int c = j * g->width + i;
                float z = w->stock[c];
                float surface = tip.z + p->offsets[o].dz;
                if (z > surface + w->tolerance) {
                    if (w->bands > 1) {
                        w->probed = 1;
                        return;
                    }
                    record_hit(w, MN_EVENT_RAPID, tip, position, c, z, surface);
                    if (w->first_hit_only) {
                        return;
                    }
                }
            }
        }
    }
    float head_bottom = tip.z + w->cutter_length;
    const mn_offset_rows* rows = w->annulus_rows;
    for (int dy = band_first_row(w, rows, cj); dy <= rows->max_dy; dy += step) {
        for (int o = rows->first[dy - rows->min_dy]; o < rows->first[dy - rows->min_dy + 1]; o++) {
            int i = ci + p->annulus[o].dx;
            int j = cj + p->annulus[o].dy;
            if (!mn_in_bounds(g, i, j)) {
                continue;
            }
            int c = j * g->width + i;
            float z = w->stock[c];
            float underside = head_bottom + p->annulus[o].dz;
            if (z > underside + w->tolerance) {
                if (w->bands > 1) {
                    w->probed = 1;
                    return;
                }
                record_hit(w, MN_EVENT_HEAD, tip, position, c, z, underside);
                if (w->first_hit_only) {
                    return;
                }
            }
        }
    }
}

static void stamp(mn_walk_state* w, mn_v3 tip)
{
    const mn_grid* g = w->g;
    int ci = mn_cell_i(g, tip.x);
    int cj = mn_cell_j(g, tip.y);
    const mn_profile* p = w->profile;
    const mn_offset_rows* rows = w->offset_rows;
    int step = w->bands > 1 ? w->bands : 1;
    for (int dy = band_first_row(w, rows, cj); dy <= rows->max_dy; dy += step) {
        for (int o = rows->first[dy - rows->min_dy]; o < rows->first[dy - rows->min_dy + 1]; o++) {
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
}

static mn_v3 point_at(mn_v3 start, mn_v3 end, float length, float distance)
{
    if (length <= 0) {
        return start;
    }
    return mn_v3_lerp(start, end, mn_clamp(distance / length, 0.0f, 1.0f));
}

/* One segment: a rapid's footprint and head at its ends and every head spacing between, a feed's
 * removal at every half cell with the head at its end and every head spacing. */
static void walk_segment(mn_walk_state* w, const mn_segment* s, int k, float sweep, float head_spacing)
{
    mn_v3 start = mn_v3_make(s->start_x, s->start_y, s->start_z);
    mn_v3 end = mn_v3_make(s->end_x, s->end_y, s->end_z);
    float length = mn_v3_distance(start, end);
    w->segment = k;
    w->event_of_kind[0] = -1;
    w->event_of_kind[1] = -1;
    if (s->kind == MN_MOVE_RAPID) {
        check_position(w, start, 1);
        for (float d = head_spacing; d < length && !w->probed; d += head_spacing) {
            check_position(w, point_at(start, end, length, d), 1);
        }
        check_position(w, end, 1);
        return;
    }
    int steps = length > 0 ? mn_f2i(ceilf(length / sweep)) : 0;
    float since_head = 0.0f;
    for (int n = 0; n <= steps && !w->probed; n++) {
        float t = steps == 0 ? 0.0f : (float)n / (float)steps;
        mn_v3 tip = mn_v3_lerp(start, end, t);
        stamp(w, tip);
        since_head += steps == 0 ? 0.0f : length / (float)steps;
        if (n == steps || since_head >= head_spacing) {
            check_position(w, tip, 0);
            since_head = 0.0f;
        }
    }
}

/* The samples a segment takes, for the batch size. */
static int64_t segment_work(const mn_segment* s, float sweep, float head_spacing)
{
    float length = mn_v3_distance(mn_v3_make(s->start_x, s->start_y, s->start_z), mn_v3_make(s->end_x, s->end_y, s->end_z));
    float spacing = s->kind == MN_MOVE_RAPID ? head_spacing : sweep;
    return 2 + (length > 0 ? (int64_t)(length / spacing) : 0);
}

/* The samples of one batch: many joins cost little, and a batch with a hit runs as one walk. */
#define MN_CHECK_BATCH_SAMPLES 65536

typedef struct check_job {
    const mn_walk_state* walk;
    const mn_segment* segments;
    int first;
    int last;
    float sweep;
    float head_spacing;
    int bands;
    float* backup;
    int saving;
    /* The rows the batch's removals can reach: only those are saved. */
    int row_first;
    int row_last;
    uint8_t* flags;
} check_job;

/* Saves (or puts back) one share of the stock rows the batch can reach. */
static void backup_row(void* context, int row, void* scratch)
{
    (void)scratch;
    const check_job* job = (const check_job*)context;
    const mn_grid* g = job->walk->g;
    int span = job->row_last - job->row_first + 1;
    int rows = (span + job->bands - 1) / job->bands;
    int j0 = job->row_first + row * rows;
    int j1 = mn_mini(j0 + rows, job->row_last + 1);
    if (j1 <= j0) {
        return;
    }
    size_t at = (size_t)j0 * (size_t)g->width;
    size_t bytes = (size_t)(j1 - j0) * (size_t)g->width * sizeof(float);
    if (job->saving) {
        memcpy(job->backup + at, job->walk->stock + at, bytes);
    } else {
        memcpy(job->walk->stock + at, job->backup + at, bytes);
    }
}

/* The walk of one band over the batch. */
static void band_row(void* context, int row, void* scratch)
{
    (void)scratch;
    const check_job* job = (const check_job*)context;
    mn_walk_state w = *job->walk;
    w.band = row;
    w.bands = job->bands;
    w.probed = 0;
    for (int k = job->first; k < job->last && !w.probed; k++) {
        walk_segment(&w, &job->segments[k], k, job->sweep, job->head_spacing);
    }
    job->flags[row] = (uint8_t)w.probed;
}

struct mn_walker {
    const mn_grid* g;
    mn_profile profile;
    mn_offset_rows offset_rows;
    mn_offset_rows annulus_rows;
    float sweep;
    float head_spacing;
    float cutter_length;
    float tolerance;
    int first_hit_only;
    int workers;
    mn_pool* pool;
    float* backup;
};

void mn_walker_close(mn_walker* walker)
{
    if (walker == NULL) {
        return;
    }
    mn_pool_destroy(walker->pool);
    free(walker->backup);
    free(walker->offset_rows.first);
    free(walker->annulus_rows.first);
    free(walker);
}

int mn_walker_open(const mn_grid* g, const mn_profile* profile, float cutter_radius, float cutter_length, float tolerance, int first_hit_only, mn_walker** result)
{
    mn_walker* walker = (mn_walker*)calloc(1, sizeof(mn_walker));
    if (walker == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision check.");
    }
    walker->g = g;
    walker->profile = *profile;
    walker->offset_rows.max_dy = -1;
    walker->annulus_rows.max_dy = -1;
    int code = offset_rows_build(profile->offsets, profile->offset_count, &walker->offset_rows);
    if (code == MN_OK) {
        code = offset_rows_build(profile->annulus, profile->annulus_count, &walker->annulus_rows);
    }
    if (code != MN_OK) {
        mn_walker_close(walker);
        return code;
    }
    walker->sweep = g->cell_size / 2.0f;
    /* RapidSampleSpacing: at least once per cutter radius, never coarser than a cell. */
    walker->head_spacing = mn_max(g->cell_size, cutter_radius);
    walker->cutter_length = cutter_length;
    walker->tolerance = tolerance;
    walker->first_hit_only = first_hit_only;
    walker->workers = mn_mini(mn_worker_count(), g->height);
    *result = walker;
    return MN_OK;
}

/* Segments [first_segment, count) over `stock` in place; the pool and the backup are made on the first
 * walk and kept. The band probing and the walking seconds go to the caller. */
int mn_walker_run(mn_walker* walker, const mn_segment* segments, int first_segment, int count, float* stock, const float* model, float floor, uint8_t* status,
    mn_hits* hits, mn_collisions* events, const mn_monitor* monitor, double* probing_seconds, double* walking_seconds)
{
    const mn_grid* g = walker->g;
    int cells = mn_cells(g);
    int code = MN_OK;
    if (walker->workers > 1 && walker->pool == NULL) {
        if (walker->backup == NULL) {
            walker->backup = (float*)mn_alloc((size_t)(cells > 0 ? cells : 1), sizeof(float));
        }
        code = walker->backup == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the collision check.") : mn_pool_create(walker->workers, NULL, &walker->pool);
    }
    mn_walk_state w;
    memset(&w, 0, sizeof(w));
    w.g = g;
    w.stock = stock;
    w.model = model;
    w.floor = floor;
    w.profile = &walker->profile;
    w.offset_rows = &walker->offset_rows;
    w.annulus_rows = &walker->annulus_rows;
    w.cutter_length = walker->cutter_length;
    w.tolerance = walker->tolerance;
    w.status = status;
    w.hits = hits;
    w.events = events;
    w.status_code = MN_OK;
    w.bands = 1;
    w.first_hit_only = walker->first_hit_only;

    float sweep = walker->sweep;
    float head_spacing = walker->head_spacing;
    int workers = walker->workers;
    mn_pool* pool = walker->pool;
    const mn_offset_rows* offset_rows = &walker->offset_rows;
    uint8_t flags[MN_MAX_WORKERS];
    check_job job;
    job.walk = &w;
    job.segments = segments;
    job.sweep = sweep;
    job.head_spacing = head_spacing;
    job.bands = workers;
    job.backup = walker->backup;
    job.flags = flags;

    double probing = 0.0;
    double walking = 0.0;
    for (int first = first_segment; first < count && code == MN_OK && w.status_code == MN_OK;) {
        if (mn_cancelled(monitor)) {
            code = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            break;
        }
        int last = first;
        int64_t work = 0;
        while (last < count && (last == first || work < MN_CHECK_BATCH_SAMPLES)) {
            work += segment_work(&segments[last], sweep, head_spacing);
            last++;
        }
        int hit = 1;
        if (pool != NULL) {
            double started = mn_seconds();
            job.first = first;
            job.last = last;
            job.saving = 1;
            /* The rows of the batch's points, grown by the footprint's rows. */
            float low = INFINITY;
            float high = -INFINITY;
            for (int k = first; k < last; k++) {
                low = mn_min(low, mn_min(segments[k].start_y, segments[k].end_y));
                high = mn_max(high, mn_max(segments[k].start_y, segments[k].end_y));
            }
            job.row_first = mn_clampi(mn_cell_j(g, low) - mn_maxi(-offset_rows->min_dy, offset_rows->max_dy) - 1, 0, g->height - 1);
            job.row_last = mn_clampi(mn_cell_j(g, high) + mn_maxi(-offset_rows->min_dy, offset_rows->max_dy) + 1, 0, g->height - 1);
            mn_pool_run(pool, 0, workers, backup_row, &job);
            mn_pool_run(pool, 0, workers, band_row, &job);
            hit = 0;
            for (int b = 0; b < workers; b++) {
                hit |= flags[b];
            }
            if (hit) {
                job.saving = 0;
                mn_pool_run(pool, 0, workers, backup_row, &job);
            }
            probing += mn_seconds() - started;
        }
        double started = mn_seconds();
        for (int k = first; hit && k < last && w.status_code == MN_OK; k++) {
            walk_segment(&w, &segments[k], k, sweep, head_spacing);
        }
        walking += mn_seconds() - started;
        mn_report(monitor, last, count, (float)last / (float)count);
        first = last;
    }
    *probing_seconds = probing;
    *walking_seconds = walking;
    return code != MN_OK ? code : w.status_code;
}

int mn_check_path(const mn_segment* segments, int count, const mn_grid* g, const float* stock, const float* model, float floor, const mn_profile* profile, float cutter_radius,
    float cutter_length, float tolerance, uint8_t* status, mn_hits* hits, mn_collisions* events, const mn_monitor* monitor, float* final_stock)
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
    mn_walker* walker = NULL;
    double probing = 0.0;
    double walking = 0.0;
    int code = mn_walker_open(g, profile, cutter_radius, cutter_length, tolerance, 0, &walker);
    if (code == MN_OK) {
        code = mn_walker_run(walker, segments, 0, count, clone, model, floor, status, hits, events, monitor, &probing, &walking);
    }
    mn_time_add(MN_TIME_CHECK_BANDS, probing);
    mn_time_add(MN_TIME_CHECK_WALK, walking);
    mn_walker_close(walker);
    if (code == MN_OK && final_stock != NULL) {
        memcpy(final_stock, clone, (size_t)cells * sizeof(float));
    }
    free(clone);
    return code;
}

typedef struct intended_job {
    const mn_plan* plan;
    float* deepest;
} intended_job;

static void deepest_level_row(void* context, int j, void* scratch)
{
    (void)scratch;
    const intended_job* job = (const intended_job*)context;
    const mn_plan* plan = job->plan;
    int width = plan->grid.width;
    int cells = mn_cells(&plan->grid);
    for (int i = 0; i < width; i++) {
        int c = j * width + i;
        float deepest = NAN;
        for (int k = plan->count - 1; k >= 0 && mn_isnan(deepest); k--) {
            if (plan->masks[(size_t)k * (size_t)cells + (size_t)c]) {
                deepest = plan->levels[k];
            }
        }
        job->deepest[c] = deepest;
    }
}

int mn_intended_compute(const mn_plan* plan, const mn_profile* profile, struct mn_gpu_session* gpu, float* deepest, float* intended)
{
    if (gpu != NULL) {
        return mn_gpu_intended(gpu, plan, intended);
    }
    intended_job job = { plan, deepest };
    MN_CHECK(mn_rows_parallel(plan->grid.height, deepest_level_row, &job, NULL, mn_worker_count()));
    return mn_remaining_compute(&plan->grid, deepest, profile->offsets, profile->offset_count, intended);
}

void mn_uncut_of(const mn_grid* g, const float* final_stock, const float* intended, float tolerance, mn_uncut* uncut)
{
    int cells = mn_cells(g);
    double area = (double)g->cell_size * (double)g->cell_size;
    memset(uncut, 0, sizeof(*uncut));
    for (int c = 0; c < cells; c++) {
        float f = final_stock[c];
        float t = intended[c];
        if (mn_isnan(f) || mn_isnan(t) || !(f > t + tolerance + MN_COLLISION_TOLERANCE)) {
            continue;
        }
        float above = f - t;
        uncut->cells++;
        uncut->volume += (double)above * area;
        if (above > uncut->max_height) {
            uncut->max_height = above;
        }
    }
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
    int result = mn_check_path(segments, count, grid, stock, model, floor, &profile, tool->cutter_diameter / 2.0f, tool->cutter_length, tolerance, status, NULL, &list, NULL, NULL);
    mn_profile_free(&profile);
    if (result != MN_OK) {
        mn_collisions_free(&list);
        return result;
    }
    *events = list.items != NULL ? list.items : (mn_collision*)mn_alloc(1, sizeof(mn_collision));
    *event_count = list.count;
    return MN_OK;
}

MN_API int32_t mn_far_withdraw(const uint8_t* places, int32_t width, int32_t height, float cell_size, float radius, uint8_t* allowed)
{
    if (places == NULL || allowed == NULL || width <= 0 || height <= 0 || !(cell_size > 0) || !(radius >= 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The far withdrawal needs the places, the allowed mask, a positive grid and a radius of 0 or more.");
    }
    int cells = width * height;
    uint8_t* near = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (near == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the far withdrawal.");
    }
    int status = mn_within_radius(places, width, height, cell_size, radius, near);
    int withdrawn = 0;
    for (int c = 0; c < cells && status == MN_OK; c++) {
        if (allowed[c] && near[c]) {
            allowed[c] = 0;
            withdrawn++;
        }
    }
    free(near);
    return status == MN_OK ? withdrawn : status;
}
