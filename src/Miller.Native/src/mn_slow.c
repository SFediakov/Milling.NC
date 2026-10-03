#include "mn_internal.h"

/* Slow zones: the turn fine of the route solver realized in the toolpath. A movement is a run of
 * consecutive feeds whose junctions are straight on in XY (mn_turn_between: the sine of the change
 * within MN_STRAIGHT_SINE and no reversal, the rule the solver fines by); its first and last
 * MN_SLOW_ZONE millimetres of XY travel run at MN_SLOW_SPEED_FACTOR of the rate, and a movement
 * shorter than two zones is slow over its whole length. Rapids, plunges, feeds without XY travel and
 * every direction change end a movement, so every new coordinate set starts and ends slow, at route
 * ends as well (the solver leaves those out of its cost because every order of a route pays them).
 * The chords are split at the zone boundaries; the original vertices stay exactly where they are. */

static float planar(const mn_segment* s)
{
    float dx = s->end_x - s->start_x;
    float dy = s->end_y - s->start_y;
    return sqrtf(dx * dx + dy * dy);
}

static int moves_in_xy(const mn_segment* s) { return s->kind == MN_MOVE_FEED && planar(s) >= MN_MIN_CHORD; }

/* Whether `next` continues the movement of `previous`: joined, at one rate and straight on in XY. */
static int straight_on(const mn_segment* previous, const mn_segment* next)
{
    if (next->rate != previous->rate || next->start_x != previous->end_x || next->start_y != previous->end_y || next->start_z != previous->end_z) {
        return 0;
    }
    float x[3] = { previous->start_x, previous->end_x, next->end_x };
    float y[3] = { previous->start_y, previous->end_y, next->end_y };
    float cosine;
    int side;
    int defined = mn_turn_between(x, y, 0, 1, 2, &cosine, &side);
    return !mn_turn_is_fined(defined, cosine, side);
}

static void remap(mn_marks* marks, int* next_mark, int input_index, int output_index)
{
    while (marks != NULL && *next_mark < marks->count && marks->segment[*next_mark] <= input_index) {
        marks->segment[(*next_mark)++] = output_index;
    }
}

static int push_piece(mn_segments* result, const mn_segment* s, mn_v3 from, mn_v3 to, int slow)
{
    mn_segment piece = *s;
    piece.start_x = from.x;
    piece.start_y = from.y;
    piece.start_z = from.z;
    piece.end_x = to.x;
    piece.end_y = to.y;
    piece.end_z = to.z;
    piece.rate = slow ? s->rate * MN_SLOW_SPEED_FACTOR : s->rate;
    return mn_segments_push(result, piece);
}

/* The segments [first, last) of one movement, split where the lead zone ends and the tail zone
 * starts; a piece is slow when its middle lies in a zone. */
static int write_movement(const mn_segment* segments, int first, int last, mn_marks* marks, int* next_mark, mn_segments* result)
{
    float length = 0.0f;
    for (int k = first; k < last; k++) {
        length += planar(&segments[k]);
    }
    int whole = length <= 2.0f * MN_SLOW_ZONE;
    float lead = MN_SLOW_ZONE;
    float tail = length - MN_SLOW_ZONE;
    float s = 0.0f;
    for (int k = first; k < last; k++) {
        remap(marks, next_mark, k, result->count);
        const mn_segment* seg = &segments[k];
        mn_v3 a = mn_v3_make(seg->start_x, seg->start_y, seg->start_z);
        mn_v3 b = mn_v3_make(seg->end_x, seg->end_y, seg->end_z);
        float chord = planar(seg);
        float end = s + chord;
        if (whole) {
            MN_CHECK(push_piece(result, seg, a, b, 1));
            s = end;
            continue;
        }
        float bounds[4];
        int n = 0;
        bounds[n++] = s;
        if (lead > s && lead < end) {
            bounds[n++] = lead;
        }
        if (tail > s && tail < end) {
            bounds[n++] = tail;
        }
        bounds[n++] = end;
        mn_v3 at = a;
        float at_s = s;
        for (int m = 1; m < n; m++) {
            int final = m == n - 1;
            mn_v3 p = final ? b : mn_v3_lerp(a, b, (bounds[m] - s) / chord);
            if (!final && mn_v3_equal(p, at)) {
                continue;
            }
            float middle = (at_s + bounds[m]) * 0.5f;
            MN_CHECK(push_piece(result, seg, at, p, middle < lead || middle > tail));
            at = p;
            at_s = bounds[m];
        }
        s = end;
    }
    return MN_OK;
}

int mn_slow_zones(const mn_segments* input, mn_marks* marks, mn_segments* result)
{
    const mn_segment* segments = input->items;
    int count = input->count;
    int next_mark = 0;
    int k = 0;
    while (k < count) {
        if (!moves_in_xy(&segments[k])) {
            remap(marks, &next_mark, k, result->count);
            MN_CHECK(mn_segments_push(result, segments[k]));
            k++;
            continue;
        }
        int end = k + 1;
        while (end < count && moves_in_xy(&segments[end]) && straight_on(&segments[end - 1], &segments[end])) {
            end++;
        }
        MN_CHECK(write_movement(segments, k, end, marks, &next_mark, result));
        k = end;
    }
    remap(marks, &next_mark, INT_MAX, result->count);
    return MN_OK;
}

MN_API int32_t mn_slow_zones_apply(const mn_segment* segments, int32_t count, mn_segment** result, int32_t* result_count)
{
    if (count < 0 || (count > 0 && segments == NULL)) {
        return mn_fail(MN_ERR_ARGUMENT, "A segment list of %d entries needs a pointer.", count);
    }
    mn_segments input = { (mn_segment*)segments, count, count };
    mn_segments output = { 0 };
    int status = mn_slow_zones(&input, NULL, &output);
    if (status != MN_OK) {
        mn_segments_free(&output);
        return status;
    }
    *result = output.items != NULL ? output.items : (mn_segment*)mn_alloc(1, sizeof(mn_segment));
    *result_count = output.count;
    return MN_OK;
}

MN_API float mn_turn_slow_speed_factor(void) { return MN_SLOW_SPEED_FACTOR; }

MN_API float mn_turn_per_slow_millimetre(void) { return MN_PER_SLOW_MILLIMETRE; }
