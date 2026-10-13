#ifndef MN_GPU_H
#define MN_GPU_H

/* GPU compute of the collision mode Backwards_GPU (T-175 to T-177): OpenCL loaded at run time from the
 * graphics driver (OpenCL.dll, libOpenCL.so.1; no SDK and no vendored package), the discrete GPU chosen
 * (a GPU device without memory shared with the host first, then the most compute units times clock),
 * the kernels built once per process without contraction and with correctly rounded division and
 * square root, so a kernel that ports a CPU loop gives its bits. Every call serializes on the shared
 * lock and returns a status with a message; there is no CPU path behind a GPU call. */

#include "mn_internal.h"

/* MN_OK and the device name (at most `capacity` bytes with the terminator), or why there is no GPU. */
int mn_gpu_ready(char* name, int capacity);

/* The closing of `in` over the profile's footprint (mn_remaining_compute on the GPU, the same bits). */
int mn_gpu_closing(const mn_grid* g, const float* in, const mn_profile* profile, float* out);

/* The session of one Backwards_GPU generation (T-183): opened when the reach map starts, it uploads
 * the tool profile at once and keeps on the GPU what later stages read again (the stock, the plan's
 * masks and levels); closed when the generation ends. One generation uses it from one thread at a
 * time; every call holds the process's GPU lock while it enqueues. */
typedef struct mn_gpu_session mn_gpu_session;
int mn_gpu_session_open(const mn_grid* g, const mn_profile* profile, mn_gpu_session** session);
void mn_gpu_session_close(mn_gpu_session* session);

/* The reach map (mn_reach_compute) on the GPU; the stock stays for the stages after it. */
int mn_gpu_reach(mn_gpu_session* session, const float* model, const float* stock, float floor, float percent, float tolerance, const mn_monitor* monitor, float* reach);

/* mn_separation_levels on the GPU: the plan's masks and the effective tip go up once, the separation's
 * masks stay as the session's (and come down into `masks`), the model region and the deepest region
 * level come down. */
int mn_gpu_separation_levels(mn_gpu_session* session, const mn_plan* plan, const float* effective_tip, const mn_tool* tool, float tolerance, float stock_top, float floor, uint8_t* masks,
    uint8_t* model_region, float* deepest);

/* The plan's masks and levels as the route stage reads them (after the islands and the bridges). */
int mn_gpu_masks(mn_gpu_session* session, const mn_plan* plan);

/* The intended surface (mn_intended_compute) from the session's masks, and the closing of a map with
 * the session's profile. */
int mn_gpu_intended(mn_gpu_session* session, const mn_plan* plan, float* intended);
int mn_gpu_closing_of(mn_gpu_session* session, const float* map, float* closing);

/* The backward check's sweeps on the GPU (mn_backwards.c) over the session's stock and masks: from the
 * deepest level of the plan's masks, every sweep tests all levels against the closing of the deepest
 * levels the sweep before reached, until a sweep changes nothing; `deepest` (cells) receives the settled
 * level indices, `closing` their closing (which the far go of a batch reads), `sweeps` their number. The
 * same result as the CPU sweeps. */
int mn_gpu_backward_settle(mn_gpu_session* session, const mn_context* context, int* deepest, float* closing, int* sweeps);

/* Start tours of the route solver (T-177, T-186, the user's idea: many combinations at once, the
 * cheapest kept): `tours` nearest-neighbour tours from `start`; tour 0 takes the
 * nearest free node in XY at every step, tour t > 0 the nearest under distances stretched by up to 25
 * percent by a fixed hash of (t, node), so the tours differ and every run gives the same ones; the fixed
 * `end` (-1 for none) goes last. begin launches them and returns at once (no job for fewer than two
 * nodes); end waits, puts the `keep` shortest in XY in `orders` (keep * n entries, ties to the lower
 * tour, at most MN_GPU_MAX_KEPT), their number in `kept`, and frees the job; cancel frees it unread. One
 * work-group per tour. */
#define MN_GPU_MAX_KEPT 16
typedef struct mn_gpu_tours_job mn_gpu_tours_job;
int mn_gpu_tours_begin(const float* x, const float* y, int n, int start, int end, int tours, mn_gpu_tours_job** job);
int mn_gpu_tours_end(mn_gpu_tours_job* job, int keep, int* orders, int* kept);
void mn_gpu_tours_cancel(mn_gpu_tours_job* job);
int mn_gpu_tours(const float* x, const float* y, int n, int start, int end, int tours, int keep, int* orders, int* kept);

#endif
