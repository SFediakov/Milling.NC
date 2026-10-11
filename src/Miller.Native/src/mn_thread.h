#ifndef MN_THREAD_H
#define MN_THREAD_H

#define MN_MAX_WORKERS 64

typedef void (*mn_row_fn)(void* context, int row, void* scratch);

typedef struct mn_pool mn_pool;

int mn_worker_count(void);

/* Caps mn_worker_count on the calling thread (0 removes the cap); returns the cap it replaces. */
int mn_set_thread_cap(int cap);

/* Starts workers - 1 threads; scratch[w] is handed to every row worker w runs (worker 0 is the
 * calling thread of mn_pool_run). Fails when a thread cannot be started. */
int mn_pool_create(int workers, void** scratch, mn_pool** pool);

/* Runs fn for every row in [first, last) on all workers and returns when every row is done. */
void mn_pool_run(mn_pool* pool, int first, int last, mn_row_fn fn, void* context);

void mn_pool_destroy(mn_pool* pool);

/* Runs fn for every row in [0, rows) on `workers` threads (a pool for this call; one worker or one row
 * runs inline); scratch may be NULL or hold `workers` entries. */
int mn_rows_parallel(int rows, mn_row_fn fn, void* context, void** scratch, int workers);

/* One function on a thread of its own (T-170: the backward check beside the route solver). */
typedef struct mn_task mn_task;
int mn_task_start(void (*fn)(void* argument), void* argument, mn_task** task);
void mn_task_join(mn_task* task);

/* Two counters that only grow, for one producer and one consumer: raise sets a counter to at least
 * `value` and wakes the waiting side; wait returns once the counter reaches `at_least`. */
#define MN_SIGNAL_DONE 0x3fffffff
typedef struct mn_signal mn_signal;
int mn_signal_create(mn_signal** signal);
void mn_signal_raise(mn_signal* signal, int which, int value);
int mn_signal_wait(mn_signal* signal, int which, int at_least);
void mn_signal_destroy(mn_signal* signal);

#endif
