#include "mn_window.h"

/* Orders the nodes of a route problem into one open path from a given start (RouteSolver): two start
 * walks over candidate lists (the 10 planar-nearest nodes of each node with their exact costs), the
 * nearest-neighbour walk and the smooth walk that also weighs the fine of the next turn; the cheaper
 * one is improved by 2-opt and Or-opt until no move improves or the allowance is spent. */

#define MN_CANDIDATES 10
#define MN_TARGET_PER_BUCKET 4.0f
#define MN_MIN_GAIN 1e-5f
#define MN_CANCEL_STRIDE (1 << 16)
#define MN_MAX_SEGMENT 3
#define MN_MIN_CACHE_BITS 12
#define MN_MAX_CACHE_BITS 20
#define MN_MAX_CHANGED 24

/* ---- spatial buckets ---- */

typedef struct scratch_item {
    float distance;
    int node;
} scratch_item;

typedef struct mn_buckets {
    const mn_problem* problem;
    float min_x;
    float min_y;
    float side;
    int cols;
    int rows;
    int* start;
    int* items;
    int* alive;
    scratch_item* scratch;
    int scratch_count;
    int scratch_capacity;
} mn_buckets;

static int bucket_col(const mn_buckets* b, float x) { return mn_clampi(mn_f2i((x - b->min_x) / b->side), 0, b->cols - 1); }

static int bucket_row(const mn_buckets* b, float y) { return mn_clampi(mn_f2i((y - b->min_y) / b->side), 0, b->rows - 1); }

static int bucket_of(const mn_buckets* b, int node) { return bucket_row(b, b->problem->y[node]) * b->cols + bucket_col(b, b->problem->x[node]); }

static void buckets_free(mn_buckets* b)
{
    free(b->start);
    free(b->items);
    free(b->alive);
    free(b->scratch);
    memset(b, 0, sizeof(*b));
}

static int buckets_init(mn_buckets* b, const mn_problem* problem)
{
    memset(b, 0, sizeof(*b));
    b->problem = problem;
    int n = problem->count;
    float min_x = INFINITY, min_y = INFINITY, max_x = -INFINITY, max_y = -INFINITY;
    for (int k = 0; k < n; k++) {
        min_x = mn_min(min_x, problem->x[k]);
        max_x = mn_max(max_x, problem->x[k]);
        min_y = mn_min(min_y, problem->y[k]);
        max_y = mn_max(max_y, problem->y[k]);
    }
    float cell = problem->grid.g.cell_size;
    float extent_x = mn_max(max_x - min_x, cell);
    float extent_y = mn_max(max_y - min_y, cell);
    b->side = mn_max(sqrtf(extent_x * extent_y * MN_TARGET_PER_BUCKET / (float)n), cell);
    b->min_x = min_x;
    b->min_y = min_y;
    b->cols = mn_f2i(extent_x / b->side) + 1;
    b->rows = mn_f2i(extent_y / b->side) + 1;
    int buckets = b->cols * b->rows;
    b->start = (int*)mn_alloc((size_t)buckets + 1, sizeof(int));
    b->items = (int*)mn_alloc((size_t)n, sizeof(int));
    b->alive = (int*)mn_alloc((size_t)buckets, sizeof(int));
    int* fill = (int*)mn_alloc((size_t)buckets, sizeof(int));
    b->scratch_capacity = 64;
    b->scratch = (scratch_item*)mn_alloc((size_t)b->scratch_capacity, sizeof(scratch_item));
    if (b->start == NULL || b->items == NULL || b->alive == NULL || fill == NULL || b->scratch == NULL) {
        free(fill);
        buckets_free(b);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the spatial buckets.");
    }
    for (int k = 0; k < n; k++) {
        b->start[bucket_of(b, k) + 1]++;
    }
    for (int k = 0; k < buckets; k++) {
        b->start[k + 1] += b->start[k];
        b->alive[k] = b->start[k + 1] - b->start[k];
    }
    for (int k = 0; k < n; k++) {
        int bucket = bucket_of(b, k);
        b->items[b->start[bucket] + fill[bucket]++] = k;
    }
    free(fill);
    return MN_OK;
}

static int scratch_add(mn_buckets* b, float distance, int node)
{
    if (b->scratch_count == b->scratch_capacity) {
        int capacity = b->scratch_capacity * 2;
        scratch_item* items = (scratch_item*)realloc(b->scratch, (size_t)capacity * sizeof(scratch_item));
        if (items == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for a ring search.");
        }
        b->scratch = items;
        b->scratch_capacity = capacity;
    }
    b->scratch[b->scratch_count].distance = distance;
    b->scratch[b->scratch_count].node = node;
    b->scratch_count++;
    return MN_OK;
}

/* Appends the nodes of ring r (excluding `self` and, with `visited`, visited nodes); 0 when all four
 * sides of the ring lie outside the grid, -1 on failure. */
static int ring(mn_buckets* b, int col, int row, int r, float x, float y, int self, const uint8_t* visited)
{
    int c0 = col - r;
    int c1 = col + r;
    int r0 = row - r;
    int r1 = row + r;
    if (c0 < 0 && r0 < 0 && c1 >= b->cols && r1 >= b->rows) {
        return 0;
    }
    for (int br = mn_maxi(r0, 0); br <= mn_mini(r1, b->rows - 1); br++) {
        int edge_row = br == r0 || br == r1;
        for (int bc = mn_maxi(c0, 0); bc <= mn_mini(c1, b->cols - 1); bc++) {
            if (!edge_row && bc != c0 && bc != c1) {
                continue;
            }
            int bucket = br * b->cols + bc;
            if (visited != NULL && b->alive[bucket] == 0) {
                continue;
            }
            for (int m = b->start[bucket]; m < b->start[bucket + 1]; m++) {
                int node = b->items[m];
                if (node == self || (visited != NULL && visited[node])) {
                    continue;
                }
                float dx = b->problem->x[node] - x;
                float dy = b->problem->y[node] - y;
                if (scratch_add(b, sqrtf(dx * dx + dy * dy), node) != MN_OK) {
                    return -1;
                }
            }
        }
    }
    return 1;
}

static int compare_items(const void* left, const void* right)
{
    const scratch_item* p = (const scratch_item*)left;
    const scratch_item* q = (const scratch_item*)right;
    if (p->distance != q->distance) {
        return p->distance < q->distance ? -1 : 1;
    }
    return p->node < q->node ? -1 : p->node > q->node ? 1 : 0;
}

/* The k nearest other nodes by planar distance, nearest first. */
static int buckets_nearest(mn_buckets* b, int node, int k, int* nodes, int* found)
{
    float x = b->problem->x[node];
    float y = b->problem->y[node];
    int col = bucket_col(b, x);
    int row = bucket_row(b, y);
    b->scratch_count = 0;
    for (int r = 0;; r++) {
        int status = ring(b, col, row, r, x, y, node, NULL);
        if (status < 0) {
            return MN_ERR_MEMORY;
        }
        if (status == 0) {
            break;
        }
        if (b->scratch_count >= k) {
            qsort(b->scratch, (size_t)b->scratch_count, sizeof(scratch_item), compare_items);
            if ((float)r * b->side >= b->scratch[k - 1].distance) {
                break;
            }
        }
    }
    qsort(b->scratch, (size_t)b->scratch_count, sizeof(scratch_item), compare_items);
    int count = mn_mini(k, b->scratch_count);
    for (int m = 0; m < count; m++) {
        nodes[m] = b->scratch[m].node;
    }
    *found = count;
    return MN_OK;
}

/* The unvisited node with the smallest lower-bound cost from the point, or -1. */
static int buckets_nearest_alive(mn_buckets* b, mn_v3 from, const uint8_t* visited, int* best_node)
{
    int col = bucket_col(b, from.x);
    int row = bucket_row(b, from.y);
    int best = -1;
    float best_cost = INFINITY;
    for (int r = 0;; r++) {
        b->scratch_count = 0;
        int status = ring(b, col, row, r, from.x, from.y, -1, visited);
        if (status < 0) {
            return MN_ERR_MEMORY;
        }
        if (status == 0) {
            break;
        }
        for (int m = 0; m < b->scratch_count; m++) {
            int node = b->scratch[m].node;
            float cost = mn_cost_lower_bound(from, mn_problem_node(b->problem, node));
            if (cost < best_cost || (cost == best_cost && node < best)) {
                best_cost = cost;
                best = node;
            }
        }
        if (best >= 0 && (float)r * b->side / MN_XY_SPEED_FACTOR >= best_cost) {
            break;
        }
    }
    *best_node = best;
    return MN_OK;
}

static void buckets_remove(mn_buckets* b, int node) { b->alive[bucket_of(b, node)]--; }

/* ---- candidate lists ---- */

typedef struct mn_candidates {
    int k;
    int* nodes;
    float* costs;
} mn_candidates;

static void candidates_free(mn_candidates* c)
{
    free(c->nodes);
    free(c->costs);
    c->nodes = NULL;
    c->costs = NULL;
}

static int candidates_init(mn_candidates* c, const mn_problem* problem, mn_buckets* buckets, int k)
{
    int n = problem->count;
    c->k = k;
    c->nodes = (int*)mn_alloc((size_t)n * (size_t)k, sizeof(int));
    c->costs = (float*)mn_alloc((size_t)n * (size_t)k, sizeof(float));
    if (c->nodes == NULL || c->costs == NULL) {
        candidates_free(c);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the candidate lists.");
    }
    for (int m = 0; m < n * k; m++) {
        c->nodes[m] = -1;
    }
    int nearest[MN_CANDIDATES];
    for (int node = 0; node < n; node++) {
        int count;
        if (buckets_nearest(buckets, node, k, nearest, &count) != MN_OK) {
            candidates_free(c);
            return mn_fail(MN_ERR_MEMORY, "Out of memory for the candidate lists.");
        }
        mn_v3 from = mn_problem_node(problem, node);
        for (int m = 0; m < count; m++) {
            c->nodes[node * k + m] = nearest[m];
            c->costs[node * k + m] = mn_cost_exact(&problem->grid, from, mn_problem_node(problem, nearest[m]));
        }
    }
    return MN_OK;
}

/* The cached cost when v is a candidate of u or u one of v; NaN otherwise. */
static float candidates_cached(const mn_candidates* c, int u, int v)
{
    const int* slots = c->nodes + u * c->k;
    for (int m = 0; m < c->k; m++) {
        if (slots[m] == v) {
            return c->costs[u * c->k + m];
        }
    }
    slots = c->nodes + v * c->k;
    for (int m = 0; m < c->k; m++) {
        if (slots[m] == u) {
            return c->costs[v * c->k + m];
        }
    }
    return NAN;
}

/* ---- path cost ---- */

float mn_path_cost(const mn_problem* problem, const int* order, int count)
{
    float cost = 0.0f;
    for (int k = 1; k < count; k++) {
        cost += mn_cost_exact(&problem->grid, mn_problem_node(problem, order[k - 1]), mn_problem_node(problem, order[k]));
    }
    return cost + mn_turn_slow(problem->x, problem->y, problem->grid.g.cell_size, order, count) * mn_per_slow_millimetre();
}

/* ---- start walks ---- */

static int nearest_neighbour(const mn_problem* problem, mn_buckets* buckets, const mn_candidates* candidates, int start, int* order)
{
    int n = problem->count;
    uint8_t* visited = (uint8_t*)mn_alloc((size_t)n, 1);
    if (visited == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the nearest-neighbour walk.");
    }
    int current = start;
    order[0] = start;
    visited[start] = 1;
    buckets_remove(buckets, start);
    int status = MN_OK;
    for (int step = 1; step < n && status == MN_OK; step++) {
        int best = -1;
        float best_cost = INFINITY;
        const int* slots = candidates->nodes + current * candidates->k;
        const float* costs = candidates->costs + current * candidates->k;
        for (int m = 0; m < candidates->k; m++) {
            int c = slots[m];
            if (c < 0) {
                break;
            }
            if (!visited[c] && costs[m] < best_cost) {
                best_cost = costs[m];
                best = c;
            }
        }
        if (best < 0) {
            status = buckets_nearest_alive(buckets, mn_problem_node(problem, current), visited, &best);
            if (status != MN_OK) {
                break;
            }
        }
        order[step] = best;
        visited[best] = 1;
        buckets_remove(buckets, best);
        current = best;
    }
    free(visited);
    return status == MN_OK ? MN_OK : mn_fail(MN_ERR_MEMORY, "Out of memory for the nearest-neighbour walk.");
}

static float prefix_term(mn_window* window, mn_view* view, const int* order, const float* lengths, const mn_problem* problem, int end)
{
    mn_view_reset(view, order, lengths, problem->x, problem->y, problem->grid.g.cell_size);
    mn_view_add(view, 0, end, 0);
    return mn_window_term(window, view, 1);
}

/* Every candidate is scored by its step and the cheapest step after it, fine increase included; a
 * candidate whose own candidates are all visited by the lower bound to the nearest unvisited node. */
static int smooth_walk(const mn_problem* problem, mn_buckets* buckets, const mn_candidates* candidates, int start, int* order)
{
    int n = problem->count;
    int status = MN_OK;
    uint8_t* visited = (uint8_t*)mn_alloc((size_t)n, 1);
    uint8_t* fined = (uint8_t*)mn_alloc((size_t)n, 1);
    float* lengths = (float*)mn_alloc((size_t)n, sizeof(float));
    mn_window window;
    int window_ready = 0;
    if (visited == NULL || fined == NULL || lengths == NULL) {
        status = mn_fail(MN_ERR_MEMORY, "Out of memory for the smooth walk.");
        goto done;
    }
    status = mn_window_init(&window, fined, 4);
    if (status != MN_OK) {
        goto done;
    }
    window_ready = 1;
    mn_view view;
    int changed[4];
    float per_mm = mn_per_slow_millimetre();
    int current = start;
    order[0] = start;
    visited[start] = 1;
    buckets_remove(buckets, start);
    for (int step = 1; step < n; step++) {
        int count = 0;
        for (int p = mn_maxi(0, step - 2); p <= mn_mini(step + 1, n - 1); p++) {
            changed[count++] = p;
        }
        mn_window_prepare(&window, order, lengths, mn_mini(step + 2, n), changed, count);
        float before = prefix_term(&window, &view, order, lengths, problem, step - 1);
        int best = -1;
        float best_value = INFINITY;
        const int* slots = candidates->nodes + current * candidates->k;
        const float* costs = candidates->costs + current * candidates->k;
        for (int m = 0; m < candidates->k; m++) {
            int c = slots[m];
            if (c < 0) {
                break;
            }
            if (visited[c]) {
                continue;
            }
            order[step] = c;
            lengths[step - 1] = mn_cost_planar(mn_problem_node(problem, current), mn_problem_node(problem, c));
            visited[c] = 1;
            float next = INFINITY;
            if (step + 1 < n) {
                const int* next_slots = candidates->nodes + c * candidates->k;
                const float* next_costs = candidates->costs + c * candidates->k;
                for (int m2 = 0; m2 < candidates->k; m2++) {
                    int d = next_slots[m2];
                    if (d < 0) {
                        break;
                    }
                    if (visited[d]) {
                        continue;
                    }
                    order[step + 1] = d;
                    lengths[step] = mn_cost_planar(mn_problem_node(problem, c), mn_problem_node(problem, d));
                    next = mn_min(next, next_costs[m2] + (prefix_term(&window, &view, order, lengths, problem, step + 1) - before) * per_mm);
                }
            }
            if (isinf(next) && next > 0) {
                int alive = -1;
                if (step + 1 < n) {
                    status = buckets_nearest_alive(buckets, mn_problem_node(problem, c), visited, &alive);
                    if (status != MN_OK) {
                        goto done;
                    }
                }
                next = (prefix_term(&window, &view, order, lengths, problem, step) - before) * per_mm
                    + (alive >= 0 ? mn_cost_lower_bound(mn_problem_node(problem, c), mn_problem_node(problem, alive)) : 0.0f);
            }
            visited[c] = 0;
            float value = costs[m] + next;
            if (value < best_value) {
                best_value = value;
                best = c;
            }
        }
        if (best < 0) {
            status = buckets_nearest_alive(buckets, mn_problem_node(problem, current), visited, &best);
            if (status != MN_OK) {
                goto done;
            }
        }
        order[step] = best;
        lengths[step - 1] = mn_cost_planar(mn_problem_node(problem, current), mn_problem_node(problem, best));
        visited[best] = 1;
        buckets_remove(buckets, best);
        if (step >= 3) {
            fined[order[step - 2]] = (uint8_t)mn_turn_fined(problem->x, problem->y, problem->grid.g.cell_size, step >= 4 ? order[step - 4] : -1, order[step - 3], order[step - 2],
                order[step - 1], best);
        }
        current = best;
    }

done:
    if (window_ready) {
        mn_window_free(&window);
    }
    free(visited);
    free(fined);
    free(lengths);
    return status;
}

/* ---- local search ---- */

typedef struct mn_search {
    const mn_problem* problem;
    const mn_candidates* candidates;
    int* order;
    int n;
    int* pos;
    float* edge;
    float* len;
    uint8_t* queued;
    int* queue;
    int queue_head;
    int queue_count;
    int64_t allowance;
    int64_t evaluations;
    const volatile int32_t* cancel;
    int cancelled;
    int64_t* cache_keys;
    float* cache_values;
    int cache_mask;
    uint8_t* fined;
    mn_window window;
    mn_view current;
    mn_view moved;
    int changed[MN_MAX_CHANGED];
    int changed_nodes[MN_MAX_CHANGED];
    int changed_count;
} mn_search;

static void push(mn_search* s, int node)
{
    if (!s->queued[node]) {
        s->queued[node] = 1;
        s->queue[(s->queue_head + s->queue_count) % s->n] = node;
        s->queue_count++;
    }
}

static int pop(mn_search* s)
{
    int node = s->queue[s->queue_head];
    s->queue_head = (s->queue_head + 1) % s->n;
    s->queue_count--;
    return node;
}

static float cost(mn_search* s, int u, int v)
{
    float cached = candidates_cached(s->candidates, u, v);
    if (!mn_isnan(cached)) {
        return cached;
    }
    int64_t key = u < v ? (((int64_t)u << 32) | (int64_t)(uint32_t)v) : (((int64_t)v << 32) | (int64_t)(uint32_t)u);
    int slot = (int)(((uint64_t)key * 0x9E3779B97F4A7C15ULL) >> 40) & s->cache_mask;
    if (s->cache_keys[slot] == key) {
        return s->cache_values[slot];
    }
    float value = mn_cost_exact(&s->problem->grid, mn_problem_node(s->problem, u), mn_problem_node(s->problem, v));
    s->cache_keys[slot] = key;
    s->cache_values[slot] = value;
    return value;
}

static float lower(const mn_search* s, int u, int v) { return mn_cost_lower_bound(mn_problem_node(s->problem, u), mn_problem_node(s->problem, v)); }

static int spend(mn_search* s)
{
    if (s->evaluations >= s->allowance) {
        return 0;
    }
    s->evaluations++;
    if ((s->evaluations & (MN_CANCEL_STRIDE - 1)) == 0 && s->cancel != NULL && *s->cancel != 0) {
        s->cancelled = 1;
        s->allowance = s->evaluations;
        return 0;
    }
    return 1;
}

static int fined_at(const mn_search* s, int k)
{
    if (!(k > 0 && k < s->n - 1)) {
        return 0;
    }
    return mn_turn_fined(s->problem->x, s->problem->y, s->problem->grid.g.cell_size, k >= 2 ? s->order[k - 2] : -1, s->order[k - 1], s->order[k], s->order[k + 1],
        k + 2 < s->n ? s->order[k + 2] : -1);
}

static void set_edge(mn_search* s, int k)
{
    if (k >= 0 && k + 1 < s->n) {
        s->edge[k] = cost(s, s->order[k], s->order[k + 1]);
        s->len[k] = mn_cost_planar(mn_problem_node(s->problem, s->order[k]), mn_problem_node(s->problem, s->order[k + 1]));
    }
}

static void reverse(mn_search* s, int l, int r)
{
    while (l < r) {
        int swap = s->order[l];
        s->order[l] = s->order[r];
        s->order[r] = swap;
        s->pos[s->order[l]] = l;
        s->pos[s->order[r]] = r;
        if (r - 1 > l) {
            float e = s->edge[l];
            s->edge[l] = s->edge[r - 1];
            s->edge[r - 1] = e;
            float g = s->len[l];
            s->len[l] = s->len[r - 1];
            s->len[r - 1] = g;
        }
        l++;
        r--;
    }
}

static void move_segment(mn_search* s, int i, int length, int t, int reversed)
{
    int last = i + length - 1;
    if (t > last) {
        if (reversed) {
            reverse(s, i, t);
            reverse(s, i, t - length);
        } else {
            reverse(s, i, last);
            reverse(s, last + 1, t);
            reverse(s, i, t);
        }
        set_edge(s, i - 1);
        set_edge(s, t - length);
        set_edge(s, t);
    } else {
        if (reversed) {
            reverse(s, t + 1, i - 1);
            reverse(s, t + 1, last);
        } else {
            reverse(s, t + 1, i - 1);
            reverse(s, i, last);
            reverse(s, t + 1, last);
        }
        set_edge(s, t);
        set_edge(s, t + length);
        set_edge(s, last);
    }
}

static void add_changed(mn_search* s, int position)
{
    s->changed[s->changed_count] = position;
    s->changed_nodes[s->changed_count] = s->order[position];
    s->changed_count++;
}

static void add_around(mn_search* s, int edge)
{
    if (edge < 0) {
        return;
    }
    for (int p = mn_maxi(edge - 1, 0); p <= mn_mini(edge + 2, s->n - 1); p++) {
        add_changed(s, p);
    }
}

/* Fine of the route in `moved` less the fine of the current route; the removed edges are given by
 * their positions in the current route (-1 for none), the added ones are the joins of `moved`. */
static float fine_change(mn_search* s, int removed_a, int removed_b, int removed_c)
{
    s->changed_count = 0;
    add_around(s, removed_a);
    add_around(s, removed_b);
    add_around(s, removed_c);
    for (int k = 0; k + 1 < s->moved.pieces; k++) {
        int e = mn_view_piece_end(&s->moved, k);
        for (int p = mn_maxi(e - 1, 0); p <= mn_mini(e + 2, s->n - 1); p++) {
            add_changed(s, mn_view_base(&s->moved, p));
        }
    }
    mn_window_prepare(&s->window, s->order, s->len, s->n, s->changed, s->changed_count);
    float after = mn_window_term(&s->window, &s->moved, 1);
    float before = mn_window_term(&s->window, &s->current, 0);
    return (after - before) * mn_per_slow_millimetre();
}

static void moved_reset(mn_search* s)
{
    mn_view_reset(&s->moved, s->order, s->len, s->problem->x, s->problem->y, s->problem->grid.g.cell_size);
}

static float reversal_fine(mn_search* s, int l, int r)
{
    moved_reset(s);
    mn_view_add(&s->moved, 0, l - 1, 0);
    mn_view_add(&s->moved, l, r, 1);
    mn_view_add(&s->moved, r + 1, s->n - 1, 0);
    return fine_change(s, l - 1, r + 1 < s->n ? r : -1, -1);
}

static float segment_fine(mn_search* s, int i, int length, int t, int reversed)
{
    int last = i + length - 1;
    moved_reset(s);
    if (t > last) {
        mn_view_add(&s->moved, 0, i - 1, 0);
        mn_view_add(&s->moved, last + 1, t, 0);
        mn_view_add(&s->moved, i, last, reversed);
        mn_view_add(&s->moved, t + 1, s->n - 1, 0);
        return fine_change(s, i - 1, last, t + 1 < s->n ? t : -1);
    }
    mn_view_add(&s->moved, 0, t, 0);
    mn_view_add(&s->moved, i, last, reversed);
    mn_view_add(&s->moved, t + 1, i - 1, 0);
    mn_view_add(&s->moved, last + 1, s->n - 1, 0);
    return fine_change(s, t, i - 1, last + 1 < s->n ? last : -1);
}

/* After a move: the status of every node the last fine_change named, which is the move just done. */
static void refresh_fined(mn_search* s)
{
    for (int k = 0; k < s->changed_count; k++) {
        int node = s->changed_nodes[k];
        s->fined[node] = (uint8_t)fined_at(s, s->pos[node]);
        push(s, node);
    }
}

static int two_opt(mn_search* s, int a)
{
    int i = s->pos[a];
    const int* slots = s->candidates->nodes + a * s->candidates->k;
    const float* costs = s->candidates->costs + a * s->candidates->k;
    for (int m = 0; m < s->candidates->k; m++) {
        int c = slots[m];
        if (c < 0) {
            break;
        }
        if (!spend(s)) {
            return 0;
        }
        float dac = costs[m];
        int j = s->pos[c];
        if (j > i + 1) {
            /* ..., a, s, ..., c, cn, ... becomes ..., a, c, ..., s, cn, ... */
            int sn = s->order[i + 1];
            int has_next = j + 1 < s->n;
            float removed = s->edge[i] + (has_next ? s->edge[j] : 0.0f);
            float fine = reversal_fine(s, i + 1, j);
            float added = 0.0f;
            if (has_next) {
                int cn = s->order[j + 1];
                if (removed - dac - fine - lower(s, sn, cn) <= MN_MIN_GAIN) {
                    continue;
                }
                added = cost(s, sn, cn);
            }
            if (removed - dac - added - fine > MN_MIN_GAIN) {
                reverse(s, i + 1, j);
                set_edge(s, i);
                set_edge(s, j);
                push(s, a);
                push(s, sn);
                push(s, c);
                if (has_next) {
                    push(s, s->order[j + 1]);
                }
                refresh_fined(s);
                return 1;
            }
        } else if (j + 1 < i) {
            /* ..., c, cs, ..., a, an, ... becomes ..., c, a, ..., cs, an, ... */
            int cs = s->order[j + 1];
            int has_next = i + 1 < s->n;
            float removed = s->edge[j] + (has_next ? s->edge[i] : 0.0f);
            float fine = reversal_fine(s, j + 1, i);
            float added = 0.0f;
            if (has_next) {
                int an = s->order[i + 1];
                if (removed - dac - fine - lower(s, cs, an) <= MN_MIN_GAIN) {
                    continue;
                }
                added = cost(s, cs, an);
            }
            if (removed - dac - added - fine > MN_MIN_GAIN) {
                reverse(s, j + 1, i);
                set_edge(s, j);
                set_edge(s, i);
                push(s, c);
                push(s, cs);
                push(s, a);
                if (has_next) {
                    push(s, s->order[i + 1]);
                }
                refresh_fined(s);
                return 1;
            }
        }
    }
    return 0;
}

static int or_opt(mn_search* s, int a)
{
    for (int length = 1; length <= MN_MAX_SEGMENT; length++) {
        int i = s->pos[a];
        if (i < 1 || i + length - 1 > s->n - 1) {
            continue;
        }
        int first = a;
        int last = s->order[i + length - 1];
        int p = s->order[i - 1];
        int has_next = i + length < s->n;
        int nx = has_next ? s->order[i + length] : -1;
        float removed = s->edge[i - 1] + (has_next ? s->edge[i + length - 1] : 0.0f);
        float bridge_lower = has_next ? lower(s, p, nx) : 0.0f;
        for (int end = 0; end < 2; end++) {
            int e = end == 0 ? first : last;
            int other = end == 0 ? last : first;
            const int* slots = s->candidates->nodes + e * s->candidates->k;
            const float* costs = s->candidates->costs + e * s->candidates->k;
            for (int m = 0; m < s->candidates->k; m++) {
                int c = slots[m];
                if (c < 0) {
                    break;
                }
                if (!spend(s)) {
                    return 0;
                }
                int jc = s->pos[c];
                if (jc >= i && jc <= i + length - 1) {
                    continue;
                }
                float dec = costs[m];
                if (c != p) {
                    /* ..., c, cs, ... becomes ..., c, e, ..., other, cs, ... */
                    int has_cs = jc + 1 < s->n;
                    int cs = has_cs ? s->order[jc + 1] : -1;
                    float removed_c = has_cs ? s->edge[jc] : 0.0f;
                    float fine = segment_fine(s, i, length, jc, end == 1);
                    if (removed + removed_c - bridge_lower - dec - fine - (has_cs ? lower(s, other, cs) : 0.0f) > MN_MIN_GAIN) {
                        float bridge = has_next ? cost(s, p, nx) : 0.0f;
                        float tail = has_cs ? cost(s, other, cs) : 0.0f;
                        if (removed + removed_c - bridge - dec - tail - fine > MN_MIN_GAIN) {
                            move_segment(s, i, length, jc, end == 1);
                            push(s, p);
                            push(s, c);
                            push(s, first);
                            push(s, last);
                            if (has_next) {
                                push(s, nx);
                            }
                            if (has_cs) {
                                push(s, cs);
                            }
                            refresh_fined(s);
                            return 1;
                        }
                    }
                }
                if (jc >= 1 && c != nx) {
                    /* ..., cp, c, ... becomes ..., cp, other, ..., e, c, ... */
                    int cp = s->order[jc - 1];
                    float removed_c = s->edge[jc - 1];
                    float fine = segment_fine(s, i, length, jc - 1, end == 0);
                    if (removed + removed_c - bridge_lower - dec - fine - lower(s, cp, other) > MN_MIN_GAIN) {
                        float bridge = has_next ? cost(s, p, nx) : 0.0f;
                        float head = cost(s, cp, other);
                        if (removed + removed_c - bridge - dec - head - fine > MN_MIN_GAIN) {
                            move_segment(s, i, length, jc - 1, end == 0);
                            push(s, p);
                            push(s, c);
                            push(s, cp);
                            push(s, first);
                            push(s, last);
                            if (has_next) {
                                push(s, nx);
                            }
                            refresh_fined(s);
                            return 1;
                        }
                    }
                }
            }
        }
    }
    return 0;
}

static void search_free(mn_search* s)
{
    free(s->pos);
    free(s->edge);
    free(s->len);
    free(s->queued);
    free(s->queue);
    free(s->cache_keys);
    free(s->cache_values);
    free(s->fined);
    mn_window_free(&s->window);
}

static int leading_zeros(uint64_t value)
{
    int count = 0;
    for (int bit = 63; bit >= 0 && ((value >> bit) & 1u) == 0; bit--) {
        count++;
    }
    return count;
}

static int local_search(const mn_problem* problem, const mn_candidates* candidates, int* order, int64_t allowance, const volatile int32_t* cancel, int64_t* evaluations, int* cancelled)
{
    mn_search s;
    memset(&s, 0, sizeof(s));
    s.problem = problem;
    s.candidates = candidates;
    s.order = order;
    s.n = problem->count;
    s.allowance = allowance;
    s.cancel = cancel;
    int n = s.n;
    int edges = mn_maxi(n - 1, 0);
    int bits = mn_clampi(64 - leading_zeros((uint64_t)mn_maxi(n * 4 - 1, 1)), MN_MIN_CACHE_BITS, MN_MAX_CACHE_BITS);
    s.pos = (int*)mn_alloc((size_t)n, sizeof(int));
    s.edge = (float*)mn_alloc((size_t)(edges > 0 ? edges : 1), sizeof(float));
    s.len = (float*)mn_alloc((size_t)(edges > 0 ? edges : 1), sizeof(float));
    s.queued = (uint8_t*)mn_alloc((size_t)n, 1);
    s.queue = (int*)mn_alloc((size_t)n, sizeof(int));
    s.cache_keys = (int64_t*)mn_alloc((size_t)1 << bits, sizeof(int64_t));
    s.cache_values = (float*)mn_alloc((size_t)1 << bits, sizeof(float));
    s.fined = (uint8_t*)mn_alloc((size_t)n, 1);
    s.cache_mask = (1 << bits) - 1;
    if (s.pos == NULL || s.edge == NULL || s.len == NULL || s.queued == NULL || s.queue == NULL || s.cache_keys == NULL || s.cache_values == NULL || s.fined == NULL) {
        search_free(&s);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for the local search of %d nodes.", n);
    }
    int status = mn_window_init(&s.window, s.fined, MN_MAX_CHANGED);
    if (status != MN_OK) {
        search_free(&s);
        return status;
    }
    for (int k = 0; k <= s.cache_mask; k++) {
        s.cache_keys[k] = -1;
    }
    mn_view_reset(&s.current, order, s.len, problem->x, problem->y, problem->grid.g.cell_size);
    mn_view_add(&s.current, 0, n - 1, 0);
    for (int k = 0; k < n; k++) {
        s.pos[order[k]] = k;
        if (k + 1 < n) {
            s.edge[k] = cost(&s, order[k], order[k + 1]);
            s.len[k] = mn_cost_planar(mn_problem_node(problem, order[k]), mn_problem_node(problem, order[k + 1]));
        }
        push(&s, order[k]);
    }
    for (int k = 0; k < n; k++) {
        s.fined[order[k]] = (uint8_t)fined_at(&s, k);
    }

    while (s.queue_count > 0 && s.evaluations < s.allowance) {
        int a = pop(&s);
        s.queued[a] = 0;
        while (s.evaluations < s.allowance && (two_opt(&s, a) || or_opt(&s, a))) {
        }
    }

    *evaluations = s.evaluations;
    *cancelled = s.cancelled;
    search_free(&s);
    return MN_OK;
}

/* ---- solve ---- */

int mn_solve(const mn_problem* problem, int start, int64_t allowance, const volatile int32_t* cancel, int* order, int64_t* evaluations)
{
    int n = problem->count;
    *evaluations = 0;
    if (n == 0) {
        return mn_fail(MN_ERR_ARGUMENT, "The problem has no nodes.");
    }
    if (start < 0 || start >= n) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Start must index one of the %d nodes.", n);
    }
    if (n == 1) {
        order[0] = start;
        return MN_OK;
    }

    int status = MN_OK;
    mn_buckets buckets;
    mn_candidates candidates = { 0 };
    int* smooth = (int*)mn_alloc((size_t)n, sizeof(int));
    if (smooth == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a route of %d nodes.", n);
    }
    status = buckets_init(&buckets, problem);
    if (status != MN_OK) {
        free(smooth);
        return status;
    }
    status = candidates_init(&candidates, problem, &buckets, MN_CANDIDATES);
    buckets_free(&buckets);
    if (status != MN_OK) {
        goto done;
    }
    if (cancel != NULL && *cancel != 0) {
        status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        goto done;
    }

    status = buckets_init(&buckets, problem);
    if (status != MN_OK) {
        goto done;
    }
    status = nearest_neighbour(problem, &buckets, &candidates, start, order);
    buckets_free(&buckets);
    if (status != MN_OK) {
        goto done;
    }
    if (cancel != NULL && *cancel != 0) {
        status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        goto done;
    }

    status = buckets_init(&buckets, problem);
    if (status != MN_OK) {
        goto done;
    }
    status = smooth_walk(problem, &buckets, &candidates, start, smooth);
    buckets_free(&buckets);
    if (status != MN_OK) {
        goto done;
    }
    if (cancel != NULL && *cancel != 0) {
        status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
        goto done;
    }

    if (mn_path_cost(problem, smooth, n) < mn_path_cost(problem, order, n)) {
        memcpy(order, smooth, (size_t)n * sizeof(int));
    }

    int cancelled = 0;
    status = local_search(problem, &candidates, order, allowance, cancel, evaluations, &cancelled);
    if (status == MN_OK && cancelled) {
        status = mn_fail(MN_ERR_CANCELLED, "Cancelled.");
    }

done:
    candidates_free(&candidates);
    free(smooth);
    return status;
}

MN_API int32_t mn_route_solve(const mn_grid* grid, const float* floor, const float* x, const float* y, const float* z, int32_t count, int32_t start, int64_t allowance, const volatile int32_t* cancel, int32_t* order, int64_t* evaluations)
{
    mn_problem problem;
    problem.grid.g = *grid;
    problem.grid.floor = floor;
    problem.x = x;
    problem.y = y;
    problem.z = z;
    problem.count = count;
    return mn_solve(&problem, start, allowance, cancel, order, evaluations);
}

MN_API float mn_route_path_cost(const mn_grid* grid, const float* floor, const float* x, const float* y, const float* z, const int32_t* order, int32_t count)
{
    mn_problem problem;
    problem.grid.g = *grid;
    problem.grid.floor = floor;
    problem.x = x;
    problem.y = y;
    problem.z = z;
    problem.count = count;
    return mn_path_cost(&problem, order, count);
}

int64_t mn_share(const mn_budget* budget, int nodes, int64_t nodes_left)
{
    int64_t remaining = budget->total - budget->used;
    return nodes_left == 0 ? 0 : mn_d2l((double)remaining * (double)nodes / (double)nodes_left);
}

MN_API int64_t mn_budget_share(int64_t remaining, int32_t nodes, int64_t nodes_left)
{
    return nodes_left == 0 ? 0 : mn_d2l((double)remaining * (double)nodes / (double)nodes_left);
}
