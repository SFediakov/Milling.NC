#ifndef MN_CORE_H
#define MN_CORE_H

/* Shared internals of the native generation library: status codes, the last error text, the grid
 * types every algorithm works on, and float helpers that reproduce the .NET semantics the C#
 * implementation used (NaN-propagating Max/Min, ties-to-even rounding, saturating float to int,
 * fused multiply-add where System.Numerics fuses), so the output stays bit-identical. */

#include <limits.h>
#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "miller_native.h"

/* ---- errors ------------------------------------------------------------------------------ */

int mn_fail(int status, const char* format, ...);
void* mn_alloc(size_t count, size_t size);

#define MN_CHECK(expr)                                                                             \
    do {                                                                                           \
        int mn_status_ = (expr);                                                                   \
        if (mn_status_ != MN_OK) {                                                                 \
            return mn_status_;                                                                     \
        }                                                                                          \
    } while (0)

/* ---- float semantics of .NET ------------------------------------------------------------- */

static inline int mn_isnan(float x) { return x != x; }

/* MathF.Max: IEEE 754:2019 maximum, NaN propagates, +0 is greater than -0. */
static inline float mn_max(float x, float y)
{
    if (x != y) {
        if (!mn_isnan(x)) {
            return y < x ? x : y;
        }
        return x;
    }
    return signbit(y) ? x : y;
}

/* MathF.Min: IEEE 754:2019 minimum, NaN propagates, -0 is less than +0. */
static inline float mn_min(float x, float y)
{
    if (x != y) {
        if (!mn_isnan(x)) {
            return x < y ? x : y;
        }
        return x;
    }
    return signbit(x) ? x : y;
}

/* Math.Clamp for floats: NaN passes through. */
static inline float mn_clamp(float value, float lo, float hi)
{
    if (value < lo) {
        return lo;
    }
    if (value > hi) {
        return hi;
    }
    return value;
}

static inline int mn_clampi(int value, int lo, int hi) { return value < lo ? lo : value > hi ? hi : value; }

static inline int mn_maxi(int a, int b) { return a > b ? a : b; }

static inline int mn_mini(int a, int b) { return a < b ? a : b; }

/* Float to int as .NET 9+ converts on x64: truncation, NaN to 0, saturation at the limits. */
static inline int mn_f2i(float x)
{
    if (mn_isnan(x)) {
        return 0;
    }
    if (x >= 2147483648.0f) {
        return INT_MAX;
    }
    if (x < -2147483648.0f) {
        return INT_MIN;
    }
    return (int)x;
}

static inline int mn_d2i(double x)
{
    if (x != x) {
        return 0;
    }
    if (x >= 2147483648.0) {
        return INT_MAX;
    }
    if (x < -2147483648.0) {
        return INT_MIN;
    }
    return (int)x;
}

static inline int64_t mn_d2l(double x)
{
    if (x != x) {
        return 0;
    }
    if (x >= 9223372036854775808.0) {
        return INT64_MAX;
    }
    if (x < -9223372036854775808.0) {
        return INT64_MIN;
    }
    return (int64_t)x;
}

/* ---- System.Numerics.Vector3 -------------------------------------------------------------- */

typedef struct mn_v3 {
    float x;
    float y;
    float z;
} mn_v3;

static inline mn_v3 mn_v3_make(float x, float y, float z)
{
    mn_v3 v;
    v.x = x;
    v.y = y;
    v.z = z;
    return v;
}

static inline mn_v3 mn_v3_sub(mn_v3 a, mn_v3 b) { return mn_v3_make(a.x - b.x, a.y - b.y, a.z - b.z); }

static inline mn_v3 mn_v3_add(mn_v3 a, mn_v3 b) { return mn_v3_make(a.x + b.x, a.y + b.y, a.z + b.z); }

static inline mn_v3 mn_v3_scale(mn_v3 a, float s) { return mn_v3_make(a.x * s, a.y * s, a.z * s); }

static inline int mn_v3_equal(mn_v3 a, mn_v3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }

/* Vector128.Dot on x64 (dpps): (x*x' + y*y') + (z*z' + 0). */
static inline float mn_v3_dot(mn_v3 a, mn_v3 b)
{
    float xy = a.x * b.x + a.y * b.y;
    float z = a.z * b.z;
    return xy + z;
}

static inline float mn_v3_distance(mn_v3 a, mn_v3 b)
{
    mn_v3 d = mn_v3_sub(a, b);
    return sqrtf(mn_v3_dot(d, d));
}

/* Vector3.Lerp: MultiplyAddEstimate(a, 1 - t, b * t), fused. */
static inline mn_v3 mn_v3_lerp(mn_v3 a, mn_v3 b, float t)
{
    float u = 1.0f - t;
    return mn_v3_make(fmaf(a.x, u, b.x * t), fmaf(a.y, u, b.y * t), fmaf(a.z, u, b.z * t));
}

/* ---- grids --------------------------------------------------------------------------------- */

typedef struct mn_map {
    mn_grid g;
    float* z;
} mn_map;

static inline int mn_in_bounds(const mn_grid* g, int i, int j) { return i >= 0 && i < g->width && j >= 0 && j < g->height; }

static inline int mn_cells(const mn_grid* g) { return g->width * g->height; }

static inline float mn_center_x(const mn_grid* g, int i) { return g->origin_x + ((float)i + 0.5f) * g->cell_size; }

static inline float mn_center_y(const mn_grid* g, int j) { return g->origin_y + ((float)j + 0.5f) * g->cell_size; }

static inline int mn_cell_i(const mn_grid* g, float x) { return mn_f2i(floorf((x - g->origin_x) / g->cell_size)); }

static inline int mn_cell_j(const mn_grid* g, float y) { return mn_f2i(floorf((y - g->origin_y) / g->cell_size)); }

static inline int mn_same_grid(const mn_grid* a, const mn_grid* b)
{
    return a->width == b->width && a->height == b->height && a->cell_size == b->cell_size && a->origin_x == b->origin_x
        && a->origin_y == b->origin_y;
}

int mn_map_create(mn_map* map, const mn_grid* g, float fill);
int mn_map_clone(mn_map* copy, const mn_map* source);
void mn_map_free(mn_map* map);
void mn_map_fill(mn_map* map, float value);
float mn_map_min(const mn_map* map);
float mn_map_max(const mn_map* map);

/* ---- growing arrays ------------------------------------------------------------------------ */

typedef struct mn_ints {
    int* items;
    int count;
    int capacity;
} mn_ints;

int mn_ints_push(mn_ints* list, int value);
void mn_ints_free(mn_ints* list);

/* ---- progress and cancellation ------------------------------------------------------------- */

typedef struct mn_monitor {
    mn_progress_fn progress;
    void* context;
    const volatile int32_t* cancel;
} mn_monitor;

static inline int mn_cancelled(const mn_monitor* m) { return m != NULL && m->cancel != NULL && *m->cancel != 0; }

static inline void mn_report(const mn_monitor* m, int step, int steps, float fraction)
{
    if (m != NULL && m->progress != NULL) {
        m->progress(m->context, step, steps, fraction);
    }
}

#endif
