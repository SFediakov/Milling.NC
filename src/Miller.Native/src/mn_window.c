#include "mn_window.h"

/* PathView and FineWindow of the route solver: a route read as up to four pieces of a base order,
 * and the part of its slow length that depends on the changed nodes (see mn_window.h). */

#define MN_REACH (2.0f * 5.0f)

void mn_view_reset(mn_view* view, const int* base, const float* lengths, const float* x, const float* y, float cell_size)
{
    view->base = base;
    view->lengths = lengths;
    view->x = x;
    view->y = y;
    view->cell_size = cell_size;
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
    if (p + 1 < view->at[piece] + view->length[piece]) {
        return view->lengths[view->reversed[piece] ? from - 1 : from];
    }
    int a = view->base[from];
    int b = mn_view_node(view, p + 1);
    float dx = view->x[a] - view->x[b];
    float dy = view->y[a] - view->y[b];
    return sqrtf(dx * dx + dy * dy);
}

int mn_view_fined(const mn_view* view, int p)
{
    if (!(p > 0 && p < view->count - 1)) {
        return 0;
    }
    return mn_turn_fined(view->x, view->y, view->cell_size, p >= 2 ? mn_view_node(view, p - 2) : -1, mn_view_node(view, p - 1), mn_view_node(view, p),
        mn_view_node(view, p + 1), p + 2 < view->count ? mn_view_node(view, p + 2) : -1);
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
