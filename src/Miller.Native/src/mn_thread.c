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
#include <time.h>
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

static int worker_limit = 0;

/* A cap for the stages a worker thread runs itself (T-167: a route unit runs its solver's start walks
 * inline while the other units occupy the processors); 0 for none. */
static _Thread_local int thread_cap = 0;

static _Thread_local double time_spent[MN_TIME_COUNT];

double mn_seconds(void)
{
#if defined(_WIN32)
    LARGE_INTEGER count;
    LARGE_INTEGER frequency;
    QueryPerformanceCounter(&count);
    QueryPerformanceFrequency(&frequency);
    return (double)count.QuadPart / (double)frequency.QuadPart;
#else
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (double)now.tv_sec + (double)now.tv_nsec * 1e-9;
#endif
}

void mn_time_add(int which, double seconds)
{
    if (which >= 0 && which < MN_TIME_COUNT) {
        time_spent[which] += seconds;
    }
}

void mn_time_take(double* into)
{
    for (int k = 0; k < MN_TIME_COUNT; k++) {
        if (into != NULL) {
            into[k] += time_spent[k];
        }
        time_spent[k] = 0.0;
    }
}

MN_API void mn_set_worker_limit(int32_t limit) { worker_limit = limit > 0 ? limit : 0; }

int mn_set_thread_cap(int cap)
{
    int previous = thread_cap;
    thread_cap = cap > 0 ? cap : 0;
    return previous;
}

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
    if (worker_limit > 0 && count > worker_limit) {
        count = worker_limit;
    }
    if (thread_cap > 0 && count > thread_cap) {
        count = thread_cap;
    }
    return count < 1 ? 1 : count > MN_MAX_WORKERS ? MN_MAX_WORKERS : count;
}

MN_API int32_t mn_workers(void) { return mn_worker_count(); }

/* Runs fn for every row in [0, rows) on a pool of its own: the pool of one stage call. */
int mn_rows_parallel(int rows, mn_row_fn fn, void* context, void** scratch, int workers)
{
    if (rows <= 0) {
        return MN_OK;
    }
    if (workers <= 1 || rows == 1) {
        for (int row = 0; row < rows; row++) {
            fn(context, row, scratch != NULL ? scratch[0] : NULL);
        }
        return MN_OK;
    }
    mn_pool* pool;
    MN_CHECK(mn_pool_create(workers, scratch, &pool));
    mn_pool_run(pool, 0, rows, fn, context);
    mn_pool_destroy(pool);
    return MN_OK;
}

static void run_rows(mn_pool* pool, int index)
{
    for (int row = pool->first + index; row < pool->last; row += pool->workers) {
        pool->fn(pool->context, row, pool->scratch != NULL ? pool->scratch[index] : NULL);
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

#if defined(_WIN32)
static SRWLOCK shared = SRWLOCK_INIT;

void mn_shared_lock(void) { AcquireSRWLockExclusive(&shared); }

void mn_shared_unlock(void) { ReleaseSRWLockExclusive(&shared); }
#else
static pthread_mutex_t shared = PTHREAD_MUTEX_INITIALIZER;

void mn_shared_lock(void) { pthread_mutex_lock(&shared); }

void mn_shared_unlock(void) { pthread_mutex_unlock(&shared); }
#endif

struct mn_task {
    void (*fn)(void* argument);
    void* argument;
    mn_thread thread;
};

#if defined(_WIN32)
static DWORD WINAPI task_main(LPVOID argument)
{
    mn_task* task = (mn_task*)argument;
    task->fn(task->argument);
    return 0;
}
#else
static void* task_main(void* argument)
{
    mn_task* task = (mn_task*)argument;
    task->fn(task->argument);
    return NULL;
}
#endif

int mn_task_start(void (*fn)(void* argument), void* argument, mn_task** result)
{
    mn_task* task = (mn_task*)mn_alloc(1, sizeof(mn_task));
    if (task == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a task.");
    }
    task->fn = fn;
    task->argument = argument;
#if defined(_WIN32)
    task->thread = CreateThread(NULL, 0, task_main, task, 0, NULL);
    int ok = task->thread != NULL;
#else
    int ok = pthread_create(&task->thread, NULL, task_main, task) == 0;
#endif
    if (!ok) {
        free(task);
        return mn_fail(MN_ERR_STATE, "Could not start a task thread.");
    }
    *result = task;
    return MN_OK;
}

void mn_task_join(mn_task* task)
{
    if (task == NULL) {
        return;
    }
#if defined(_WIN32)
    WaitForSingleObject(task->thread, INFINITE);
    CloseHandle(task->thread);
#else
    pthread_join(task->thread, NULL);
#endif
    free(task);
}

struct mn_signal {
    mn_mutex mutex;
    mn_condition changed;
    int value[2];
};

int mn_signal_create(mn_signal** result)
{
    mn_signal* signal = (mn_signal*)mn_alloc(1, sizeof(mn_signal));
    if (signal == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a signal.");
    }
    mn_mutex_init(&signal->mutex);
    mn_condition_init(&signal->changed);
    *result = signal;
    return MN_OK;
}

void mn_signal_raise(mn_signal* signal, int which, int value)
{
    mn_lock(&signal->mutex);
    if (value > signal->value[which]) {
        signal->value[which] = value;
    }
    mn_wake_all(&signal->changed);
    mn_unlock(&signal->mutex);
}

int mn_signal_wait(mn_signal* signal, int which, int at_least)
{
    mn_lock(&signal->mutex);
    while (signal->value[which] < at_least) {
        mn_wait(&signal->changed, &signal->mutex);
    }
    int value = signal->value[which];
    mn_unlock(&signal->mutex);
    return value;
}

void mn_signal_destroy(mn_signal* signal)
{
    if (signal == NULL) {
        return;
    }
    mn_condition_destroy(&signal->changed);
    mn_mutex_destroy(&signal->mutex);
    free(signal);
}
