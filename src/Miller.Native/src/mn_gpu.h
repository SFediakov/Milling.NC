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

/* The backward check's sweeps on the GPU (mn_backwards.c): from the deepest level of the plan's masks,
 * every sweep tests all levels against the closing of the deepest levels the sweep before reached,
 * until a sweep changes nothing; `deepest` (cells) receives the settled level indices, `sweeps` their
 * number. The same result as the CPU sweeps. */
int mn_gpu_backward_settle(const mn_context* context, const mn_profile* profile, int* deepest, int* sweeps);

/* The route floors of one Backwards_GPU generation (lift_uncut in mn_strategies.c, guard off): open
 * uploads its stock, strategy tip and safe plane, lift turns one route floor (cells) into the lifted route
 * and touch floors (2 * cells), close releases them. Each generation holds its own. */
typedef struct mn_gpu_floors mn_gpu_floors;
int mn_gpu_floors_open(const float* stock, const float* tip, int cells, float safe, mn_gpu_floors** floors);
int mn_gpu_floors_lift(mn_gpu_floors* floors, const float* floor, int cells, float* lifted);
void mn_gpu_floors_close(mn_gpu_floors* floors);

/* Start tours of the route solver (T-177, the user's idea: many combinations at once, the cheapest
 * kept): `tours` nearest-neighbour tours from `start`, one work-group each; tour 0 takes the nearest free
 * node in XY at every step, tour t > 0 the nearest under distances stretched by up to 25 percent by a
 * fixed hash of (t, node), so the tours differ and every run gives the same ones; the fixed `end` (-1 for
 * none) goes last. `orders` receives the `keep` shortest in XY (keep * n entries, ties to the lower tour)
 * and `kept` how many. */
int mn_gpu_tours(const float* x, const float* y, int n, int start, int end, int tours, int keep, int* orders, int* kept);

#endif
