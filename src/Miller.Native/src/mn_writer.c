#include "mn_internal.h"

/* Turns tool positions into segments (RouteWriter). A cut follows the surface polyline: level,
 * rising and gently descending parts are feeds, a descent steeper than MN_MAX_RAMP_SLOPE is a feed
 * over the lower point and a plunge. A travel takes that polyline or a retract to safe Z, a rapid and
 * a plunge, whichever the rates make faster; the program starts with a plunge from safe Z. */

#define MN_WRITER_LEVEL_EPSILON 1e-5f
#define MN_MAX_RAMP_SLOPE 2.0f

struct mn_writer {
    mn_parameters parameters;
    float safe_z;
    mn_segments path;
    mn_points buffer;
    mn_segments scratch;
    int has_position;
    mn_v3 position;
};

int mn_segments_push(mn_segments* list, mn_segment segment)
{
    if (list->count == list->capacity) {
        int capacity = list->capacity == 0 ? 64 : list->capacity * 2;
        mn_segment* items = (mn_segment*)realloc(list->items, (size_t)capacity * sizeof(mn_segment));
        if (items == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for a toolpath of %d segments.", capacity);
        }
        list->items = items;
        list->capacity = capacity;
    }
    list->items[list->count++] = segment;
    return MN_OK;
}

void mn_segments_free(mn_segments* list)
{
    free(list->items);
    list->items = NULL;
    list->count = 0;
    list->capacity = 0;
}

static mn_v3 seg_start(const mn_segment* s) { return mn_v3_make(s->start_x, s->start_y, s->start_z); }

static mn_v3 seg_end(const mn_segment* s) { return mn_v3_make(s->end_x, s->end_y, s->end_z); }

float mn_segment_length(const mn_segment* s) { return mn_v3_distance(seg_start(s), seg_end(s)); }

static mn_segment make_segment(mn_v3 start, mn_v3 end, int kind, float rate)
{
    mn_segment s;
    s.start_x = start.x;
    s.start_y = start.y;
    s.start_z = start.z;
    s.end_x = end.x;
    s.end_y = end.y;
    s.end_z = end.z;
    s.kind = kind;
    s.rate = rate;
    return s;
}

MN_API int32_t mn_writer_create(const mn_parameters* parameters, float safe_z, mn_writer** writer)
{
    if (!(parameters->feed_rate > 0) || !(parameters->plunge_rate > 0) || !(parameters->rapid_rate > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "Feed, plunge and rapid rates must be positive.");
    }
    mn_writer* w = (mn_writer*)mn_alloc(1, sizeof(mn_writer));
    if (w == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a route writer.");
    }
    w->parameters = *parameters;
    w->safe_z = safe_z;
    *writer = w;
    return MN_OK;
}

MN_API void mn_writer_free(mn_writer* writer)
{
    if (writer == NULL) {
        return;
    }
    mn_segments_free(&writer->path);
    mn_segments_free(&writer->scratch);
    mn_points_free(&writer->buffer);
    free(writer);
}

static int add(mn_writer* w, mn_segment segment)
{
    if (segment.start_x == segment.end_x && segment.start_y == segment.end_y && segment.start_z == segment.end_z) {
        return MN_OK;
    }
    MN_CHECK(mn_segments_push(&w->path, segment));
    w->position = seg_end(&segment);
    w->has_position = 1;
    return MN_OK;
}

static float minutes(const mn_writer* w, const mn_segment* segments, int count)
{
    float total = 0.0f;
    for (int k = 0; k < count; k++) {
        float rate = segments[k].kind == MN_MOVE_RAPID ? w->parameters.rapid_rate : segments[k].rate;
        total += mn_segment_length(&segments[k]) / rate;
    }
    return total;
}

static int cut(mn_writer* w, mn_v3 from, mn_v3 to, const mn_route_grid* grid, mn_segments* segments)
{
    w->buffer.count = 0;
    int status;
    mn_trace(grid, from, to, &w->buffer, &status);
    MN_CHECK(status);
    mn_v3 at = from;
    for (int k = 0; k < w->buffer.count; k++) {
        mn_v3 next = w->buffer.items[k];
        if (mn_v3_equal(next, at)) {
            continue;
        }
        float dx = next.x - at.x;
        float dy = next.y - at.y;
        float planar = sqrtf(dx * dx + dy * dy);
        float drop = at.z - next.z;
        if (drop > MN_WRITER_LEVEL_EPSILON && drop > MN_MAX_RAMP_SLOPE * planar) {
            if (planar > 0) {
                mn_v3 over = mn_v3_make(next.x, next.y, at.z);
                MN_CHECK(mn_segments_push(segments, make_segment(at, over, MN_MOVE_FEED, w->parameters.feed_rate)));
                at = over;
            }
            MN_CHECK(mn_segments_push(segments, make_segment(at, next, MN_MOVE_PLUNGE, w->parameters.plunge_rate)));
        } else {
            MN_CHECK(mn_segments_push(segments, make_segment(at, next, MN_MOVE_FEED, w->parameters.feed_rate)));
        }
        at = next;
    }
    return MN_OK;
}

int mn_writer_travel_to(mn_writer* w, mn_v3 to, const mn_route_grid* grid)
{
    if (to.z > w->safe_z) {
        return mn_fail(MN_ERR_ARGUMENT, "Position <%g, %g, %g> lies above safe Z %g.", (double)to.x, (double)to.y, (double)to.z, (double)w->safe_z);
    }
    if (!w->has_position) {
        mn_v3 above = mn_v3_make(to.x, to.y, w->safe_z);
        MN_CHECK(mn_segments_push(&w->path, make_segment(above, to, MN_MOVE_PLUNGE, w->parameters.plunge_rate)));
        w->position = to;
        w->has_position = 1;
        return MN_OK;
    }

    mn_v3 from = w->position;
    w->scratch.count = 0;
    MN_CHECK(cut(w, from, to, grid, &w->scratch));
    float along = minutes(w, w->scratch.items, w->scratch.count);
    mn_v3 up = mn_v3_make(from.x, from.y, w->safe_z);
    mn_v3 over = mn_v3_make(to.x, to.y, w->safe_z);
    mn_segment retract[3];
    retract[0] = make_segment(from, up, MN_MOVE_RAPID, w->parameters.rapid_rate);
    retract[1] = make_segment(up, over, MN_MOVE_RAPID, w->parameters.rapid_rate);
    retract[2] = make_segment(over, to, MN_MOVE_PLUNGE, w->parameters.plunge_rate);
    if (along <= minutes(w, retract, 3)) {
        for (int k = 0; k < w->scratch.count; k++) {
            MN_CHECK(add(w, w->scratch.items[k]));
        }
    } else {
        for (int k = 0; k < 3; k++) {
            MN_CHECK(add(w, retract[k]));
        }
    }
    return MN_OK;
}

int mn_writer_follow_to(mn_writer* w, mn_v3 to, const mn_route_grid* grid)
{
    if (!w->has_position) {
        return mn_fail(MN_ERR_STATE, "Travel to the first position before following the surface.");
    }
    w->scratch.count = 0;
    MN_CHECK(cut(w, w->position, to, grid, &w->scratch));
    for (int k = 0; k < w->scratch.count; k++) {
        MN_CHECK(add(w, w->scratch.items[k]));
    }
    return MN_OK;
}

int mn_writer_has_position(const mn_writer* writer, mn_v3* position)
{
    if (writer->has_position) {
        *position = writer->position;
    }
    return writer->has_position;
}

/* Finish: the retract to safe Z, then the segments move to `result`. */
int mn_writer_take(mn_writer* w, mn_segments* result)
{
    if (w->has_position) {
        mn_v3 last = w->position;
        MN_CHECK(add(w, make_segment(last, mn_v3_make(last.x, last.y, w->safe_z), MN_MOVE_RAPID, w->parameters.rapid_rate)));
    }
    *result = w->path;
    memset(&w->path, 0, sizeof(w->path));
    return MN_OK;
}

MN_API int32_t mn_writer_travel(mn_writer* writer, const float* to, const mn_grid* grid, const float* floor)
{
    mn_route_grid route = { *grid, floor };
    return mn_writer_travel_to(writer, mn_v3_make(to[0], to[1], to[2]), &route);
}

MN_API int32_t mn_writer_follow(mn_writer* writer, const float* to, const mn_grid* grid, const float* floor)
{
    mn_route_grid route = { *grid, floor };
    return mn_writer_follow_to(writer, mn_v3_make(to[0], to[1], to[2]), &route);
}

MN_API int32_t mn_writer_position(const mn_writer* writer, float* position)
{
    mn_v3 p;
    if (!mn_writer_has_position(writer, &p)) {
        return 0;
    }
    position[0] = p.x;
    position[1] = p.y;
    position[2] = p.z;
    return 1;
}

MN_API int32_t mn_writer_count(const mn_writer* writer) { return writer->path.count; }

MN_API int32_t mn_writer_finish(mn_writer* writer, mn_segment** segments, int32_t* count)
{
    mn_segments result;
    MN_CHECK(mn_writer_take(writer, &result));
    *segments = result.items != NULL ? result.items : (mn_segment*)mn_alloc(1, sizeof(mn_segment));
    *count = result.count;
    return MN_OK;
}
