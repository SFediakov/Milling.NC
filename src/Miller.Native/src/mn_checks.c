#include "mn_internal.h"

/* Gouge check on the plateau model (GougeChecker), vector output (ToolpathSimplifier) and the
 * toolpath statistics (ToolpathStatistics). */

static mn_v3 seg_start(const mn_segment* s) { return mn_v3_make(s->start_x, s->start_y, s->start_z); }

static mn_v3 seg_end(const mn_segment* s) { return mn_v3_make(s->end_x, s->end_y, s->end_z); }

/* How far a sample sits below the tip map; negative infinity where nothing constrains it. */
static float depth(mn_v3 p, const mn_grid* g, const float* tip)
{
    int i = mn_cell_i(g, p.x);
    int j = mn_cell_j(g, p.y);
    if (!mn_in_bounds(g, i, j)) {
        return -INFINITY;
    }
    float limit = tip[j * g->width + i];
    return mn_isnan(limit) ? -INFINITY : limit - p.z;
}

static int sample_count(const mn_segment* s, float step)
{
    float length = mn_segment_length(s);
    return length > 0 ? mn_f2i(ceilf(length / step)) : 0;
}

static mn_v3 sample(const mn_segment* s, int k, int samples)
{
    return samples == 0 ? seg_start(s) : mn_v3_lerp(seg_start(s), seg_end(s), (float)k / (float)samples);
}

int mn_is_clear(const mn_segment* segment, const mn_grid* g, const float* effective_tip, float tolerance)
{
    int samples = sample_count(segment, g->cell_size / 2.0f);
    for (int k = 0; k <= samples; k++) {
        if (depth(sample(segment, k, samples), g, effective_tip) > tolerance) {
            return 0;
        }
    }
    return 1;
}

MN_API int32_t mn_gouge_is_clear(const mn_segment* segment, const mn_grid* grid, const float* effective_tip, float tolerance)
{
    return mn_is_clear(segment, grid, effective_tip, tolerance);
}

MN_API int32_t mn_gouge_verify(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, int32_t** segment_index, float** positions, float** depths, int32_t* violation_count)
{
    mn_ints indices = { 0 };
    int capacity = 0;
    float* position_list = NULL;
    float* depth_list = NULL;
    int n = 0;
    for (int index = 0; index < count; index++) {
        const mn_segment* segment = &segments[index];
        if (segment->kind == MN_MOVE_RAPID) {
            continue;
        }
        int samples = sample_count(segment, grid->cell_size / 2.0f);
        for (int k = 0; k <= samples; k++) {
            mn_v3 p = sample(segment, k, samples);
            float d = depth(p, grid, effective_tip);
            if (!(d > tolerance)) {
                continue;
            }
            if (n == capacity) {
                capacity = capacity == 0 ? 64 : capacity * 2;
                float* grown_positions = (float*)realloc(position_list, (size_t)capacity * 3 * sizeof(float));
                float* grown_depths = grown_positions != NULL ? (float*)realloc(depth_list, (size_t)capacity * sizeof(float)) : NULL;
                if (grown_positions != NULL) {
                    position_list = grown_positions;
                }
                if (grown_depths == NULL) {
                    free(position_list);
                    free(depth_list);
                    mn_ints_free(&indices);
                    return mn_fail(MN_ERR_MEMORY, "Out of memory for the gouge list.");
                }
                depth_list = grown_depths;
            }
            if (mn_ints_push(&indices, index) != MN_OK) {
                free(position_list);
                free(depth_list);
                mn_ints_free(&indices);
                return MN_ERR_MEMORY;
            }
            position_list[3 * n] = p.x;
            position_list[3 * n + 1] = p.y;
            position_list[3 * n + 2] = p.z;
            depth_list[n] = d;
            n++;
        }
    }
    *segment_index = indices.items != NULL ? indices.items : (int*)mn_alloc(1, sizeof(int));
    *positions = position_list != NULL ? position_list : (float*)mn_alloc(1, sizeof(float));
    *depths = depth_list != NULL ? depth_list : (float*)mn_alloc(1, sizeof(float));
    *violation_count = n;
    return MN_OK;
}

/* ---- simplifier ---- */

float mn_distance_to(mn_v3 p, mn_v3 a, mn_v3 b)
{
    mn_v3 ab = mn_v3_sub(b, a);
    float length_squared = mn_v3_dot(ab, ab);
    if (length_squared == 0) {
        return mn_v3_distance(p, a);
    }
    float t = mn_clamp(mn_v3_dot(mn_v3_sub(p, a), ab) / length_squared, 0.0f, 1.0f);
    return mn_v3_distance(p, mn_v3_add(a, mn_v3_scale(ab, t)));
}

MN_API float mn_distance_to_segment(const float* p, const float* a, const float* b)
{
    return mn_distance_to(mn_v3_make(p[0], p[1], p[2]), mn_v3_make(a[0], a[1], a[2]), mn_v3_make(b[0], b[1], b[2]));
}

static mn_segment chord(mn_v3 a, mn_v3 b, float feed_rate)
{
    mn_segment s;
    s.start_x = a.x;
    s.start_y = a.y;
    s.start_z = a.z;
    s.end_x = b.x;
    s.end_y = b.y;
    s.end_z = b.z;
    s.kind = MN_MOVE_FEED;
    s.rate = feed_rate;
    return s;
}

static int holds(const mn_v3* points, int a, int b, const mn_grid* g, const float* tip, float tolerance, float feed_rate)
{
    for (int n = a + 1; n < b; n++) {
        if (mn_distance_to(points[n], points[a], points[b]) > tolerance) {
            return 0;
        }
    }
    mn_segment s = chord(points[a], points[b], feed_rate);
    return mn_is_clear(&s, g, tip, tolerance);
}

static int split(const mn_v3* points, int a, int b, const mn_grid* g, const float* tip, float tolerance, float feed_rate)
{
    int farthest = -1;
    float farthest_distance = -1.0f;
    for (int n = a + 1; n < b; n++) {
        float d = mn_distance_to(points[n], points[a], points[b]);
        if (d > farthest_distance) {
            farthest_distance = d;
            farthest = n;
        }
    }
    if (farthest_distance > tolerance) {
        return farthest;
    }
    mn_segment s = chord(points[a], points[b], feed_rate);
    if (mn_is_clear(&s, g, tip, tolerance)) {
        return -1;
    }
    return farthest_distance > 0 ? farthest : (a + b) / 2;
}

/* Indices kept by Douglas-Peucker (ascending, 0 and the last included) after the merge pass. */
static int kept_indices(const mn_v3* points, int count, const mn_grid* g, const float* tip, float tolerance, float feed_rate, mn_ints* kept)
{
    uint8_t* keep = (uint8_t*)mn_alloc((size_t)count, 1);
    int* stack = (int*)mn_alloc((size_t)count * 2 + 2, sizeof(int));
    if (keep == NULL || stack == NULL) {
        free(keep);
        free(stack);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the simplifier.");
    }
    keep[0] = 1;
    keep[count - 1] = 1;
    int top = 0;
    stack[top++] = 0;
    stack[top++] = count - 1;
    while (top > 0) {
        int b = stack[--top];
        int a = stack[--top];
        if (b - a < 2) {
            continue;
        }
        int s = split(points, a, b, g, tip, tolerance, feed_rate);
        if (s < 0) {
            continue;
        }
        keep[s] = 1;
        stack[top++] = a;
        stack[top++] = s;
        stack[top++] = s;
        stack[top++] = b;
    }
    free(stack);

    int status = MN_OK;
    for (int n = 0; n < count && status == MN_OK; n++) {
        if (keep[n]) {
            status = mn_ints_push(kept, n);
        }
    }
    free(keep);
    MN_CHECK(status);

    int m = 1;
    while (m + 1 < kept->count) {
        if (holds(points, kept->items[m - 1], kept->items[m + 1], g, tip, tolerance, feed_rate)) {
            memmove(kept->items + m, kept->items + m + 1, (size_t)(kept->count - m - 1) * sizeof(int));
            kept->count--;
        } else {
            m++;
        }
    }
    return MN_OK;
}

MN_API int32_t mn_kept_indices(const float* points, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, float feed_rate, int32_t** kept, int32_t* kept_count)
{
    mn_ints list = { 0 };
    int status = kept_indices((const mn_v3*)points, count, grid, effective_tip, tolerance, feed_rate, &list);
    if (status != MN_OK) {
        mn_ints_free(&list);
        return status;
    }
    *kept = list.items;
    *kept_count = list.count;
    return MN_OK;
}

static int continues(const mn_segment* previous, const mn_segment* next)
{
    return next->kind == MN_MOVE_FEED && next->rate == previous->rate && next->start_x == previous->end_x && next->start_y == previous->end_y
        && next->start_z == previous->end_z;
}

/* A maximal run of consecutive feeds at one rate is a polyline reduced to the kept vertices; rapids,
 * plunges and the ends of every run stay where the strategy put them. */
int mn_simplify_path(const mn_segments* input, const mn_grid* g, const float* effective_tip, float tolerance, mn_segments* result)
{
    if (!(tolerance >= 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Tolerance must be zero or positive.");
    }
    const mn_segment* segments = input->items;
    int count = input->count;
    mn_points points = { 0 };
    mn_ints kept = { 0 };
    int status = MN_OK;
    int k = 0;
    while (k < count && status == MN_OK) {
        const mn_segment* first = &segments[k];
        if (first->kind != MN_MOVE_FEED) {
            status = mn_segments_push(result, *first);
            k++;
            continue;
        }
        int end = k + 1;
        while (end < count && continues(&segments[end - 1], &segments[end])) {
            end++;
        }
        points.count = 0;
        status = mn_points_push(&points, seg_start(first));
        for (int n = k; n < end && status == MN_OK; n++) {
            status = mn_points_push(&points, seg_end(&segments[n]));
        }
        kept.count = 0;
        if (status == MN_OK) {
            status = kept_indices(points.items, points.count, g, effective_tip, tolerance, first->rate, &kept);
        }
        int previous = 0;
        for (int m = 0; m < kept.count && status == MN_OK; m++) {
            int index = kept.items[m];
            if (index > 0) {
                status = mn_segments_push(result, chord(points.items[previous], points.items[index], first->rate));
                previous = index;
            }
        }
        k = end;
    }
    mn_points_free(&points);
    mn_ints_free(&kept);
    return status;
}

MN_API int32_t mn_simplify(const mn_segment* segments, int32_t count, const mn_grid* grid, const float* effective_tip, float tolerance, mn_segment** result, int32_t* result_count)
{
    mn_segments input = { (mn_segment*)segments, count, count };
    mn_segments output = { 0 };
    int status = mn_simplify_path(&input, grid, effective_tip, tolerance, &output);
    if (status != MN_OK) {
        mn_segments_free(&output);
        return status;
    }
    *result = output.items != NULL ? output.items : (mn_segment*)mn_alloc(1, sizeof(mn_segment));
    *result_count = output.count;
    return MN_OK;
}

/* ---- statistics: minutes = sum of length / rate; rapids use the machine's rapid rate ---- */

void mn_statistics_of(const mn_segment* segments, int count, float rapid_rate, mn_statistics* statistics)
{
    float rapid = 0.0f, feed = 0.0f, plunge = 0.0f;
    double minutes = 0.0;
    int retracts = 0;
    for (int k = 0; k < count; k++) {
        const mn_segment* s = &segments[k];
        float length = mn_segment_length(s);
        if (s->kind == MN_MOVE_RAPID) {
            rapid += length;
            minutes += (double)(length / rapid_rate);
            if (s->end_z > s->start_z) {
                retracts++;
            }
        } else if (s->kind == MN_MOVE_FEED) {
            feed += length;
            minutes += (double)(length / s->rate);
        } else {
            plunge += length;
            minutes += (double)(length / s->rate);
        }
    }
    statistics->rapid_length = rapid;
    statistics->feed_length = feed;
    statistics->plunge_length = plunge;
    statistics->segment_count = count;
    statistics->estimated_minutes = (float)minutes;
    statistics->retract_count = retracts;
}

MN_API int32_t mn_statistics_compute(const mn_segment* segments, int32_t count, float rapid_rate, mn_statistics* statistics)
{
    if (!(rapid_rate > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "Rapid rate must be positive, got %g.", (double)rapid_rate);
    }
    mn_statistics_of(segments, count, rapid_rate, statistics);
    return MN_OK;
}
