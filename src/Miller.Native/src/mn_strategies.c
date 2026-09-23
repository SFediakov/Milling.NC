#include "mn_internal.h"

/* Node lattice, cave tree and the two routing strategies (NodeLattice, CaveTree,
 * ZLayerByLayerStrategy, ThreeAxisFreedomStrategy). */

/* NodeLattice.StepEpsilon: guards floor() against 3 / 0.5 evaluating to 5.9999995. */
#define MN_STEP_EPSILON 1e-4f

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

/* ---- shared helpers of the strategies ---- */

/* The node nearest in XY to the tool, or the first node before the program starts. */
static int nearest_node(const float* x, const float* y, int count, const mn_writer* writer)
{
    mn_v3 from;
    if (!mn_writer_has_position(writer, &from)) {
        return 0;
    }
    int best = 0;
    float best_distance = INFINITY;
    for (int k = 0; k < count; k++) {
        float dx = x[k] - from.x;
        float dy = y[k] - from.y;
        float d = dx * dx + dy * dy;
        if (d < best_distance) {
            best_distance = d;
            best = k;
        }
    }
    return best;
}

/* Solves one route over the nodes and writes it: travel to the first node, follow the rest. */
static int route_nodes(const mn_route_grid* grid, const float* x, const float* y, const float* z, int count, mn_budget* budget, int64_t nodes_left, const volatile int32_t* cancel, mn_writer* writer)
{
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
    int start = nearest_node(x, y, count, writer);
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

/* ---- Z layer by layer ---- */

/* Cells whose cutter footprint holds a should-cut cell (T-136). */
static int footprint_touches(const mn_context* context, uint8_t* touches)
{
    const mn_grid* g = &context->grid;
    mn_profile profile;
    MN_CHECK(mn_profile_build(&context->tool, g->cell_size, &profile));
    for (int j = 0; j < g->height; j++) {
        for (int i = 0; i < g->width; i++) {
            uint8_t found = 0;
            for (int o = 0; o < profile.offset_count && !found; o++) {
                int ii = i + profile.offsets[o].dx;
                int jj = j + profile.offsets[o].dy;
                found = (uint8_t)(mn_in_bounds(g, ii, jj) && context->should_cut[jj * g->width + ii]);
            }
            touches[j * g->width + i] = found;
        }
    }
    mn_profile_free(&profile);
    return MN_OK;
}

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

/* One should-cut route over the pass cells in `inside`: the nodes at their tip, the pass cells as
 * floor at their tip; the pass cells keep that height in `planned` afterwards. */
static int should_cut_route(const mn_context* context, const uint8_t* inside, const mn_ints* cut, float* planned, float* clearance, float* xs, float* ys, float* zs, mn_budget* budget,
    int64_t nodes_left, const mn_monitor* monitor, mn_writer* writer)
{
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    for (int k = 0; k < cells; k++) {
        clearance[k] = inside[k] ? context->effective_tip[k] : planned[k];
    }
    mn_route_grid grid = { *g, clearance };
    for (int k = 0; k < cut->count; k++) {
        int cell = cut->items[k];
        xs[k] = mn_center_x(g, cell % g->width);
        ys[k] = mn_center_y(g, cell / g->width);
        zs[k] = context->effective_tip[cell];
    }
    int status = route_nodes(&grid, xs, ys, zs, cut->count, budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
    memcpy(planned, clearance, (size_t)cells * sizeof(float));
    return status;
}

/* Cave by cave, level by level: a cave is cut completely at its level along the fastest route through
 * its nodes, then the tool drops one level into the first child cave, and it rises for the next
 * sibling only when a whole subtree is done. Every route is solved over the material as it will stand
 * at that moment. After the level route of a cave, a should-cut route (T-136) visits the cave cells
 * over should-cut stock whose tip lies between this level and the next at their tip (every such cell
 * is a node), so that stock is at its closing before the tool goes deeper
 * beside it; the band between the stock top and the first level, which belongs to no cave, gets its
 * should-cut route first. Without should-cut cells there is no such route. */
int mn_z_layer(const mn_context* context, const mn_monitor* monitor, mn_segments* result)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    int cells = mn_cells(g);
    int width = g->width;
    int status = MN_OK;
    mn_cave_tree tree;
    MN_CHECK(cave_tree_build(plan, &tree));

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
    int* stack = (int*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1) + 1, sizeof(int));
    mn_ints pending = { 0 };
    mn_ints top_nodes = { 0 };
    float* xs = NULL;
    float* ys = NULL;
    float* zs = NULL;
    mn_writer* writer = NULL;
    if (nodes == NULL || cut_nodes == NULL || touches == NULL || inside == NULL || planned == NULL || clearance == NULL || stack == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    if (context->should_cut != NULL) {
        status = footprint_touches(context, touches);
        if (status != MN_OK) {
            goto done;
        }
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

    mn_budget budget = { MN_BUDGET_MAX_EVALUATIONS, 0 };
    int64_t total = nodes_left;
    int pass = 0;
    if (top_nodes.count > 0) {
        pass++;
        top_band_cells(context, touches, inside);
        status = should_cut_route(context, inside, &top_nodes, planned, clearance, xs, ys, zs, &budget, nodes_left, monitor, writer);
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
    int* list_start = (int*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1) + 1, sizeof(int));
    int* list_end = (int*)mn_alloc((size_t)(tree.cave_count > 0 ? tree.cave_count : 1) + 1, sizeof(int));
    if (list_start == NULL || list_end == NULL) {
        free(list_start);
        free(list_end);
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
            memcpy(clearance, planned, (size_t)cells * sizeof(float));
            for (int m = 0; m < cave->cells_count; m++) {
                clearance[cave_cells[m]] = level;
            }
            mn_route_grid grid = { *g, clearance };
            for (int k = 0; k < cave_nodes->count; k++) {
                int cell = cave_nodes->items[k];
                xs[k] = mn_center_x(g, cell % width);
                ys[k] = mn_center_y(g, cell / width);
                zs[k] = level;
            }
            status = route_nodes(&grid, xs, ys, zs, cave_nodes->count, &budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
            nodes_left -= cave_nodes->count;
            if (status == MN_OK) {
                mn_report(monitor, pass, pass_count, (float)(total - nodes_left) / (float)total);
            }
        }
        for (int m = 0; m < cave->cells_count; m++) {
            planned[cave_cells[m]] = level;
        }

        const mn_ints* cut = &cut_nodes[cave_index];
        if (cut->count > 0 && status == MN_OK) {
            pass++;
            float next_level = cave->level + 1 < plan->count ? plan->levels[cave->level + 1] : -INFINITY;
            memset(inside, 0, (size_t)cells);
            should_cut_cells(context->effective_tip, touches, cave_cells, cave->cells_count, level, next_level, inside);
            status = should_cut_route(context, inside, cut, planned, clearance, xs, ys, zs, &budget, nodes_left, monitor, writer);
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
    free(list_start);
    free(list_end);

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
    free(nodes);
    free(cut_nodes);
    free(touches);
    free(inside);
    free(planned);
    free(clearance);
    free(stack);
    free(xs);
    free(ys);
    free(zs);
    mn_ints_free(&pending);
    mn_ints_free(&top_nodes);
    mn_writer_free(writer);
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
 * cell of the level map max(tip, L)), each at max(tip, L); one route per level over that level map. */
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

    mn_ints* passes = (mn_ints*)mn_alloc((size_t)(levels > 0 ? levels : 1), sizeof(mn_ints));
    float* level_maps = (float*)mn_alloc((size_t)(levels > 0 ? levels : 1) * (size_t)cells, sizeof(float));
    uint8_t* inside = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* marked = (uint8_t*)mn_alloc((size_t)cells, 1);
    float* xs = NULL;
    float* ys = NULL;
    float* zs = NULL;
    mn_writer* writer = NULL;
    if (passes == NULL || level_maps == NULL || inside == NULL || marked == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the 3 axis strategy.");
        goto done;
    }
    status = writer_for(context, &writer);
    if (status != MN_OK) {
        goto done;
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
            mn_route_grid grid = { *g, level_map };
            for (int k = 0; k < passes[l].count; k++) {
                int cell = passes[l].items[k];
                xs[k] = mn_center_x(g, cell % width);
                ys[k] = mn_center_y(g, cell / width);
                zs[k] = level_map[cell];
            }
            status = route_nodes(&grid, xs, ys, zs, passes[l].count, &budget, nodes_left, monitor != NULL ? monitor->cancel : NULL, writer);
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
    free(xs);
    free(ys);
    free(zs);
    mn_writer_free(writer);
    return status;
}

typedef struct stage_bridge {
    mn_progress_fn progress;
    void* context;
} stage_bridge;

MN_API int32_t mn_strategy_generate(int32_t strategy, const mn_context* context, mn_progress_fn progress, void* progress_context, const volatile int32_t* cancel, mn_segment** segments, int32_t* count)
{
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
