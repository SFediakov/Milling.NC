#include "mn_window.h"

/* PathView and FineWindow of the route solver: a route read as up to four pieces of a base order,
 * the fined status of its nodes, and the part of its slow length that depends on the changed nodes
 * (see mn_window.h). */

#define MN_REACH (2.0f * MN_SLOW_ZONE)

/* ---- turn cache ---- */

int mn_turn_cache_init(mn_turn_cache* cache, int n)
{
    size_t count = (size_t)(n > 0 ? n : 1);
    memset(cache, 0, sizeof(*cache));
    cache->cosine = (float*)mn_alloc(count, sizeof(float));
    cache->side = (signed char*)mn_alloc(count, 1);
    cache->defined = (uint8_t*)mn_alloc(count, 1);
    cache->arc = (uint8_t*)mn_alloc(count, 1);
    cache->until = -1;
    if (cache->cosine == NULL || cache->side == NULL || cache->defined == NULL || cache->arc == NULL) {
        mn_turn_cache_free(cache);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the turn cache of %d positions.", n);
    }
    return MN_OK;
}

void mn_turn_cache_free(mn_turn_cache* cache)
{
    free(cache->cosine);
    free(cache->side);
    free(cache->defined);
    free(cache->arc);
    memset(cache, 0, sizeof(*cache));
    cache->until = -1;
}

void mn_turn_cache_turn(mn_turn_cache* cache, const float* x, const float* y, const int* order, int k)
{
    float cosine;
    int side;
    cache->defined[k] = (uint8_t)mn_turn_between(x, y, order[k - 1], order[k], order[k + 1], &cosine, &side);
    cache->cosine[k] = cosine;
    cache->side[k] = (signed char)side;
}

void mn_turn_cache_arc(mn_turn_cache* cache, const float* x, const float* y, float cell_size, const int* order, int q)
{
    cache->arc[q] = (uint8_t)mn_arc_between(x, y, MN_CIRCLE_TOLERANCE_CELLS * cell_size, order[q], order[q + 1], order[q + 2], order[q + 3]);
}

void mn_turn_cache_reverse(mn_turn_cache* cache, int l, int r)
{
    for (int a = l + 1, b = r - 1; a <= b; a++, b--) {
        float cosine = cache->cosine[a];
        cache->cosine[a] = cache->cosine[b];
        cache->cosine[b] = cosine;
        uint8_t defined = cache->defined[a];
        cache->defined[a] = cache->defined[b];
        cache->defined[b] = defined;
        signed char side = cache->side[a];
        cache->side[a] = (signed char)-cache->side[b];
        cache->side[b] = (signed char)-side;
    }
    for (int a = l, b = r - 3; a < b; a++, b--) {
        uint8_t arc = cache->arc[a];
        cache->arc[a] = cache->arc[b];
        cache->arc[b] = arc;
    }
}

/* ---- view ---- */

void mn_view_reset(mn_view* view, const int* base, const float* lengths, const float* x, const float* y, float cell_size, const mn_turn_cache* cache)
{
    view->base = base;
    view->lengths = lengths;
    view->x = x;
    view->y = y;
    view->cell_size = cell_size;
    view->cache = cache;
    view->pieces = 0;
    view->count = 0;
}

void mn_view_add(mn_view* view, int first, int last, int reversed)
{
    if (last < first) {
        return;
    }
    int k = view->pieces++;
    view->first[k] = first;
    view->length[k] = last - first + 1;
    view->reversed[k] = reversed;
    view->at[k] = view->count;
    view->count += last - first + 1;
}

static int piece_of(const mn_view* view, int p)
{
    int k = 0;
    while (p >= view->at[k] + view->length[k]) {
        k++;
    }
    return k;
}

static int base_in_piece(const mn_view* view, int p, int piece)
{
    int offset = p - view->at[piece];
    return view->reversed[piece] ? view->first[piece] + view->length[piece] - 1 - offset : view->first[piece] + offset;
}

int mn_view_base(const mn_view* view, int p) { return base_in_piece(view, p, piece_of(view, p)); }

int mn_view_position(const mn_view* view, int base_position)
{
    for (int k = 0; k < view->pieces; k++) {
        int offset = base_position - view->first[k];
        if (offset >= 0 && offset < view->length[k]) {
            return view->reversed[k] ? view->at[k] + view->length[k] - 1 - offset : view->at[k] + offset;
        }
    }
    return -1;
}

int mn_view_node(const mn_view* view, int p) { return view->base[mn_view_base(view, p)]; }

float mn_view_length(const mn_view* view, int p)
{
    int piece = piece_of(view, p);
    int from = base_in_piece(view, p, piece);
    if (view->lengths != NULL && p + 1 < view->at[piece] + view->length[piece]) {
        return view->lengths[view->reversed[piece] ? from - 1 : from];
    }
    int a = view->base[from];
    int b = mn_view_node(view, p + 1);
    float dx = view->x[a] - view->x[b];
    float dy = view->y[a] - view->y[b];
    return sqrtf(dx * dx + dy * dy);
}

/* ---- fined status ---- */

/* The piece holding positions first..last, or -1 when they span a join. */
static int one_piece(const mn_view* view, int first, int last)
{
    int piece = piece_of(view, first);
    return last <= mn_view_piece_end(view, piece) ? piece : -1;
}

static int view_turn(const mn_view* view, int p, float* cosine, int* side)
{
    const mn_turn_cache* cache = view->cache;
    int piece;
    if (cache != NULL && (piece = one_piece(view, p - 1, p + 1)) >= 0) {
        int b = base_in_piece(view, p, piece);
        if (b + 1 <= cache->until) {
            *cosine = cache->cosine[b];
            *side = view->reversed[piece] ? -cache->side[b] : cache->side[b];
            return cache->defined[b];
        }
    }
    return mn_turn_between(view->x, view->y, mn_view_node(view, p - 1), mn_view_node(view, p), mn_view_node(view, p + 1), cosine, side);
}

int mn_view_turn_kind(const mn_view* view, int p)
{
    float cosine;
    int side;
    if (!(p > 0 && p < view->count - 1) || !view_turn(view, p, &cosine, &side)) {
        return MN_TURN_NONE;
    }
    if (cosine < MN_COS_SHARP) {
        return MN_TURN_SHARP;
    }
    return side == 0 ? MN_TURN_NONE : MN_TURN_SOFT;
}

int mn_view_arc(const mn_view* view, int q)
{
    const mn_turn_cache* cache = view->cache;
    int piece;
    if (cache != NULL && (piece = one_piece(view, q, q + 3)) >= 0) {
        int start = view->reversed[piece] ? base_in_piece(view, q + 3, piece) : base_in_piece(view, q, piece);
        if (start + 3 <= cache->until) {
            return cache->arc[start];
        }
    }
    return mn_arc_between(view->x, view->y, MN_CIRCLE_TOLERANCE_CELLS * view->cell_size, mn_view_node(view, q), mn_view_node(view, q + 1), mn_view_node(view, q + 2),
        mn_view_node(view, q + 3));
}

/* Whether the node at p turns on a circular section of at least MN_CIRCLE_MIN_LENGTH: a chain of
 * consecutive arcs (four positions each) with p inside one of them, from the first node of the
 * chain to its last, followed at most MN_CHAIN_REACH arcs each way from the arcs of p. */
static int circular(const mn_view* view, int p)
{
    int last_start = view->count - 4;
    int a = -1;
    int b = -1;
    for (int q = p - 2; q <= p - 1; q++) {
        if (q >= 0 && q <= last_start && mn_view_arc(view, q)) {
            if (a < 0) {
                a = q;
            }
            b = q;
        }
    }
    if (a < 0) {
        return 0;
    }
    double length = 0.0;
    for (int k = a; k <= b + 2; k++) {
        length += (double)mn_view_length(view, k);
    }
    int grow_left = 1;
    int grow_right = 1;
    for (int step = 0; length < MN_CIRCLE_MIN_LENGTH && step < MN_CHAIN_REACH && (grow_left || grow_right); step++) {
        if (grow_left) {
            if (a - 1 >= 0 && mn_view_arc(view, a - 1)) {
                a--;
                length += (double)mn_view_length(view, a);
            } else {
                grow_left = 0;
            }
        }
        if (grow_right) {
            if (b + 1 <= last_start && mn_view_arc(view, b + 1)) {
                b++;
                length += (double)mn_view_length(view, b + 2);
            } else {
                grow_right = 0;
            }
        }
    }
    return length >= MN_CIRCLE_MIN_LENGTH;
}

/* The soft turns next to p in one direction, nearest first, with their path distance from p: at most
 * MN_TURN_WINDOW - 1 of them within MN_TURN_REACH positions and less than MN_TURN_SPAN away, past
 * straight positions, up to a sharp turn or a turn of a long circular section. */
static int gather(const mn_view* view, int p, int direction, int* turns, double* distances)
{
    int found = 0;
    double d = 0.0;
    for (int step = 1; step <= MN_TURN_REACH && found < MN_TURN_WINDOW - 1; step++) {
        int k = p + direction * step;
        if (k < 1 || k > view->count - 2) {
            break;
        }
        d += (double)mn_view_length(view, direction < 0 ? k : k - 1);
        if (!(d < MN_TURN_SPAN)) {
            break;
        }
        int kind = mn_view_turn_kind(view, k);
        if (kind == MN_TURN_SHARP) {
            break;
        }
        if (kind == MN_TURN_NONE) {
            continue;
        }
        if (circular(view, k)) {
            break;
        }
        turns[found] = k;
        distances[found] = d;
        found++;
    }
    return found;
}

/* Cosine of the angle between chord a (position a to a + 1) and chord b. */
static float chord_cosine(const mn_view* view, int a, int b)
{
    int a0 = mn_view_node(view, a);
    int a1 = mn_view_node(view, a + 1);
    int b0 = mn_view_node(view, b);
    int b1 = mn_view_node(view, b + 1);
    float ax = view->x[a1] - view->x[a0];
    float ay = view->y[a1] - view->y[a0];
    float bx = view->x[b1] - view->x[b0];
    float by = view->y[b1] - view->y[b0];
    return (ax * bx + ay * by) / (sqrtf(ax * ax + ay * ay) * sqrtf(bx * bx + by * by));
}

/* Whether the soft turn at p belongs to a compound turn: up to MN_TURN_WINDOW consecutive soft turns
 * (straight positions between them skipped) spanning less than MN_TURN_SPAN, with the chord into
 * the first and the chord out of the last more than the sharp angle apart. */
static int compound(const mn_view* view, int p)
{
    int left[MN_TURN_WINDOW - 1];
    int right[MN_TURN_WINDOW - 1];
    double left_distance[MN_TURN_WINDOW - 1];
    double right_distance[MN_TURN_WINDOW - 1];
    int nl = gather(view, p, -1, left, left_distance);
    int nr = gather(view, p, 1, right, right_distance);
    for (int f = 0; f <= nl; f++) {
        for (int g = 0; g <= nr; g++) {
            int size = f + g + 1;
            if (size < 2 || size > MN_TURN_WINDOW) {
                continue;
            }
            double span = (f > 0 ? left_distance[f - 1] : 0.0) + (g > 0 ? right_distance[g - 1] : 0.0);
            if (!(span < MN_TURN_SPAN)) {
                continue;
            }
            int first = f > 0 ? left[f - 1] : p;
            int last = g > 0 ? right[g - 1] : p;
            if (chord_cosine(view, first - 1, last) < MN_COS_SHARP) {
                return 1;
            }
        }
    }
    return 0;
}

int mn_view_fined(const mn_view* view, int p)
{
    int kind = mn_view_turn_kind(view, p);
    if (kind == MN_TURN_NONE || (kind == MN_TURN_SOFT && !compound(view, p))) {
        return 0;
    }
    return !circular(view, p);
}

/* ---- window ---- */

static mn_walk walk(float distance, int zones, int far)
{
    mn_walk w;
    w.distance = distance;
    w.zones = zones;
    w.far = far;
    return w;
}

static mn_walk behind(mn_walk w)
{
    w.distance = -w.distance;
    return w;
}

static float overlap_with(mn_walk w, int zones, float at) { return w.far ? 0.0f : mn_turn_overlap_of(w.zones, zones, at - w.distance); }

int mn_window_init(mn_window* window, const uint8_t* fined, int capacity)
{
    memset(window, 0, sizeof(*window));
    window->fined = fined;
    window->capacity = capacity;
    window->changed = (int*)mn_alloc((size_t)capacity, sizeof(int));
    window->positions = (int*)mn_alloc((size_t)capacity, sizeof(int));
    window->stretches = (int*)mn_alloc((size_t)capacity, sizeof(int));
    window->low = (mn_walk*)mn_alloc((size_t)capacity + 1, sizeof(mn_walk));
    window->high = (mn_walk*)mn_alloc((size_t)capacity + 1, sizeof(mn_walk));
    window->span = (float*)mn_alloc((size_t)capacity + 1, sizeof(float));
    window->through = (uint8_t*)mn_alloc((size_t)capacity + 1, 1);
    if (window->changed == NULL || window->positions == NULL || window->stretches == NULL || window->low == NULL || window->high == NULL || window->span == NULL
        || window->through == NULL) {
        mn_window_free(window);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the fine window.");
    }
    return MN_OK;
}

void mn_window_free(mn_window* window)
{
    free(window->changed);
    free(window->positions);
    free(window->stretches);
    free(window->low);
    free(window->high);
    free(window->span);
    free(window->through);
    memset(window, 0, sizeof(*window));
}

/* Up the base order from position a (exclusive) to the first event before position `stop`. */
static mn_walk up(const mn_window* window, int a, int stop, uint8_t* through, float* span)
{
    *through = 0;
    *span = 0.0f;
    float d = 0.0f;
    int q = a;
    for (;;) {
        d += window->lengths[q];
        q++;
        if (q == stop) {
            *through = 1;
            *span = d;
            return walk(d, 0, 1);
        }
        if (d >= MN_REACH) {
            return walk(d, 0, 1);
        }
        if (q == window->count - 1) {
            return walk(d, 0, 0);
        }
        if (window->fined[window->base[q]]) {
            return walk(d, 1, 0);
        }
    }
}

/* Down the base order from position b (exclusive) to the first event after position `stop`. */
static mn_walk down(const mn_window* window, int b, int stop)
{
    float d = 0.0f;
    int q = b;
    for (;;) {
        q--;
        d += window->lengths[q];
        if (q == stop) {
            return walk(d, 0, 1);
        }
        if (d >= MN_REACH) {
            return walk(d, 0, 1);
        }
        if (q == 0) {
            return walk(d, 0, 0);
        }
        if (window->fined[window->base[q]]) {
            return walk(d, 1, 0);
        }
    }
}

void mn_window_prepare(mn_window* window, const int* base, const float* lengths, int count, const int* changed, int changed_count)
{
    window->base = base;
    window->lengths = lengths;
    window->count = count;
    window->changed_count = 0;
    for (int k = 0; k < changed_count; k++) {
        int b = changed[k];
        int at = window->changed_count;
        while (at > 0 && window->changed[at - 1] > b) {
            at--;
        }
        if (at > 0 && window->changed[at - 1] == b) {
            continue;
        }
        for (int m = window->changed_count; m > at; m--) {
            window->changed[m] = window->changed[m - 1];
        }
        window->changed[at] = b;
        window->changed_count++;
    }

    if (window->changed[0] > 0) {
        window->high[0] = down(window, window->changed[0], -1);
    }
    for (int k = 1; k < window->changed_count; k++) {
        int a = window->changed[k - 1];
        int b = window->changed[k];
        if (b == a + 1) {
            continue;
        }
        window->low[k] = up(window, a, b, &window->through[k], &window->span[k]);
        if (!window->through[k]) {
            window->high[k] = down(window, b, a);
        }
    }
    int last = window->changed[window->changed_count - 1];
    if (last < window->count - 1) {
        uint8_t through;
        float span;
        window->low[window->changed_count] = up(window, last, window->count, &through, &span);
    }
}

float mn_window_term(mn_window* window, const mn_view* view, int fresh)
{
    int n = 0;
    for (int k = 0; k < window->changed_count; k++) {
        int p = mn_view_position(view, window->changed[k]);
        if (p < 0) {
            continue;
        }
        int at = n;
        while (at > 0 && window->positions[at - 1] > p) {
            window->positions[at] = window->positions[at - 1];
            window->stretches[at] = window->stretches[at - 1];
            at--;
        }
        window->positions[at] = p;
        window->stretches[at] = k;
        n++;
    }

    int end = view->count - 1;
    float total = 0.0f;
    float s = 0.0f;
    mn_walk last = window->positions[0] == 0 ? walk(0.0f, 0, 0) : behind(window->high[0]);
    for (int i = 0; i < n; i++) {
        int p = window->positions[i];
        if (p == end) {
            total -= overlap_with(last, 0, s);
            break;
        }

        if (p > 0 && (fresh ? mn_view_fined(view, p) : window->fined[mn_view_node(view, p)])) {
            total += 2.0f * 5.0f - overlap_with(last, 1, s);
            last = walk(s, 1, 0);
        }

        if (i + 1 == n) {
            mn_walk after = window->low[window->changed_count];
            if (!after.far) {
                total -= overlap_with(last, after.zones, s + after.distance);
            }
            break;
        }

        int next = window->positions[i + 1];
        if (next == p + 1) {
            s += mn_view_length(view, p);
            continue;
        }

        int from = window->stretches[i];
        int to = window->stretches[i + 1];
        int stretch = mn_maxi(from, to);
        if (window->through[stretch]) {
            s += window->span[stretch];
            continue;
        }

        mn_walk entry = from < to ? window->low[stretch] : window->high[stretch];
        if (!entry.far) {
            total -= overlap_with(last, entry.zones, s + entry.distance);
        }
        s = 0.0f;
        last = behind(from < to ? window->high[stretch] : window->low[stretch]);
    }
    return total;
}
