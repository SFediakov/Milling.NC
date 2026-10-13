#include "mn_internal.h"
#include "mn_thread.h"

/* Placement, grids and top-down rasterization (Mesh.Transform, MeshRasterizer, StockModel).
 * Transform follows System.Numerics Vector3.Transform: row-major 4 x 4 matrix with row vectors,
 * the second and third rows fused into the running sum. */

/* MeshRasterizer.BarycentricTolerance and MinProjectedArea; CellCountEpsilon of CreateGridFor. */
#define MN_BARYCENTRIC_TOLERANCE 1e-5f
#define MN_MIN_PROJECTED_AREA 1e-7f
#define MN_CELL_COUNT_EPSILON 1e-4f

void mn_transform_point(const float* point, const float* m, float* result)
{
    float px = point[0];
    float py = point[1];
    float pz = point[2];
    for (int c = 0; c < 3; c++) {
        float r = m[c] * px;
        r = fmaf(m[4 + c], py, r);
        r = fmaf(m[8 + c], pz, r);
        result[c] = r + m[12 + c];
    }
}

MN_API void mn_transform_points(const float* points, int32_t point_count, const float* matrix, float* result)
{
    for (int k = 0; k < point_count; k++) {
        mn_transform_point(points + 3 * k, matrix, result + 3 * k);
    }
}

int mn_grid_from_bounds(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid)
{
    if (!(cell_size > 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Cell size must be positive.");
    }
    if (min_x > max_x || min_y > max_y) {
        return mn_fail(MN_ERR_ARGUMENT, "Cannot create a grid for empty bounds.");
    }
    float size_x = max_x - min_x;
    float size_y = max_y - min_y;
    grid->origin_x = min_x;
    grid->origin_y = min_y;
    grid->cell_size = cell_size;
    grid->width = mn_maxi(1, mn_f2i(ceilf(size_x / cell_size - MN_CELL_COUNT_EPSILON)));
    grid->height = mn_maxi(1, mn_f2i(ceilf(size_y / cell_size - MN_CELL_COUNT_EPSILON)));
    return MN_OK;
}

MN_API int32_t mn_grid_for(float min_x, float min_y, float max_x, float max_y, float cell_size, mn_grid* grid)
{
    return mn_grid_from_bounds(min_x, min_y, max_x, max_y, cell_size, grid);
}

/* The cells of row j that triangle t covers, within [i0, i1]: a cell takes the triangle's height
 * when higher (or when it had none yet). */
static void rasterize_row(const float* t, const mn_grid* g, float* z, int j, int i0, int i1, float inv_area)
{
    float ax = t[0], ay = t[1], bx = t[3], by = t[4], cx = t[6], cy = t[7];
    float py = mn_center_y(g, j);
    for (int i = i0; i <= i1; i++) {
        float px = mn_center_x(g, i);
        float wa = ((bx - px) * (cy - py) - (cx - px) * (by - py)) * inv_area;
        float wb = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) * inv_area;
        float wc = 1.0f - wa - wb;
        if (wa < -MN_BARYCENTRIC_TOLERANCE || wb < -MN_BARYCENTRIC_TOLERANCE || wc < -MN_BARYCENTRIC_TOLERANCE) {
            continue;
        }

        float height = wa * t[2] + wb * t[5] + wc * t[8];
        float* current = &z[j * g->width + i];
        if (mn_isnan(*current) || height > *current) {
            *current = height;
        }
    }
}

typedef struct raster_job {
    const float* triangles;
    const mn_grid* g;
    float* z;
    const int* row_start;
    const int* row_items;
    const int* i0;
    const int* i1;
    const float* inv_area;
} raster_job;

static void raster_row(void* context, int j, void* scratch)
{
    (void)scratch;
    const raster_job* job = (const raster_job*)context;
    for (int m = job->row_start[j]; m < job->row_start[j + 1]; m++) {
        int k = job->row_items[m];
        rasterize_row(job->triangles + 9 * k, job->g, job->z, j, job->i0[k], job->i1[k], job->inv_area[k]);
    }
}

/* Rows run on the worker pool (T-160): every row holds the triangles that cover it, in triangle
 * order, so a cell sees the same heights in the same order as a triangle-by-triangle pass, and the
 * highest wins whatever the order. */
int mn_rasterize_triangles(const float* triangles, int count, const mn_grid* g, float* z)
{
    int height = g->height;
    int* i0 = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    int* i1 = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    int* j0 = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    int* j1 = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    float* inv_area = (float*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(float));
    int* row_start = (int*)mn_alloc((size_t)height + 1, sizeof(int));
    int* row_items = NULL;
    int* cursor = NULL;
    int status = MN_OK;
    if (i0 == NULL || i1 == NULL || j0 == NULL || j1 == NULL || inv_area == NULL || row_start == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for rasterizing %d triangles.", count);
        goto done;
    }
    for (int k = 0; k < count; k++) {
        const float* t = triangles + 9 * k;
        float ax = t[0], ay = t[1], bx = t[3], by = t[4], cx = t[6], cy = t[7];
        float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        j0[k] = 1;
        j1[k] = 0;
        if (fabsf(area) < MN_MIN_PROJECTED_AREA) {
            continue;
        }
        inv_area[k] = 1.0f / area;
        i0[k] = mn_maxi(mn_cell_i(g, mn_min(ax, mn_min(bx, cx))), 0);
        j0[k] = mn_maxi(mn_cell_j(g, mn_min(ay, mn_min(by, cy))), 0);
        i1[k] = mn_mini(mn_cell_i(g, mn_max(ax, mn_max(bx, cx))), g->width - 1);
        j1[k] = mn_mini(mn_cell_j(g, mn_max(ay, mn_max(by, cy))), height - 1);
        for (int j = j0[k]; j <= j1[k]; j++) {
            row_start[j + 1]++;
        }
    }
    for (int j = 0; j < height; j++) {
        row_start[j + 1] += row_start[j];
    }
    int total = row_start[height];
    row_items = (int*)mn_alloc((size_t)(total > 0 ? total : 1), sizeof(int));
    cursor = (int*)mn_alloc((size_t)height + 1, sizeof(int));
    if (row_items == NULL || cursor == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for rasterizing %d triangles.", count);
        goto done;
    }
    memcpy(cursor, row_start, (size_t)height * sizeof(int));
    for (int k = 0; k < count; k++) {
        for (int j = j0[k]; j <= j1[k]; j++) {
            row_items[cursor[j]++] = k;
        }
    }
    raster_job job = { triangles, g, z, row_start, row_items, i0, i1, inv_area };
    status = mn_rows_parallel(height, raster_row, &job, NULL, mn_worker_count());

done:
    free(i0);
    free(i1);
    free(j0);
    free(j1);
    free(inv_area);
    free(row_start);
    free(row_items);
    free(cursor);
    return status;
}

MN_API int32_t mn_rasterize(const float* triangles, int32_t triangle_count, const mn_grid* grid, float* z)
{
    return mn_rasterize_triangles(triangles, triangle_count, grid, z);
}

/* Box: every cell at the top; cylinder: cells whose centre lies outside the circle inscribed in the
 * bounding square hold no material. */
int mn_make_stock(float corner_x, float corner_y, float size_x, float size_y, float top, int cylinder, float diameter, float cell_size, mn_map* stock)
{
    mn_grid g;
    MN_CHECK(mn_grid_from_bounds(corner_x, corner_y, corner_x + size_x, corner_y + size_y, cell_size, &g));
    MN_CHECK(mn_map_create(stock, &g, top));
    if (cylinder) {
        float centre_x = corner_x + size_x / 2.0f;
        float centre_y = corner_y + size_y / 2.0f;
        float radius = diameter / 2.0f;
        float r2 = radius * radius;
        for (int j = 0; j < g.height; j++) {
            for (int i = 0; i < g.width; i++) {
                float dx = mn_center_x(&g, i) - centre_x;
                float dy = mn_center_y(&g, j) - centre_y;
                if (dx * dx + dy * dy > r2) {
                    stock->z[j * g.width + i] = NAN;
                }
            }
        }
    }
    return MN_OK;
}

MN_API int32_t mn_stock_map(float corner_x, float corner_y, float size_x, float size_y, float top, int32_t cylinder, float diameter, float cell_size, mn_grid* grid, float** z)
{
    mn_map stock;
    MN_CHECK(mn_make_stock(corner_x, corner_y, size_x, size_y, top, cylinder, diameter, cell_size, &stock));
    *grid = stock.g;
    *z = stock.z;
    return MN_OK;
}
