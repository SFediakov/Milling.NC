#include "mn_core.h"
#include "mn_thread.h"

/* A pool of worker threads that runs the rows of one block at a time: worker w takes rows first + w,
 * first + w + workers, ... and the calling thread is worker 0. Every row writes its own cells only,
 * so the result does not depend on the split. The workers stay alive between blocks and wait on a
 * condition variable for the next block. */

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
typedef CRITICAL_SECTION mn_mutex;
typedef CONDITION_VARIABLE mn_condition;
typedef HANDLE mn_thread;
#define mn_mutex_init(m) InitializeCriticalSection(m)
#define mn_mutex_destroy(m) DeleteCriticalSection(m)
#define mn_lock(m) EnterCriticalSection(m)
#define mn_unlock(m) LeaveCriticalSection(m)
#define mn_condition_init(c) InitializeConditionVariable(c)
#define mn_condition_destroy(c) ((void)(c))
#define mn_wait(c, m) SleepConditionVariableCS(c, m, INFINITE)
#define mn_wake_all(c) WakeAllConditionVariable(c)
#else
#include <pthread.h>
#include <unistd.h>
typedef pthread_mutex_t mn_mutex;
typedef pthread_cond_t mn_condition;
typedef pthread_t mn_thread;
#define mn_mutex_init(m) pthread_mutex_init(m, NULL)
#define mn_mutex_destroy(m) pthread_mutex_destroy(m)
#define mn_lock(m) pthread_mutex_lock(m)
#define mn_unlock(m) pthread_mutex_unlock(m)
#define mn_condition_init(c) pthread_cond_init(c, NULL)
#define mn_condition_destroy(c) pthread_cond_destroy(c)
#define mn_wait(c, m) pthread_cond_wait(c, m)
#define mn_wake_all(c) pthread_cond_broadcast(c)
#endif

typedef struct mn_worker {
    mn_pool* pool;
    int index;
    mn_thread thread;
} mn_worker;

struct mn_pool {
    int workers;
    int started;
    mn_worker items[MN_MAX_WORKERS];
    void** scratch;
    mn_mutex mutex;
    mn_condition work;
    mn_condition done;
    long generation;
    int pending;
    int stop;
    mn_row_fn fn;
    void* context;
    int first;
    int last;
};

int mn_worker_count(void)
{
#if defined(_WIN32)
    SYSTEM_INFO info;
    GetSystemInfo(&info);
    int count = (int)info.dwNumberOfProcessors;
#else
    long online = sysconf(_SC_NPROCESSORS_ONLN);
    int count = online > 0 ? (int)online : 1;
#endif
    return count < 1 ? 1 : count > MN_MAX_WORKERS ? MN_MAX_WORKERS : count;
}

static void run_rows(mn_pool* pool, int index)
{
    for (int row = pool->first + index; row < pool->last; row += pool->workers) {
        pool->fn(pool->context, row, pool->scratch[index]);
    }
}

static void worker_loop(mn_worker* worker)
{
    mn_pool* pool = worker->pool;
    long seen = 0;
    for (;;) {
        mn_lock(&pool->mutex);
        while (!pool->stop && pool->generation == seen) {
            mn_wait(&pool->work, &pool->mutex);
        }
        if (pool->stop) {
            mn_unlock(&pool->mutex);
            return;
        }
        seen = pool->generation;
        mn_unlock(&pool->mutex);

        run_rows(pool, worker->index);

        mn_lock(&pool->mutex);
        if (--pool->pending == 0) {
            mn_wake_all(&pool->done);
        }
        mn_unlock(&pool->mutex);
    }
}

#if defined(_WIN32)
static DWORD WINAPI thread_main(LPVOID argument)
{
    worker_loop((mn_worker*)argument);
    return 0;
}
#else
static void* thread_main(void* argument)
{
    worker_loop((mn_worker*)argument);
    return NULL;
}
#endif

int mn_pool_create(int workers, void** scratch, mn_pool** result)
{
    mn_pool* pool = (mn_pool*)mn_alloc(1, sizeof(mn_pool));
    if (pool == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a worker pool.");
    }
    pool->workers = workers < 1 ? 1 : workers > MN_MAX_WORKERS ? MN_MAX_WORKERS : workers;
    pool->scratch = scratch;
    mn_mutex_init(&pool->mutex);
    mn_condition_init(&pool->work);
    mn_condition_init(&pool->done);
    for (int w = 1; w < pool->workers; w++) {
        mn_worker* worker = &pool->items[w];
        worker->pool = pool;
        worker->index = w;
#if defined(_WIN32)
        worker->thread = CreateThread(NULL, 0, thread_main, worker, 0, NULL);
        int ok = worker->thread != NULL;
#else
        int ok = pthread_create(&worker->thread, NULL, thread_main, worker) == 0;
#endif
        if (!ok) {
            mn_pool_destroy(pool);
            return mn_fail(MN_ERR_STATE, "Could not start worker thread %d of %d.", w, workers);
        }
        pool->started = w;
    }
    *result = pool;
    return MN_OK;
}

void mn_pool_run(mn_pool* pool, int first, int last, mn_row_fn fn, void* context)
{
    if (last <= first) {
        return;
    }
    mn_lock(&pool->mutex);
    pool->fn = fn;
    pool->context = context;
    pool->first = first;
    pool->last = last;
    pool->pending = pool->workers - 1;
    pool->generation++;
    mn_wake_all(&pool->work);
    mn_unlock(&pool->mutex);

    run_rows(pool, 0);

    mn_lock(&pool->mutex);
    while (pool->pending > 0) {
        mn_wait(&pool->done, &pool->mutex);
    }
    mn_unlock(&pool->mutex);
}

void mn_pool_destroy(mn_pool* pool)
{
    if (pool == NULL) {
        return;
    }
    mn_lock(&pool->mutex);
    pool->stop = 1;
    mn_wake_all(&pool->work);
    mn_unlock(&pool->mutex);
    for (int w = 1; w <= pool->started; w++) {
#if defined(_WIN32)
        WaitForSingleObject(pool->items[w].thread, INFINITE);
        CloseHandle(pool->items[w].thread);
#else
        pthread_join(pool->items[w].thread, NULL);
#endif
    }
    mn_condition_destroy(&pool->work);
    mn_condition_destroy(&pool->done);
    mn_mutex_destroy(&pool->mutex);
    free(pool);
}
