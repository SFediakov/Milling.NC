#ifndef MN_THREAD_H
#define MN_THREAD_H

#define MN_MAX_WORKERS 64

typedef void (*mn_row_fn)(void* context, int row, void* scratch);

typedef struct mn_pool mn_pool;

int mn_worker_count(void);

/* Starts workers - 1 threads; scratch[w] is handed to every row worker w runs (worker 0 is the
 * calling thread of mn_pool_run). Fails when a thread cannot be started. */
int mn_pool_create(int workers, void** scratch, mn_pool** pool);

/* Runs fn for every row in [first, last) on all workers and returns when every row is done. */
void mn_pool_run(mn_pool* pool, int first, int last, mn_row_fn fn, void* context);

void mn_pool_destroy(mn_pool* pool);

#endif
