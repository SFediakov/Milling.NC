#include "mn_internal.h"

/* Node lattice, cave tree and the two routing strategies (NodeLattice, CaveTree,
 * ZLayerByLayerStrategy, ThreeAxisFreedomStrategy), and the one run collision rule (T-150) that
 * evaluates the nodes of every route against the material as it stands before the route. */

/* NodeLattice.StepEpsilon: guards floor() against 3 / 0.5 evaluating to 5.9999995. */
#define MN_STEP_EPSILON 1e-4f

/* One run: rounds of node evaluation per route (a dropped node can unblock or block another). */
#define MN_GUARD_ROUNDS 4

int mn_lattice_step(float spacing, float cell_size) { return mn_maxi(1, mn_f2i(floorf(spacing / cell_size + MN_STEP_EPSILON))); }

MN_API int32_t mn_step_cells(float spacing, float cell_size) { return mn_lattice_step(spacing, cell_size); }

static int on_lattice(int index, int count, int step) { return index % step == 0 || index == count - 1; }

MN_API int32_t mn_on_lattice(int32_t index, int32_t count, int32_t step) { return on_lattice(index, count, step); }

int mn_outline(const uint8_t* inside, int width, int height, int i, int j)
{
    for (int dj = -1; dj <= 1; dj++) {
        for (int di = -1; di <= 1; di++) {
            int ii = i + di;
            int jj = j + dj;
            if ((di != 0 || dj != 0) && ii >= 0 && ii < width && jj >= 0 && jj < height && !inside[jj * width + ii]) {
                return 1;
            }
        }
    }
    return 0;
}

MN_API int32_t mn_is_outline(const uint8_t* inside, int32_t width, int32_t height, int32_t i, int32_t j) { return mn_outline(inside, width, height, i, j); }

/* The region's cells on the square lattice plus its outline cells, in row-major order. */
int mn_lattice(const uint8_t* inside, int width, int height, int step, mn_ints* nodes)
{
    if (step < 1) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Lattice step must be at least one cell.");
    }
    for (int j = 0; j < height; j++) {
        for (int i = 0; i < width; i++) {
            if (!inside[j * width + i]) {
                continue;
            }
            if ((on_lattice(i, width, step) && on_lattice(j, height, step)) || mn_outline(inside, width, height, i, j)) {
                MN_CHECK(mn_ints_push(nodes, j * width + i));
            }
        }
    }
    return MN_OK;
}

MN_API int32_t mn_lattice_nodes(const uint8_t* inside, int32_t width, int32_t height, int32_t step, int32_t** nodes, int32_t* count)
{
    mn_ints list = { 0 };
    int status = mn_lattice(inside, width, height, step, &list);
    if (status != MN_OK) {
        mn_ints_free(&list);
        return status;
    }
    *nodes = list.items != NULL ? list.items : (int*)mn_alloc(1, sizeof(int));
    *count = list.count;
    return MN_OK;
}

/* ---- cave tree ---- */

typedef struct mn_cave {
    int level;
    int cells_first;
    int cells_count;
    mn_ints children;
} mn_cave;

typedef struct mn_cave_tree {
    int* labels; /* per level, cells each */
    mn_ints cells;
    mn_cave* caves;
    int cave_count;
    int* level_first; /* first cave index of every level, count + 1 entries */
    mn_ints roots;
} mn_cave_tree;

static void cave_tree_free(mn_cave_tree* tree)
{
    for (int c = 0; c < tree->cave_count; c++) {
        mn_ints_free(&tree->caves[c].children);
    }
    free(tree->caves);
    free(tree->labels);
    free(tree->level_first);
    mn_ints_free(&tree->cells);
    mn_ints_free(&tree->roots);
}

static int cave_tree_build(const mn_plan* plan, mn_cave_tree* tree)
{
    memset(tree, 0, sizeof(*tree));
    int cells = mn_cells(&plan->grid);
    int count = plan->count;
    tree->labels = (int*)mn_alloc((size_t)(count > 0 ? count : 1) * (size_t)cells, sizeof(int));
    tree->level_first = (int*)mn_alloc((size_t)count + 1, sizeof(int));
    if (tree->labels == NULL || tree->level_first == NULL) {
        cave_tree_free(tree);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the cave tree.");
    }
    int capacity = 0;
    int status = MN_OK;
    for (int k = 0; k < count && status == MN_OK; k++) {
        mn_ints offsets = { 0 };
        int* labels = tree->labels + (size_t)k * (size_t)cells;
        status = mn_components(plan->masks + (size_t)k * (size_t)cells, plan->grid.width, plan->grid.height, labels, &tree->cells, &offsets);
        tree->level_first[k] = tree->cave_count;
        for (int id = 0; status == MN_OK && id + 1 < offsets.count; id++) {
            if (tree->cave_count == capacity) {
                capacity = capacity == 0 ? 64 : capacity * 2;
                mn_cave* grown = (mn_cave*)realloc(tree->caves, (size_t)capacity * sizeof(mn_cave));
                if (grown == NULL) {
                    status = mn_fail(MN_ERR_MEMORY, "Out of memory for the cave tree.");
                    break;
                }
                tree->caves = grown;
            }
            mn_cave* cave = &tree->caves[tree->cave_count];
            memset(cave, 0, sizeof(*cave));
            cave->level = k;
            cave->cells_first = offsets.items[id];
            cave->cells_count = offsets.items[id + 1] - offsets.items[id];
            int index = tree->cave_count++;
            int parent = k > 0 ? tree->labels[(size_t)(k - 1) * (size_t)cells + (size_t)tree->cells.items[cave->cells_first]] : -1;
            if (parent >= 0) {
                status = mn_ints_push(&tree->caves[tree->level_first[k - 1] + parent].children, index);
            } else {
                status = mn_ints_push(&tree->roots, index);
            }
        }
        mn_ints_free(&offsets);
    }
    tree->level_first[count] = tree->cave_count;
    if (status != MN_OK) {
        cave_tree_free(tree);
    }
    return status;
}

MN_API int32_t mn_caves(const uint8_t* masks, int32_t level_count, int32_t width, int32_t height, int32_t* labels, int32_t** cave_levels, int32_t** cave_ids, int32_t** cave_cells, int32_t** cave_cell_offsets, int32_t** cave_children, int32_t** cave_child_offsets, int32_t** roots, int32_t* cave_count, int32_t* root_count)
{
    mn_plan plan;
    memset(&plan, 0, sizeof(plan));
    plan.grid.width = width;
    plan.grid.height = height;
    plan.grid.cell_size = 1.0f;
    plan.count = level_count;
    plan.masks = (uint8_t*)masks;
    mn_cave_tree tree;
    MN_CHECK(cave_tree_build(&plan, &tree));
    int caves = tree.cave_count;
    size_t size = (size_t)(caves > 0 ? caves : 1);
    int* levels = (int*)mn_alloc(size, sizeof(int));
    int* ids = (int*)mn_alloc(size, sizeof(int));
    int* cell_offsets = (int*)mn_alloc(size + 1, sizeof(int));
    int* child_offsets = (int*)mn_alloc(size + 1, sizeof(int));
    mn_ints cells = { 0 };
    mn_ints children = { 0 };
    int status = levels == NULL || ids == NULL || cell_offsets == NULL || child_offsets == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the caves.") : MN_OK;
    for (int c = 0; c < caves && status == MN_OK; c++) {
        const mn_cave* cave = &tree.caves[c];
        levels[c] = cave->level;
        ids[c] = c - tree.level_first[cave->level];
        cell_offsets[c] = cells.count;
        child_offsets[c] = children.count;
        for (int m = 0; m < cave->cells_count && status == MN_OK; m++) {
            status = mn_ints_push(&cells, tree.cells.items[cave->cells_first + m]);
        }
        for (int m = 0; m < cave->children.count && status == MN_OK; m++) {
            status = mn_ints_push(&children, cave->children.items[m]);
        }
    }
    if (status == MN_OK) {
        cell_offsets[caves] = cells.count;
        child_offsets[caves] = children.count;
        memcpy(labels, tree.labels, (size_t)level_count * (size_t)width * (size_t)height * sizeof(int));
        *cave_levels = levels;
        *cave_ids = ids;
        *cave_cells = cells.items != NULL ? cells.items : (int*)mn_alloc(1, sizeof(int));
        *cave_cell_offsets = cell_offsets;
        *cave_children = children.items != NULL ? children.items : (int*)mn_alloc(1, sizeof(int));
        *cave_child_offsets = child_offsets;
        *roots = tree.roots.items != NULL ? tree.roots.items : (int*)mn_alloc(1, sizeof(int));
        *cave_count = caves;
        *root_count = tree.roots.count;
        memset(&tree.roots, 0, sizeof(tree.roots));
    } else {
        free(levels);
        free(ids);
        free(cell_offsets);
        free(child_offsets);
        mn_ints_free(&cells);
        mn_ints_free(&children);
    }
    cave_tree_free(&tree);
    return status;
}

/* ---- one run collision rule (T-150) ---- */

/* What the rule keeps while a strategy runs: the material as the routes so far leave it (the
 * footprint of every visited node stamped in), the raised tips of the positions not achieved, and
 * per route the kept nodes, the precedence pairs and the cells to clear before the route. Every
 * blocked cell of a tool position is either cut low enough by a node of the route (for a node, a
 * pair orders that node first), cut by a clearing route at heights not below the route (positions
 * at their tip), or impossible: the model itself, or stock only the model's removal would free. A
 * node with impossible blockers is achieved when the model cells only it finishes outnumber them
 * by more than `ratio` times; otherwise it is dropped and its position raised until the head
 * clears. Every other cell of the route's region is lifted the same way, so the chords between the
 * nodes and the travels climb over what their head would meet. */
typedef struct mn_guard {
    const mn_context* context;
    mn_profile profile;
    float* raised;      /* output: every lift, the floor chords and travels climb */
    int own_raised;
    float* dropped;     /* the drops alone: the height a dropped position cuts at when it clears for another */
    float* material;
    int* node_at;
    int* cover;
    uint8_t* keep;
    int* cell_of;
    int* pending;
    int node_capacity;
    uint8_t* mark;
    mn_ints before;
    mn_ints after;
    mn_ints clearing;
    int enabled;
} mn_guard;

static void guard_free(mn_guard* g)
{
    mn_profile_free(&g->profile);
    if (g->own_raised) {
        free(g->raised);
    }
    free(g->dropped);
    free(g->material);
    free(g->node_at);
    free(g->cover);
    free(g->keep);
    free(g->cell_of);
    free(g->pending);
    free(g->mark);
    mn_ints_free(&g->before);
    mn_ints_free(&g->after);
    mn_ints_free(&g->clearing);
    memset(g, 0, sizeof(*g));
}

static int guard_init(mn_guard* g, const mn_context* context)
{
    memset(g, 0, sizeof(*g));
    g->context = context;
    g->enabled = context->collision_mode == MN_COLLISION_ONE_RUN;
    if (!g->enabled) {
        return MN_OK;
    }
    int cells = mn_cells(&context->grid);
    MN_CHECK(mn_profile_build(&context->tool, context->grid.cell_size, &g->profile));
    g->raised = context->raised;
    if (g->raised == NULL) {
        g->raised = (float*)mn_alloc((size_t)cells, sizeof(float));
        g->own_raised = 1;
        if (g->raised != NULL) {
            for (int k = 0; k < cells; k++) {
                g->raised[k] = NAN;
            }
        }
    }
    g->dropped = (float*)mn_alloc((size_t)cells, sizeof(float));
    g->material = (float*)mn_alloc((size_t)cells, sizeof(float));
    g->node_at = (int*)mn_alloc((size_t)cells, sizeof(int));
    g->cover = (int*)mn_alloc((size_t)cells, sizeof(int));
    g->mark = (uint8_t*)mn_alloc((size_t)cells, 1);
    if (g->raised == NULL || g->dropped == NULL || g->material == NULL || g->node_at == NULL || g->cover == NULL || g->mark == NULL) {
        guard_free(g);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision rule.");
    }
    memcpy(g->material, context->stock, (size_t)cells * sizeof(float));
    for (int k = 0; k < cells; k++) {
        g->dropped[k] = NAN;
    }
    return MN_OK;
}

/* The route floor at a cell under the rule: never below the raised tip there. */
static float guard_floor(const mn_guard* g, int cell, float base)
{
    if (!g->enabled) {
        return base;
    }
    float r = g->raised[cell];
    return !mn_isnan(r) && r > base ? r : base;
}

static int guard_reserve(mn_guard* g, int count)
{
    if (count <= g->node_capacity) {
        return MN_OK;
    }
    int capacity = mn_maxi(count, g->node_capacity * 2);
    uint8_t* keep = (uint8_t*)realloc(g->keep, (size_t)capacity);
    int* cell_of = (int*)realloc(g->cell_of, (size_t)capacity * sizeof(int));
    int* pending = (int*)realloc(g->pending, (size_t)capacity * sizeof(int));
    if (keep != NULL) {
        g->keep = keep;
    }
    if (cell_of != NULL) {
        g->cell_of = cell_of;
    }
    if (pending != NULL) {
        g->pending = pending;
    }
    if (keep == NULL || cell_of == NULL || pending == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the collision rule of %d nodes.", count);
    }
    g->node_capacity = capacity;
    return MN_OK;
}

/* The material after a route: every node's footprint lowers the cells under it. */
static void guard_stamp(mn_guard* g, const float* xs, const float* ys, const float* zs, int count)
{
    if (!g->enabled) {
        return;
    }
    const mn_grid* gr = &g->context->grid;
    for (int k = 0; k < count; k++) {
        int ci = mn_cell_i(gr, xs[k]);
        int cj = mn_cell_j(gr, ys[k]);
        for (int o = 0; o < g->profile.offset_count; o++) {
            int i = ci + g->profile.offsets[o].dx;
            int j = cj + g->profile.offsets[o].dy;
            if (!mn_in_bounds(gr, i, j)) {
                continue;
            }
            int c = j * gr->width + i;
            float cut = zs[k] + g->profile.offsets[o].dz;
            if (!mn_isnan(g->material[c]) && cut < g->material[c]) {
                g->material[c] = cut;
            }
        }
    }
}

static void guard_cover(mn_guard* g, int node, int delta)
{
    const mn_grid* gr = &g->context->grid;
    int c0 = g->cell_of[node];
    int ci = c0 % gr->width;
    int cj = c0 / gr->width;
    for (int o = 0; o < g->profile.offset_count; o++) {
        int i = ci + g->profile.offsets[o].dx;
        int j = cj + g->profile.offsets[o].dy;
        if (mn_in_bounds(gr, i, j)) {
            g->cover[j * gr->width + i] += delta;
        }
    }
}

/* The tip that clears what stays: the next pass's path may run `tolerance` above its planned tip
 * and the check allows MN_COLLISION_TOLERANCE. */
static float guard_lift(const mn_guard* g, float need) { return need + g->context->parameters.tolerance + MN_COLLISION_TOLERANCE; }

static void guard_raise(mn_guard* g, int c, float need)
{
    float lifted = guard_lift(g, need);
    if (mn_isnan(g->raised[c]) || lifted > g->raised[c]) {
        g->raised[c] = lifted;
    }
}

static void guard_drop(mn_guard* g, int node, float need)
{
    int c = g->cell_of[node];
    float lifted = guard_lift(g, need);
    if (mn_isnan(g->dropped[c]) || lifted > g->dropped[c]) {
        g->dropped[c] = lifted;
    }
    g->keep[node] = 0;
    g->node_at[c] = -1;
    guard_cover(g, node, -1);
    guard_raise(g, c, need);
}

/* The blockers of one tool position (cell c, tip z, node index or -1 for a cell a chord crosses)
 * against the material: an internal blocker (cut low enough by a kept node of the route) gives a
 * pair when the position is a node; a clearing blocker is appended to `clearing`; every other one
 * counts as impossible and lifts `raise`. Returns the number of impossible blockers. */
static int guard_blockers(mn_guard* g, int node, int c0, float z, const float* xs, const float* ys, const float* zs, float floor_level, int allow_clearing, mn_ints* pairs_before,
    mn_ints* pairs_after, mn_ints* clearing, float* raise, int* status)
{
    const mn_context* ctx = g->context;
    const mn_grid* gr = &ctx->grid;
    const mn_profile* p = &g->profile;
    float tolerance = ctx->parameters.tolerance;
    float cutter_length = ctx->tool.cutter_length;
    int ci = c0 % gr->width;
    int cj = c0 / gr->width;
    int damaged = 0;
    *raise = NAN;
    for (int o = 0; o < p->annulus_count && *status == MN_OK; o++) {
        int i = ci + p->annulus[o].dx;
        int j = cj + p->annulus[o].dy;
        if (!mn_in_bounds(gr, i, j)) {
            continue;
        }
        int c = j * gr->width + i;
        float h = g->material[c];
        float u = z + cutter_length + p->annulus[o].dz;
        if (mn_isnan(h) || !(h > u + MN_COLLISION_TOLERANCE)) {
            continue;
        }
        /* Cut low enough by a kept node of this route: that node goes first. */
        int best = -1;
        float best_distance = INFINITY;
        int cleared = 0;
        for (int o2 = 0; o2 < p->offset_count; o2++) {
            int qi = i - p->offsets[o2].dx;
            int qj = j - p->offsets[o2].dy;
            if (!mn_in_bounds(gr, qi, qj)) {
                continue;
            }
            int q = qj * gr->width + qi;
            int m = g->node_at[q];
            if (m >= 0 && m != node && g->keep[m] && zs[m] + p->offsets[o2].dz <= u + MN_COLLISION_TOLERANCE) {
                float dx = xs[m] - mn_center_x(gr, ci);
                float dy = ys[m] - mn_center_y(gr, cj);
                float d = dx * dx + dy * dy;
                if (d < best_distance) {
                    best_distance = d;
                    best = m;
                }
            }
            if (best < 0 && allow_clearing && !cleared) {
                float tq = ctx->effective_tip[q];
                if (!mn_isnan(tq)) {
                    float zq = mn_max(tq, floor_level);
                    if (!mn_isnan(g->dropped[q]) && g->dropped[q] > zq) {
                        zq = g->dropped[q];
                    }
                    cleared = zq + p->offsets[o2].dz + tolerance <= u + MN_COLLISION_TOLERANCE;
                }
            }
        }
        if (best >= 0) {
            if (node >= 0) {
                *status = mn_ints_push(pairs_before, best);
                if (*status == MN_OK) {
                    *status = mn_ints_push(pairs_after, node);
                }
            }
            continue;
        }
        if (cleared) {
            /* Stock a clearing route lowers before this route, at heights not below it. */
            *status = mn_ints_push(clearing, c);
            continue;
        }
        damaged++;
        float need_here = h - p->annulus[o].dz - cutter_length;
        if (mn_isnan(*raise) || need_here > *raise) {
            *raise = need_here;
        }
    }
    return damaged;
}

/* One node: pairs and clearing cells are appended (the caller discards them when the node is
 * dropped); returns 1 when the node is achieved, 0 when it must be dropped (`need` then holds the
 * tip that clears). */
static int guard_node(mn_guard* g, int node, const float* xs, const float* ys, const float* zs, float floor_level, int allow_clearing, mn_ints* pairs_before, mn_ints* pairs_after,
    mn_ints* clearing, float* need, int* status)
{
    const mn_context* ctx = g->context;
    const mn_grid* gr = &ctx->grid;
    const mn_profile* p = &g->profile;
    float tolerance = ctx->parameters.tolerance;
    int c0 = g->cell_of[node];
    float z = zs[node];
    float raise;
    int damaged = guard_blockers(g, node, c0, z, xs, ys, zs, floor_level, allow_clearing, pairs_before, pairs_after, clearing, &raise, status);
    if (*status != MN_OK || damaged == 0) {
        return 1;
    }
    /* The model cells the cutter finishes at this node: its bottom reaches their surface. */
    int ci = c0 % gr->width;
    int cj = c0 / gr->width;
    int finished = 0;
    for (int o = 0; o < p->offset_count; o++) {
        int i = ci + p->offsets[o].dx;
        int j = cj + p->offsets[o].dy;
        if (!mn_in_bounds(gr, i, j)) {
            continue;
        }
        int c = j * gr->width + i;
        float m = ctx->model[c];
        if (m > ctx->floor + MN_FLOOR_TOLERANCE && z + p->offsets[o].dz <= m + tolerance) {
            finished++;
        }
    }
    if ((float)finished > ctx->ratio * (float)damaged) {
        return 1;
    }
    *need = raise;
    return 0;
}

/* Every cell of the route's region that is not a kept node, at the height the route floor gives
 * it: impossible blockers lift its floor, clearing blockers join the clearing cells. */
static int guard_region(mn_guard* g, const float* xs, const float* ys, const float* zs, float floor_level, int allow_clearing, const float* floor_map, const int* region, int count)
{
    int status = MN_OK;
    mn_ints clearing = { 0 };
    for (int k = 0; k < count && status == MN_OK; k++) {
        int c = region[k];
        int node = g->node_at[c];
        if (node >= 0 && g->keep[node]) {
            continue;
        }
        float z = floor_map[c];
        if (mn_isnan(z)) {
            continue;
        }
        if (!mn_isnan(g->raised[c]) && g->raised[c] > z) {
            z = g->raised[c];
        }
        float raise;
        clearing.count = 0;
        guard_blockers(g, -1, c, z, xs, ys, zs, floor_level, allow_clearing, NULL, NULL, &clearing, &raise, &status);
        if (status != MN_OK) {
            break;
        }
        if (!mn_isnan(raise)) {
            guard_raise(g, c, raise);
        }
        for (int m = 0; m < clearing.count && status == MN_OK; m++) {
            int cell = clearing.items[m];
            if (!g->mark[cell]) {
                g->mark[cell] = 1;
                status = mn_ints_push(&g->clearing, cell);
            }
        }
    }
    mn_ints_free(&clearing);
    return status;
}

/* Kahn over the pairs of the kept nodes; nodes that never become ready lie on a cycle and are
 * dropped (their blockers count as impossible). Returns how many were dropped. */
static int guard_break_cycles(mn_guard* g, int count, const float* zs, int* status)
{
    if (g->before.count == 0) {
        return 0;
    }
    int* indegree = (int*)mn_alloc((size_t)count, sizeof(int));
    int* queue = (int*)mn_alloc((size_t)count, sizeof(int));
    uint8_t* done = (uint8_t*)mn_alloc((size_t)count, 1);
    if (indegree == NULL || queue == NULL || done == NULL) {
        free(indegree);
        free(queue);
        free(done);
        *status = mn_fail(MN_ERR_MEMORY, "Out of memory for the precedence check.");
        return 0;
    }
    for (int k = 0; k < g->before.count; k++) {
        indegree[g->after.items[k]]++;
    }
    int head = 0;
    int tail = 0;
    for (int k = 0; k < count; k++) {
        if (g->keep[k] && indegree[k] == 0) {
            queue[tail++] = k;
        }
    }
    while (head < tail) {
        int v = queue[head++];
        done[v] = 1;
        for (int k = 0; k < g->before.count; k++) {
            if (g->before.items[k] == v && --indegree[g->after.items[k]] == 0) {
                queue[tail++] = g->after.items[k];
            }
        }
    }
    int dropped = 0;
    for (int k = 0; k < count; k++) {
        if (g->keep[k] && !done[k]) {
            guard_drop(g, k, zs[k]);
            dropped++;
        }
    }
    free(indegree);
    free(queue);
    free(done);
    return dropped;
}

/* Evaluates the nodes of one route (compacted in place to the kept ones) and, when given, the
 * cells of its region at the heights of `floor_map`; afterwards `before` and `after` hold the pairs
 * over the kept indices, `pending` the predecessor count per kept node and `clearing` the cells to
 * lower first. floor_level NaN means the lowest node of the route. */
static int guard_evaluate(mn_guard* g, float* xs, float* ys, float* zs, int* count, float floor_level, int allow_clearing, const float* floor_map, const int* region, int region_count)
{
    g->before.count = 0;
    g->after.count = 0;
    g->clearing.count = 0;
    if (!g->enabled || *count == 0) {
        return MN_OK;
    }
    const mn_grid* gr = &g->context->grid;
    int cells = mn_cells(gr);
    int n = *count;
    MN_CHECK(guard_reserve(g, n));
    if (mn_isnan(floor_level)) {
        floor_level = INFINITY;
        for (int k = 0; k < n; k++) {
            floor_level = mn_min(floor_level, zs[k]);
        }
    }
    for (int k = 0; k < cells; k++) {
        g->node_at[k] = -1;
        g->cover[k] = 0;
        g->mark[k] = 0;
    }
    for (int k = 0; k < n; k++) {
        int c = mn_cell_j(gr, ys[k]) * gr->width + mn_cell_i(gr, xs[k]);
        g->cell_of[k] = c;
        g->keep[k] = 1;
        g->node_at[c] = k;
        guard_cover(g, k, 1);
    }

    int status = MN_OK;
    mn_ints pairs_before = { 0 };
    mn_ints pairs_after = { 0 };
    mn_ints clearing = { 0 };
    for (int round = 0; round < MN_GUARD_ROUNDS && status == MN_OK; round++) {
        g->before.count = 0;
        g->after.count = 0;
        for (int k = 0; k < g->clearing.count; k++) {
            g->mark[g->clearing.items[k]] = 0;
        }
        g->clearing.count = 0;
        int dropped = 0;
        for (int k = 0; k < n && status == MN_OK; k++) {
            if (!g->keep[k]) {
                continue;
            }
            pairs_before.count = 0;
            pairs_after.count = 0;
            clearing.count = 0;
            float need = NAN;
            int achieved = guard_node(g, k, xs, ys, zs, floor_level, allow_clearing, &pairs_before, &pairs_after, &clearing, &need, &status);
            if (status != MN_OK) {
                break;
            }
            if (!achieved) {
                guard_drop(g, k, need);
                dropped++;
                continue;
            }
            for (int m = 0; m < pairs_before.count && status == MN_OK; m++) {
                status = mn_ints_push(&g->before, pairs_before.items[m]);
                if (status == MN_OK) {
                    status = mn_ints_push(&g->after, pairs_after.items[m]);
                }
            }
            for (int m = 0; m < clearing.count && status == MN_OK; m++) {
                int c = clearing.items[m];
                if (!g->mark[c]) {
                    g->mark[c] = 1;
                    status = mn_ints_push(&g->clearing, c);
                }
            }
        }
        if (status == MN_OK && dropped == 0) {
            dropped = guard_break_cycles(g, n, zs, &status);
        }
        if (dropped == 0) {
            break;
        }
    }
    mn_ints_free(&pairs_before);
    mn_ints_free(&pairs_after);
    mn_ints_free(&clearing);
    MN_CHECK(status);
    /* An achieved node supersedes an earlier lift of its cell: the axis goes there. */
    for (int k = 0; k < n; k++) {
        int c = g->cell_of[k];
        if (g->keep[k] && !mn_isnan(g->raised[c]) && g->raised[c] > zs[k]) {
            g->raised[c] = NAN;
        }
    }
    if (floor_map != NULL && region != NULL) {
        MN_CHECK(guard_region(g, xs, ys, zs, floor_level, allow_clearing, floor_map, region, region_count));
    }

    /* Compact to the kept nodes; pending[k] counts the predecessors of kept node k. */
    int kept = 0;
    for (int k = 0; k < n; k++) {
        g->cell_of[k] = g->keep[k] ? kept : -1;
        if (g->keep[k]) {
            xs[kept] = xs[k];
            ys[kept] = ys[k];
            zs[kept] = zs[k];
            kept++;
        }
    }
    for (int k = 0; k < kept; k++) {
        g->pending[k] = 0;
    }
    int pairs = 0;
    for (int k = 0; k < g->before.count; k++) {
        int a = g->cell_of[g->before.items[k]];
        int b = g->cell_of[g->after.items[k]];
        if (a >= 0 && b >= 0) {
            g->before.items[pairs] = a;
            g->after.items[pairs] = b;
            g->pending[b]++;
            pairs++;
        }
    }
    g->before.count = pairs;
    g->after.count = pairs;
    *count = kept;
    return MN_OK;
}

/* ---- shared helpers of the strategies ---- */

/* The node nearest in XY to the tool among the nodes without a predecessor, or the first such
 * node before the program starts. */
static int nearest_node(const float* x, const float* y, int count, const int* pending, const mn_writer* writer)
{
    mn_v3 from;
    int has_position = mn_writer_has_position(writer, &from);
    int best = -1;
    float best_distance = INFINITY;
    for (int k = 0; k < count; k++) {
        if (pending != NULL && pending[k] > 0) {
            continue;
        }
        if (!has_position) {
            return k;
        }
        float dx = x[k] - from.x;
        float dy = y[k] - from.y;
        float d = dx * dx + dy * dy;
        if (d < best_distance) {
            best_distance = d;
            best = k;
        }
    }
    return best < 0 ? 0 : best;
}

/* Solves one route over the nodes and writes it: travel to the first node, follow the rest. The
 * guard supplies the precedence pairs of the route (none when it is off). */
static int route_nodes(const mn_route_grid* grid, const float* x, const float* y, const float* z, int count, const mn_guard* guard, mn_budget* budget, int64_t nodes_left, const volatile int32_t* cancel, mn_writer* writer)
{
    if (count == 0) {
        return MN_OK;
    }
    int* order = (int*)mn_alloc((size_t)count, sizeof(int));
    if (order == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a route of %d nodes.", count);
    }
    mn_problem problem;
    problem.grid = *grid;
    problem.x = x;
    problem.y = y;
    problem.z = z;
    problem.count = count;
    problem.before = guard->enabled ? guard->before.items : NULL;
    problem.after = guard->enabled ? guard->after.items : NULL;
    problem.pair_count = guard->enabled ? guard->before.count : 0;
    int start = nearest_node(x, y, count, guard->enabled ? guard->pending : NULL, writer);
    int64_t evaluations = 0;
    int status = mn_solve(&problem, start, mn_share(budget, count, nodes_left), cancel, order, &evaluations);
    if (status == MN_OK) {
        budget->used += evaluations;
        status = mn_writer_travel_to(writer, mn_problem_node(&problem, order[0]), grid);
        for (int k = 1; k < count && status == MN_OK; k++) {
            status = mn_writer_follow_to(writer, mn_problem_node(&problem, order[k]), grid);
        }
    }
    free(order);
    return status;
}

static int writer_for(const mn_context* context, mn_writer** writer) { return mn_writer_create(&context->parameters, context->stock_top + context->parameters.safe_height, writer); }

/* Cells whose cutter footprint holds a marked cell. */
static void footprint_touches(const mn_grid* g, const mn_profile* profile, const uint8_t* marked, uint8_t* touches)
{
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            uint8_t found = 0;
            for (int o = 0; o < profile->offset_count && !found; o++) {
                int ii = i + profile->offsets[o].dx;
                int jj = j + profile->offsets[o].dy;
                found = (uint8_t)(mn_in_bounds(g, ii, jj) && marked[jj * g->width + ii]);
            }
            touches[j * g->width + i] = found;
        }
    }
}

/* One route over the pass cells in `inside` at max(their tip, floor_level): the pass cells take that
 * height as the route floor and keep it in `planned` afterwards. The node arrays must hold
 * cut->count entries. */
static int tip_route(const mn_context* context, const uint8_t* inside, const mn_ints* cut, float floor_level, float* planned, float* clearance, float* xs, float* ys, float* zs, mn_guard* guard,
    int allow_clearing, mn_budget* budget, int64_t nodes_left, const mn_monitor* monitor, mn_writer* writer);

/* The clearing route of the guard: every position whose footprint holds a clearing cell, at
 * max(tip, floor_level), before the route that needs the cells lowered. */
static int clearing_route(const mn_context* context, mn_guard* guard, float floor_level, float* planned, float* clearance, uint8_t* inside, mn_budget* budget, int64_t nodes_left,
    const mn_monitor* monitor, mn_writer* writer)
{
    if (!guard->enabled || guard->clearing.count == 0) {
        return MN_OK;
    }
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    memset(inside, 0, (size_t)cells);
    for (int k = 0; k < guard->clearing.count; k++) {
        int c = guard->clearing.items[k];
        int ci = c % g->width;
        int cj = c / g->width;
        for (int o = 0; o < guard->profile.offset_count; o++) {
            int qi = ci - guard->profile.offsets[o].dx;
            int qj = cj - guard->profile.offsets[o].dy;
            if (!mn_in_bounds(g, qi, qj)) {
                continue;
            }
            int q = qj * g->width + qi;
            if (!mn_isnan(context->effective_tip[q])) {
                inside[q] = 1;
            }
        }
    }
    mn_ints cut = { 0 };
    int status = mn_lattice(inside, g->width, g->height, 1, &cut);
    float* xs = NULL;
    float* ys = NULL;
    float* zs = NULL;
    if (status == MN_OK && cut.count > 0) {
        xs = (float*)mn_alloc((size_t)cut.count, sizeof(float));
        ys = (float*)mn_alloc((size_t)cut.count, sizeof(float));
        zs = (float*)mn_alloc((size_t)cut.count, sizeof(float));
        if (xs == NULL || ys == NULL || zs == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for a clearing route of %d nodes.", cut.count);
        } else {
            status = tip_route(context, inside, &cut, floor_level, planned, clearance, xs, ys, zs, guard, 0, budget, nodes_left, monitor, writer);
        }
    }
    free(xs);
    free(ys);
    free(zs);
    mn_ints_free(&cut);
    return status;
}

static int tip_route(const mn_context* context, const uint8_t* inside, const mn_ints* cut, float floor_level, float* planned, float* clearance, float* xs, float* ys, float* zs, mn_guard* guard,
    int allow_clearing, mn_budget* budget, int64_t nodes_left, const mn_monitor* monitor, mn_writer* writer)
{
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    int count = cut->count;
    for (int k = 0; k < count; k++) {
        int cell = cut->items[k];
        xs[k] = mn_center_x(g, cell % g->width);
        ys[k] = mn_center_y(g, cell / g->width);
        zs[k] = mn_max(context->effective_tip[cell], floor_level);
    }
    MN_CHECK(guard_evaluate(guard, xs, ys, zs, &count, NAN, allow_clearing, NULL, NULL, 0));
    if (allow_clearing && guard->clearing.count > 0) {
        /* The clearing cells are lowered first; `inside` is rebuilt by the clearing route, so the
         * pass cells are marked again afterwards from the cut list. */
        uint8_t* scratch = (uint8_t*)mn_alloc((size_t)cells, 1);
        if (scratch == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for the clearing route.");
        }
        int status = clearing_route(context, guard, floor_level, planned, clearance, scratch, budget, nodes_left, monitor, writer);
        free(scratch);
        MN_CHECK(status);
        MN_CHECK(guard_evaluate(guard, xs, ys, zs, &count, NAN, 0, NULL, NULL, 0));
    }
    for (int k = 0; k < cells; k++) {
        clearance[k] = guard_floor(guard, k, inside[k] ? mn_max(context->effective_tip[k], floor_level) : planned[k]);
    }
    mn_route_grid grid = { *g, clearance };
    int status = route_nodes(&grid, xs, ys, zs, count, guard, budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
    guard_stamp(guard, xs, ys, zs, count);
    memcpy(planned, clearance, (size_t)cells * sizeof(float));
    return status;
}

/* ---- Z layer by layer ---- */

/* The cave cells of the should-cut pass: their footprint holds a should-cut cell and their tip lies
 * below the level and above the next one, so no level route takes them to the tip. */
static void should_cut_cells(const float* tip, const uint8_t* touches, const int* cave_cells, int count, float level, float next_level, uint8_t* inside)
{
    for (int m = 0; m < count; m++) {
        int cell = cave_cells[m];
        float z = tip[cell];
        inside[cell] = (uint8_t)(touches[cell] && z < level - MN_LEVEL_TOLERANCE && z > next_level + MN_LEVEL_TOLERANCE);
    }
}

/* The should-cut cells of the band between the stock top and the first level, which lies in no cave. */
static void top_band_cells(const mn_context* context, const uint8_t* touches, uint8_t* inside)
{
    const mn_plan* plan = context->plan;
    int cells = mn_cells(&context->grid);
    float first_level = plan->levels[0];
    for (int k = 0; k < cells; k++) {
        float z = context->effective_tip[k];
        inside[k] = (uint8_t)(touches[k] && plan->coverage[k] && z < context->stock_top - MN_LEVEL_TOLERANCE && z > first_level + MN_LEVEL_TOLERANCE);
    }
}

/* Cave by cave, level by level: a cave is cut completely at its level along the fastest route through
 * its nodes, then the tool drops one level into the first child cave, and it rises for the next
 * sibling only when a whole subtree is done. Every route is solved over the material as it will stand
 * at that moment. After the level route of a cave, a should-cut route (T-136) visits the cave cells
 * over should-cut stock whose tip lies between this level and the next at their tip (every such cell
 * is a node), so that stock is at its closing before the tool goes deeper
 * beside it; the band between the stock top and the first level, which belongs to no cave, gets its
 * should-cut route first. Without should-cut cells there is no such route. In one run mode every
 * route passes the guard first (T-150). */
int mn_z_layer(const mn_context* context, const mn_monitor* monitor, mn_segments* result)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    int cells = mn_cells(g);
    int width = g->width;
    int status = MN_OK;
    mn_cave_tree tree;
    MN_CHECK(cave_tree_build(plan, &tree));
    mn_guard guard;
    status = guard_init(&guard, context);
    if (status != MN_OK) {
        cave_tree_free(&tree);
        return status;
    }

    int step = mn_lattice_step(context->parameters.stepover, g->cell_size);
    /* Every should-cut pass cell is a node: the head limit counts on the tool at the tip of every
     * position over should-cut stock, and a lattice at the finishing stepover left the stock higher. */
    int cut_step = 1;
    mn_ints* nodes = (mn_ints*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1), sizeof(mn_ints));
    mn_ints* cut_nodes = (mn_ints*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1), sizeof(mn_ints));
    uint8_t* touches = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* inside = (uint8_t*)mn_alloc((size_t)cells, 1);
    float* planned = (float*)mn_alloc((size_t)cells, sizeof(float));
    float* clearance = (float*)mn_alloc((size_t)cells, sizeof(float));
    mn_ints pending = { 0 };
    mn_ints top_nodes = { 0 };
    float* xs = NULL;
    float* ys = NULL;
    float* zs = NULL;
    int* list_start = NULL;
    int* list_end = NULL;
    mn_writer* writer = NULL;
    mn_profile profile = { 0 };
    if (nodes == NULL || cut_nodes == NULL || touches == NULL || inside == NULL || planned == NULL || clearance == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    if (context->should_cut != NULL) {
        status = mn_profile_build(&context->tool, g->cell_size, &profile);
        if (status != MN_OK) {
            goto done;
        }
        footprint_touches(g, &profile, context->should_cut, touches);
    }

    int64_t nodes_left = 0;
    int pass_count = 0;
    int max_nodes = 0;
    if (context->should_cut != NULL && plan->count > 0) {
        top_band_cells(context, touches, inside);
        status = mn_lattice(inside, width, g->height, cut_step, &top_nodes);
        nodes_left += top_nodes.count;
        pass_count += top_nodes.count > 0 ? 1 : 0;
        max_nodes = mn_maxi(max_nodes, top_nodes.count);
    }
    for (int c = 0; c < tree.cave_count && status == MN_OK; c++) {
        const mn_cave* cave = &tree.caves[c];
        const int* labels = tree.labels + (size_t)cave->level * (size_t)cells;
        int id = c - tree.level_first[cave->level];
        for (int k = 0; k < cells; k++) {
            inside[k] = (uint8_t)(labels[k] == id);
        }
        status = mn_lattice(inside, width, g->height, step, &nodes[c]);
        nodes_left += nodes[c].count;
        pass_count += nodes[c].count > 0 ? 1 : 0;
        max_nodes = mn_maxi(max_nodes, nodes[c].count);
        if (status == MN_OK && context->should_cut != NULL) {
            const int* cave_cells = tree.cells.items + cave->cells_first;
            float next_level = cave->level + 1 < plan->count ? plan->levels[cave->level + 1] : -INFINITY;
            memset(inside, 0, (size_t)cells);
            should_cut_cells(context->effective_tip, touches, cave_cells, cave->cells_count, plan->levels[cave->level], next_level, inside);
            status = mn_lattice(inside, width, g->height, cut_step, &cut_nodes[c]);
            nodes_left += cut_nodes[c].count;
            pass_count += cut_nodes[c].count > 0 ? 1 : 0;
            max_nodes = mn_maxi(max_nodes, cut_nodes[c].count);
        }
    }
    if (status != MN_OK) {
        goto done;
    }

    xs = (float*)mn_alloc((size_t)(max_nodes > 0 ? max_nodes : 1), sizeof(float));
    ys = (float*)mn_alloc((size_t)(max_nodes > 0 ? max_nodes : 1), sizeof(float));
    zs = (float*)mn_alloc((size_t)(max_nodes > 0 ? max_nodes : 1), sizeof(float));
    if (xs == NULL || ys == NULL || zs == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    memcpy(planned, context->stock, (size_t)cells * sizeof(float));
    status = writer_for(context, &writer);
    if (status != MN_OK) {
        goto done;
    }
    if (guard.enabled) {
        mn_writer_guard(writer, guard.material, &guard.profile, context->tool.cutter_diameter / 2.0f, context->tool.cutter_length);
    }

    mn_budget budget = { MN_BUDGET_MAX_EVALUATIONS, 0 };
    int64_t total = nodes_left;
    int pass = 0;
    if (top_nodes.count > 0) {
        pass++;
        top_band_cells(context, touches, inside);
        status = tip_route(context, inside, &top_nodes, plan->levels[0], planned, clearance, xs, ys, zs, &guard, 1, &budget, nodes_left, monitor, writer);
        nodes_left -= top_nodes.count;
        if (status == MN_OK) {
            mn_report(monitor, pass, pass_count, (float)(total - nodes_left) / (float)total);
        }
    }
    /* The sibling lists live in `pending`: every entry of the stack starts a list whose removed caves
     * are marked -1; a list ends at the entry that holds its length. */
    int depth = 0;
    int first = pending.count;
    for (int r = 0; r < tree.roots.count && status == MN_OK; r++) {
        status = mn_ints_push(&pending, tree.roots.items[r]);
    }
    list_start = (int*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1) + 1, sizeof(int));
    list_end = (int*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1) + 1, sizeof(int));
    if (list_start == NULL || list_end == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    list_start[depth] = first;
    list_end[depth] = pending.count;
    depth = 1;

    while (depth > 0 && status == MN_OK) {
        int from = list_start[depth - 1];
        int to = list_end[depth - 1];
        int remaining = 0;
        for (int k = from; k < to; k++) {
            remaining += pending.items[k] >= 0 ? 1 : 0;
        }
        if (remaining == 0) {
            depth--;
            pending.count = from;
            continue;
        }
        if (mn_cancelled(monitor)) {
            status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            break;
        }

        /* The sibling whose nearest node is nearest to the tool; the first one before the start. */
        mn_v3 position;
        int has_position = mn_writer_has_position(writer, &position);
        int best_slot = -1;
        float best_distance = INFINITY;
        for (int k = from; k < to; k++) {
            int c = pending.items[k];
            if (c < 0) {
                continue;
            }
            if (best_slot < 0) {
                best_slot = k;
                if (!has_position) {
                    break;
                }
            }
            for (int m = 0; m < nodes[c].count; m++) {
                int cell = nodes[c].items[m];
                float dx = mn_center_x(g, cell % width) - position.x;
                float dy = mn_center_y(g, cell / width) - position.y;
                float d = dx * dx + dy * dy;
                if (d < best_distance) {
                    best_distance = d;
                    best_slot = k;
                }
            }
        }
        int cave_index = pending.items[best_slot];
        pending.items[best_slot] = -1;
        const mn_cave* cave = &tree.caves[cave_index];
        float level = plan->levels[cave->level];
        const mn_ints* cave_nodes = &nodes[cave_index];
        const int* cave_cells = tree.cells.items + cave->cells_first;
        if (cave_nodes->count > 0) {
            pass++;
            int count = cave_nodes->count;
            for (int k = 0; k < count; k++) {
                int cell = cave_nodes->items[k];
                xs[k] = mn_center_x(g, cell % width);
                ys[k] = mn_center_y(g, cell / width);
                zs[k] = level;
            }
            memcpy(clearance, planned, (size_t)cells * sizeof(float));
            for (int m = 0; m < cave->cells_count; m++) {
                clearance[cave_cells[m]] = level;
            }
            status = guard_evaluate(&guard, xs, ys, zs, &count, level, 1, clearance, cave_cells, cave->cells_count);
            if (status == MN_OK && guard.clearing.count > 0) {
                status = clearing_route(context, &guard, level, planned, clearance, inside, &budget, nodes_left, monitor, writer);
                if (status == MN_OK) {
                    memcpy(clearance, planned, (size_t)cells * sizeof(float));
                    for (int m = 0; m < cave->cells_count; m++) {
                        clearance[cave_cells[m]] = level;
                    }
                    status = guard_evaluate(&guard, xs, ys, zs, &count, level, 0, clearance, cave_cells, cave->cells_count);
                }
            }
            if (status == MN_OK) {
                memcpy(clearance, planned, (size_t)cells * sizeof(float));
                for (int m = 0; m < cave->cells_count; m++) {
                    int cell = cave_cells[m];
                    clearance[cell] = guard_floor(&guard, cell, level);
                }
                mn_route_grid grid = { *g, clearance };
                status = route_nodes(&grid, xs, ys, zs, count, &guard, &budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
                guard_stamp(&guard, xs, ys, zs, count);
            }
            nodes_left -= cave_nodes->count;
            if (status == MN_OK) {
                mn_report(monitor, pass, pass_count, (float)(total - nodes_left) / (float)total);
            }
        }
        for (int m = 0; m < cave->cells_count; m++) {
            int cell = cave_cells[m];
            planned[cell] = guard_floor(&guard, cell, level);
        }

        const mn_ints* cut = &cut_nodes[cave_index];
        if (cut->count > 0 && status == MN_OK) {
            pass++;
            float next_level = cave->level + 1 < plan->count ? plan->levels[cave->level + 1] : -INFINITY;
            memset(inside, 0, (size_t)cells);
            should_cut_cells(context->effective_tip, touches, cave_cells, cave->cells_count, level, next_level, inside);
            status = tip_route(context, inside, cut, -INFINITY, planned, clearance, xs, ys, zs, &guard, 1, &budget, nodes_left, monitor, writer);
            nodes_left -= cut->count;
            if (status == MN_OK) {
                mn_report(monitor, pass, pass_count, (float)(total - nodes_left) / (float)total);
            }
        }

        int child_first = pending.count;
        for (int k = 0; k < cave->children.count && status == MN_OK; k++) {
            status = mn_ints_push(&pending, cave->children.items[k]);
        }
        list_start[depth] = child_first;
        list_end[depth] = pending.count;
        depth++;
    }

    if (status == MN_OK) {
        status = mn_writer_take(writer, result);
    }

done:
    if (nodes != NULL) {
        for (int c = 0; c < tree.cave_count; c++) {
            mn_ints_free(&nodes[c]);
        }
    }
    if (cut_nodes != NULL) {
        for (int c = 0; c < tree.cave_count; c++) {
            mn_ints_free(&cut_nodes[c]);
        }
    }
    free(list_start);
    free(list_end);
    free(nodes);
    free(cut_nodes);
    free(touches);
    free(inside);
    free(planned);
    free(clearance);
    free(xs);
    free(ys);
    free(zs);
    mn_ints_free(&pending);
    mn_ints_free(&top_nodes);
    mn_profile_free(&profile);
    mn_writer_free(writer);
    guard_free(&guard);
    cave_tree_free(&tree);
    return status;
}

/* ---- 3 axis freedom ---- */

/* True where a neighbour's height differs by more than the tolerance or holds no material. */
static int is_step(const mn_grid* g, const float* map, int i, int j, float tolerance)
{
    float z = map[j * g->width + i];
    for (int dj = -1; dj <= 1; dj++) {
        for (int di = -1; di <= 1; di++) {
            int ii = i + di;
            int jj = j + dj;
            if ((di == 0 && dj == 0) || !mn_in_bounds(g, ii, jj)) {
                continue;
            }
            float n = map[jj * g->width + ii];
            if (mn_isnan(n) || fabsf(n - z) > tolerance) {
                return 1;
            }
        }
    }
    return 0;
}

MN_API int32_t mn_is_step(const mn_grid* grid, const float* map, int32_t i, int32_t j, float tolerance) { return is_step(grid, map, i, j, tolerance); }

static void level_map_of(const float* tip, int cells, float level, float* result)
{
    for (int k = 0; k < cells; k++) {
        float z = tip[k];
        result[k] = (!mn_isnan(z) && z < level) ? level : z;
    }
}

MN_API void mn_level_map(const float* tip, int32_t cells, float level, float* result) { level_map_of(tip, cells, level, result); }

static int compare_ints(const void* left, const void* right)
{
    int a = *(const int*)left;
    int b = *(const int*)right;
    return a < b ? -1 : a > b ? 1 : 0;
}

/* Free 3-axis moves over the surface, one stepdown at a time: at level L the nodes are the coverage
 * cells whose tip lies below the previous level (lattice at the finishing stepover plus every step
 * cell of the level map max(tip, L)), each at max(tip, L); one route per level over that level map.
 * In one run mode the guard evaluates the nodes of every level against the material the previous
 * levels left; a clearing route runs over the previous level map first when it needs one. */
int mn_three_axis_freedom(const mn_context* context, const mn_monitor* monitor, mn_segments* result)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    const float* map = context->effective_tip;
    const float* stock = context->stock;
    int cells = mn_cells(g);
    int width = g->width;
    int height = g->height;
    int levels = plan->count;
    int status = MN_OK;
    int step = mn_lattice_step(context->parameters.finishing_stepover, g->cell_size);
    float tolerance = context->parameters.tolerance;
    mn_guard guard;
    MN_CHECK(guard_init(&guard, context));

    mn_ints* passes = (mn_ints*)mn_alloc((size_t)(levels > 0 ? levels : 1), sizeof(mn_ints));
    float* level_maps = (float*)mn_alloc((size_t)(levels > 0 ? levels : 1) * (size_t)cells, sizeof(float));
    uint8_t* inside = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* marked = (uint8_t*)mn_alloc((size_t)cells, 1);
    mn_ints region = { 0 };
    float* planned = NULL;
    float* clearance = NULL;
    float* xs = NULL;
    float* ys = NULL;
    float* zs = NULL;
    mn_writer* writer = NULL;
    if (passes == NULL || level_maps == NULL || inside == NULL || marked == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the 3 axis strategy.");
        goto done;
    }
    if (guard.enabled) {
        planned = (float*)mn_alloc((size_t)cells, sizeof(float));
        clearance = (float*)mn_alloc((size_t)cells, sizeof(float));
        if (planned == NULL || clearance == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for the 3 axis strategy.");
            goto done;
        }
    }
    status = writer_for(context, &writer);
    if (status != MN_OK) {
        goto done;
    }
    if (guard.enabled) {
        mn_writer_guard(writer, guard.material, &guard.profile, context->tool.cutter_diameter / 2.0f, context->tool.cutter_length);
    }

    int64_t nodes_left = 0;
    int max_nodes = 0;
    float above = context->stock_top;
    for (int l = 0; l < levels && status == MN_OK; l++) {
        float level = plan->levels[l];
        float previous = above;
        float* level_map = level_maps + (size_t)l * (size_t)cells;
        for (int k = 0; k < cells; k++) {
            float tip = map[k];
            inside[k] = (uint8_t)(plan->coverage[k] && !mn_isnan(tip) && tip < previous - MN_LEVEL_TOLERANCE && stock[k] > level + MN_LEVEL_TOLERANCE);
            marked[k] = 0;
        }
        level_map_of(map, cells, level, level_map);
        status = mn_lattice(inside, width, height, step, &passes[l]);
        for (int k = 0; k < passes[l].count; k++) {
            marked[passes[l].items[k]] = 1;
        }
        for (int j = 0; j < height && status == MN_OK; j++) {
            for (int i = 0; i < width && status == MN_OK; i++) {
                int c = j * width + i;
                if (!marked[c] && inside[c] && is_step(g, level_map, i, j, tolerance)) {
                    marked[c] = 1;
                    status = mn_ints_push(&passes[l], c);
                }
            }
        }
        if (passes[l].count > 1) {
            qsort(passes[l].items, (size_t)passes[l].count, sizeof(int), compare_ints);
        }
        nodes_left += passes[l].count;
        max_nodes = mn_maxi(max_nodes, passes[l].count);
        above = level;
    }
    if (status != MN_OK) {
        goto done;
    }

    if (nodes_left > 0) {
        xs = (float*)mn_alloc((size_t)max_nodes, sizeof(float));
        ys = (float*)mn_alloc((size_t)max_nodes, sizeof(float));
        zs = (float*)mn_alloc((size_t)max_nodes, sizeof(float));
        if (xs == NULL || ys == NULL || zs == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for the 3 axis strategy.");
            goto done;
        }
        mn_budget budget = { MN_BUDGET_MAX_EVALUATIONS, 0 };
        int64_t total = nodes_left;
        int pass_count = 0;
        for (int l = 0; l < levels; l++) {
            pass_count += passes[l].count > 0 ? 1 : 0;
        }
        int pass = 0;
        for (int l = 0; l < levels && status == MN_OK; l++) {
            if (mn_cancelled(monitor)) {
                status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
                break;
            }
            if (passes[l].count == 0) {
                continue;
            }
            pass++;
            const float* level_map = level_maps + (size_t)l * (size_t)cells;
            int count = passes[l].count;
            for (int k = 0; k < count; k++) {
                int cell = passes[l].items[k];
                xs[k] = mn_center_x(g, cell % width);
                ys[k] = mn_center_y(g, cell / width);
                zs[k] = level_map[cell];
            }
            if (guard.enabled) {
                /* The region of the level: every coverage cell the level works on. */
                float previous_level = l > 0 ? plan->levels[l - 1] : context->stock_top;
                region.count = 0;
                for (int c = 0; c < cells && status == MN_OK; c++) {
                    float tip = map[c];
                    if (plan->coverage[c] && !mn_isnan(tip) && tip < previous_level - MN_LEVEL_TOLERANCE && stock[c] > plan->levels[l] + MN_LEVEL_TOLERANCE) {
                        status = mn_ints_push(&region, c);
                    }
                }
            }
            if (status == MN_OK) {
                status = guard_evaluate(&guard, xs, ys, zs, &count, plan->levels[l], 1, level_map, region.items, region.count);
            }
            if (status == MN_OK && guard.clearing.count > 0) {
                /* The clearing route runs over the material the previous level left. */
                const float* previous = l > 0 ? level_maps + (size_t)(l - 1) * (size_t)cells : stock;
                memcpy(planned, previous, (size_t)cells * sizeof(float));
                status = clearing_route(context, &guard, plan->levels[l], planned, clearance, inside, &budget, nodes_left, monitor, writer);
                if (status == MN_OK) {
                    status = guard_evaluate(&guard, xs, ys, zs, &count, plan->levels[l], 0, level_map, region.items, region.count);
                }
            }
            if (status == MN_OK) {
                const float* floor = level_map;
                if (guard.enabled) {
                    for (int k = 0; k < cells; k++) {
                        clearance[k] = guard_floor(&guard, k, level_map[k]);
                    }
                    floor = clearance;
                }
                mn_route_grid grid = { *g, floor };
                status = route_nodes(&grid, xs, ys, zs, count, &guard, &budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
                guard_stamp(&guard, xs, ys, zs, count);
            }
            nodes_left -= passes[l].count;
            if (status == MN_OK) {
                mn_report(monitor, pass, pass_count, (float)(total - nodes_left) / (float)total);
            }
        }
    }

    if (status == MN_OK) {
        status = mn_writer_take(writer, result);
    }

done:
    if (passes != NULL) {
        for (int l = 0; l < levels; l++) {
            mn_ints_free(&passes[l]);
        }
    }
    free(passes);
    free(level_maps);
    free(inside);
    free(marked);
    mn_ints_free(&region);
    free(planned);
    free(clearance);
    free(xs);
    free(ys);
    free(zs);
    mn_writer_free(writer);
    guard_free(&guard);
    return status;
}

MN_API int32_t mn_strategy_generate(int32_t strategy, const mn_context* context, mn_progress_fn progress, void* progress_context, const volatile int32_t* cancel, mn_segment** segments, int32_t* count)
{
    if (context->collision_mode != MN_COLLISION_RECURSION && context->collision_mode != MN_COLLISION_ONE_RUN) {
        return mn_fail(MN_ERR_ARGUMENT, "Unknown collision mode %d.", context->collision_mode);
    }
    if (context->collision_mode == MN_COLLISION_ONE_RUN && !(context->ratio >= 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "The one run ratio must be zero or positive.");
    }
    mn_monitor monitor;
    monitor.progress = progress;
    monitor.context = progress_context;
    monitor.cancel = cancel;
    mn_segments result = { 0 };
    int status;
    if (strategy == MN_STRATEGY_Z_LAYER) {
        status = mn_z_layer(context, &monitor, &result);
    } else if (strategy == MN_STRATEGY_THREE_AXIS_FREEDOM) {
        status = mn_three_axis_freedom(context, &monitor, &result);
    } else {
        status = mn_fail(MN_ERR_ARGUMENT, "Unknown strategy %d.", strategy);
    }
    if (status != MN_OK) {
        mn_segments_free(&result);
        return status;
    }
    *segments = result.items != NULL ? result.items : (mn_segment*)mn_alloc(1, sizeof(mn_segment));
    *count = result.count;
    return MN_OK;
}
