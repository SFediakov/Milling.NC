#include <stdio.h>

#include "mn_gpu.h"
#include "mn_thread.h"

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define CL_CALL __stdcall
#else
#include <dlfcn.h>
#define CL_CALL
#endif

/* ---- the OpenCL 1.2 API as far as used here (types and values of the Khronos headers) ---- */

typedef int32_t cl_int;
typedef uint32_t cl_uint;
typedef uint64_t cl_ulong;
typedef cl_ulong cl_bitfield;
typedef cl_bitfield cl_device_type;
typedef cl_bitfield cl_mem_flags;
typedef cl_bitfield cl_command_queue_properties;
typedef cl_uint cl_bool;
typedef intptr_t cl_context_properties;
typedef struct _cl_platform_id* cl_platform_id;
typedef struct _cl_device_id* cl_device_id;
typedef struct _cl_context* cl_context;
typedef struct _cl_command_queue* cl_command_queue;
typedef struct _cl_mem* cl_mem;
typedef struct _cl_program* cl_program;
typedef struct _cl_kernel* cl_kernel;
typedef struct _cl_event* cl_event;

#define CL_SUCCESS 0
#define CL_TRUE 1
#define CL_DEVICE_TYPE_GPU ((cl_bitfield)1 << 2)
#define CL_DEVICE_MAX_COMPUTE_UNITS 0x1002
#define CL_DEVICE_MAX_CLOCK_FREQUENCY 0x100C
#define CL_DEVICE_MAX_MEM_ALLOC_SIZE 0x1010
#define CL_DEVICE_SINGLE_FP_CONFIG 0x101B
#define CL_DEVICE_GLOBAL_MEM_SIZE 0x101F
#define CL_DEVICE_NAME 0x102B
#define CL_DEVICE_HOST_UNIFIED_MEMORY 0x1035
#define CL_FP_CORRECTLY_ROUNDED_DIVIDE_SQRT ((cl_bitfield)1 << 7)
#define CL_PROGRAM_BUILD_LOG 0x1183
#define CL_MEM_READ_WRITE ((cl_bitfield)1 << 0)
#define CL_MEM_READ_ONLY ((cl_bitfield)1 << 2)
#define CL_MEM_COPY_HOST_PTR ((cl_bitfield)1 << 5)
#define CL_FALSE 0

typedef cl_int(CL_CALL* pfn_get_platforms)(cl_uint, cl_platform_id*, cl_uint*);
typedef cl_int(CL_CALL* pfn_get_devices)(cl_platform_id, cl_device_type, cl_uint, cl_device_id*, cl_uint*);
typedef cl_int(CL_CALL* pfn_device_info)(cl_device_id, cl_uint, size_t, void*, size_t*);
typedef cl_context(CL_CALL* pfn_create_context)(const cl_context_properties*, cl_uint, const cl_device_id*, void(CL_CALL*)(const char*, const void*, size_t, void*), void*, cl_int*);
typedef cl_command_queue(CL_CALL* pfn_create_queue)(cl_context, cl_device_id, cl_command_queue_properties, cl_int*);
typedef cl_program(CL_CALL* pfn_create_program)(cl_context, cl_uint, const char**, const size_t*, cl_int*);
typedef cl_int(CL_CALL* pfn_build_program)(cl_program, cl_uint, const cl_device_id*, const char*, void(CL_CALL*)(cl_program, void*), void*);
typedef cl_int(CL_CALL* pfn_build_info)(cl_program, cl_device_id, cl_uint, size_t, void*, size_t*);
typedef cl_kernel(CL_CALL* pfn_create_kernel)(cl_program, const char*, cl_int*);
typedef cl_mem(CL_CALL* pfn_create_buffer)(cl_context, cl_mem_flags, size_t, void*, cl_int*);
typedef cl_int(CL_CALL* pfn_set_arg)(cl_kernel, cl_uint, size_t, const void*);
typedef cl_int(CL_CALL* pfn_enqueue_kernel)(cl_command_queue, cl_kernel, cl_uint, const size_t*, const size_t*, const size_t*, cl_uint, const cl_event*, cl_event*);
typedef cl_int(CL_CALL* pfn_enqueue_write)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, const void*, cl_uint, const cl_event*, cl_event*);
typedef cl_int(CL_CALL* pfn_enqueue_read)(cl_command_queue, cl_mem, cl_bool, size_t, size_t, void*, cl_uint, const cl_event*, cl_event*);
typedef cl_int(CL_CALL* pfn_enqueue_fill)(cl_command_queue, cl_mem, const void*, size_t, size_t, size_t, cl_uint, const cl_event*, cl_event*);
typedef cl_int(CL_CALL* pfn_enqueue_copy)(cl_command_queue, cl_mem, cl_mem, size_t, size_t, size_t, cl_uint, const cl_event*, cl_event*);
typedef cl_int(CL_CALL* pfn_finish)(cl_command_queue);
typedef cl_int(CL_CALL* pfn_flush)(cl_command_queue);
typedef cl_int(CL_CALL* pfn_release_mem)(cl_mem);
typedef cl_int(CL_CALL* pfn_wait_events)(cl_uint, const cl_event*);
typedef cl_int(CL_CALL* pfn_release_event)(cl_event);

/* ---- kernels ---- */

/* The CPU loops they port: remaining_row (mn_maps.c), start_row, height_row, source_row and verdict_row
 * (mn_backwards.c), reach_row (mn_reach.c), the level loop of mn_separation_levels and mn_obstacles,
 * mn_model_region (mn_slicing.c), deepest_level_row (mn_collision.c), mn_head_radius_at and
 * mn_meet_radius_at (mn_profile.c), mn_min and mn_max (mn_core.h); the same float operations in the same
 * order. The reach map's vote picks the lowest top at which it passes by bisection over the ordered bits
 * of the tops (the vote only grows with the value, so the first passing top is the one the CPU's
 * selection returns); the distance tests of the separation take the squared distance to the nearest
 * marked cell within the radius by two windowed minima of whole squares, exact in floats, which is what
 * the CPU's distance transform gives there. The power-distance
 * envelope of the check (mn_lower_envelope on the CPU, a sequential scan per line) is two windowed minima
 * here, one per cell: a verdict only asks whether the envelope reaches 0, a value at or below 0 comes from
 * a disk within the widest radius of the level, and within that window both take the same terms
 * f + d * d, so the verdicts agree while the many cells run in parallel. */
static const char* kernel_source =
    "#pragma OPENCL FP_CONTRACT OFF\n"
    "float net_max(float x, float y) { if (x != y) { if (!isnan(x)) { return y < x ? x : y; } return x; } return signbit(y) ? x : y; }\n"
    "float net_min(float x, float y) { if (x != y) { if (!isnan(x)) { return x < y ? x : y; } return x; } return signbit(x) ? x : y; }\n"
    "kernel void closing(global const float* in, int width, int height, global const int* odx, global const int* ody, global const float* odz, int count, global float* out)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= width * height) { return; }\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    float best = NAN;\n"
    "    for (int o = 0; o < count; o++) {\n"
    "        int ii = i + odx[o];\n"
    "        int jj = j + ody[o];\n"
    "        if (ii < 0 || ii >= width || jj < 0 || jj >= height) { continue; }\n"
    "        float t = in[jj * width + ii];\n"
    "        if (isnan(t)) { continue; }\n"
    "        float candidate = t + odz[o];\n"
    "        if (isnan(best) || candidate < best) { best = candidate; }\n"
    "    }\n"
    "    out[c] = best;\n"
    "}\n"
    "kernel void mask_deepest(global const uchar* masks, int levels, int cells, global int* deepest, global int* next)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    int d = -1;\n"
    "    for (int k = levels - 1; k >= 0 && d < 0; k--) { if (masks[(size_t)k * (size_t)cells + (size_t)c]) { d = k; } }\n"
    "    deepest[c] = d;\n"
    "    next[c] = d;\n"
    "}\n"
    "kernel void settle(global int* deepest, global int* next, global const float* levels, int cells, global float* heights, global int* changed)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    int n = next[c];\n"
    "    if (n != deepest[c]) { atomic_inc(changed); }\n"
    "    deepest[c] = n;\n"
    "    heights[c] = n >= 0 ? levels[n] : NAN;\n"
    "    next[c] = -1;\n"
    "}\n"
    "float meet_radius(float h, float bottom, float top, float widening, float head_radius, float cutter_radius)\n"
    "{\n"
    "    float radius = (!(widening > 0) || h >= widening) ? head_radius : bottom + (top - bottom) * net_max(0.0f, h) / widening;\n"
    "    return net_max(radius, cutter_radius);\n"
    "}\n"
    "kernel void backward_sources(global const float* stock, global const float* closing, global const float* levels, int first_level, int cells, float stock_top,\n"
    "    float cutter_length, float margin, float cell_size, float bottom, float top, float widening, float head_radius, float cutter_radius, global float* field, global int* sources,\n"
    "    global int* widest)\n"
    "{\n"
    "    size_t gid = get_global_id(0);\n"
    "    int l = (int)(gid / (size_t)cells);\n"
    "    int c = (int)(gid % (size_t)cells);\n"
    "    int k = first_level + l;\n"
    "    float head_bottom = levels[k] + cutter_length;\n"
    "    float above = k > 0 ? levels[k - 1] : stock_top;\n"
    "    float s = stock[c];\n"
    "    float a = closing[c];\n"
    "    float value = INFINITY;\n"
    "    if (!isnan(s)) {\n"
    "        float t = isnan(a) ? s : net_min(s, net_max(a, above));\n"
    "        float h = t - head_bottom;\n"
    "        if (h > 1e-4f) {\n"
    "            float radius = (meet_radius(h, bottom, top, widening, head_radius, cutter_radius) + margin) / cell_size;\n"
    "            value = -radius * radius;\n"
    "            atomic_inc(&sources[l]);\n"
    "            atomic_max(&widest[l], as_int(radius));\n"
    "        }\n"
    "    }\n"
    "    field[gid] = value;\n"
    "}\n"
    "kernel void window_columns(global const float* in, global float* out, int width, int height, global const int* widest)\n"
    "{\n"
    "    size_t gid = get_global_id(0);\n"
    "    size_t cells = (size_t)width * (size_t)height;\n"
    "    size_t l = gid / cells;\n"
    "    int c = (int)(gid % cells);\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    int w = (int)ceil(as_float(widest[l]));\n"
    "    global const float* f = in + l * cells;\n"
    "    float best = INFINITY;\n"
    "    for (int dj = -w; dj <= w; dj++) {\n"
    "        int jj = j + dj;\n"
    "        if (jj < 0 || jj >= height) { continue; }\n"
    "        float v = f[(size_t)jj * (size_t)width + (size_t)i];\n"
    "        if (v == INFINITY) { continue; }\n"
    "        float candidate = v + (float)dj * (float)dj;\n"
    "        if (candidate < best) { best = candidate; }\n"
    "    }\n"
    "    out[gid] = best;\n"
    "}\n"
    "kernel void window_rows(global const float* in, global float* out, int width, int height, global const int* widest)\n"
    "{\n"
    "    size_t gid = get_global_id(0);\n"
    "    size_t cells = (size_t)width * (size_t)height;\n"
    "    size_t l = gid / cells;\n"
    "    int c = (int)(gid % cells);\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    int w = (int)ceil(as_float(widest[l]));\n"
    "    global const float* f = in + l * cells + (size_t)j * (size_t)width;\n"
    "    float best = INFINITY;\n"
    "    for (int di = -w; di <= w; di++) {\n"
    "        int ii = i + di;\n"
    "        if (ii < 0 || ii >= width) { continue; }\n"
    "        float v = f[ii];\n"
    "        if (v == INFINITY) { continue; }\n"
    "        float candidate = v + (float)di * (float)di;\n"
    "        if (candidate < best) { best = candidate; }\n"
    "    }\n"
    "    out[gid] = best;\n"
    "}\n"
    "kernel void backward_verdict(global const float* field, global const uchar* masks, global const int* sources, int first_level, int cells, global int* next)\n"
    "{\n"
    "    size_t gid = get_global_id(0);\n"
    "    int l = (int)(gid / (size_t)cells);\n"
    "    int c = (int)(gid % (size_t)cells);\n"
    "    int k = first_level + l;\n"
    "    if (!masks[(size_t)k * (size_t)cells + (size_t)c]) { return; }\n"
    "    if (sources[l] == 0 || field[gid] > 0.0f) { atomic_max(&next[c], k); }\n"
    "}\n"
    "uint ordered(float f) { uint k = as_uint(f); return (k & 0x80000000u) != 0u ? ~k : (k | 0x80000000u); }\n"
    "float unordered(uint k) { return as_float((k & 0x80000000u) != 0u ? (k & 0x7fffffffu) : ~k); }\n"
    "kernel void reach_values(global const float* model, global const float* stock, int cells, float floor, global float* value)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    float top = model[c];\n"
    "    value[c] = isnan(stock[c]) ? NAN : isnan(top) ? floor : top;\n"
    "}\n"
    "kernel void reach_round(global const float* value, global const uchar* reached, global uchar* reached_next, global float* reach, int width, int height,\n"
    "    global const int* odx, global const int* ody, global const float* odz, int count, global const int* needed, int full, int last, float floor, float tolerance)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= width * height) { return; }\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    int n = 0;\n"
    "    int excluded = 0;\n"
    "    float top_max = 0.0f;\n"
    "    uint lo = 0xffffffffu;\n"
    "    uint hi = 0u;\n"
    "    for (int o = 0; o < count; o++) {\n"
    "        int ii = i + odx[o];\n"
    "        int jj = j + ody[o];\n"
    "        if (ii < 0 || ii >= width || jj < 0 || jj >= height) { continue; }\n"
    "        int k = jj * width + ii;\n"
    "        float top = value[k];\n"
    "        if (isnan(top)) { continue; }\n"
    "        float t = top - odz[o];\n"
    "        top_max = n == 0 ? t : net_max(top_max, t);\n"
    "        excluded += reached[k] ? 1 : 0;\n"
    "        uint key = ordered(t);\n"
    "        lo = min(lo, key);\n"
    "        hi = max(hi, key);\n"
    "        n++;\n"
    "    }\n"
    "    if (n == 0 || excluded == n) { return; }\n"
    "    float threshold = top_max;\n"
    "    if (!full) {\n"
    "        int rank = clamp(needed[n] - 1, 0, n - 1) + 1;\n"
    "        while (lo < hi) {\n"
    "            uint mid = lo + (hi - lo) / 2u;\n"
    "            int below = 0;\n"
    "            int below_excluded = 0;\n"
    "            for (int o = 0; o < count; o++) {\n"
    "                int ii = i + odx[o];\n"
    "                int jj = j + ody[o];\n"
    "                if (ii < 0 || ii >= width || jj < 0 || jj >= height) { continue; }\n"
    "                int k = jj * width + ii;\n"
    "                float top = value[k];\n"
    "                if (isnan(top) || ordered(top - odz[o]) > mid) { continue; }\n"
    "                below++;\n"
    "                below_excluded += reached[k] ? 1 : 0;\n"
    "            }\n"
    "            int pass = excluded == 0 ? below >= rank : below - below_excluded >= needed[n - below_excluded];\n"
    "            if (pass) { hi = mid; } else { lo = mid + 1u; }\n"
    "        }\n"
    "        threshold = unordered(lo);\n"
    "    }\n"
    "    float z = net_max(threshold, floor);\n"
    "    float previous = reach[c];\n"
    "    if (!(isnan(previous) || z < previous)) { return; }\n"
    "    reach[c] = z;\n"
    "    if (last) { return; }\n"
    "    float touched = z - tolerance;\n"
    "    for (int o = 0; o < count; o++) {\n"
    "        int ii = i + odx[o];\n"
    "        int jj = j + ody[o];\n"
    "        if (ii < 0 || ii >= width || jj < 0 || jj >= height) { continue; }\n"
    "        int k = jj * width + ii;\n"
    "        float top = value[k];\n"
    "        if (!isnan(top) && top - odz[o] >= touched) { reached_next[k] = 1; }\n"
    "    }\n"
    "}\n"
    "kernel void mark_above(global const float* tip, float level, int cells, global uchar* out)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    float z = tip[c];\n"
    "    out[c] = (uchar)(!isnan(z) && z > level);\n"
    "}\n"
    "kernel void distance_columns(global const uchar* mask, int level, int width, int height, int w, global float* out)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= width * height) { return; }\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    global const uchar* m = mask + (size_t)level * (size_t)width * (size_t)height;\n"
    "    float best = INFINITY;\n"
    "    for (int dj = -w; dj <= w; dj++) {\n"
    "        int jj = j + dj;\n"
    "        if (jj < 0 || jj >= height || !m[(size_t)jj * (size_t)width + (size_t)i]) { continue; }\n"
    "        float candidate = (float)dj * (float)dj;\n"
    "        if (candidate < best) { best = candidate; }\n"
    "    }\n"
    "    out[c] = best;\n"
    "}\n"
    "kernel void distance_rows_within(global const float* in, int width, int height, int w, float cell_size, float radius, global uchar* out)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= width * height) { return; }\n"
    "    int i = c % width;\n"
    "    int j = c / width;\n"
    "    global const float* f = in + (size_t)j * (size_t)width;\n"
    "    float best = INFINITY;\n"
    "    for (int di = -w; di <= w; di++) {\n"
    "        int ii = i + di;\n"
    "        if (ii < 0 || ii >= width) { continue; }\n"
    "        float v = f[ii];\n"
    "        if (v == INFINITY) { continue; }\n"
    "        float candidate = v + (float)di * (float)di;\n"
    "        if (candidate < best) { best = candidate; }\n"
    "    }\n"
    "    out[c] = (uchar)(best != INFINITY && sqrt(best) * cell_size <= radius);\n"
    "}\n"
    "kernel void slope_sources(global const float* deepest_position, int cells, float slab_top, float tolerance, float cutter_length, float bottom, float top, float widening,\n"
    "    float head_radius, float cutter_radius, float margin, float cell_size, global float* field, global int* sources, global int* widest)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    float h = slab_top + tolerance - deepest_position[c] - cutter_length;\n"
    "    float radius = meet_radius(h, bottom, top, widening, head_radius, cutter_radius) - cutter_radius + margin;\n"
    "    int widens = h > 1e-4f && !(h > widening + 1e-4f);\n"
    "    if (!widens) { field[c] = INFINITY; return; }\n"
    "    float r = radius / cell_size;\n"
    "    field[c] = -r * r;\n"
    "    atomic_inc(sources);\n"
    "    atomic_max(widest, as_int(r));\n"
    "}\n"
    "kernel void separation_update(global const uchar* allowed, global const uchar* hug, global uchar* masks, int level_index, int has_below, global const uchar* cleared,\n"
    "    int any_cleared, global const float* slope, global const int* sources, global const uchar* model_region, float level, int cells, global float* deepest,\n"
    "    global float* deepest_position)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    size_t at = (size_t)level_index * (size_t)cells + (size_t)c;\n"
    "    int clear = any_cleared && cleared[c];\n"
    "    if (sources[0] > 0) { clear = clear || slope[c] <= 0.0f; }\n"
    "    int region = allowed[at] && (hug[c] || (has_below && masks[at + (size_t)cells]) || clear);\n"
    "    if (region && isnan(deepest[c])) { deepest[c] = level; }\n"
    "    int m = region || (model_region[c] && allowed[at]);\n"
    "    masks[at] = (uchar)m;\n"
    "    if (m && isnan(deepest_position[c])) { deepest_position[c] = level; }\n"
    "}\n"
    "kernel void model_region(global const float* tip, int cells, float floor, global uchar* region)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    float z = tip[c];\n"
    "    region[c] = (uchar)(!isnan(z) && z > floor + 1e-4f);\n"
    "}\n"
    "kernel void deepest_heights(global const uchar* masks, global const float* levels, int count, int cells, global float* out)\n"
    "{\n"
    "    int c = get_global_id(0);\n"
    "    if (c >= cells) { return; }\n"
    "    float d = NAN;\n"
    "    for (int k = count - 1; k >= 0 && isnan(d); k--) { if (masks[(size_t)k * (size_t)cells + (size_t)c]) { d = levels[k]; } }\n"
    "    out[c] = d;\n"
    "}\n"
    "uint hash32(uint a)\n"
    "{\n"
    "    a ^= a >> 16; a *= 0x7feb352dU; a ^= a >> 15; a *= 0x846ca68bU; a ^= a >> 16;\n"
    "    return a;\n"
    "}\n"
    "kernel void tours(global const float* x, global const float* y, int n, int start, int end, global int* remaining, global int* orders, global float* lengths,\n"
    "    local float* best_d, local int* best_k, local int* best_p)\n"
    "{\n"
    "    int t = get_group_id(0);\n"
    "    int lid = get_local_id(0);\n"
    "    int size = get_local_size(0);\n"
    "    global int* left = remaining + (size_t)t * (size_t)n;\n"
    "    global int* order = orders + (size_t)t * (size_t)n;\n"
    "    for (int k = lid; k < n; k += size) { left[k] = k; }\n"
    "    barrier(CLK_GLOBAL_MEM_FENCE);\n"
    "    if (lid == 0) { left[start] = n - 1; order[0] = start; }\n"
    "    barrier(CLK_GLOBAL_MEM_FENCE);\n"
    "    int m = n - 1;\n"
    "    int current = start;\n"
    "    float length = 0.0f;\n"
    "    for (int step = 1; step < n; step++) {\n"
    "        float cx = x[current];\n"
    "        float cy = y[current];\n"
    "        float mine = INFINITY;\n"
    "        int pick = -1;\n"
    "        int at = -1;\n"
    "        int last = step == n - 1;\n"
    "        for (int q = lid; q < m; q += size) {\n"
    "            int k = left[q];\n"
    "            if (k == end && !last) { continue; }\n"
    "            float dx = x[k] - cx;\n"
    "            float dy = y[k] - cy;\n"
    "            float d = sqrt(dx * dx + dy * dy);\n"
    "            if (t > 0) { d = d * (1.0f + 0.25f * (float)(hash32((uint)t * 0x9e3779b9U ^ (uint)k) & 0xffffU) / 65535.0f); }\n"
    "            if (d < mine || (d == mine && k < pick)) { mine = d; pick = k; at = q; }\n"
    "        }\n"
    "        best_d[lid] = mine;\n"
    "        best_k[lid] = pick;\n"
    "        best_p[lid] = at;\n"
    "        barrier(CLK_LOCAL_MEM_FENCE);\n"
    "        for (int span = size / 2; span > 0; span /= 2) {\n"
    "            if (lid < span) {\n"
    "                float od = best_d[lid + span];\n"
    "                int ok = best_k[lid + span];\n"
    "                if (ok >= 0 && (best_k[lid] < 0 || od < best_d[lid] || (od == best_d[lid] && ok < best_k[lid]))) { best_d[lid] = od; best_k[lid] = ok; best_p[lid] = best_p[lid + span]; }\n"
    "            }\n"
    "            barrier(CLK_LOCAL_MEM_FENCE);\n"
    "        }\n"
    "        int next = best_k[0];\n"
    "        if (lid == 0) {\n"
    "            left[best_p[0]] = left[m - 1];\n"
    "            order[step] = next;\n"
    "            float dx = x[next] - cx;\n"
    "            float dy = y[next] - cy;\n"
    "            length += sqrt(dx * dx + dy * dy);\n"
    "        }\n"
    "        m--;\n"
    "        current = next;\n"
    "        barrier(CLK_GLOBAL_MEM_FENCE | CLK_LOCAL_MEM_FENCE);\n"
    "    }\n"
    "    if (lid == 0) { lengths[t] = length; }\n"
    "}\n";

#define MN_GPU_KERNELS 17
static const char* kernel_names[MN_GPU_KERNELS] = { "closing", "mask_deepest", "settle", "backward_sources", "window_columns", "window_rows", "backward_verdict", "tours",
    "reach_values", "reach_round", "mark_above", "distance_columns", "distance_rows_within", "slope_sources", "separation_update", "model_region", "deepest_heights" };
enum { K_CLOSING, K_MASK_DEEPEST, K_SETTLE, K_SOURCES, K_COLUMNS, K_ROWS, K_VERDICT, K_TOURS, K_REACH_VALUES, K_REACH_ROUND, K_MARK_ABOVE, K_DISTANCE_COLUMNS, K_DISTANCE_ROWS,
    K_SLOPE_SOURCES, K_SEPARATION_UPDATE, K_MODEL_REGION, K_DEEPEST_HEIGHTS };

/* The tour work-group: a power of two for the reduction. */
#define MN_GPU_TOUR_GROUP 256
/* The device memory one sweep's chunk of levels may take (the fields and the envelope scratch). */
#define MN_GPU_CHUNK_BUDGET ((size_t)1 << 30)

typedef struct mn_gpu {
    int tried;
    int status;
    char error[512];
    char name[256];
    cl_ulong memory;
    cl_ulong max_alloc;
    void* library;
    cl_device_id device;
    cl_context context;
    cl_command_queue queue;
    cl_program program;
    cl_kernel kernels[MN_GPU_KERNELS];
    pfn_get_platforms get_platforms;
    pfn_get_devices get_devices;
    pfn_device_info device_info;
    pfn_create_context create_context;
    pfn_create_queue create_queue;
    pfn_create_program create_program;
    pfn_build_program build_program;
    pfn_build_info build_info;
    pfn_create_kernel create_kernel;
    pfn_create_buffer create_buffer;
    pfn_set_arg set_arg;
    pfn_enqueue_kernel enqueue_kernel;
    pfn_enqueue_write enqueue_write;
    pfn_enqueue_read enqueue_read;
    pfn_enqueue_fill enqueue_fill;
    pfn_enqueue_copy enqueue_copy;
    pfn_finish finish;
    pfn_flush flush;
    pfn_release_mem release_mem;
    pfn_wait_events wait_events;
    pfn_release_event release_event;
} mn_gpu;

static mn_gpu gpu;

static void* library_open(void)
{
#if defined(_WIN32)
    return (void*)LoadLibraryA("OpenCL.dll");
#else
    void* handle = dlopen("libOpenCL.so.1", RTLD_NOW | RTLD_LOCAL);
    return handle != NULL ? handle : dlopen("libOpenCL.so", RTLD_NOW | RTLD_LOCAL);
#endif
}

static void* library_symbol(void* library, const char* name)
{
#if defined(_WIN32)
    return (void*)GetProcAddress((HMODULE)library, name);
#else
    return dlsym(library, name);
#endif
}

static int gpu_fail(const char* what, cl_int code)
{
    snprintf(gpu.error, sizeof(gpu.error), "OpenCL %s failed with error %d.", what, (int)code);
    return MN_ERR_STATE;
}

#define MN_GPU_SYMBOL(field, type, name)                                                           \
    do {                                                                                           \
        gpu.field = (type)library_symbol(gpu.library, name);                                       \
        if (gpu.field == NULL) {                                                                   \
            snprintf(gpu.error, sizeof(gpu.error), "The OpenCL runtime has no %s.", name);         \
            return MN_ERR_STATE;                                                                   \
        }                                                                                          \
    } while (0)

/* The device: a GPU without memory shared with the host first, then the most compute units times clock;
 * it must divide and take square roots correctly rounded. */
static int choose_device(void)
{
    cl_platform_id platforms[16];
    cl_uint platform_count = 0;
    cl_int code = gpu.get_platforms(16, platforms, &platform_count);
    if (code != CL_SUCCESS || platform_count == 0) {
        snprintf(gpu.error, sizeof(gpu.error), "No OpenCL platform (error %d).", (int)code);
        return MN_ERR_STATE;
    }
    int found = 0;
    int best_discrete = 0;
    cl_ulong best_power = 0;
    for (cl_uint p = 0; p < platform_count && p < 16; p++) {
        cl_device_id devices[16];
        cl_uint device_count = 0;
        if (gpu.get_devices(platforms[p], CL_DEVICE_TYPE_GPU, 16, devices, &device_count) != CL_SUCCESS) {
            continue;
        }
        for (cl_uint d = 0; d < device_count && d < 16; d++) {
            cl_bitfield fp = 0;
            cl_bool unified = 1;
            cl_uint units = 0;
            cl_uint clock = 0;
            if (gpu.device_info(devices[d], CL_DEVICE_SINGLE_FP_CONFIG, sizeof(fp), &fp, NULL) != CL_SUCCESS || (fp & CL_FP_CORRECTLY_ROUNDED_DIVIDE_SQRT) == 0) {
                continue;
            }
            gpu.device_info(devices[d], CL_DEVICE_HOST_UNIFIED_MEMORY, sizeof(unified), &unified, NULL);
            gpu.device_info(devices[d], CL_DEVICE_MAX_COMPUTE_UNITS, sizeof(units), &units, NULL);
            gpu.device_info(devices[d], CL_DEVICE_MAX_CLOCK_FREQUENCY, sizeof(clock), &clock, NULL);
            int discrete = unified == 0;
            cl_ulong power = (cl_ulong)units * (cl_ulong)clock;
            if (!found || discrete > best_discrete || (discrete == best_discrete && power > best_power)) {
                found = 1;
                best_discrete = discrete;
                best_power = power;
                gpu.device = devices[d];
            }
        }
    }
    if (!found) {
        snprintf(gpu.error, sizeof(gpu.error), "No OpenCL GPU with correctly rounded division and square root.");
        return MN_ERR_STATE;
    }
    gpu.device_info(gpu.device, CL_DEVICE_NAME, sizeof(gpu.name) - 1, gpu.name, NULL);
    gpu.device_info(gpu.device, CL_DEVICE_GLOBAL_MEM_SIZE, sizeof(gpu.memory), &gpu.memory, NULL);
    gpu.device_info(gpu.device, CL_DEVICE_MAX_MEM_ALLOC_SIZE, sizeof(gpu.max_alloc), &gpu.max_alloc, NULL);
    return MN_OK;
}

static int open_gpu(void)
{
    gpu.library = library_open();
    if (gpu.library == NULL) {
        snprintf(gpu.error, sizeof(gpu.error), "No OpenCL runtime: the graphics driver installs OpenCL.dll or libOpenCL.so.1.");
        return MN_ERR_STATE;
    }
    MN_GPU_SYMBOL(get_platforms, pfn_get_platforms, "clGetPlatformIDs");
    MN_GPU_SYMBOL(get_devices, pfn_get_devices, "clGetDeviceIDs");
    MN_GPU_SYMBOL(device_info, pfn_device_info, "clGetDeviceInfo");
    MN_GPU_SYMBOL(create_context, pfn_create_context, "clCreateContext");
    MN_GPU_SYMBOL(create_queue, pfn_create_queue, "clCreateCommandQueue");
    MN_GPU_SYMBOL(create_program, pfn_create_program, "clCreateProgramWithSource");
    MN_GPU_SYMBOL(build_program, pfn_build_program, "clBuildProgram");
    MN_GPU_SYMBOL(build_info, pfn_build_info, "clGetProgramBuildInfo");
    MN_GPU_SYMBOL(create_kernel, pfn_create_kernel, "clCreateKernel");
    MN_GPU_SYMBOL(create_buffer, pfn_create_buffer, "clCreateBuffer");
    MN_GPU_SYMBOL(set_arg, pfn_set_arg, "clSetKernelArg");
    MN_GPU_SYMBOL(enqueue_kernel, pfn_enqueue_kernel, "clEnqueueNDRangeKernel");
    MN_GPU_SYMBOL(enqueue_write, pfn_enqueue_write, "clEnqueueWriteBuffer");
    MN_GPU_SYMBOL(enqueue_read, pfn_enqueue_read, "clEnqueueReadBuffer");
    MN_GPU_SYMBOL(enqueue_fill, pfn_enqueue_fill, "clEnqueueFillBuffer");
    MN_GPU_SYMBOL(enqueue_copy, pfn_enqueue_copy, "clEnqueueCopyBuffer");
    MN_GPU_SYMBOL(finish, pfn_finish, "clFinish");
    MN_GPU_SYMBOL(flush, pfn_flush, "clFlush");
    MN_GPU_SYMBOL(release_mem, pfn_release_mem, "clReleaseMemObject");
    MN_GPU_SYMBOL(wait_events, pfn_wait_events, "clWaitForEvents");
    MN_GPU_SYMBOL(release_event, pfn_release_event, "clReleaseEvent");
    MN_CHECK(choose_device());
    cl_int code = CL_SUCCESS;
    gpu.context = gpu.create_context(NULL, 1, &gpu.device, NULL, NULL, &code);
    if (gpu.context == NULL || code != CL_SUCCESS) {
        return gpu_fail("clCreateContext", code);
    }
    gpu.queue = gpu.create_queue(gpu.context, gpu.device, 0, &code);
    if (gpu.queue == NULL || code != CL_SUCCESS) {
        return gpu_fail("clCreateCommandQueue", code);
    }
    gpu.program = gpu.create_program(gpu.context, 1, &kernel_source, NULL, &code);
    if (gpu.program == NULL || code != CL_SUCCESS) {
        return gpu_fail("clCreateProgramWithSource", code);
    }
    code = gpu.build_program(gpu.program, 1, &gpu.device, "-cl-fp32-correctly-rounded-divide-sqrt", NULL, NULL);
    if (code != CL_SUCCESS) {
        size_t size = 0;
        gpu.build_info(gpu.program, gpu.device, CL_PROGRAM_BUILD_LOG, 0, NULL, &size);
        char* log = (char*)mn_alloc(size + 1, 1);
        if (log != NULL) {
            gpu.build_info(gpu.program, gpu.device, CL_PROGRAM_BUILD_LOG, size, log, NULL);
        }
        snprintf(gpu.error, sizeof(gpu.error), "OpenCL kernels did not build (error %d): %s", (int)code, log != NULL ? log : "");
        free(log);
        return MN_ERR_STATE;
    }
    for (int k = 0; k < MN_GPU_KERNELS; k++) {
        gpu.kernels[k] = gpu.create_kernel(gpu.program, kernel_names[k], &code);
        if (gpu.kernels[k] == NULL || code != CL_SUCCESS) {
            return gpu_fail(kernel_names[k], code);
        }
    }
    return MN_OK;
}

/* Opens the GPU once per process under the shared lock, which the caller then holds; a failure stays. */
static int gpu_enter(void)
{
    mn_shared_lock();
    if (!gpu.tried) {
        double started = mn_seconds();
        gpu.tried = 1;
        gpu.status = open_gpu();
        mn_time_add(MN_TIME_GPU_SETUP, mn_seconds() - started);
    }
    if (gpu.status != MN_OK) {
        int status = gpu.status;
        char error[512];
        memcpy(error, gpu.error, sizeof(error));
        mn_shared_unlock();
        return mn_fail(status, "%s", error);
    }
    return MN_OK;
}

static void gpu_leave(void) { mn_shared_unlock(); }

int mn_gpu_ready(char* name, int capacity)
{
    MN_CHECK(gpu_enter());
    if (name != NULL && capacity > 0) {
        snprintf(name, (size_t)capacity, "%s", gpu.name);
    }
    gpu_leave();
    return MN_OK;
}

MN_API int32_t mn_gpu_device(char* name, int32_t capacity) { return mn_gpu_ready(name, capacity); }

/* ---- buffers and launches (the shared lock held) ---- */

typedef struct gpu_buffers {
    cl_mem items[16];
    int count;
} gpu_buffers;

static void buffers_release(gpu_buffers* b)
{
    for (int k = 0; k < b->count; k++) {
        if (b->items[k] != NULL) {
            gpu.release_mem(b->items[k]);
        }
    }
    b->count = 0;
}

static int buffer(gpu_buffers* b, size_t bytes, const void* data, cl_mem* result)
{
    cl_int code = CL_SUCCESS;
    cl_mem mem = gpu.create_buffer(gpu.context, CL_MEM_READ_WRITE, bytes > 0 ? bytes : 4, NULL, &code);
    if (mem == NULL || code != CL_SUCCESS) {
        return gpu_fail("clCreateBuffer", code);
    }
    b->items[b->count++] = mem;
    if (data != NULL) {
        double started = mn_seconds();
        code = gpu.enqueue_write(gpu.queue, mem, CL_TRUE, 0, bytes, data, 0, NULL, NULL);
        mn_time_add(MN_TIME_GPU_TRANSFER, mn_seconds() - started);
        if (code != CL_SUCCESS) {
            return gpu_fail("clEnqueueWriteBuffer", code);
        }
    }
    *result = mem;
    return MN_OK;
}

static int read_back(cl_mem mem, size_t bytes, void* data)
{
    double started = mn_seconds();
    cl_int code = gpu.enqueue_read(gpu.queue, mem, CL_TRUE, 0, bytes, data, 0, NULL, NULL);
    mn_time_add(MN_TIME_GPU_TRANSFER, mn_seconds() - started);
    return code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueReadBuffer", code);
}

static int zero(cl_mem mem, size_t bytes)
{
    int value = 0;
    cl_int code = gpu.enqueue_fill(gpu.queue, mem, &value, sizeof(value), 0, bytes, 0, NULL, NULL);
    return code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueFillBuffer", code);
}

typedef struct kernel_arg {
    size_t size;
    const void* value;
} kernel_arg;

static int launch(int kernel, const kernel_arg* args, int count, size_t global, size_t local)
{
    cl_kernel k = gpu.kernels[kernel];
    for (int a = 0; a < count; a++) {
        cl_int code = gpu.set_arg(k, (cl_uint)a, args[a].size, args[a].value);
        if (code != CL_SUCCESS) {
            return gpu_fail(kernel_names[kernel], code);
        }
    }
    if (global == 0) {
        return MN_OK;
    }
    cl_int code = gpu.enqueue_kernel(gpu.queue, k, 1, NULL, &global, local > 0 ? &local : NULL, 0, NULL, NULL);
    return code == CL_SUCCESS ? MN_OK : gpu_fail(kernel_names[kernel], code);
}

static int done(void)
{
    cl_int code = gpu.finish(gpu.queue);
    return code == CL_SUCCESS ? MN_OK : gpu_fail("clFinish", code);
}

#define ARG(x) { sizeof(x), &(x) }

/* ---- closing ---- */

typedef struct gpu_profile {
    cl_mem dx;
    cl_mem dy;
    cl_mem dz;
    int count;
} gpu_profile;

static int upload_profile(gpu_buffers* b, const mn_profile* profile, gpu_profile* result)
{
    int count = profile->offset_count;
    int* dx = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    int* dy = (int*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(int));
    float* dz = (float*)mn_alloc((size_t)(count > 0 ? count : 1), sizeof(float));
    int status = dx == NULL || dy == NULL || dz == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the GPU profile.") : MN_OK;
    for (int o = 0; o < count && status == MN_OK; o++) {
        dx[o] = profile->offsets[o].dx;
        dy[o] = profile->offsets[o].dy;
        dz[o] = profile->offsets[o].dz;
    }
    if (status == MN_OK) {
        status = buffer(b, (size_t)count * sizeof(int), dx, &result->dx);
    }
    if (status == MN_OK) {
        status = buffer(b, (size_t)count * sizeof(int), dy, &result->dy);
    }
    if (status == MN_OK) {
        status = buffer(b, (size_t)count * sizeof(float), dz, &result->dz);
    }
    result->count = count;
    free(dx);
    free(dy);
    free(dz);
    return status;
}

static int closing_on(const mn_grid* g, cl_mem in, const gpu_profile* profile, cl_mem out)
{
    int width = g->width;
    int height = g->height;
    int count = profile->count;
    kernel_arg args[] = { ARG(in), ARG(width), ARG(height), ARG(profile->dx), ARG(profile->dy), ARG(profile->dz), ARG(count), ARG(out) };
    return launch(K_CLOSING, args, 8, (size_t)mn_cells(g), 0);
}

int mn_gpu_closing(const mn_grid* g, const float* in, const mn_profile* profile, float* out)
{
    MN_CHECK(gpu_enter());
    size_t bytes = (size_t)mn_cells(g) * sizeof(float);
    gpu_buffers b = { { 0 }, 0 };
    gpu_profile p;
    cl_mem source = NULL;
    cl_mem target = NULL;
    int status = upload_profile(&b, profile, &p);
    if (status == MN_OK) {
        status = buffer(&b, bytes, in, &source);
    }
    if (status == MN_OK) {
        status = buffer(&b, bytes, NULL, &target);
    }
    if (status == MN_OK) {
        status = closing_on(g, source, &p, target);
    }
    if (status == MN_OK) {
        status = read_back(target, bytes, out);
    }
    buffers_release(&b);
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

/* ---- the session of one generation (T-183) ---- */

/* What a Backwards_GPU generation keeps on the GPU from the stage that makes it to the stage that last
 * reads it: the tool profile (from the reach map on), the stock (uploaded once for the reach map), the
 * plan's masks and levels (made by the separation, uploaded again as the route stage reads them after
 * the islands and the bridges). Every other buffer lives for one call. */
struct mn_gpu_session {
    mn_grid grid;
    int cells;
    gpu_profile profile;
    cl_mem stock;
    cl_mem masks;
    size_t mask_bytes;
    cl_mem levels;
    int level_count;
};

static void release(cl_mem* mem)
{
    if (*mem != NULL) {
        gpu.release_mem(*mem);
        *mem = NULL;
    }
}

static int create_mem(size_t bytes, cl_mem* result)
{
    cl_int code = CL_SUCCESS;
    *result = gpu.create_buffer(gpu.context, CL_MEM_READ_WRITE, bytes > 0 ? bytes : 4, NULL, &code);
    return *result != NULL && code == CL_SUCCESS ? MN_OK : gpu_fail("clCreateBuffer", code);
}

static int write_mem(cl_mem mem, size_t bytes, const void* data)
{
    double started = mn_seconds();
    cl_int code = gpu.enqueue_write(gpu.queue, mem, CL_TRUE, 0, bytes, data, 0, NULL, NULL);
    mn_time_add(MN_TIME_GPU_TRANSFER, mn_seconds() - started);
    return code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueWriteBuffer", code);
}

static int fill_float(cl_mem mem, size_t count, float value)
{
    cl_int code = gpu.enqueue_fill(gpu.queue, mem, &value, sizeof(value), 0, count * sizeof(float), 0, NULL, NULL);
    return code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueFillBuffer", code);
}

int mn_gpu_session_open(const mn_grid* g, const mn_profile* profile, mn_gpu_session** result)
{
    *result = NULL;
    MN_CHECK(gpu_enter());
    mn_gpu_session* session = (mn_gpu_session*)mn_alloc(1, sizeof(mn_gpu_session));
    if (session == NULL) {
        gpu_leave();
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the GPU session.");
    }
    session->grid = *g;
    session->cells = mn_cells(g);
    gpu_buffers b = { { 0 }, 0 };
    int status = upload_profile(&b, profile, &session->profile);
    if (status != MN_OK) {
        buffers_release(&b);
        free(session);
        gpu_leave();
        return mn_fail(status, "%s", gpu.error);
    }
    gpu_leave();
    *result = session;
    return MN_OK;
}

void mn_gpu_session_close(mn_gpu_session* session)
{
    if (session == NULL) {
        return;
    }
    mn_shared_lock();
    release(&session->profile.dx);
    release(&session->profile.dy);
    release(&session->profile.dz);
    release(&session->stock);
    release(&session->masks);
    release(&session->levels);
    mn_shared_unlock();
    free(session);
}

int mn_gpu_reach(mn_gpu_session* session, const float* model, const float* stock, float floor, float percent, float tolerance, const mn_monitor* monitor, float* reach)
{
    MN_CHECK(mn_reach_check(percent, tolerance));
    MN_CHECK(gpu_enter());
    const mn_grid* g = &session->grid;
    int cells = session->cells;
    int width = g->width;
    int height = g->height;
    int count = session->profile.count;
    size_t floats = (size_t)cells * sizeof(float);
    int* needed = (int*)mn_alloc((size_t)count + 1, sizeof(int));
    gpu_buffers b = { { 0 }, 0 };
    cl_mem model_mem = NULL, value = NULL, reached = NULL, next = NULL, out = NULL, needed_mem = NULL;
    int status = needed == NULL ? mn_fail(MN_ERR_MEMORY, "Out of memory for the reach map.") : MN_OK;
    if (status == MN_OK && session->stock == NULL) {
        status = create_mem(floats, &session->stock);
        if (status == MN_OK) {
            status = write_mem(session->stock, floats, stock);
        }
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, model, &model_mem);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &value);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &reached);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &next);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &out);
    }
    if (status == MN_OK) {
        status = buffer(&b, ((size_t)count + 1) * sizeof(int), NULL, &needed_mem);
    }
    if (status == MN_OK) {
        kernel_arg args[] = { ARG(model_mem), ARG(session->stock), ARG(cells), ARG(floor), ARG(value) };
        status = launch(K_REACH_VALUES, args, 5, (size_t)cells, 0);
    }
    if (status == MN_OK) {
        status = zero(reached, (size_t)cells);
    }
    if (status == MN_OK) {
        status = zero(next, (size_t)cells);
    }
    if (status == MN_OK) {
        status = fill_float(out, (size_t)cells, NAN);
    }
    for (int round = 0; round < MN_REACH_ROUNDS && status == MN_OK; round++) {
        double round_percent = mn_reach_round_percent(percent, round);
        int last = round == MN_REACH_ROUNDS - 1;
        if (round > 0 && round_percent == mn_reach_round_percent(percent, round - 1)) {
            continue;
        }
        if (mn_cancelled(monitor)) {
            status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
            break;
        }
        int full = round_percent >= (double)MN_REACH_MAX_PERCENT;
        for (int m = 0; m <= count; m++) {
            needed[m] = mn_reach_needed(m, round_percent);
        }
        status = write_mem(needed_mem, ((size_t)count + 1) * sizeof(int), needed);
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(value), ARG(reached), ARG(next), ARG(out), ARG(width), ARG(height), ARG(session->profile.dx), ARG(session->profile.dy), ARG(session->profile.dz),
                ARG(count), ARG(needed_mem), ARG(full), ARG(last), ARG(floor), ARG(tolerance) };
            status = launch(K_REACH_ROUND, args, 15, (size_t)cells, 0);
        }
        if (status == MN_OK && !last) {
            cl_int code = gpu.enqueue_copy(gpu.queue, next, reached, 0, 0, (size_t)cells, 0, NULL, NULL);
            status = code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueCopyBuffer", code);
        }
    }
    if (status == MN_OK) {
        status = read_back(out, floats, reach);
    }
    buffers_release(&b);
    gpu_leave();
    free(needed);
    return status == MN_OK || status == MN_ERR_CANCELLED ? status : mn_fail(status, "%s", gpu.error);
}

int mn_gpu_separation_levels(mn_gpu_session* session, const mn_plan* plan, const float* effective_tip, const mn_tool* tool, float tolerance, float stock_top, float floor, uint8_t* masks,
    uint8_t* model_region, float* deepest)
{
    MN_CHECK(gpu_enter());
    const mn_grid* g = &session->grid;
    int cells = session->cells;
    int width = g->width;
    int height = g->height;
    int count = plan->count;
    float cell_size = g->cell_size;
    size_t floats = (size_t)cells * sizeof(float);
    size_t all = (size_t)count * (size_t)cells;
    gpu_buffers b = { { 0 }, 0 };
    cl_mem tip = NULL, allowed = NULL, obstacles = NULL, hug = NULL, cleared = NULL, squares = NULL, field = NULL, scratch = NULL, deep = NULL, position = NULL, region = NULL,
           sources = NULL, widest = NULL;
    release(&session->masks);
    session->mask_bytes = all > 0 ? all : 1;
    int status = create_mem(session->mask_bytes, &session->masks);
    session->level_count = 0;
    if (status == MN_OK) {
        status = buffer(&b, floats, effective_tip, &tip);
    }
    if (status == MN_OK) {
        status = buffer(&b, all, plan->masks, &allowed);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &obstacles);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &hug);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &cleared);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &squares);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &field);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &scratch);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &deep);
    }
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &position);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells, NULL, &region);
    }
    if (status == MN_OK) {
        status = buffer(&b, sizeof(int), NULL, &sources);
    }
    if (status == MN_OK) {
        status = buffer(&b, sizeof(int), NULL, &widest);
    }
    if (status == MN_OK) {
        status = fill_float(deep, (size_t)cells, NAN);
    }
    if (status == MN_OK) {
        status = fill_float(position, (size_t)cells, NAN);
    }
    if (status == MN_OK) {
        kernel_arg args[] = { ARG(tip), ARG(cells), ARG(floor), ARG(region) };
        status = launch(K_MODEL_REGION, args, 4, (size_t)cells, 0);
    }
    float hug_radius = mn_adjacency_margin(cell_size) + MN_RADIUS_TOLERANCE;
    float margin = mn_head_margin(cell_size, tolerance);
    float overhang = mn_head_overhang(tool) + margin;
    float cutter_radius = tool->cutter_diameter / 2.0f;
    float cutter_length = tool->cutter_length;
    float widening = mn_head_widening(tool);
    float bottom = tool->head_diameter / 2.0f;
    float top = tool->head_top_diameter / 2.0f;
    float head_radius = mn_head_radius(tool);
    /* Every cell within a radius r of a marked cell lies at most r / cell_size cells away on each axis. */
    int hug_window = mn_f2i(ceilf(hug_radius / cell_size)) + 1;
    int overhang_window = mn_f2i(ceilf(overhang / cell_size)) + 1;
    int none = 0;
    for (int k = count - 1; k >= 0 && status == MN_OK; k--) {
        float level = plan->levels[k];
        {
            kernel_arg args[] = { ARG(tip), ARG(level), ARG(cells), ARG(obstacles) };
            status = launch(K_MARK_ABOVE, args, 4, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(obstacles), ARG(none), ARG(width), ARG(height), ARG(hug_window), ARG(squares) };
            status = launch(K_DISTANCE_COLUMNS, args, 6, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(squares), ARG(width), ARG(height), ARG(hug_window), ARG(cell_size), ARG(hug_radius), ARG(hug) };
            status = launch(K_DISTANCE_ROWS, args, 7, (size_t)cells, 0);
        }
        int reach = mn_head_reach(plan->levels, count, k, stock_top, cutter_length + widening - tolerance);
        if (status == MN_OK && reach >= 0) {
            kernel_arg args[] = { ARG(session->masks), ARG(reach), ARG(width), ARG(height), ARG(overhang_window), ARG(squares) };
            status = launch(K_DISTANCE_COLUMNS, args, 6, (size_t)cells, 0);
        }
        if (status == MN_OK && reach >= 0) {
            kernel_arg args[] = { ARG(squares), ARG(width), ARG(height), ARG(overhang_window), ARG(cell_size), ARG(overhang), ARG(cleared) };
            status = launch(K_DISTANCE_ROWS, args, 7, (size_t)cells, 0);
        }
        float slab_top = k == 0 ? stock_top : plan->levels[k - 1];
        if (status == MN_OK) {
            status = zero(sources, sizeof(int));
        }
        if (status == MN_OK) {
            status = zero(widest, sizeof(int));
        }
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(position), ARG(cells), ARG(slab_top), ARG(tolerance), ARG(cutter_length), ARG(bottom), ARG(top), ARG(widening), ARG(head_radius), ARG(cutter_radius),
                ARG(margin), ARG(cell_size), ARG(field), ARG(sources), ARG(widest) };
            status = launch(K_SLOPE_SOURCES, args, 15, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(field), ARG(scratch), ARG(width), ARG(height), ARG(widest) };
            status = launch(K_COLUMNS, args, 5, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(scratch), ARG(field), ARG(width), ARG(height), ARG(widest) };
            status = launch(K_ROWS, args, 5, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            int has_below = k + 1 < count;
            int any_cleared = reach >= 0;
            kernel_arg args[] = { ARG(allowed), ARG(hug), ARG(session->masks), ARG(k), ARG(has_below), ARG(cleared), ARG(any_cleared), ARG(field), ARG(sources), ARG(region),
                ARG(level), ARG(cells), ARG(deep), ARG(position) };
            status = launch(K_SEPARATION_UPDATE, args, 14, (size_t)cells, 0);
        }
    }
    if (status == MN_OK && all > 0) {
        status = read_back(session->masks, all, masks);
    }
    if (status == MN_OK) {
        status = read_back(region, (size_t)cells, model_region);
    }
    if (status == MN_OK) {
        status = read_back(deep, floats, deepest);
    }
    buffers_release(&b);
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

int mn_gpu_masks(mn_gpu_session* session, const mn_plan* plan)
{
    MN_CHECK(gpu_enter());
    size_t all = (size_t)plan->count * (size_t)session->cells;
    int status = MN_OK;
    if (session->masks == NULL || session->mask_bytes != (all > 0 ? all : 1)) {
        release(&session->masks);
        session->mask_bytes = all > 0 ? all : 1;
        status = create_mem(session->mask_bytes, &session->masks);
    }
    if (status == MN_OK && all > 0) {
        status = write_mem(session->masks, all, plan->masks);
    }
    release(&session->levels);
    if (status == MN_OK) {
        status = create_mem((size_t)(plan->count > 0 ? plan->count : 1) * sizeof(float), &session->levels);
    }
    if (status == MN_OK && plan->count > 0) {
        status = write_mem(session->levels, (size_t)plan->count * sizeof(float), plan->levels);
    }
    session->level_count = status == MN_OK ? plan->count : 0;
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

int mn_gpu_intended(mn_gpu_session* session, const mn_plan* plan, float* intended)
{
    if (session->level_count != plan->count) {
        return mn_fail(MN_ERR_STATE, "The GPU holds %d levels, the plan %d.", session->level_count, plan->count);
    }
    MN_CHECK(gpu_enter());
    int cells = session->cells;
    int count = plan->count;
    size_t floats = (size_t)cells * sizeof(float);
    gpu_buffers b = { { 0 }, 0 };
    cl_mem deep = NULL, out = NULL;
    int status = buffer(&b, floats, NULL, &deep);
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &out);
    }
    if (status == MN_OK) {
        kernel_arg args[] = { ARG(session->masks), ARG(session->levels), ARG(count), ARG(cells), ARG(deep) };
        status = launch(K_DEEPEST_HEIGHTS, args, 5, (size_t)cells, 0);
    }
    if (status == MN_OK) {
        status = closing_on(&session->grid, deep, &session->profile, out);
    }
    if (status == MN_OK) {
        status = read_back(out, floats, intended);
    }
    buffers_release(&b);
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

int mn_gpu_closing_of(mn_gpu_session* session, const float* map, float* closing)
{
    MN_CHECK(gpu_enter());
    size_t floats = (size_t)session->cells * sizeof(float);
    gpu_buffers b = { { 0 }, 0 };
    cl_mem in = NULL, out = NULL;
    int status = buffer(&b, floats, map, &in);
    if (status == MN_OK) {
        status = buffer(&b, floats, NULL, &out);
    }
    if (status == MN_OK) {
        status = closing_on(&session->grid, in, &session->profile, out);
    }
    if (status == MN_OK) {
        status = read_back(out, floats, closing);
    }
    buffers_release(&b);
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

/* ---- the backward check's sweeps ---- */

int mn_gpu_backward_settle(mn_gpu_session* session, const mn_context* context, int* deepest, int* sweeps)
{
    const mn_grid* g = &context->grid;
    const mn_plan* plan = context->plan;
    const mn_tool* tool = &context->tool;
    int cells = mn_cells(g);
    int width = g->width;
    int height = g->height;
    int levels = plan->count;
    *sweeps = 0;
    if (levels == 0) {
        for (int c = 0; c < cells; c++) {
            deepest[c] = -1;
        }
        return MN_OK;
    }
    if (session->level_count != levels || session->stock == NULL) {
        return mn_fail(MN_ERR_STATE, "The GPU holds %d levels, the plan %d.", session->level_count, levels);
    }
    MN_CHECK(gpu_enter());
    /* Per level of a chunk: two fields of floats. */
    size_t per_level = (size_t)cells * 2 * sizeof(float);
    size_t budget = MN_GPU_CHUNK_BUDGET < (size_t)gpu.max_alloc ? MN_GPU_CHUNK_BUDGET : (size_t)gpu.max_alloc;
    int chunk = mn_clampi((int)(budget / per_level), 1, mn_maxi(1, levels));
    gpu_buffers b = { { 0 }, 0 };
    cl_mem stock = session->stock, masks = session->masks, level_buffer = session->levels;
    cl_mem deep = NULL, next = NULL, heights = NULL, closing = NULL, field = NULL, scratch = NULL, sources = NULL, widest = NULL, changed = NULL;
    int status = buffer(&b, (size_t)cells * sizeof(int), NULL, &deep);
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells * sizeof(int), NULL, &next);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells * sizeof(float), NULL, &heights);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)cells * sizeof(float), NULL, &closing);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)chunk * (size_t)cells * sizeof(float), NULL, &field);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)chunk * (size_t)cells * sizeof(float), NULL, &scratch);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)chunk * sizeof(int), NULL, &sources);
    }
    if (status == MN_OK) {
        status = buffer(&b, (size_t)chunk * sizeof(int), NULL, &widest);
    }
    if (status == MN_OK) {
        status = buffer(&b, sizeof(int), NULL, &changed);
    }
    if (status == MN_OK) {
        kernel_arg args[] = { ARG(masks), ARG(levels), ARG(cells), ARG(deep), ARG(next) };
        status = launch(K_MASK_DEEPEST, args, 5, (size_t)cells, 0);
    }

    float stock_top = context->stock_top;
    float cutter_length = tool->cutter_length;
    float margin = mn_head_margin(g->cell_size, context->parameters.tolerance);
    float cell_size = g->cell_size;
    float bottom = tool->head_diameter / 2.0f;
    float top = tool->head_top_diameter / 2.0f;
    float widening = mn_head_widening(tool);
    float head_radius = mn_head_radius(tool);
    float cutter_radius = tool->cutter_diameter / 2.0f;
    for (int round = 0; round <= levels && status == MN_OK; round++) {
        double started = mn_seconds();
        int moved = 0;
        status = zero(changed, sizeof(int));
        if (status == MN_OK) {
            kernel_arg args[] = { ARG(deep), ARG(next), ARG(level_buffer), ARG(cells), ARG(heights), ARG(changed) };
            status = launch(K_SETTLE, args, 6, (size_t)cells, 0);
        }
        if (status == MN_OK) {
            status = closing_on(g, heights, &session->profile, closing);
        }
        if (status == MN_OK) {
            status = read_back(changed, sizeof(int), &moved);
        }
        mn_time_add(MN_TIME_BACK_SETTLE, mn_seconds() - started);
        if (status != MN_OK || (round > 0 && moved == 0)) {
            break;
        }
        started = mn_seconds();
        for (int first = 0; first < levels && status == MN_OK; first += chunk) {
            int n = mn_mini(chunk, levels - first);
            size_t items = (size_t)n * (size_t)cells;
            status = zero(sources, (size_t)chunk * sizeof(int));
            if (status == MN_OK) {
                status = zero(widest, (size_t)chunk * sizeof(int));
            }
            if (status == MN_OK) {
                kernel_arg args[] = { ARG(stock), ARG(closing), ARG(level_buffer), ARG(first), ARG(cells), ARG(stock_top), ARG(cutter_length), ARG(margin), ARG(cell_size), ARG(bottom),
                    ARG(top), ARG(widening), ARG(head_radius), ARG(cutter_radius), ARG(field), ARG(sources), ARG(widest) };
                status = launch(K_SOURCES, args, 17, items, 0);
            }
            if (status == MN_OK) {
                kernel_arg args[] = { ARG(field), ARG(scratch), ARG(width), ARG(height), ARG(widest) };
                status = launch(K_COLUMNS, args, 5, items, 0);
            }
            if (status == MN_OK) {
                kernel_arg args[] = { ARG(scratch), ARG(field), ARG(width), ARG(height), ARG(widest) };
                status = launch(K_ROWS, args, 5, items, 0);
            }
            if (status == MN_OK) {
                kernel_arg args[] = { ARG(field), ARG(masks), ARG(sources), ARG(first), ARG(cells), ARG(next) };
                status = launch(K_VERDICT, args, 6, items, 0);
            }
            if (status == MN_OK) {
                status = done();
            }
        }
        mn_time_add(MN_TIME_BACK_LEVELS, mn_seconds() - started);
        (*sweeps)++;
    }
    if (status == MN_OK) {
        status = read_back(deep, (size_t)cells * sizeof(int), deepest);
    }
    buffers_release(&b);
    gpu_leave();
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

MN_API int32_t mn_gpu_remaining(const mn_grid* grid, const float* tip, const mn_offset* offsets, int32_t count, float* remaining)
{
    mn_profile profile;
    memset(&profile, 0, sizeof(profile));
    profile.offsets = (mn_offset*)offsets;
    profile.offset_count = count;
    return mn_gpu_closing(grid, tip, &profile, remaining);
}

MN_API int32_t mn_gpu_start_tours(const float* x, const float* y, int32_t count, int32_t start, int32_t end, int32_t tours, int32_t keep, int32_t* orders, int32_t* kept)
{
    if (count < 1 || start < 0 || start >= count || end < -1 || end >= count || (end == start && count > 1)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Start and end must index the %d nodes, the end not the start.", count);
    }
    return mn_gpu_tours(x, y, count, start, end, tours, keep, orders, kept);
}

/* ---- start tours ---- */

/* Tours of the route solver (T-177, T-186): one work-group per tour; every step the work items scan the
 * nodes not yet visited (kept packed: the visited one's place takes the last) and reduce to the nearest,
 * ties to the lower index. A job is launched at once and collected after the solver's own start walks,
 * so the GPU builds its tours while the processor builds its walks; the shared lock is held for the
 * launch and the reads, never while waiting. A tour per work item over a bucket grid (T-186, tried
 * first) gave the same tours and took 2,557 s of waiting on the heart: one GPU thread walking a tour of
 * thousands of steps is slower than the processor by far. */
struct mn_gpu_tours_job {
    cl_mem items[5];
    int count;
    int n;
    int tours;
    float* lengths;
    cl_event done;
};

enum { TOUR_ORDERS = 3 };

static void tours_release(mn_gpu_tours_job* job)
{
    for (int k = 0; k < job->count; k++) {
        if (job->items[k] != NULL) {
            gpu.release_mem(job->items[k]);
        }
    }
    free(job->lengths);
    free(job);
}

static int tour_buffer(mn_gpu_tours_job* job, size_t bytes, const void* data, cl_mem* result)
{
    cl_int code = CL_SUCCESS;
    cl_mem mem = gpu.create_buffer(gpu.context, data != NULL ? CL_MEM_READ_ONLY | CL_MEM_COPY_HOST_PTR : CL_MEM_READ_WRITE, bytes > 0 ? bytes : 4, (void*)data, &code);
    if (mem == NULL || code != CL_SUCCESS) {
        return gpu_fail("clCreateBuffer", code);
    }
    job->items[job->count++] = mem;
    *result = mem;
    return MN_OK;
}

int mn_gpu_tours_begin(const float* x, const float* y, int n, int start, int end, int tours, mn_gpu_tours_job** result)
{
    *result = NULL;
    if (n < 2 || tours < 1) {
        return MN_OK;
    }
    double started = mn_seconds();
    mn_gpu_tours_job* job = (mn_gpu_tours_job*)mn_alloc(1, sizeof(mn_gpu_tours_job));
    float* lengths = (float*)mn_alloc((size_t)tours, sizeof(float));
    if (job == NULL || lengths == NULL) {
        free(job);
        free(lengths);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the GPU tours.");
    }
    job->n = n;
    job->tours = tours;
    job->lengths = lengths;
    int status = gpu_enter();
    if (status != MN_OK) {
        free(job->lengths);
        free(job);
        return status;
    }
    cl_mem xs = NULL, ys = NULL, left = NULL, orders = NULL, out = NULL;
    status = tour_buffer(job, (size_t)n * sizeof(float), x, &xs);
    if (status == MN_OK) {
        status = tour_buffer(job, (size_t)n * sizeof(float), y, &ys);
    }
    if (status == MN_OK) {
        status = tour_buffer(job, (size_t)tours * (size_t)n * sizeof(int), NULL, &left);
    }
    if (status == MN_OK) {
        status = tour_buffer(job, (size_t)tours * (size_t)n * sizeof(int), NULL, &orders);
    }
    if (status == MN_OK) {
        status = tour_buffer(job, (size_t)tours * sizeof(float), NULL, &out);
    }
    if (status == MN_OK) {
        kernel_arg args[] = { ARG(xs), ARG(ys), ARG(n), ARG(start), ARG(end), ARG(left), ARG(orders), ARG(out), { MN_GPU_TOUR_GROUP * sizeof(float), NULL },
            { MN_GPU_TOUR_GROUP * sizeof(int), NULL }, { MN_GPU_TOUR_GROUP * sizeof(int), NULL } };
        status = launch(K_TOURS, args, 11, (size_t)tours * MN_GPU_TOUR_GROUP, MN_GPU_TOUR_GROUP);
    }
    if (status == MN_OK) {
        cl_int code = gpu.enqueue_read(gpu.queue, out, CL_FALSE, 0, (size_t)tours * sizeof(float), job->lengths, 0, NULL, &job->done);
        status = code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueReadBuffer", code);
    }
    if (status == MN_OK) {
        gpu.flush(gpu.queue);
    } else {
        tours_release(job);
        job = NULL;
        status = mn_fail(status, "%s", gpu.error);
    }
    gpu_leave();
    mn_time_add(MN_TIME_GPU_TOURS, mn_seconds() - started);
    *result = job;
    return status;
}

int mn_gpu_tours_end(mn_gpu_tours_job* job, int keep, int* orders, int* kept)
{
    *kept = 0;
    if (job == NULL) {
        return MN_OK;
    }
    double started = mn_seconds();
    cl_int code = gpu.wait_events(1, &job->done);
    gpu.release_event(job->done);
    int status = code == CL_SUCCESS ? MN_OK : gpu_fail("clWaitForEvents", code);
    int n = job->n;
    /* The `keep` shortest, ties to the lower tour. */
    int picks[MN_GPU_MAX_KEPT];
    int count = 0;
    for (int k = 0; k < keep && k < MN_GPU_MAX_KEPT && status == MN_OK; k++) {
        int best = -1;
        for (int t = 0; t < job->tours; t++) {
            if (job->lengths[t] == job->lengths[t] && (best < 0 || job->lengths[t] < job->lengths[best])) {
                best = t;
            }
        }
        if (best < 0) {
            break;
        }
        picks[count++] = best;
        job->lengths[best] = NAN;
    }
    cl_event read = NULL;
    mn_shared_lock();
    for (int k = 0; k < count && status == MN_OK; k++) {
        code = gpu.enqueue_read(gpu.queue, job->items[TOUR_ORDERS], CL_FALSE, (size_t)picks[k] * (size_t)n * sizeof(int), (size_t)n * sizeof(int), orders + (size_t)k * (size_t)n, 0,
            NULL, k == count - 1 ? &read : NULL);
        status = code == CL_SUCCESS ? MN_OK : gpu_fail("clEnqueueReadBuffer", code);
    }
    if (read != NULL) {
        gpu.flush(gpu.queue);
    }
    mn_shared_unlock();
    if (read != NULL) {
        code = gpu.wait_events(1, &read);
        gpu.release_event(read);
        if (status == MN_OK && code != CL_SUCCESS) {
            status = gpu_fail("clWaitForEvents", code);
        }
    }
    *kept = status == MN_OK ? count : 0;
    mn_shared_lock();
    tours_release(job);
    mn_shared_unlock();
    mn_time_add(MN_TIME_GPU_TOURS, mn_seconds() - started);
    return status == MN_OK ? MN_OK : mn_fail(status, "%s", gpu.error);
}

void mn_gpu_tours_cancel(mn_gpu_tours_job* job)
{
    if (job == NULL) {
        return;
    }
    gpu.wait_events(1, &job->done);
    gpu.release_event(job->done);
    mn_shared_lock();
    tours_release(job);
    mn_shared_unlock();
}

int mn_gpu_tours(const float* x, const float* y, int n, int start, int end, int tours, int keep, int* orders, int* kept)
{
    *kept = 0;
    mn_gpu_tours_job* job = NULL;
    MN_CHECK(mn_gpu_tours_begin(x, y, n, start, end, tours, &job));
    return mn_gpu_tours_end(job, keep, orders, kept);
}
