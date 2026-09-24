#ifndef MN_WINDOW_H
#define MN_WINDOW_H

#include "mn_internal.h"

#define MN_MAX_PIECES 4

/* The turn at every position of a base order (cosine, side and whether it is defined) and the arc
 * test of the four positions from every start, kept by the route solver so that the fined status of
 * a node reads them instead of measuring again. Values are valid for base positions up to `until`
 * (a turn at k needs k + 1, an arc from q needs q + 3). */
typedef struct mn_turn_cache {
    float* cosine;
    signed char* side;
    uint8_t* defined;
    uint8_t* arc;
    int until;
} mn_turn_cache;

int mn_turn_cache_init(mn_turn_cache* cache, int n);
void mn_turn_cache_free(mn_turn_cache* cache);
void mn_turn_cache_turn(mn_turn_cache* cache, const float* x, const float* y, const int* order, int k);
void mn_turn_cache_arc(mn_turn_cache* cache, const float* x, const float* y, float cell_size, const int* order, int q);
/* The stored values after reversing base positions l..r of the order: a turn or an arc that lies
 * inside the range moves with its nodes, the side of a turn flips; the ones across an end of the
 * range must be measured again. */
void mn_turn_cache_reverse(mn_turn_cache* cache, int l, int r);

/* A route read as up to four pieces of a base order, each forward or reversed: the route a
 * candidate move would produce, without building it. A chord inside a piece is read from the XY
 * lengths of the base chords (lengths[k]: base position k to k + 1) when given; a join is measured.
 * A turn or an arc inside a piece is read from the cache when given. */
typedef struct mn_view {
    const int* base;
    const float* lengths;
    const float* x;
    const float* y;
    float cell_size;
    const mn_turn_cache* cache;
    int first[MN_MAX_PIECES];
    int length[MN_MAX_PIECES];
    int reversed[MN_MAX_PIECES];
    int at[MN_MAX_PIECES];
    int pieces;
    int count;
} mn_view;

void mn_view_reset(mn_view* view, const int* base, const float* lengths, const float* x, const float* y, float cell_size, const mn_turn_cache* cache);
void mn_view_add(mn_view* view, int first, int last, int reversed);
int mn_view_base(const mn_view* view, int p);
int mn_view_position(const mn_view* view, int base_position);
int mn_view_node(const mn_view* view, int p);
float mn_view_length(const mn_view* view, int p);
static inline int mn_view_piece_end(const mn_view* view, int piece) { return view->at[piece] + view->length[piece] - 1; }

/* The turn at a position: none (an end, a chord without direction or straight on), soft or sharp. */
enum { MN_TURN_NONE = 0, MN_TURN_SOFT = 1, MN_TURN_SHARP = 2 };
int mn_view_turn_kind(const mn_view* view, int p);
int mn_view_arc(const mn_view* view, int q);

/* The fined status of the node at position p (TurnFine, T-133 and T-139): a sharp turn, or a soft
 * turn of a compound turn, unless the node turns on a circular section of at least
 * MN_CIRCLE_MIN_LENGTH. The same for the reversed route. */
int mn_view_fined(const mn_view* view, int p);

/* An event at `distance` from where a walk started, with 1 zone for a fined node and 0 for a route
 * end; far when none lies within reach. */
typedef struct mn_walk {
    float distance;
    int zones;
    int far;
} mn_walk;

/* The part of a route's slow length that depends on a set of changed nodes: 2 x SlowZone for every
 * changed node that is fined, less the overlap of every two consecutive events whose stretch holds
 * a changed node. The stretches between changed nodes are walked once (prepare) and shared by
 * every route of the same base order. `fined` holds the status of every node of the base order. */
typedef struct mn_window {
    const uint8_t* fined;
    const int* base;
    const float* lengths;
    int count;
    int capacity;
    int* changed;
    int changed_count;
    int* positions;
    int* stretches;
    mn_walk* low;
    mn_walk* high;
    float* span;
    uint8_t* through;
} mn_window;

int mn_window_init(mn_window* window, const uint8_t* fined, int capacity);
void mn_window_free(mn_window* window);
void mn_window_prepare(mn_window* window, const int* base, const float* lengths, int count, const int* changed, int changed_count);
float mn_window_term(mn_window* window, const mn_view* view, int fresh);

float mn_per_slow_millimetre(void);

#endif
