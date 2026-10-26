#include <stdio.h>

#include "mn_gpu.h"
#include "mn_thread.h"

/* Node lattice, cave tree and the routing strategy (NodeLattice, CaveTree, ZLayerByLayerStrategy). */

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

mn_box mn_box_of_cells(const mn_grid* g, const int* cells, int count)
{
    mn_box box = mn_box_empty();
    for (int k = 0; k < count; k++) {
        mn_box_include(&box, cells[k] % g->width, cells[k] / g->width);
    }
    return box;
}

/* The region's cells on the square lattice plus its outline cells, in row-major order. */
int mn_lattice(const uint8_t* inside, int width, int height, int step, mn_ints* nodes)
{
    mn_box box = { 0, 0, width - 1, height - 1 };
    return mn_lattice_in(inside, width, height, step, box, nodes);
}

int mn_lattice_in(const uint8_t* inside, int width, int height, int step, mn_box box, mn_ints* nodes)
{
    return mn_lattice_rows_in(inside, width, height, step, step, box, nodes);
}

/* The region's cells on rows `row_step` cells apart with a node every `column_step` cells along a row,
 * plus its outline cells, in row-major order; the square lattice has both steps equal. */
int mn_lattice_rows_in(const uint8_t* inside, int width, int height, int row_step, int column_step, mn_box box, mn_ints* nodes)
{
    if (row_step < 1 || column_step < 1) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Lattice step must be at least one cell.");
    }
    for (int j = box.j0; j <= box.j1; j++) {
        for (int i = box.i0; i <= box.i1; i++) {
            if (!inside[j * width + i]) {
                continue;
            }
            if ((on_lattice(i, width, column_step) && on_lattice(j, height, row_step)) || mn_outline(inside, width, height, i, j)) {
                MN_CHECK(mn_ints_push(nodes, j * width + i));
            }
        }
    }
    return MN_OK;
}

/* Whether nodes on rows `a` cells apart, `b` cells apart along a row, reach every cell between them:
 * the cell farthest from the four nodes of a rectangle, by the footprint's rule (mn_profile_build). */
static int rows_covered(int a, int b, float radius, float cell_size)
{
    for (int y = 0; y <= a; y++) {
        for (int x = 0; x <= b; x++) {
            int dx = mn_mini(x, b - x);
            int dy = mn_mini(y, a - y);
            if (cell_size * sqrtf((float)(dx * dx + dy * dy)) > radius + MN_RADIUS_TOLERANCE) {
                return 0;
            }
        }
    }
    return 1;
}

/* The node step along a row (T-210): the largest step up to the row step whose node footprints alone
 * cover the cells between two rows. A square lattice up to r * sqrt(2) apart covers and keeps its
 * step; wider rows get their nodes closer along the row, so no cell between the rows waits for a gap
 * node off the row and a route runs straight along the rows, turning at their ends. */
int mn_lattice_column_step(int row_step, float cutter_radius, float cell_size)
{
    for (int b = row_step; b > 1; b--) {
        if (rows_covered(row_step, b, cutter_radius, cell_size)) {
            return b;
        }
    }
    return 1;
}

MN_API int32_t mn_column_step(int32_t row_step, float cutter_radius, float cell_size) { return mn_lattice_column_step(row_step, cutter_radius, cell_size); }

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
            /* The parent holds any cell of the cave: with nested masks that is the first cell, in a
             * group plan (T-157) a far cell rejoins the masks below its step and has no cave above. */
            int parent = -1;
            if (k > 0) {
                const int* above = tree->labels + (size_t)(k - 1) * (size_t)cells;
                for (int m = 0; m < cave->cells_count && parent < 0; m++) {
                    parent = above[tree->cells.items[cave->cells_first + m]];
                }
            }
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

static void cover_footprint(const mn_grid* g, const mn_profile* profile, int cell, uint8_t* covered)
{
    int ci = cell % g->width;
    int cj = cell / g->width;
    for (int o = 0; o < profile->offset_count; o++) {
        int i = ci + profile->offsets[o].dx;
        int j = cj + profile->offsets[o].dy;
        if (mn_in_bounds(g, i, j)) {
            covered[j * g->width + i] = 1;
        }
    }
}

/* Every cell of the region lies under the cutter of a node: a lattice wider than the cutter radius
 * times sqrt(2) leaves cells no node footprint reaches, and the route between the nodes does not
 * always pass over them, so the stock there stood as a spike through every level. Each such cell, in
 * row-major order, becomes a node itself. The region lies in `box`; `covered` is read only there, so
 * only there it is cleared. */
static int cover_gaps(const uint8_t* inside, const mn_grid* g, const mn_profile* profile, mn_box box, mn_ints* nodes, uint8_t* covered)
{
    int width = g->width;
    for (int j = box.j0; j <= box.j1; j++) {
        memset(covered + (size_t)j * (size_t)width + (size_t)box.i0, 0, (size_t)(box.i1 - box.i0 + 1));
    }
    for (int n = 0; n < nodes->count; n++) {
        cover_footprint(g, profile, nodes->items[n], covered);
    }
    for (int j = box.j0; j <= box.j1; j++) {
        for (int i = box.i0; i <= box.i1; i++) {
            int c = j * width + i;
            if (inside[c] && !covered[c]) {
                MN_CHECK(mn_ints_push(nodes, c));
                cover_footprint(g, profile, c, covered);
            }
        }
    }
    return MN_OK;
}

/* The route floor and touch floor (`lifted`, cells each) over material no route has taken, whose
 * floor still stands at its stock height. A move through such a cell would run on the stock top, so
 * the cell rises to the safe plane (stock top plus the safe height). A point on its edge or corner, a
 * diagonal step between neighbouring nodes, only has to stay above what must remain there, the
 * strategy tip: the footprints of the nodes cut that stock anyway. Where what must remain reaches the
 * stock top (a model face or standing stock flush with it), the point takes the safe plane as well;
 * nothing moves on the stock top. */
static void lift_uncut(const mn_context* context, const float* floor, mn_box box, float* lifted)
{
    int width = context->grid.width;
    int cells = mn_cells(&context->grid);
    const float* stock = context->stock;
    float safe = context->stock_top + context->parameters.safe_height;
    float* touch = lifted + cells;
    for (int j = box.j0; j <= box.j1; j++) {
        for (int i = box.i0; i <= box.i1; i++) {
            int c = j * width + i;
            float f = floor[c];
            float s = stock[c];
            if (mn_isnan(s) || mn_isnan(f) || f < s - MN_LEVEL_TOLERANCE) {
                lifted[c] = f;
                touch[c] = f;
                continue;
            }
            float remains = context->effective_tip[c];
            lifted[c] = mn_max(f, safe);
            touch[c] = !mn_isnan(remains) && remains >= s - MN_LEVEL_TOLERANCE ? mn_max(remains, safe) : remains;
        }
    }
}

/* The cells a route reads (T-185): mn_trace reads the cells between two of its points and `touching`
 * one cell beyond, and every point is a node or the tool's position before the route, so the box of
 * those cells grown by one cell holds every read; the route floors are built there only. */
static mn_box route_box(const mn_grid* g, const float* x, const float* y, int count, const mn_writer* writer)
{
    mn_box box = mn_box_empty();
    for (int k = 0; k < count; k++) {
        mn_box_include(&box, mn_clampi(mn_cell_i(g, x[k]), 0, g->width - 1), mn_clampi(mn_cell_j(g, y[k]), 0, g->height - 1));
    }
    mn_v3 at;
    if (mn_writer_has_position(writer, &at)) {
        mn_box_include(&box, mn_clampi(mn_cell_i(g, at.x), 0, g->width - 1), mn_clampi(mn_cell_j(g, at.y), 0, g->height - 1));
    }
    return mn_box_grow(box, 1, g);
}

/* `target` takes `source` over the cells of `box`. */
static void copy_box(float* target, const float* source, const mn_grid* g, mn_box box)
{
    for (int j = box.j0; j <= box.j1; j++) {
        size_t row = (size_t)j * (size_t)g->width + (size_t)box.i0;
        memcpy(target + row, source + row, (size_t)(box.i1 - box.i0 + 1) * sizeof(float));
    }
}

/* The node nearest in XY to the tool, or the first node before the program starts; `exclude` (the
 * fixed end of a route of more than one node, -1 for none) never starts it. */
static int nearest_node(const float* x, const float* y, int count, int exclude, const mn_writer* writer)
{
    mn_v3 from;
    int has_position = mn_writer_has_position(writer, &from);
    int best = -1;
    float best_distance = INFINITY;
    for (int k = 0; k < count; k++) {
        if (k == exclude && count > 1) {
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

/* Solves one route over the nodes and writes it: travel to the first node, follow the rest, both over
 * the route floor with the uncut stock lifted to the safe plane (`lifted_floor` receives the lifted
 * route and touch floors, cells each, over `box`, route_box of the nodes; `grid->floor` must hold the
 * route floor there). `end` is the node the route must finish at (the exit of a route unit, T-166),
 * -1 for none. */
static int route_nodes(const mn_context* context, const mn_route_grid* grid, mn_box box, float* lifted_floor, const float* x, const float* y, const float* z, int count, int end, mn_budget* budget, int64_t nodes_left, const volatile int32_t* cancel, mn_writer* writer)
{
    if (count == 0) {
        return MN_OK;
    }
    int* order = (int*)mn_alloc((size_t)count, sizeof(int));
    if (order == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a route of %d nodes.", count);
    }
    double started = mn_seconds();
    lift_uncut(context, grid->floor, box, lifted_floor);
    mn_time_add(MN_TIME_FLOORS, mn_seconds() - started);
    int status = MN_OK;
    mn_route_grid lifted = { grid->g, lifted_floor, lifted_floor + mn_cells(&grid->g) };
    mn_problem problem;
    problem.grid = lifted;
    problem.x = x;
    problem.y = y;
    problem.z = z;
    problem.count = count;
    problem.gpu_tours = context->collision_mode == MN_COLLISION_BACKWARDS_GPU;
    int start = nearest_node(x, y, count, end, writer);
    int64_t evaluations = 0;
    status = mn_solve(&problem, start, count > 1 ? end : -1, mn_share(budget, count, nodes_left), cancel, order, &evaluations);
    if (status == MN_OK) {
        budget->used += evaluations;
        started = mn_seconds();
        status = mn_writer_travel_to(writer, mn_problem_node(&problem, order[0]), &lifted);
        for (int k = 1; k < count && status == MN_OK; k++) {
            status = mn_writer_follow_to(writer, mn_problem_node(&problem, order[k]), &lifted);
        }
        mn_time_add(MN_TIME_WRITE, mn_seconds() - started);
    }
    free(order);
    return status;
}

static int writer_for(const mn_context* context, mn_writer** writer) { return mn_writer_create(&context->parameters, context->stock_top + context->parameters.safe_height, writer); }

/* The exit of a route unit (T-166) as one more node after the route's own; returns the new count. */
static int append_exit(float* xs, float* ys, float* zs, int count, const mn_v3* exit)
{
    xs[count] = exit->x;
    ys[count] = exit->y;
    zs[count] = exit->z;
    return count + 1;
}

/* Where the exit lies among the nodes: the last node at its position, -1 when there is no exit. */
static int exit_index(const float* xs, const float* ys, const float* zs, int count, const mn_v3* exit)
{
    if (exit == NULL) {
        return -1;
    }
    int end = -1;
    for (int k = count - 1; k >= 0 && end < 0; k--) {
        if (xs[k] == exit->x && ys[k] == exit->y && zs[k] == exit->z) {
            end = k;
        }
    }
    return end;
}

/* ---- Z layer by layer ---- */

/* Cave by cave, level by level: a cave is cut completely at its level along the fastest route through
 * its nodes, then the tool drops one level into the first child cave, and it rises for the next
 * sibling only when a whole subtree is done. Every route is solved over the material as it will stand
 * at that moment. With a far stepdown (T-156, k times the stepdown) the levels
 * form groups of k: the far region of the group's bottom level (its mask at a distance of at least the
 * stepover from everything that stays above that level) is routed first, in steps of at most the
 * cutter length from where the material stands (T-157, far_steps_of), then the caves of the group's
 * levels without the far cells run as above, and the next group starts at the level the far block
 * reached; a group of one level has no far block. */

/* The shared state of the routes of one generation: the maps the routes read and write, the scratch
 * arrays, the budget and the progress counters. */
typedef struct mn_z_state {
    const mn_context* context;
    const mn_monitor* monitor;
    const mn_profile* profile;
    mn_writer* writer;
    float* planned;
    float* clearance;
    float* lifted;
    uint8_t* inside;
    uint8_t* covered;
    float* xs;
    float* ys;
    float* zs;
    mn_budget budget;
    int64_t nodes_left;
    int64_t total;
    /* Route units (T-167): the nodes of the units before this one and of this one; the progress is
     * (done + unit_nodes - nodes_left) / total. */
    int64_t done;
    int64_t unit_nodes;
    int pass;
    int pass_count;
    int step;
    int column_step;
} mn_z_state;

/* One step of the far block: its plan level, the far positions cut to it in one step and their nodes. */
typedef struct mn_z_step {
    int level;
    mn_ints cells;
    mn_ints nodes;
    /* The slab cave (T-166) of every cell and node above. */
    mn_ints cell_comp;
    mn_ints node_comp;
} mn_z_step;

/* One group of levels: its plan (the context's plan itself without a far stepdown; otherwise the
 * group's levels with the far cells taken out of the masks down to the step that cuts them), its cave
 * tree, the nodes per cave, and the steps of its far block. */
typedef struct mn_z_group {
    mn_plan plan;
    int owns_plan;
    int first;
    int last;
    mn_cave_tree tree;
    mn_ints* nodes;
    mn_z_step* steps;
    int step_count;
    /* The caves of the slab (T-166): the connected areas of the union of its level masks; per cave of
     * the tree the one it lies in, per slab cave its nodes and its caves of the tree. */
    int comp_count;
    int* cave_comp;
    int64_t* comp_nodes;
    int* comp_caves;
} mn_z_group;

static void z_group_free(mn_z_group* group)
{
    for (int c = 0; c < group->tree.cave_count; c++) {
        if (group->nodes != NULL) {
            mn_ints_free(&group->nodes[c]);
        }
    }
    free(group->nodes);
    cave_tree_free(&group->tree);
    if (group->owns_plan) {
        free(group->plan.levels);
        free(group->plan.masks);
    }
    for (int t = 0; t < group->step_count; t++) {
        mn_ints_free(&group->steps[t].cells);
        mn_ints_free(&group->steps[t].nodes);
        mn_ints_free(&group->steps[t].cell_comp);
        mn_ints_free(&group->steps[t].node_comp);
    }
    free(group->steps);
    free(group->cave_comp);
    free(group->comp_nodes);
    free(group->comp_caves);
    memset(group, 0, sizeof(*group));
}

/* The far region of a level (T-156): the cells of its mask at least the stepover away from every cell
 * whose strategy tip stands above the level (the model, the standing stock and the raised positions;
 * cells without a tip are no obstacle). The footprints of those positions cover exactly the material
 * at least the stepover away from what stays, the band nearer to it is left to the other positions.
 * A position withdrawn around a collision (T-164, far_allowed) is never far. */
static int far_cells_of(const mn_context* context, const uint8_t* mask, float level, mn_ints* far)
{
    const mn_grid* g = &context->grid;
    const uint8_t* allowed = context->far_allowed;
    int cells = mn_cells(g);
    uint8_t* obstacles = (uint8_t*)mn_alloc((size_t)cells, 1);
    float* distance = (float*)mn_alloc((size_t)cells, sizeof(float));
    int status = MN_OK;
    if (obstacles == NULL || distance == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the far region.");
    } else {
        for (int c = 0; c < cells; c++) {
            float tip = context->effective_tip[c];
            obstacles[c] = (uint8_t)(!mn_isnan(tip) && tip > level + MN_LEVEL_TOLERANCE);
        }
        status = mn_distance_transform(obstacles, g->width, g->height, g->cell_size, distance);
        for (int c = 0; c < cells && status == MN_OK; c++) {
            if (mask[c] && (allowed == NULL || allowed[c]) && distance[c] + MN_LEVEL_TOLERANCE >= context->parameters.stepover) {
                status = mn_ints_push(far, c);
            }
        }
    }
    free(obstacles);
    free(distance);
    return status;
}

/* Whether the whole footprint of a position lies in the region: the position stands at least the
 * cutter radius from every wall. */
static int footprint_inside(const uint8_t* inside, const mn_grid* g, const mn_profile* profile, int cell)
{
    int ci = cell % g->width;
    int cj = cell / g->width;
    for (int o = 0; o < profile->offset_count; o++) {
        int i = ci + profile->offsets[o].dx;
        int j = cj + profile->offsets[o].dy;
        if (!mn_in_bounds(g, i, j) || !inside[j * g->width + i]) {
            return 0;
        }
    }
    return 1;
}

/* The nodes of a region: its outline cells, which trace the walls, the lattice at the stepover and every
 * cell no footprint covers. A cave's routes (`rows`, T-210) take the lattice as rows
 * (mn_lattice_column_step) and only where a node stands at least the cutter radius from every wall: a
 * nearer node cuts what the outline nodes cut and was the reason of a detour out to it and back from the
 * wall pass, and a band no wider than the cutter keeps its wall passes only. The far block keeps the
 * square lattice, whose positions its step rules were measured with (T-157, T-162). The region
 * (`inside`) lies in `box` and `inside` is zero around it. */
static int region_nodes(mn_z_state* s, const uint8_t* inside, mn_box box, int rows, mn_ints* nodes)
{
    const mn_grid* g = &s->context->grid;
    int first = nodes->count;
    MN_CHECK(mn_lattice_rows_in(inside, g->width, g->height, s->step, rows ? s->column_step : s->step, box, nodes));
    if (rows) {
        int kept = first;
        for (int n = first; n < nodes->count; n++) {
            int c = nodes->items[n];
            if (mn_outline(inside, g->width, g->height, c % g->width, c / g->width) || footprint_inside(inside, g, s->profile, c)) {
                nodes->items[kept++] = c;
            }
        }
        nodes->count = kept;
    }
    return cover_gaps(inside, g, s->profile, box, nodes, s->covered);
}

/* The nodes of the listed cells: marked in s->inside, which is zero outside a marking and zero again
 * afterwards (T-185), so the lattice reads the box of the cells only. */
static int list_nodes(mn_z_state* s, const int* cells, int count, int rows, mn_ints* nodes)
{
    const mn_grid* g = &s->context->grid;
    for (int m = 0; m < count; m++) {
        s->inside[cells[m]] = 1;
    }
    int status = region_nodes(s, s->inside, mn_box_of_cells(g, cells, count), rows, nodes);
    for (int m = 0; m < count; m++) {
        s->inside[cells[m]] = 0;
    }
    return status;
}

static void count_pass(mn_z_state* s, const mn_ints* nodes, int* max_nodes)
{
    s->nodes_left += nodes->count;
    s->pass_count += nodes->count > 0 ? 1 : 0;
    *max_nodes = mn_maxi(*max_nodes, nodes->count);
}

/* The steps of a group's far block (T-157): a single step never goes deeper than the cutter length,
 * because the flutes end there and the material ahead above them meets the head (or, under a head
 * narrower than the cutter, stays uncut above the flutes) whatever the order of the nodes, so the far
 * region is cut in steps of the largest multiple of the stepdown within
 * the cutter length (one step when the far stepdown fits), shallow to deep; which positions each
 * step may take is decided by far_steps_of (T-162). */

/* The cells a far position cuts stand at the step's level from then on. */
static void cover_level(const mn_grid* g, const mn_profile* profile, int cell, float level, float* far_level)
{
    int ci = cell % g->width;
    int cj = cell / g->width;
    for (int o = 0; o < profile->offset_count; o++) {
        int i = ci + profile->offsets[o].dx;
        int j = cj + profile->offsets[o].dy;
        if (mn_in_bounds(g, i, j)) {
            far_level[j * g->width + i] = level;
        }
    }
}

/* Where the larger depth is allowed (T-162): a far position is cut at a step only if the head at that
 * step's level clears what stands at that moment, and a position allowed at a step was allowed at
 * every shallower one, so no route drops more than one step. What stands is the `standing` map (the
 * stock as the levels above the group leave it: the model, uncut stock, the near band at the group's
 * top) and, once a step is assigned, the cells its footprints cut, at that step's level. A cell
 * standing H above the head bottom of a step forbids the positions within R(H) + margin of it, R(H)
 * the head radius there and at least the cutter radius (mn_meet_radius_at, mn_head_margin: a head
 * narrower than the cutter still leaves above the flutes what a footprint cannot cut, T-165); the
 * power-distance lower envelope of those disks tells for
 * every position whether one reaches it. With every standing cell at the group's top this is the
 * threshold rule of T-157; a wall higher than that top forbids the far depth as far as the head
 * reaches it, and the near band cuts those positions level by level. */
static int far_steps_of(mn_z_state* s, mn_z_group* group, const mn_ints* far, const float* standing, int* max_nodes)
{
    const mn_context* context = s->context;
    const mn_plan* plan = context->plan;
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    int first = group->first;
    int last = group->last;
    float stepdown = context->parameters.stepdown;
    float cutter_length = context->tool.cutter_length;
    float margin = mn_head_margin(g->cell_size, context->parameters.tolerance);
    int per_step = mn_maxi(1, mn_f2i(floorf((cutter_length + MN_LEVEL_TOLERANCE) / stepdown)));
    int count = (last - first + per_step) / per_step;
    group->steps = (mn_z_step*)mn_alloc((size_t)count, sizeof(mn_z_step));
    float* far_level = (float*)mn_alloc((size_t)cells, sizeof(float));
    float* field = (float*)mn_alloc((size_t)cells, sizeof(float));
    int* deepest = (int*)mn_alloc((size_t)(far->count > 0 ? far->count : 1), sizeof(int));
    int status = MN_OK;
    if (group->steps == NULL || far_level == NULL || field == NULL || deepest == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the far block of %d steps.", count);
        goto done;
    }
    group->step_count = count;
    for (int t = 0; t < count; t++) {
        group->steps[t].level = mn_mini(first + (t + 1) * per_step - 1, last);
    }
    for (int c = 0; c < cells; c++) {
        far_level[c] = NAN;
    }
    for (int n = 0; n < far->count; n++) {
        deepest[n] = -1;
    }

    for (int t = 0; t < count && status == MN_OK; t++) {
        float z = plan->levels[group->steps[t].level];
        float head_bottom = z + cutter_length;
        int sources = 0;
        for (int c = 0; c < cells; c++) {
            float top = mn_isnan(far_level[c]) ? standing[c] : far_level[c];
            field[c] = INFINITY;
            if (mn_isnan(top)) {
                continue;
            }
            float h = top - head_bottom;
            if (!(h > MN_LEVEL_TOLERANCE)) {
                continue;
            }
            float radius = (mn_meet_radius_at(&context->tool, h) + margin) / g->cell_size;
            field[c] = -radius * radius;
            sources++;
        }
        if (sources > 0) {
            status = mn_lower_envelope(field, g->width, g->height);
            if (status != MN_OK) {
                break;
            }
        }
        mn_z_step* step = &group->steps[t];
        for (int n = 0; n < far->count && status == MN_OK; n++) {
            if (deepest[n] != t - 1) {
                continue;
            }
            int c = far->items[n];
            if (sources > 0 && !(field[c] > 0.0f)) {
                continue;
            }
            deepest[n] = t;
            status = mn_ints_push(&step->cells, c);
        }
        if (step->cells.count == 0) {
            break;
        }
        for (int m = 0; m < step->cells.count; m++) {
            cover_level(g, s->profile, step->cells.items[m], z, far_level);
        }
    }
    for (int n = 0; n < far->count && status == MN_OK; n++) {
        if (deepest[n] < 0) {
            continue;
        }
        int c = far->items[n];
        for (int k = 0; k <= group->steps[deepest[n]].level - first; k++) {
            group->plan.masks[(size_t)k * (size_t)cells + (size_t)c] = 0;
        }
    }
    for (int t = 0; t < count && status == MN_OK; t++) {
        mn_z_step* step = &group->steps[t];
        if (step->cells.count == 0) {
            continue;
        }
        status = list_nodes(s, step->cells.items, step->cells.count, 0, &step->nodes);
        if (status != MN_OK) {
            break;
        }
        count_pass(s, &step->nodes, max_nodes);
    }

done:
    free(far_level);
    free(field);
    free(deepest);
    return status;
}

/* Every cell within the cutter radius of a position of `mask` drops to `level` plus the tool bottom
 * height at its distance. */
static int fold_level(const mn_context* context, const uint8_t* mask, float level, float* distance, float* standing)
{
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    float r = context->tool.cutter_diameter / 2.0f;
    MN_CHECK(mn_distance_transform(mask, g->width, g->height, g->cell_size, distance));
    for (int c = 0; c < cells; c++) {
        float d = distance[c];
        if (!(d <= r + MN_RADIUS_TOLERANCE) || mn_isnan(standing[c])) {
            continue;
        }
        float cut = level + mn_bottom_height(context->tool.tip_type, r, d);
        if (cut < standing[c]) {
            standing[c] = cut;
        }
    }
    return MN_OK;
}

/* The standing map after the levels first..last are cut. */
static int standing_fold(const mn_context* context, int first, int last, float* distance, float* standing)
{
    const mn_plan* plan = context->plan;
    int cells = mn_cells(&context->grid);
    for (int k = first; k <= last; k++) {
        MN_CHECK(fold_level(context, plan->masks + (size_t)k * (size_t)cells, plan->levels[k], distance, standing));
    }
    return MN_OK;
}

/* The slab cave of every cell of `list` into `comp`, adding the count to the cave's nodes when asked. */
static int comp_of_list(const int* labels, const mn_ints* list, mn_ints* comp, int64_t* comp_nodes)
{
    for (int k = 0; k < list->count; k++) {
        int label = labels[list->items[k]];
        if (label < 0) {
            return mn_fail(MN_ERR_STATE, "Cell %d of the far block lies outside the masks of its levels.", list->items[k]);
        }
        MN_CHECK(mn_ints_push(comp, label));
        if (comp_nodes != NULL) {
            comp_nodes[label]++;
        }
    }
    return MN_OK;
}

/* The caves of a slab (T-166): the connected areas of the union of the masks of its levels. Every cave
 * of the group's tree, every far cell and every node lies in exactly one, because the masks nest and a
 * cave is connected. */
static int group_components(mn_z_state* s, mn_z_group* group)
{
    const mn_context* context = s->context;
    const mn_plan* plan = context->plan;
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    for (int c = 0; c < cells; c++) {
        uint8_t any = 0;
        for (int k = group->first; k <= group->last && !any; k++) {
            any = plan->masks[(size_t)k * (size_t)cells + (size_t)c];
        }
        s->inside[c] = any;
    }
    int* labels = (int*)mn_alloc((size_t)cells, sizeof(int));
    mn_ints comp_cells = { 0 };
    mn_ints comp_offsets = { 0 };
    int caves = group->tree.cave_count;
    int status = labels == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the caves of a slab.") : mn_components(s->inside, g->width, g->height, labels, &comp_cells, &comp_offsets);
    if (status == MN_OK) {
        group->comp_count = comp_offsets.count - 1;
        group->cave_comp = (int*)mn_alloc((size_t)(caves > 0 ? caves : 1), sizeof(int));
        group->comp_nodes = (int64_t*)mn_alloc((size_t)(group->comp_count > 0 ? group->comp_count : 1), sizeof(int64_t));
        group->comp_caves = (int*)mn_alloc((size_t)(group->comp_count > 0 ? group->comp_count : 1), sizeof(int));
        if (group->cave_comp == NULL || group->comp_nodes == NULL || group->comp_caves == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for the caves of a slab.");
        }
    }
    for (int c = 0; c < caves && status == MN_OK; c++) {
        const mn_cave* cave = &group->tree.caves[c];
        int label = labels[group->tree.cells.items[cave->cells_first]];
        if (label < 0) {
            status = mn_fail(MN_ERR_STATE, "Cave %d lies outside the masks of its levels.", c);
            break;
        }
        group->cave_comp[c] = label;
        group->comp_caves[label]++;
        group->comp_nodes[label] += group->nodes[c].count;
    }
    for (int t = 0; t < group->step_count && status == MN_OK; t++) {
        mn_z_step* step = &group->steps[t];
        status = comp_of_list(labels, &step->cells, &step->cell_comp, NULL);
        if (status == MN_OK) {
            status = comp_of_list(labels, &step->nodes, &step->node_comp, group->comp_nodes);
        }
    }
    free(labels);
    mn_ints_free(&comp_cells);
    mn_ints_free(&comp_offsets);
    return status;
}

/* Prepares the group of the plan levels first..last: its plan, its far region, its cave tree and the
 * nodes of every route. With `own_plan` the group plan holds its own levels, their masks taken from the
 * plan, and its far block is the far region of far_cells_of in the steps of far_steps_of; otherwise the
 * group is the whole plan. */
static int z_group_prepare(mn_z_state* s, int first, int last, int own_plan, const float* standing, mn_z_group* group, int* max_nodes)
{
    const mn_context* context = s->context;
    const mn_plan* plan = context->plan;
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    memset(group, 0, sizeof(*group));
    group->first = first;
    group->last = last;
    memset(s->inside, 0, (size_t)cells);
    if (!own_plan) {
        group->plan = *plan;
    } else {
        int count = last - first + 1;
        group->plan.grid = *g;
        group->plan.count = count;
        group->plan.coverage = plan->coverage;
        group->plan.lowest = plan->lowest;
        group->plan.levels = (float*)mn_alloc((size_t)count, sizeof(float));
        group->plan.masks = (uint8_t*)mn_alloc((size_t)count * (size_t)cells, 1);
        group->owns_plan = 1;
        if (group->plan.levels == NULL || group->plan.masks == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for a group of %d levels.", count);
        }
        const uint8_t* source = plan->masks + (size_t)first * (size_t)cells;
        for (int k = 0; k < count; k++) {
            group->plan.levels[k] = plan->levels[first + k];
            memcpy(group->plan.masks + (size_t)k * (size_t)cells, source + (size_t)k * (size_t)cells, (size_t)cells);
        }
        if (last > first) {
            mn_ints region = { 0 };
            int status = far_cells_of(context, plan->masks + (size_t)last * (size_t)cells, plan->levels[last], &region);
            if (status == MN_OK) {
                status = far_steps_of(s, group, &region, standing, max_nodes);
            }
            mn_ints_free(&region);
            MN_CHECK(status);
        }
    }
    MN_CHECK(cave_tree_build(&group->plan, &group->tree));
    /* The labels only served the parents; the caves keep their cell lists. */
    free(group->tree.labels);
    group->tree.labels = NULL;
    int caves = group->tree.cave_count;
    group->nodes = (mn_ints*)mn_alloc((size_t)(caves > 0 ? caves : 1), sizeof(mn_ints));
    if (group->nodes == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
    }
    for (int c = 0; c < caves; c++) {
        const mn_cave* cave = &group->tree.caves[c];
        MN_CHECK(list_nodes(s, group->tree.cells.items + cave->cells_first, cave->cells_count, 1, &group->nodes[c]));
        count_pass(s, &group->nodes[c], max_nodes);
    }
    return group_components(s, group);
}

static void z_report(mn_z_state* s)
{
    mn_report(s->monitor, s->pass, s->pass_count, (float)(s->done + s->unit_nodes - s->nodes_left) / (float)s->total);
}

/* One level route over `nodes` with the cells of `region` taken to `level`, then the planned heights of
 * the region. `exit` (T-166, NULL for none) is the last node of the block's route, which then runs even
 * without nodes of its own. */
static int z_level_block(mn_z_state* s, float level, const int* region, int region_count, const mn_ints* nodes, const mn_v3* exit)
{
    const mn_context* context = s->context;
    const mn_grid* g = &context->grid;
    int width = g->width;
    int status = mn_writer_mark(s->writer, level);
    if (status == MN_OK && (nodes->count > 0 || exit != NULL)) {
        if (nodes->count > 0) {
            s->pass++;
        }
        int count = nodes->count;
        for (int k = 0; k < count; k++) {
            int cell = nodes->items[k];
            s->xs[k] = mn_center_x(g, cell % width);
            s->ys[k] = mn_center_y(g, cell / width);
            s->zs[k] = level;
        }
        if (exit != NULL) {
            count = append_exit(s->xs, s->ys, s->zs, count, exit);
        }
        /* The floor is needed in the route's box only. */
        mn_box box = route_box(g, s->xs, s->ys, count, s->writer);
        copy_box(s->clearance, s->planned, g, box);
        for (int m = 0; m < region_count; m++) {
            s->clearance[region[m]] = level;
        }
        mn_route_grid grid = { *g, s->clearance, s->clearance };
        int end = exit_index(s->xs, s->ys, s->zs, count, exit);
        status = route_nodes(context, &grid, box, s->lifted, s->xs, s->ys, s->zs, count, end, &s->budget, s->nodes_left, s->monitor != NULL ? s->monitor->cancel : NULL, s->writer);
        s->nodes_left -= nodes->count;
        if (status == MN_OK) {
            z_report(s);
        }
    }
    for (int m = 0; m < region_count; m++) {
        s->planned[region[m]] = level;
    }
    return status;
}

/* The leaf cave of slab cave `comp` with the node nearest to `exit` in XY, and in `on_path` that cave
 * and its ancestors (T-166). */
static int exit_branch(const mn_z_group* group, int comp, const mn_grid* g, mn_v3 exit, int* parent, uint8_t* on_path)
{
    const mn_cave_tree* tree = &group->tree;
    for (int c = 0; c < tree->cave_count; c++) {
        parent[c] = -1;
        on_path[c] = 0;
    }
    for (int c = 0; c < tree->cave_count; c++) {
        for (int k = 0; k < tree->caves[c].children.count; k++) {
            parent[tree->caves[c].children.items[k]] = c;
        }
    }
    int target = -1;
    float best = INFINITY;
    for (int c = 0; c < tree->cave_count; c++) {
        if (group->cave_comp[c] != comp || tree->caves[c].children.count > 0) {
            continue;
        }
        for (int m = 0; m < group->nodes[c].count; m++) {
            int cell = group->nodes[c].items[m];
            float dx = mn_center_x(g, cell % g->width) - exit.x;
            float dy = mn_center_y(g, cell / g->width) - exit.y;
            float d = dx * dx + dy * dy;
            if (d < best) {
                best = d;
                target = c;
            }
        }
    }
    for (int c = target; c >= 0; c = parent[c]) {
        on_path[c] = 1;
    }
    return target;
}

/* The depth-first walk over the caves of a group that lie in the slab cave `comp` (T-166): a cave, then
 * its children before the next sibling, the sibling whose nearest node is nearest to the tool first.
 * With an exit, the branch to the leaf cave nearest to it goes last among its siblings at every level,
 * so the walk ends there and the last of its `caves` caves ends at the exit after a short way. */
static int z_walk(mn_z_state* s, const mn_z_group* group, int comp, int caves_in_comp, const mn_v3* exit)
{
    const mn_grid* g = &s->context->grid;
    int width = g->width;
    const mn_cave_tree* tree = &group->tree;
    int caves = tree->cave_count;
    mn_ints pending = { 0 };
    int* list_start = (int*)mn_alloc((size_t)(caves > 0 ? caves : 1) + 1, sizeof(int));
    int* list_end = (int*)mn_alloc((size_t)(caves > 0 ? caves : 1) + 1, sizeof(int));
    int* parent = (int*)mn_alloc((size_t)(caves > 0 ? caves : 1), sizeof(int));
    uint8_t* on_path = (uint8_t*)mn_alloc((size_t)(caves > 0 ? caves : 1), 1);
    int status = list_start == NULL || list_end == NULL || parent == NULL || on_path == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.") : MN_OK;
    if (status == MN_OK && exit != NULL) {
        exit_branch(group, comp, g, *exit, parent, on_path);
    }
    /* The sibling lists live in `pending`: every entry of the stack starts a list whose removed caves
     * are marked -1; a list ends at the entry that holds its length. */
    int depth = 0;
    int first = pending.count;
    for (int r = 0; r < tree->roots.count && status == MN_OK; r++) {
        if (group->cave_comp[tree->roots.items[r]] == comp) {
            status = mn_ints_push(&pending, tree->roots.items[r]);
        }
    }
    int visited = 0;
    if (status == MN_OK) {
        list_start[depth] = first;
        list_end[depth] = pending.count;
        depth = 1;
    }

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
        if (mn_cancelled(s->monitor)) {
            status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            break;
        }

        mn_v3 position;
        int has_position = mn_writer_has_position(s->writer, &position);
        int best_slot = -1;
        float best_distance = INFINITY;
        for (int k = from; k < to; k++) {
            int c = pending.items[k];
            if (c < 0 || (remaining > 1 && on_path[c])) {
                continue;
            }
            if (best_slot < 0) {
                best_slot = k;
                if (!has_position) {
                    break;
                }
            }
            for (int m = 0; m < group->nodes[c].count; m++) {
                int cell = group->nodes[c].items[m];
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
        const mn_cave* cave = &tree->caves[cave_index];
        float level = group->plan.levels[cave->level];
        const int* cave_cells = tree->cells.items + cave->cells_first;
        visited++;
        status = z_level_block(s, level, cave_cells, cave->cells_count, &group->nodes[cave_index], visited == caves_in_comp ? exit : NULL);

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
    free(parent);
    free(on_path);
    mn_ints_free(&pending);
    return status;
}

/* ---- route units (T-166, T-167) ---- */

/* The entry and exit of a route unit lie this far from the model in XY, or as far as the unit's first
 * route allows (user request). */
#define MN_UNIT_MODEL_DISTANCE 1.0f

#define MN_UNIT_ERROR 512

/* One route unit: one cave of one slab of far stepdown height (the whole plan without a far stepdown).
 * Its routes run in the established order over the material as the units before it leave it; its first
 * route starts at the entry, its last route ends at the exit, which is the next unit's entry. */
typedef struct mn_z_unit {
    int group;
    int comp;
    int has_entry;
    mn_v3 entry;
    int has_exit;
    mn_v3 exit;
    int64_t nodes;
    int64_t budget;
} mn_z_unit;

static float planar_distance(mn_v3 a, mn_v3 b)
{
    float dx = a.x - b.x;
    float dy = a.y - b.y;
    return sqrtf(dx * dx + dy * dy);
}

/* Whether a node at model distance d, distance p from the previous point and cell `cell` is a better
 * entry than the best so far: one at MN_UNIT_MODEL_DISTANCE or more beats one nearer the model; among
 * those the nearest to that distance, among the nearer ones the farthest; then the nearer to the
 * previous point, then the lower cell. */
static int entry_better(float d, float p, int cell, float best_d, float best_p, int best_cell)
{
    int reach = d >= MN_UNIT_MODEL_DISTANCE;
    int best_reach = best_d >= MN_UNIT_MODEL_DISTANCE;
    if (reach != best_reach) {
        return reach;
    }
    if (d != best_d) {
        return reach ? d < best_d : d > best_d;
    }
    if (p != best_p) {
        return p < best_p;
    }
    return cell < best_cell;
}

/* The entry of the unit of slab cave `comp`: a node of its first route (its first far step, else its
 * root caves) by entry_better; returns 0 when it has none. */
static int unit_entry(const mn_z_state* s, const mn_z_group* group, int comp, const float* model_distance, int has_from, mn_v3 from, mn_v3* entry)
{
    const mn_grid* g = &s->context->grid;
    const mn_plan* plan = s->context->plan;
    int found = 0;
    float best_d = 0.0f;
    float best_p = 0.0f;
    int best_cell = -1;
    for (int pass = 0; pass < 2 && !found; pass++) {
        const mn_z_step* step = pass == 0 && group->step_count > 0 ? &group->steps[0] : NULL;
        int lists = pass == 0 ? (step != NULL ? 1 : 0) : group->tree.roots.count;
        for (int r = 0; r < lists; r++) {
            const mn_ints* nodes;
            float z;
            if (pass == 0) {
                nodes = &step->nodes;
                z = plan->levels[step->level];
            } else {
                int cave = group->tree.roots.items[r];
                if (group->cave_comp[cave] != comp) {
                    continue;
                }
                nodes = &group->nodes[cave];
                z = group->plan.levels[group->tree.caves[cave].level];
            }
            for (int k = 0; k < nodes->count; k++) {
                if (pass == 0 && step->node_comp.items[k] != comp) {
                    continue;
                }
                int cell = nodes->items[k];
                mn_v3 at = mn_v3_make(mn_center_x(g, cell % g->width), mn_center_y(g, cell / g->width), z);
                float d = model_distance[cell];
                float p = has_from ? planar_distance(at, from) : 0.0f;
                if (!found || entry_better(d, p, cell, best_d, best_p, best_cell)) {
                    found = 1;
                    best_d = d;
                    best_p = p;
                    best_cell = cell;
                    *entry = at;
                }
            }
        }
    }
    return found;
}

/* The units in order (T-166): slab after slab; inside a slab the cave whose entry lies nearest to the
 * previous entry next (the first unit takes the first cave); the exit of every unit is the entry of
 * the next; the evaluation budget shared by node count in that order. */
static int plan_units(const mn_z_state* s, const mn_z_group* groups, int group_count, const float* model_distance, mn_z_unit** result, int* result_count)
{
    int capacity = 1;
    int widest = 1;
    for (int gi = 0; gi < group_count; gi++) {
        capacity += groups[gi].comp_count;
        widest = mn_maxi(widest, groups[gi].comp_count);
    }
    mn_z_unit* units = (mn_z_unit*)mn_alloc((size_t)capacity, sizeof(mn_z_unit));
    uint8_t* taken = (uint8_t*)mn_alloc((size_t)widest, 1);
    if (units == NULL || taken == NULL) {
        free(units);
        free(taken);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for %d route units.", capacity);
    }
    int count = 0;
    int has_from = 0;
    mn_v3 from = mn_v3_make(0.0f, 0.0f, 0.0f);
    int status = MN_OK;
    for (int gi = 0; gi < group_count && status == MN_OK; gi++) {
        const mn_z_group* group = &groups[gi];
        int remaining = 0;
        for (int k = 0; k < group->comp_count; k++) {
            taken[k] = (uint8_t)(group->comp_nodes[k] == 0);
            remaining += taken[k] ? 0 : 1;
        }
        while (remaining > 0 && status == MN_OK) {
            int best = -1;
            float best_distance = INFINITY;
            mn_v3 best_entry = from;
            for (int k = 0; k < group->comp_count; k++) {
                if (taken[k]) {
                    continue;
                }
                mn_v3 entry;
                if (!unit_entry(s, group, k, model_distance, has_from, from, &entry)) {
                    status = mn_fail(MN_ERR_STATE, "Cave %d of slab %d has nodes but no first route.", k, gi);
                    break;
                }
                float d = has_from ? planar_distance(entry, from) : 0.0f;
                if (best < 0 || d < best_distance) {
                    best = k;
                    best_distance = d;
                    best_entry = entry;
                }
            }
            if (status != MN_OK) {
                break;
            }
            mn_z_unit* unit = &units[count];
            memset(unit, 0, sizeof(*unit));
            unit->group = gi;
            unit->comp = best;
            unit->has_entry = count > 0;
            unit->entry = best_entry;
            unit->nodes = group->comp_nodes[best];
            count++;
            from = best_entry;
            has_from = 1;
            taken[best] = 1;
            remaining--;
        }
    }
    free(taken);
    if (status != MN_OK) {
        free(units);
        return status;
    }
    int64_t held = 0;
    for (int u = 0; u < count; u++) {
        held += units[u].nodes;
        if (u + 1 < count) {
            units[u].has_exit = 1;
            units[u].exit = units[u + 1].entry;
        }
    }
    if (held != s->total) {
        free(units);
        return mn_fail(MN_ERR_STATE, "The route units hold %lld of the %lld nodes.", (long long)held, (long long)s->total);
    }
    int64_t remaining_budget = s->budget.total;
    int64_t left = s->total;
    for (int u = 0; u < count; u++) {
        units[u].budget = left == 0 ? 0 : mn_d2l((double)remaining_budget * (double)units[u].nodes / (double)left);
        remaining_budget -= units[u].budget;
        left -= units[u].nodes;
    }
    *result = units;
    *result_count = count;
    return MN_OK;
}

/* Collects the entries of `list` whose slab cave is `comp` into `out`. */
static int filter_comp(const mn_ints* list, const mn_ints* comps, int comp, mn_ints* out)
{
    out->count = 0;
    for (int k = 0; k < list->count; k++) {
        if (comps->items[k] == comp) {
            MN_CHECK(mn_ints_push(out, list->items[k]));
        }
    }
    return MN_OK;
}

/* The routes of one unit in the established order: the far block steps over the unit's cave, then the
 * walk over its caves; the last route ends at the exit. */
static int z_unit_run(mn_z_state* s, const mn_z_group* groups, const mn_z_unit* unit)
{
    const mn_plan* plan = s->context->plan;
    int status = MN_OK;
    const mn_v3* exit = unit->has_exit ? &unit->exit : NULL;
    const mn_z_group* group = &groups[unit->group];
    int comp = unit->comp;
    int caves = group->comp_caves[comp];
    int last_step = -1;
    for (int t = 0; t < group->step_count; t++) {
        for (int k = 0; k < group->steps[t].cell_comp.count && last_step < t; k++) {
            if (group->steps[t].cell_comp.items[k] == comp) {
                last_step = t;
            }
        }
    }
    mn_ints region = { 0 };
    mn_ints nodes = { 0 };
    for (int t = 0; t <= last_step && status == MN_OK; t++) {
        const mn_z_step* step = &group->steps[t];
        status = filter_comp(&step->cells, &step->cell_comp, comp, &region);
        if (status == MN_OK) {
            status = filter_comp(&step->nodes, &step->node_comp, comp, &nodes);
        }
        if (status != MN_OK || region.count == 0) {
            continue;
        }
        status = z_level_block(s, plan->levels[step->level], region.items, region.count, &nodes, caves == 0 && t == last_step ? exit : NULL);
    }
    mn_ints_free(&region);
    mn_ints_free(&nodes);
    if (status == MN_OK && caves > 0) {
        status = z_walk(s, group, comp, caves, exit);
    }
    return status;
}

/* The planned heights a unit leaves, written without routing: its regions at their levels, the writes
 * its routes make. The units start from these (T-167). */
static void replay_unit(const mn_z_state* s, const mn_z_group* groups, const mn_z_unit* unit, float* planned)
{
    const mn_plan* plan = s->context->plan;
    const mn_z_group* group = &groups[unit->group];
    int comp = unit->comp;
    for (int t = 0; t < group->step_count; t++) {
        const mn_z_step* step = &group->steps[t];
        float level = plan->levels[step->level];
        for (int k = 0; k < step->cells.count; k++) {
            if (step->cell_comp.items[k] == comp) {
                planned[step->cells.items[k]] = level;
            }
        }
    }
    for (int c = 0; c < group->tree.cave_count; c++) {
        if (group->cave_comp[c] != comp) {
            continue;
        }
        const mn_cave* cave = &group->tree.caves[c];
        float level = group->plan.levels[cave->level];
        const int* cave_cells = group->tree.cells.items + cave->cells_first;
        for (int m = 0; m < cave->cells_count; m++) {
            planned[cave_cells[m]] = level;
        }
    }
}

/* The scratch of one worker: the planned heights, the route floors, the pass cells and the node arrays. */
typedef struct mn_unit_scratch {
    float* planned;
    float* clearance;
    float* lifted;
    uint8_t* inside;
    float* xs;
    float* ys;
    float* zs;
} mn_unit_scratch;

/* The units on the workers (T-167, T-195): a worker takes the next unit in order as soon as it is free,
 * with a copy of the planned heights the units before it leave. Those are the units' replayed writes,
 * so the copy is taken at once and no unit waits for another, and a unit starts at its entry, where the
 * unit before ends. Every unit has its own writer; the paths are appended in unit order afterwards. */
typedef struct mn_unit_job {
    const mn_z_state* base;
    const mn_z_group* groups;
    const mn_z_unit* units;
    int count;
    int quiet;
    int has_position;
    mn_v3 position;
    float* frontier;
    int next;
    int64_t done_nodes;
    int passes;
    mn_gate* gate;
    mn_segments* paths;
    mn_marks* marks;
    int* status;
    double (*times)[MN_TIME_COUNT];
    char (*errors)[MN_UNIT_ERROR];
} mn_unit_job;

static void unit_row(void* context, int row, void* scratch)
{
    (void)row;
    mn_unit_job* job = (mn_unit_job*)context;
    const mn_unit_scratch* w = (const mn_unit_scratch*)scratch;
    const mn_z_state* base = job->base;
    int cells = mn_cells(&base->context->grid);
    mn_z_state s = *base;
    mn_gate_enter(job->gate);
    int u = job->next++;
    const mn_z_unit* unit = &job->units[u];
    memcpy(w->planned, job->frontier, (size_t)cells * sizeof(float));
    replay_unit(base, job->groups, unit, job->frontier);
    s.done = job->done_nodes;
    s.pass = job->passes;
    mn_gate_leave(job->gate);
    /* Several units at once fill the processors: the solver's start walks run inline. */
    int cap = job->quiet ? mn_set_thread_cap(1) : 0;
    mn_monitor quiet;
    quiet.progress = NULL;
    quiet.context = NULL;
    quiet.cancel = base->monitor != NULL ? base->monitor->cancel : NULL;
    s.monitor = job->quiet ? &quiet : base->monitor;
    s.planned = w->planned;
    s.clearance = w->clearance;
    s.lifted = w->lifted;
    s.inside = w->inside;
    s.xs = w->xs;
    s.ys = w->ys;
    s.zs = w->zs;
    s.budget.total = unit->budget;
    s.budget.used = 0;
    s.nodes_left = unit->nodes;
    s.unit_nodes = unit->nodes;
    int pass_before = s.pass;
    mn_writer* writer = NULL;
    int status = writer_for(s.context, &writer);
    if (status == MN_OK) {
        if (unit->has_entry) {
            mn_writer_place(writer, unit->entry);
        } else if (job->has_position) {
            mn_writer_place(writer, job->position);
        }
        s.writer = writer;
        status = z_unit_run(&s, job->groups, unit);
    }
    if (status == MN_OK) {
        status = mn_writer_release(writer, &job->paths[u], &job->marks[u]);
    }
    if (status != MN_OK) {
        snprintf(job->errors[u], MN_UNIT_ERROR, "%s", mn_last_error());
    }
    job->status[u] = status;
    memset(job->times[u], 0, sizeof(job->times[u]));
    mn_time_take(job->times[u]);
    mn_writer_free(writer);
    mn_gate_enter(job->gate);
    job->done_nodes += unit->nodes;
    job->passes += s.pass - pass_before;
    if (job->quiet && status == MN_OK) {
        mn_report(base->monitor, job->passes, base->pass_count, (float)job->done_nodes / (float)base->total);
    }
    mn_gate_leave(job->gate);
    if (job->quiet) {
        mn_set_thread_cap(cap);
    }
}

/* The XY distance of every cell to the model (cells whose model stands above the floor). */
static int model_distance_of(mn_z_state* s, float** result)
{
    const mn_context* context = s->context;
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    float* distance = (float*)mn_alloc((size_t)cells, sizeof(float));
    if (distance == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the distance to the model.");
    }
    for (int k = 0; k < cells; k++) {
        s->inside[k] = (uint8_t)(context->model[k] > context->floor + MN_FLOOR_TOLERANCE);
    }
    int status = mn_distance_transform(s->inside, g->width, g->height, g->cell_size, distance);
    if (status != MN_OK) {
        free(distance);
        return status;
    }
    *result = distance;
    return MN_OK;
}

/* Routes every unit and appends their paths to `writer` in unit order (T-167): up to
 * min(MN_MAX_WORKERS, processors) units run at once, each from the planned heights the units before
 * it leave (replayed). */
static int z_units(mn_z_state* s, const mn_z_group* groups, int group_count, int max_nodes, const float* model_distance, mn_writer* writer, mn_points* entries)
{
    const mn_context* context = s->context;
    const mn_grid* g = &context->grid;
    int cells = mn_cells(g);
    mn_z_unit* units = NULL;
    int unit_count = 0;
    int status = plan_units(s, groups, group_count, model_distance, &units, &unit_count);
    for (int u = 1; u < unit_count && status == MN_OK && entries != NULL; u++) {
        status = mn_points_push(entries, units[u].entry);
    }
    if (status != MN_OK || unit_count == 0) {
        free(units);
        return status;
    }

    int workers = mn_mini(mn_mini(MN_MAX_WORKERS, mn_worker_count()), unit_count);
    size_t nodes = (size_t)max_nodes + 1;
    size_t slots = (size_t)unit_count;
    mn_unit_scratch* sets = (mn_unit_scratch*)mn_alloc((size_t)workers, sizeof(mn_unit_scratch));
    void** scratch = (void**)mn_alloc((size_t)workers, sizeof(void*));
    mn_segments* paths = (mn_segments*)mn_alloc(slots, sizeof(mn_segments));
    mn_marks* marks = (mn_marks*)mn_alloc(slots, sizeof(mn_marks));
    int* row_status = (int*)mn_alloc(slots, sizeof(int));
    char (*errors)[MN_UNIT_ERROR] = (char (*)[MN_UNIT_ERROR])mn_alloc(slots, MN_UNIT_ERROR);
    double (*times)[MN_TIME_COUNT] = (double (*)[MN_TIME_COUNT])mn_alloc(slots, sizeof(double[MN_TIME_COUNT]));
    mn_gate* gate = NULL;
    double units_started = mn_seconds();
    if (sets == NULL || scratch == NULL || paths == NULL || marks == NULL || row_status == NULL || errors == NULL || times == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for %d route units.", unit_count);
        goto done;
    }
    sets[0].clearance = s->clearance;
    sets[0].lifted = s->lifted;
    sets[0].inside = s->inside;
    memset(s->inside, 0, (size_t)cells);
    sets[0].xs = s->xs;
    sets[0].ys = s->ys;
    sets[0].zs = s->zs;
    for (int w = 0; w < workers && status == MN_OK; w++) {
        if (w > 0) {
            sets[w].clearance = (float*)mn_alloc((size_t)cells, sizeof(float));
            sets[w].lifted = (float*)mn_alloc(2 * (size_t)cells, sizeof(float));
            sets[w].inside = (uint8_t*)mn_alloc((size_t)cells, 1);
            sets[w].xs = (float*)mn_alloc(nodes, sizeof(float));
            sets[w].ys = (float*)mn_alloc(nodes, sizeof(float));
            sets[w].zs = (float*)mn_alloc(nodes, sizeof(float));
        }
        sets[w].planned = (float*)mn_alloc((size_t)cells, sizeof(float));
        scratch[w] = &sets[w];
        if (sets[w].clearance == NULL || sets[w].lifted == NULL || sets[w].inside == NULL || sets[w].xs == NULL || sets[w].ys == NULL || sets[w].zs == NULL || sets[w].planned == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for route worker %d of %d.", w + 1, workers);
        }
    }
    if (status == MN_OK) {
        status = mn_gate_create(&gate);
    }
    if (status == MN_OK) {
        mn_unit_job job;
        memset(&job, 0, sizeof(job));
        job.base = s;
        job.groups = groups;
        job.units = units;
        job.count = unit_count;
        job.quiet = workers > 1;
        job.has_position = mn_writer_has_position(writer, &job.position);
        job.frontier = s->planned;
        job.gate = gate;
        job.paths = paths;
        job.marks = marks;
        job.status = row_status;
        job.times = times;
        job.errors = errors;
        status = mn_rows_dynamic(unit_count, unit_row, &job, scratch, workers);
    }
    for (int u = 0; u < unit_count && times != NULL; u++) {
        for (int k = 0; k < MN_TIME_COUNT; k++) {
            mn_time_add(k, times[u][k]);
        }
    }
    for (int u = 0; u < unit_count && status == MN_OK; u++) {
        if (row_status[u] == MN_ERR_CANCELLED) {
            status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        } else if (row_status[u] != MN_OK) {
            status = mn_fail(row_status[u], "Route unit %d: %s", u + 1, errors[u]);
        }
    }
    for (int u = 0; u < unit_count && paths != NULL; u++) {
        if (status == MN_OK) {
            status = mn_writer_append(writer, &paths[u], &marks[u]);
        }
        mn_segments_free(&paths[u]);
        mn_marks_free(&marks[u]);
    }

done:
    for (int w = 0; sets != NULL && w < workers; w++) {
        if (w > 0) {
            free(sets[w].clearance);
            free(sets[w].lifted);
            free(sets[w].inside);
            free(sets[w].xs);
            free(sets[w].ys);
            free(sets[w].zs);
        }
        free(sets[w].planned);
    }
    mn_gate_destroy(gate);
    free(sets);
    free(scratch);
    free(paths);
    free(marks);
    free(row_status);
    free(errors);
    free(times);
    free(units);
    mn_time_add(MN_TIME_UNITS, mn_seconds() - units_started);
    return status;
}

/* The groups prepared at once (T-195): a group needs the standing map the groups above it leave (its far
 * block's permission) and nothing another group prepares. Every group's levels are folded on a worker of
 * their own into the lowest cut they leave, and the maps follow as running minima in group order (a
 * minimum keeps the earlier of equal values, as the fold level by level does); then the groups take one
 * worker each, the work inside a group inline. The node and route counts are summed afterwards, and the
 * far area is written from the groups' steps, so the result is the one of the groups prepared one after
 * the other. */
typedef struct mn_fold_job {
    const mn_context* context;
    int group_size;
    float** standing;
    int* status;
    char (*errors)[MN_UNIT_ERROR];
} mn_fold_job;

/* Group `row`'s levels folded from nothing standing into standing[row + 1]. */
static void fold_row(void* context, int row, void* scratch)
{
    mn_fold_job* job = (mn_fold_job*)context;
    const mn_context* ctx = job->context;
    int cells = mn_cells(&ctx->grid);
    int cap = mn_set_thread_cap(1);
    float* partial = job->standing[row + 1];
    for (int c = 0; c < cells; c++) {
        partial[c] = INFINITY;
    }
    int first = row * job->group_size;
    job->status[row] = standing_fold(ctx, first, mn_mini(first + job->group_size, ctx->plan->count) - 1, (float*)scratch, partial);
    if (job->status[row] != MN_OK) {
        snprintf(job->errors[row], MN_UNIT_ERROR, "%s", mn_last_error());
    }
    mn_set_thread_cap(cap);
}
typedef struct mn_group_scratch {
    uint8_t* inside;
    uint8_t* covered;
} mn_group_scratch;

typedef struct mn_group_job {
    const mn_z_state* base;
    mn_z_group* groups;
    int group_size;
    int far_on;
    float** standing; /* per group the map before it, NULL without a far stepdown */
    int64_t* nodes;
    int* passes;
    int* max_nodes;
    int* status;
    char (*errors)[MN_UNIT_ERROR];
} mn_group_job;

static void group_row(void* context, int gi, void* scratch)
{
    mn_group_job* job = (mn_group_job*)context;
    const mn_group_scratch* w = (const mn_group_scratch*)scratch;
    int cap = mn_set_thread_cap(1);
    mn_z_state s = *job->base;
    s.inside = w->inside;
    s.covered = w->covered;
    s.nodes_left = 0;
    s.pass_count = 0;
    int first = gi * job->group_size;
    int last = mn_mini(first + job->group_size, s.context->plan->count) - 1;
    job->max_nodes[gi] = 0;
    job->status[gi] = z_group_prepare(&s, first, last, job->far_on, job->standing != NULL ? job->standing[gi] : NULL, &job->groups[gi], &job->max_nodes[gi]);
    if (job->status[gi] != MN_OK) {
        snprintf(job->errors[gi], MN_UNIT_ERROR, "%s", mn_last_error());
    }
    job->nodes[gi] = s.nodes_left;
    job->passes[gi] = s.pass_count;
    mn_set_thread_cap(cap);
}

static int z_groups_prepare(mn_z_state* s, mn_z_group* groups, int group_count, int group_size, int far_on, int* max_nodes)
{
    const mn_context* context = s->context;
    int cells = mn_cells(&context->grid);
    int workers = mn_maxi(1, mn_mini(mn_worker_count(), group_count));
    size_t slots = (size_t)group_count;
    float** standing = far_on ? (float**)mn_alloc(slots, sizeof(float*)) : NULL;
    int folds = far_on ? mn_maxi(1, mn_mini(workers, group_count - 1)) : 0;
    float** distance = far_on ? (float**)mn_alloc((size_t)(folds > 0 ? folds : 1), sizeof(float*)) : NULL;
    mn_group_scratch* sets = (mn_group_scratch*)mn_alloc((size_t)workers, sizeof(mn_group_scratch));
    void** scratch = (void**)mn_alloc((size_t)workers, sizeof(void*));
    int64_t* nodes = (int64_t*)mn_alloc(slots, sizeof(int64_t));
    int* passes = (int*)mn_alloc(slots, sizeof(int));
    int* most = (int*)mn_alloc(slots, sizeof(int));
    int* row_status = (int*)mn_alloc(slots, sizeof(int));
    char (*errors)[MN_UNIT_ERROR] = (char (*)[MN_UNIT_ERROR])mn_alloc(slots, MN_UNIT_ERROR);
    int status = (far_on && (standing == NULL || distance == NULL)) || sets == NULL || scratch == NULL || nodes == NULL || passes == NULL || most == NULL || row_status == NULL || errors == NULL
        ? mn_fail(MN_ERR_MEMORY, "Out of memory for %d level groups.", group_count)
        : MN_OK;
    for (int gi = 0; gi < group_count && far_on && status == MN_OK; gi++) {
        standing[gi] = (float*)mn_alloc((size_t)cells, sizeof(float));
        if (standing[gi] == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for the standing map of group %d.", gi + 1);
        }
    }
    for (int w = 0; w < folds && status == MN_OK; w++) {
        distance[w] = (float*)mn_alloc((size_t)cells, sizeof(float));
        if (distance[w] == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for fold worker %d of %d.", w + 1, folds);
        }
    }
    if (far_on && status == MN_OK) {
        memcpy(standing[0], context->stock, (size_t)cells * sizeof(float));
        mn_fold_job fold = { context, group_size, standing, row_status, errors };
        status = group_count > 1 ? mn_rows_dynamic(group_count - 1, fold_row, &fold, (void**)distance, folds) : MN_OK;
        for (int gi = 0; gi + 1 < group_count && status == MN_OK; gi++) {
            if (row_status[gi] != MN_OK) {
                status = mn_fail(row_status[gi], "Standing map of group %d: %s", gi + 2, errors[gi]);
            }
        }
        for (int gi = 1; gi < group_count && status == MN_OK; gi++) {
            const float* before = standing[gi - 1];
            float* map = standing[gi];
            for (int c = 0; c < cells; c++) {
                map[c] = mn_isnan(before[c]) || !(map[c] < before[c]) ? before[c] : map[c];
            }
        }
    }
    for (int w = 0; w < workers && status == MN_OK; w++) {
        sets[w].inside = (uint8_t*)mn_alloc((size_t)cells, 1);
        sets[w].covered = (uint8_t*)mn_alloc((size_t)cells, 1);
        scratch[w] = &sets[w];
        if (sets[w].inside == NULL || sets[w].covered == NULL) {
            status = mn_fail(MN_ERR_MEMORY, "Out of memory for group worker %d of %d.", w + 1, workers);
        }
    }
    if (status == MN_OK) {
        mn_group_job job = { s, groups, group_size, far_on, standing, nodes, passes, most, row_status, errors };
        status = mn_rows_dynamic(group_count, group_row, &job, scratch, workers);
    }
    for (int gi = 0; gi < group_count && status == MN_OK; gi++) {
        if (row_status[gi] != MN_OK) {
            status = mn_fail(row_status[gi], "Level group %d: %s", gi + 1, errors[gi]);
            break;
        }
        s->nodes_left += nodes[gi];
        s->pass_count += passes[gi];
        *max_nodes = mn_maxi(*max_nodes, most[gi]);
        for (int t = 0; t < groups[gi].step_count && context->far_area != NULL; t++) {
            for (int m = 0; m < groups[gi].steps[t].cells.count; m++) {
                context->far_area[groups[gi].steps[t].cells.items[m]] = 1;
            }
        }
    }
    for (int gi = 0; standing != NULL && gi < group_count; gi++) {
        free(standing[gi]);
    }
    for (int w = 0; distance != NULL && w < folds; w++) {
        free(distance[w]);
    }
    for (int w = 0; sets != NULL && w < workers; w++) {
        free(sets[w].inside);
        free(sets[w].covered);
    }
    free(standing);
    free(distance);
    free(sets);
    free(scratch);
    free(nodes);
    free(passes);
    free(most);
    free(row_status);
    free(errors);
    return status;
}

/* The routes of the Z layer over the context's plan and strategy tip, in groups of `group_size` levels
 * (far stepdown slabs when `far_on`): every group's far block and cave walk, in route units. Backwards
 * gives it the plan and tip of its check (T-193). */
static int z_layer_route(const mn_context* context, const mn_monitor* monitor, int group_size, int far_on, mn_segments* result, mn_marks* marks, mn_points* entries)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    int cells = mn_cells(g);
    int status = MN_OK;
    mn_z_state s;
    memset(&s, 0, sizeof(s));
    s.context = context;
    s.monitor = monitor;
    s.step = mn_lattice_step(context->parameters.stepover, g->cell_size);
    s.column_step = mn_lattice_column_step(s.step, context->tool.cutter_diameter / 2.0f, g->cell_size);
    mn_profile profile = { 0 };
    s.profile = &profile;
    mn_z_group* groups = NULL;
    int group_count = 0;
    mn_writer* writer = NULL;
    s.inside = (uint8_t*)mn_alloc((size_t)cells, 1);
    s.covered = (uint8_t*)mn_alloc((size_t)cells, 1);
    s.planned = (float*)mn_alloc((size_t)cells, sizeof(float));
    s.clearance = (float*)mn_alloc((size_t)cells, sizeof(float));
    if (s.inside == NULL || s.covered == NULL || s.planned == NULL || s.clearance == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    if (context->far_area != NULL) {
        memset(context->far_area, 0, (size_t)cells);
    }
    s.lifted =(float*)mn_alloc(2 * (size_t)cells, sizeof(float));
    status = s.lifted == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the route floor of %d cells.", cells) : mn_profile_build(&context->tool, g->cell_size, &profile);
    if (status != MN_OK) {
        goto done;
    }

    int max_nodes = 0;
    group_count = plan->count > 0 ? (plan->count + group_size - 1) / group_size : 1;
    groups = (mn_z_group*)mn_alloc((size_t)group_count, sizeof(mn_z_group));
    if (groups == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for %d level groups.", group_count);
        goto done;
    }
    double prepared = mn_seconds();
    status = z_groups_prepare(&s, groups, group_count, group_size, far_on, &max_nodes);
    mn_time_add(MN_TIME_GROUPS, mn_seconds() - prepared);
    if (status != MN_OK) {
        goto done;
    }

    /* One more entry for the exit a unit's last route ends at (T-166). */
    s.xs = (float*)mn_alloc((size_t)max_nodes + 1, sizeof(float));
    s.ys = (float*)mn_alloc((size_t)max_nodes + 1, sizeof(float));
    s.zs = (float*)mn_alloc((size_t)max_nodes + 1, sizeof(float));
    if (s.xs == NULL || s.ys == NULL || s.zs == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the layer strategy.");
        goto done;
    }
    memcpy(s.planned, context->stock, (size_t)cells * sizeof(float));
    status = writer_for(context, &writer);
    if (status != MN_OK) {
        goto done;
    }

    s.total = s.nodes_left;
    s.budget.total = s.total * MN_EVALUATIONS_PER_NODE;
    float* model_distance = NULL;
    status = model_distance_of(&s, &model_distance);
    if (status == MN_OK) {
        status = z_units(&s, groups, group_count, max_nodes, model_distance, writer, entries);
    }
    free(model_distance);
    if (status == MN_OK) {
        status = mn_writer_take(writer, result, marks);
    }

done:
    if (groups != NULL) {
        for (int gi = 0; gi < group_count; gi++) {
            z_group_free(&groups[gi]);
        }
    }
    free(groups);
    free(s.inside);
    free(s.covered);
    free(s.planned);
    free(s.clearance);
    free(s.xs);
    free(s.ys);
    free(s.zs);
    mn_profile_free(&profile);
    free(s.lifted);
    mn_writer_free(writer);
    return status;
}

/* ---- Backwards (T-170, T-193) ---- */

/* Collision mode Backwards: the backward check (mn_backwards.c) runs on a thread of its own with half the
 * workers and settles every level from the lowest up; the Z layer then routes the plan of the positions
 * reached at each level, with the positions that lose a level raised to the deepest level they reach
 * (T-193). So the routes follow every rule of the Z layer: the far stepdown only at least the stepover
 * from the model, the standing stock and the raised positions, in steps within the cutter length where
 * the head clears what stands (T-156, T-157, T-162), everything else at every level, cave by cave, in
 * route units ending where the next one begins, priced with the turn fine. Where the check leaves nothing
 * unreached the plan and the tip are the pass's own, and so is the toolpath. */
static int z_backwards(const mn_context* context, const mn_monitor* monitor, int group_size, int far_on, mn_segments* result, mn_marks* marks, mn_points* entries, mn_unreached* unreached)
{
    if (context->collision_mode == MN_COLLISION_BACKWARDS_GPU && context->gpu == NULL) {
        return mn_fail(MN_ERR_STATE, "Backwards_GPU needs the generation's GPU session.");
    }
    mn_backwards* check = NULL;
    MN_CHECK(mn_backwards_start(context, mn_maxi(1, mn_worker_count() / 2), monitor != NULL ? monitor->cancel : NULL, &check));
    const mn_plan* reached = NULL;
    const float* tip = NULL;
    double waited = mn_seconds();
    int status = mn_backwards_settled(check, &reached, &tip);
    mn_time_add(MN_TIME_BACK_WAIT, mn_seconds() - waited);
    if (status == MN_OK) {
        mn_context routed = *context;
        routed.plan = reached;
        routed.effective_tip = tip;
        status = z_layer_route(&routed, monitor, group_size, far_on, result, marks, entries);
    }
    int64_t counted = 0;
    int64_t model_cells = 0;
    int sweeps = 0;
    int check_status = mn_backwards_finish(check, status != MN_OK, &counted, &model_cells, &sweeps);
    if (status == MN_OK) {
        status = check_status;
    }
    if (status == MN_OK && unreached != NULL) {
        unreached->checked = 1;
        unreached->unreached = counted;
        unreached->model_cells = model_cells;
        unreached->sweeps = sweeps;
    }
    return status;
}

int mn_z_layer(const mn_context* context, const mn_monitor* monitor, mn_segments* result, mn_marks* marks, mn_points* entries, mn_unreached* unreached)
{
    const mn_plan* plan = context->plan;
    int k = mn_far_levels(&context->parameters);
    if (unreached != NULL) {
        memset(unreached, 0, sizeof(*unreached));
    }
    if (k == 0) {
        return mn_fail(MN_ERR_ARGUMENT, "Far stepdown %g must be 0 or the stepdown %g (none) or a whole multiple of it, at least twice it.", (double)context->parameters.far_stepdown,
            (double)context->parameters.stepdown);
    }
    int far_on = k >= 2;
    int group_size = far_on ? k : (plan->count > 0 ? plan->count : 1);
    return z_backwards(context, monitor, group_size, far_on, result, marks, entries, unreached);
}

MN_API int32_t mn_strategy_generate(int32_t strategy, const mn_context* context, mn_progress_fn progress, void* progress_context, const volatile int32_t* cancel, mn_segment** segments, int32_t* count)
{
    if (!mn_backwards_mode(context->collision_mode)) {
        return mn_fail(MN_ERR_ARGUMENT, "Unknown collision mode %d.", context->collision_mode);
    }
    if (strategy != MN_STRATEGY_Z_LAYER) {
        return mn_fail(MN_ERR_ARGUMENT, "Unknown strategy %d.", strategy);
    }
    mn_monitor monitor;
    monitor.progress = progress;
    monitor.context = progress_context;
    monitor.cancel = cancel;
    mn_segments result = { 0 };
    int status = mn_z_layer(context, &monitor, &result, NULL, NULL, NULL);
    if (status != MN_OK) {
        mn_segments_free(&result);
        return status;
    }
    *segments = result.items != NULL ? result.items : (mn_segment*)mn_alloc(1, sizeof(mn_segment));
    *count = result.count;
    return MN_OK;
}
