#include "mn_internal.h"

/* Levels, their masks and the cut scope (Slicer, SeparationRegion, MaterialIslands, DistanceTransform,
 * CaveTree.Label). Masks are row-major bytes like the maps. */

/* ---- exact Euclidean distance transform (Felzenszwalb and Huttenlocher) ---- */

static void transform_line(const float* f, int n, float* d, int* v, float* z)
{
    int k = 0;
    v[0] = 0;
    z[0] = -INFINITY;
    z[1] = INFINITY;
    for (int q = 1; q < n; q++) {
        if (f[q] == INFINITY) {
            continue;
        }
        float s;
        for (;;) {
            int p = v[k];
            if (f[p] == INFINITY) {
                s = -INFINITY;
            } else {
                s = ((f[q] + (float)q * (float)q) - (f[p] + (float)p * (float)p)) / (2.0f * (float)(q - p));
            }
            if (s > z[k] || k == 0) {
                break;
            }
            k--;
        }
        k++;
        v[k] = q;
        z[k] = s;
        z[k + 1] = INFINITY;
    }

    if (f[v[0]] == INFINITY && k == 0) {
        for (int q = 0; q < n; q++) {
            d[q] = INFINITY;
        }
        return;
    }

    k = 0;
    for (int q = 0; q < n; q++) {
        while (z[k + 1] < (float)q) {
            k++;
        }
        int p = v[k];
        d[q] = f[p] == INFINITY ? INFINITY : f[p] + (float)(q - p) * (float)(q - p);
    }
}

int mn_distance_transform(const uint8_t* mask, int width, int height, float cell_size, float* distances)
{
    if (!(cell_size > 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Cell size must be positive.");
    }
    int length = mn_maxi(width, height);
    float* line = (float*)mn_alloc((size_t)length, sizeof(float));
    float* output = (float*)mn_alloc((size_t)length, sizeof(float));
    int* vertices = (int*)mn_alloc((size_t)length, sizeof(int));
    float* boundaries = (float*)mn_alloc((size_t)length + 1, sizeof(float));
    if (line == NULL || output == NULL || vertices == NULL || boundaries == NULL) {
        free(line);
        free(output);
        free(vertices);
        free(boundaries);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a distance transform.");
    }

    for (int i = 0; i < width; i++) {
        for (int j = 0; j < height; j++) {
            line[j] = mask[j * width + i] ? 0.0f : INFINITY;
        }
        transform_line(line, height, output, vertices, boundaries);
        for (int j = 0; j < height; j++) {
            distances[j * width + i] = output[j];
        }
    }

    for (int j = 0; j < height; j++) {
        for (int i = 0; i < width; i++) {
            line[i] = distances[j * width + i];
        }
        transform_line(line, width, output, vertices, boundaries);
        for (int i = 0; i < width; i++) {
            distances[j * width + i] = output[i] == INFINITY ? INFINITY : sqrtf(output[i]) * cell_size;
        }
    }

    free(line);
    free(output);
    free(vertices);
    free(boundaries);
    return MN_OK;
}

MN_API int32_t mn_distances(const uint8_t* mask, int32_t width, int32_t height, float cell_size, float* distances)
{
    return mn_distance_transform(mask, width, height, cell_size, distances);
}

int mn_within_radius(const uint8_t* marked, int width, int height, float cell_size, float radius, uint8_t* result)
{
    int cells = width * height;
    float* distance = (float*)mn_alloc((size_t)cells, sizeof(float));
    if (distance == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a distance transform.");
    }
    int status = mn_distance_transform(marked, width, height, cell_size, distance);
    if (status == MN_OK) {
        for (int k = 0; k < cells; k++) {
            result[k] = (uint8_t)(distance[k] <= radius);
        }
    }
    free(distance);
    return status;
}

MN_API int32_t mn_within(const uint8_t* marked, int32_t width, int32_t height, float cell_size, float radius, uint8_t* result)
{
    return mn_within_radius(marked, width, height, cell_size, radius, result);
}

/* ---- 8-connected components in row-major scan order, cells in breadth-first order; `cells` may
 * already hold items and the offsets index it absolutely ---- */

int mn_components(const uint8_t* mask, int width, int height, int* labels, mn_ints* cells, mn_ints* offsets)
{
    int count = width * height;
    int* queue = (int*)mn_alloc((size_t)count, sizeof(int));
    if (queue == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for labelling %d cells.", count);
    }
    for (int k = 0; k < count; k++) {
        labels[k] = -1;
    }
    int status = mn_ints_push(offsets, cells->count);
    int components = 0;
    for (int j = 0; j < height && status == MN_OK; j++) {
        for (int i = 0; i < width && status == MN_OK; i++) {
            int seed = j * width + i;
            if (!mask[seed] || labels[seed] >= 0) {
                continue;
            }
            int id = components++;
            labels[seed] = id;
            int head = 0;
            int tail = 0;
            queue[tail++] = seed;
            while (head < tail && status == MN_OK) {
                int c = queue[head++];
                status = mn_ints_push(cells, c);
                int ci = c % width;
                int cj = c / width;
                for (int dj = -1; dj <= 1; dj++) {
                    for (int di = -1; di <= 1; di++) {
                        int ii = ci + di;
                        int jj = cj + dj;
                        if (ii < 0 || ii >= width || jj < 0 || jj >= height || !mask[jj * width + ii]) {
                            continue;
                        }
                        int n = jj * width + ii;
                        if (labels[n] < 0) {
                            labels[n] = id;
                            queue[tail++] = n;
                        }
                    }
                }
            }
            if (status == MN_OK) {
                status = mn_ints_push(offsets, cells->count);
            }
        }
    }
    free(queue);
    return status;
}

MN_API int32_t mn_label(const uint8_t* mask, int32_t width, int32_t height, int32_t* labels, int32_t** component_cells, int32_t** component_offsets, int32_t* component_count)
{
    mn_ints cells = { 0 };
    mn_ints offsets = { 0 };
    int status = mn_components(mask, width, height, labels, &cells, &offsets);
    if (status != MN_OK) {
        mn_ints_free(&cells);
        mn_ints_free(&offsets);
        return status;
    }
    *component_cells = cells.items;
    *component_offsets = offsets.items;
    *component_count = offsets.count - 1;
    return MN_OK;
}

/* ---- levels ---- */

float mn_ceil_level(float z, float stock_top, float stepdown)
{
    if (mn_isnan(z) || z >= stock_top) {
        return z;
    }
    float steps = floorf((stock_top - z) / stepdown + MN_LEVEL_TOLERANCE);
    return stock_top - steps * stepdown;
}

MN_API float mn_ceil_to_level(float z, float stock_top, float stepdown) { return mn_ceil_level(z, stock_top, stepdown); }

MN_API void mn_ceil_to_levels(const float* z, int32_t cells, float stock_top, float stepdown, float* result)
{
    for (int k = 0; k < cells; k++) {
        result[k] = mn_ceil_level(z[k], stock_top, stepdown);
    }
}

MN_API int32_t mn_reachable_at(float reach_floor, float level) { return mn_reachable_at_level(reach_floor, level); }

int mn_levels_list(float stock_top, float lowest, float stepdown, float** levels, int* count)
{
    int capacity = 16;
    int n = 0;
    float* list = (float*)malloc((size_t)capacity * sizeof(float));
    if (list == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the levels.");
    }
    for (int k = 1;; k++) {
        float level = stock_top - (float)k * stepdown;
        int last = level <= lowest;
        if (last) {
            if (!(lowest < stock_top)) {
                break;
            }
            level = lowest;
        }
        if (n == capacity) {
            capacity *= 2;
            float* grown = (float*)realloc(list, (size_t)capacity * sizeof(float));
            if (grown == NULL) {
                free(list);
                return mn_fail(MN_ERR_MEMORY, "Out of memory for the levels.");
            }
            list = grown;
        }
        list[n++] = level;
        if (last) {
            break;
        }
    }
    *levels = list;
    *count = n;
    return MN_OK;
}

MN_API int32_t mn_levels(float stock_top, float lowest, float stepdown, float** levels, int32_t* count)
{
    if (!(stepdown > 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Stepdown must be positive.");
    }
    return mn_levels_list(stock_top, lowest, stepdown, levels, count);
}

int mn_plan_alloc(const mn_grid* g, int count, mn_plan** plan)
{
    size_t cells = (size_t)mn_cells(g);
    mn_plan* p = (mn_plan*)mn_alloc(1, sizeof(mn_plan));
    if (p == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a plan.");
    }
    p->grid = *g;
    p->count = count;
    p->levels = (float*)mn_alloc((size_t)count, sizeof(float));
    p->masks = (uint8_t*)mn_alloc((size_t)count * cells, 1);
    p->coverage = (uint8_t*)mn_alloc(cells, 1);
    if (p->levels == NULL || p->masks == NULL || p->coverage == NULL) {
        mn_plan_free(p);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a plan of %d levels.", count);
    }
    *plan = p;
    return MN_OK;
}

MN_API void mn_plan_free(mn_plan* plan)
{
    if (plan == NULL) {
        return;
    }
    free(plan->levels);
    free(plan->masks);
    free(plan->coverage);
    free(plan);
}

int mn_slice_build(const mn_grid* g, const float* effective_tip, const float* stock, float stepdown, mn_plan** plan)
{
    if (!(stepdown > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "Stepdown must be positive, got %g.", (double)stepdown);
    }
    mn_map stock_map = { *g, (float*)stock };
    mn_map tip_map = { *g, (float*)effective_tip };
    float stock_top = mn_map_max(&stock_map);
    float lowest = mn_map_min(&tip_map);
    if (mn_isnan(stock_top) || mn_isnan(lowest)) {
        return mn_fail(MN_ERR_ARGUMENT, "Stock or tip map holds no material at all.");
    }

    float* levels;
    int count;
    MN_CHECK(mn_levels_list(stock_top, lowest, stepdown, &levels, &count));
    mn_plan* p;
    int status = mn_plan_alloc(g, count, &p);
    if (status != MN_OK) {
        free(levels);
        return status;
    }

    int cells = mn_cells(g);
    for (int k = 0; k < count; k++) {
        float level = levels[k];
        p->levels[k] = level;
        uint8_t* mask = p->masks + (size_t)k * (size_t)cells;
        for (int c = 0; c < cells; c++) {
            float s = stock[c];
            mask[c] = (uint8_t)(mn_reachable_at_level(effective_tip[c], level) && !mn_isnan(s) && s > level);
        }
    }
    for (int c = 0; c < cells; c++) {
        p->coverage[c] = (uint8_t)!mn_isnan(effective_tip[c]);
    }
    p->lowest = lowest;
    free(levels);
    *plan = p;
    return MN_OK;
}

MN_API int32_t mn_slice(const mn_grid* grid, const float* effective_tip, const float* stock, float stepdown, mn_plan** plan)
{
    return mn_slice_build(grid, effective_tip, stock, stepdown, plan);
}

MN_API int32_t mn_plan_create(const mn_grid* grid, int32_t level_count, const float* levels, const uint8_t* masks, const uint8_t* coverage, float lowest, mn_plan** plan)
{
    mn_plan* p;
    MN_CHECK(mn_plan_alloc(grid, level_count, &p));
    size_t cells = (size_t)mn_cells(grid);
    memcpy(p->levels, levels, (size_t)level_count * sizeof(float));
    memcpy(p->masks, masks, (size_t)level_count * cells);
    memcpy(p->coverage, coverage, cells);
    p->lowest = lowest;
    *plan = p;
    return MN_OK;
}

MN_API int32_t mn_plan_level_count(const mn_plan* plan) { return plan->count; }

MN_API float mn_plan_lowest(const mn_plan* plan) { return plan->lowest; }

MN_API void mn_plan_read(const mn_plan* plan, float* levels, uint8_t* masks, uint8_t* coverage)
{
    size_t cells = (size_t)mn_cells(&plan->grid);
    memcpy(levels, plan->levels, (size_t)plan->count * sizeof(float));
    memcpy(masks, plan->masks, (size_t)plan->count * cells);
    memcpy(coverage, plan->coverage, cells);
}

/* ---- separation scope ---- */

MN_API void mn_model_region(const float* effective_tip, int32_t cells, float floor, uint8_t* region)
{
    for (int k = 0; k < cells; k++) {
        float z = effective_tip[k];
        region[k] = (uint8_t)(!mn_isnan(z) && z > floor + MN_FLOOR_TOLERANCE);
    }
}

MN_API int32_t mn_head_reach(const float* levels, int32_t count, int32_t k, float stock_top, float cutter_length)
{
    float slab_top = k == 0 ? stock_top : levels[k - 1];
    for (int m = k + 1; m < count; m++) {
        if (slab_top > levels[m] + cutter_length + MN_LEVEL_TOLERANCE) {
            return m;
        }
    }
    return -1;
}

MN_API void mn_obstacles(const float* effective_tip, int32_t cells, float level, uint8_t* obstacles)
{
    for (int k = 0; k < cells; k++) {
        float z = effective_tip[k];
        obstacles[k] = (uint8_t)(!mn_isnan(z) && z > level);
    }
}

MN_API float mn_adjacency_margin(float cell_size) { return sqrtf(2.0f) * cell_size; }

MN_API float mn_head_margin(float cell_size, float tolerance) { return mn_adjacency_margin(cell_size) + tolerance; }

/* How far the head reaches beyond the cutter edge; the widest head radius for a frustum. */
MN_API float mn_head_overhang(const mn_tool* tool) { return mn_head_radius(tool) - tool->cutter_diameter / 2.0f; }

static int touches_outside(const int* cells, int count, const mn_grid* g, const float* stock)
{
    for (int n = 0; n < count; n++) {
        int i = cells[n] % g->width;
        int j = cells[n] / g->width;
        for (int dj = -1; dj <= 1; dj++) {
            for (int di = -1; di <= 1; di++) {
                int ii = i + di;
                int jj = j + dj;
                if (!mn_in_bounds(g, ii, jj) || mn_isnan(stock[jj * g->width + ii])) {
                    return 1;
                }
            }
        }
    }
    return 0;
}

/* Islands of standing stock the trench encloses: 8-connected components of the cells standing above
 * the reach floor by more than the tolerance that touch neither the grid border nor a cell without
 * stock, with the volume milling them out removes. */
int mn_islands(const mn_grid* g, const float* standing, const float* effective_tip, const float* stock, float tolerance, mn_ints* cells, mn_ints* offsets, float** volumes, int* count)
{
    int n = mn_cells(g);
    uint8_t* mask = (uint8_t*)mn_alloc((size_t)n, 1);
    int* labels = (int*)mn_alloc((size_t)n, sizeof(int));
    mn_ints components = { 0 };
    mn_ints component_offsets = { 0 };
    int status = MN_OK;
    if (mask == NULL || labels == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the islands.");
        goto done;
    }
    for (int k = 0; k < n; k++) {
        float above = standing[k] - effective_tip[k];
        mask[k] = (uint8_t)(!mn_isnan(stock[k]) && above > tolerance);
    }
    status = mn_components(mask, g->width, g->height, labels, &components, &component_offsets);
    if (status != MN_OK) {
        goto done;
    }

    int total = component_offsets.count - 1;
    *volumes = (float*)mn_alloc((size_t)(total > 0 ? total : 1), sizeof(float));
    if (*volumes == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the islands.");
        goto done;
    }
    *count = 0;
    status = mn_ints_push(offsets, 0);
    float area = g->cell_size * g->cell_size;
    for (int c = 0; c < total && status == MN_OK; c++) {
        const int* list = components.items + component_offsets.items[c];
        int size = component_offsets.items[c + 1] - component_offsets.items[c];
        if (touches_outside(list, size, g, stock)) {
            continue;
        }
        float volume = 0.0f;
        for (int m = 0; m < size && status == MN_OK; m++) {
            volume += (standing[list[m]] - effective_tip[list[m]]) * area;
            status = mn_ints_push(cells, list[m]);
        }
        if (status == MN_OK) {
            (*volumes)[(*count)++] = volume;
            status = mn_ints_push(offsets, cells->count);
        }
    }

done:
    free(mask);
    free(labels);
    mn_ints_free(&components);
    mn_ints_free(&component_offsets);
    return status;
}

MN_API int32_t mn_islands_find(const mn_grid* grid, const float* standing, const float* effective_tip, const float* stock, float tolerance, int32_t** island_cells, int32_t** island_offsets, float** island_volumes, int32_t* island_count)
{
    mn_ints cells = { 0 };
    mn_ints offsets = { 0 };
    float* volumes = NULL;
    int count = 0;
    int status = mn_islands(grid, standing, effective_tip, stock, tolerance, &cells, &offsets, &volumes, &count);
    if (status != MN_OK) {
        mn_ints_free(&cells);
        mn_ints_free(&offsets);
        free(volumes);
        return status;
    }
    *island_cells = cells.items != NULL ? cells.items : (int*)mn_alloc(1, sizeof(int));
    *island_offsets = offsets.items;
    *island_volumes = volumes;
    *island_count = count;
    return MN_OK;
}

/* The plan restricted to the separation scope (SeparationRegion.Build): per level the model region
 * plus the trench of that level, built from the bottom up; standing stock outside; islands below
 * the volume to keep milled out as the unrestricted plan has them. */
int mn_separation_build(mn_plan* plan, const float* effective_tip, const float* stock, const mn_tool* tool, float tolerance, float stock_top, float floor, float min_island_volume, float* standing, mn_ints* island_cells, mn_ints* island_offsets, float** island_volumes, int* island_count)
{
    const mn_grid* g = &plan->grid;
    int width = g->width;
    int height = g->height;
    int cells = width * height;
    int count = plan->count;
    float cell_size = g->cell_size;
    int status = MN_OK;
    float hug_radius = mn_adjacency_margin(cell_size) + MN_RADIUS_TOLERANCE;
    float overhang = mn_head_overhang(tool) + mn_head_margin(cell_size, tolerance);

    uint8_t* model_region = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* masks = (uint8_t*)mn_alloc((size_t)count * (size_t)cells, 1);
    uint8_t* obstacles = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* hug = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* cleared = (uint8_t*)mn_alloc((size_t)cells, 1);
    uint8_t* coverage = (uint8_t*)mn_alloc((size_t)cells, 1);
    float* deepest = (float*)mn_alloc((size_t)cells, sizeof(float));
    mn_ints found_cells = { 0 };
    mn_ints found_offsets = { 0 };
    float* found_volumes = NULL;
    int found_count = 0;
    if (model_region == NULL || masks == NULL || obstacles == NULL || hug == NULL || cleared == NULL || coverage == NULL || deepest == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the separation region.");
        goto done;
    }

    mn_model_region(effective_tip, cells, floor, model_region);
    for (int c = 0; c < cells; c++) {
        deepest[c] = NAN;
    }

    for (int k = count - 1; k >= 0; k--) {
        const uint8_t* allowed = plan->masks + (size_t)k * (size_t)cells;
        mn_obstacles(effective_tip, cells, plan->levels[k], obstacles);
        status = mn_within_radius(obstacles, width, height, cell_size, hug_radius, hug);
        if (status != MN_OK) {
            goto done;
        }
        const uint8_t* below = k + 1 < count ? masks + (size_t)(k + 1) * (size_t)cells : NULL;
        int reach = mn_head_reach(plan->levels, count, k, stock_top, tool->cutter_length);
        if (reach >= 0) {
            status = mn_within_radius(masks + (size_t)reach * (size_t)cells, width, height, cell_size, overhang, cleared);
            if (status != MN_OK) {
                goto done;
            }
        }
        uint8_t* mask = masks + (size_t)k * (size_t)cells;
        for (int c = 0; c < cells; c++) {
            int region = allowed[c] && (hug[c] || (below != NULL && below[c]) || (reach >= 0 && cleared[c]));
            if (region && mn_isnan(deepest[c])) {
                deepest[c] = plan->levels[k];
            }
            mask[c] = (uint8_t)(region || (model_region[c] && allowed[c]));
        }
    }

    const uint8_t* innermost = count > 0 ? masks + (size_t)(count - 1) * (size_t)cells : NULL;
    for (int c = 0; c < cells; c++) {
        standing[c] = NAN;
        float tip = effective_tip[c];
        float stock_z = stock[c];
        coverage[c] = (uint8_t)(plan->coverage[c] && (model_region[c] || (innermost != NULL && innermost[c])));
        if (mn_isnan(tip) || mn_isnan(stock_z) || model_region[c]) {
            continue;
        }
        standing[c] = mn_isnan(deepest[c]) ? stock_z : deepest[c];
    }

    status = mn_islands(g, standing, effective_tip, stock, tolerance, &found_cells, &found_offsets, &found_volumes, &found_count);
    if (status != MN_OK) {
        goto done;
    }
    if (!(min_island_volume >= 0)) {
        status = mn_fail(MN_ERR_OUT_OF_RANGE, "The island volume to keep must be 0 or greater.");
        goto done;
    }

    *island_volumes = (float*)mn_alloc((size_t)(found_count > 0 ? found_count : 1), sizeof(float));
    if (*island_volumes == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the islands.");
        goto done;
    }
    *island_count = 0;
    status = mn_ints_push(island_offsets, 0);
    for (int island = 0; island < found_count && status == MN_OK; island++) {
        if (!(found_volumes[island] < min_island_volume)) {
            continue;
        }
        for (int m = found_offsets.items[island]; m < found_offsets.items[island + 1] && status == MN_OK; m++) {
            int c = found_cells.items[m];
            for (int k = 0; k < count; k++) {
                masks[(size_t)k * (size_t)cells + (size_t)c] = plan->masks[(size_t)k * (size_t)cells + (size_t)c];
            }
            coverage[c] = plan->coverage[c];
            standing[c] = NAN;
            status = mn_ints_push(island_cells, c);
        }
        if (status == MN_OK) {
            (*island_volumes)[(*island_count)++] = found_volumes[island];
            status = mn_ints_push(island_offsets, island_cells->count);
        }
    }

    if (status == MN_OK) {
        memcpy(plan->masks, masks, (size_t)count * (size_t)cells);
        memcpy(plan->coverage, coverage, (size_t)cells);
    }

done:
    free(model_region);
    free(masks);
    free(obstacles);
    free(hug);
    free(cleared);
    free(coverage);
    free(deepest);
    mn_ints_free(&found_cells);
    mn_ints_free(&found_offsets);
    free(found_volumes);
    return status;
}

MN_API int32_t mn_separation(mn_plan* plan, const float* effective_tip, const float* stock, const mn_tool* tool, float tolerance, float stock_top, float floor, float min_island_volume, float* standing, int32_t** island_cells, int32_t** island_offsets, float** island_volumes, int32_t* island_count)
{
    mn_ints cells = { 0 };
    mn_ints offsets = { 0 };
    float* volumes = NULL;
    int count = 0;
    int status = mn_separation_build(plan, effective_tip, stock, tool, tolerance, stock_top, floor, min_island_volume, standing, &cells, &offsets, &volumes, &count);
    if (status != MN_OK) {
        mn_ints_free(&cells);
        mn_ints_free(&offsets);
        free(volumes);
        return status;
    }
    *island_cells = cells.items != NULL ? cells.items : (int*)mn_alloc(1, sizeof(int));
    *island_offsets = offsets.items;
    *island_volumes = volumes;
    *island_count = count;
    return MN_OK;
}

MN_API int32_t mn_islands_remove_below(const int32_t* island_cells, const int32_t* island_offsets, const float* island_volumes, int32_t island_count, float min_volume, int32_t level_count, int32_t cells, const uint8_t* allowed, uint8_t* masks, const uint8_t* full_coverage, uint8_t* coverage, float* standing, int32_t* removed, int32_t* removed_count)
{
    if (!(min_volume >= 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "The island volume to keep must be 0 or greater.");
    }
    int count = 0;
    for (int island = 0; island < island_count; island++) {
        if (!(island_volumes[island] < min_volume)) {
            continue;
        }
        for (int m = island_offsets[island]; m < island_offsets[island + 1]; m++) {
            int c = island_cells[m];
            for (int k = 0; k < level_count; k++) {
                masks[(size_t)k * (size_t)cells + (size_t)c] = allowed[(size_t)k * (size_t)cells + (size_t)c];
            }
            coverage[c] = full_coverage[c];
            standing[c] = NAN;
        }
        removed[count++] = island;
    }
    *removed_count = count;
    return MN_OK;
}
