#include <stdarg.h>
#include <stdio.h>

#include "mn_core.h"

#define MN_ERROR_SIZE 512

static _Thread_local char mn_error_text[MN_ERROR_SIZE];

int mn_fail(int status, const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(mn_error_text, MN_ERROR_SIZE, format, args);
    va_end(args);
    return status;
}

MN_API const char* mn_last_error(void) { return mn_error_text; }

MN_API void mn_free(void* pointer) { free(pointer); }

/* Zeroed memory; NULL on overflow of count * size or when the system has none. */
void* mn_alloc(size_t count, size_t size)
{
    if (count == 0 || size == 0) {
        return calloc(1, 1);
    }
    if (count > SIZE_MAX / size) {
        return NULL;
    }
    return calloc(count, size);
}

int mn_map_create(mn_map* map, const mn_grid* g, float fill)
{
    map->g = *g;
    map->z = (float*)mn_alloc((size_t)g->width * (size_t)g->height, sizeof(float));
    if (map->z == NULL) {
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a %d x %d map.", g->width, g->height);
    }
    mn_map_fill(map, fill);
    return MN_OK;
}

int mn_map_clone(mn_map* copy, const mn_map* source)
{
    MN_CHECK(mn_map_create(copy, &source->g, 0.0f));
    memcpy(copy->z, source->z, (size_t)mn_cells(&source->g) * sizeof(float));
    return MN_OK;
}

void mn_map_free(mn_map* map)
{
    free(map->z);
    map->z = NULL;
}

void mn_map_fill(mn_map* map, float value)
{
    int n = mn_cells(&map->g);
    for (int k = 0; k < n; k++) {
        map->z[k] = value;
    }
}

/* HeightMap.Min / Max: NaN when every cell is NaN. */
float mn_map_min(const mn_map* map)
{
    float min = NAN;
    int n = mn_cells(&map->g);
    for (int k = 0; k < n; k++) {
        float z = map->z[k];
        if (!mn_isnan(z) && (mn_isnan(min) || z < min)) {
            min = z;
        }
    }
    return min;
}

float mn_map_max(const mn_map* map)
{
    float max = NAN;
    int n = mn_cells(&map->g);
    for (int k = 0; k < n; k++) {
        float z = map->z[k];
        if (!mn_isnan(z) && (mn_isnan(max) || z > max)) {
            max = z;
        }
    }
    return max;
}

int mn_ints_push(mn_ints* list, int value)
{
    if (list->count == list->capacity) {
        int capacity = list->capacity == 0 ? 16 : list->capacity * 2;
        int* items = (int*)realloc(list->items, (size_t)capacity * sizeof(int));
        if (items == NULL) {
            return mn_fail(MN_ERR_MEMORY, "Out of memory for a list of %d items.", capacity);
        }
        list->items = items;
        list->capacity = capacity;
    }
    list->items[list->count++] = value;
    return MN_OK;
}

void mn_ints_free(mn_ints* list)
{
    free(list->items);
    list->items = NULL;
    list->count = 0;
    list->capacity = 0;
}
