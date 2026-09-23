#include "mn_core.h"
#include "mn_internal.h"

/* The tool as seen by the grid (ToolProfile): every cell offset under the cutter with the height of
 * the tool bottom above the tip, and the ring of cell offsets under the head only, with the height
 * of the head underside above the head bottom there. A cell centre displaced by (dx, dy) cells lies
 * at cell * sqrt(dx^2 + dy^2) from the axis. The cylinder head is flat underneath (height 0); the
 * frustum is flat inside its bottom radius and, when it widens upward, rises linearly to its length
 * at the top radius; wider cells are never under the head. */

MN_API float mn_bottom_height(int32_t tip_type, float radius, float distance)
{
    if (tip_type == MN_TIP_BALL) {
        float inside = mn_max(0.0f, radius * radius - distance * distance);
        return radius - sqrtf(inside);
    }
    return 0.0f;
}

float mn_head_radius(const mn_tool* tool)
{
    float bottom = tool->head_diameter / 2.0f;
    if (tool->head_shape == MN_HEAD_FRUSTUM) {
        return mn_max(bottom, tool->head_top_diameter / 2.0f);
    }
    return bottom;
}

/* Height of the head underside above the head bottom at lateral distance d inside the head radius. */
static float head_underside(const mn_tool* tool, float d)
{
    if (tool->head_shape != MN_HEAD_FRUSTUM) {
        return 0.0f;
    }
    float bottom = tool->head_diameter / 2.0f;
    float top = tool->head_top_diameter / 2.0f;
    if (d <= bottom + MN_RADIUS_TOLERANCE || !(top > bottom)) {
        return 0.0f;
    }
    float h = tool->head_length * (d - bottom) / (top - bottom);
    return mn_min(h, tool->head_length);
}

int mn_profile_build(const mn_tool* tool, float cell_size, mn_profile* profile)
{
    memset(profile, 0, sizeof(*profile));
    if (!(cell_size > 0)) {
        return mn_fail(MN_ERR_OUT_OF_RANGE, "Cell size must be positive.");
    }
    if (!(tool->cutter_diameter > 0)) {
        return mn_fail(MN_ERR_ARGUMENT, "Cutter diameter must be positive, got %g.", (double)tool->cutter_diameter);
    }

    float r = tool->cutter_diameter / 2.0f;
    float head = mn_head_radius(tool);
    int reach = mn_f2i(ceilf(mn_max(r, head) / cell_size));
    size_t side = (size_t)(2 * reach + 1);
    profile->offsets = (mn_offset*)mn_alloc(side * side, sizeof(mn_offset));
    profile->annulus = (mn_offset*)mn_alloc(side * side, sizeof(mn_offset));
    if (profile->offsets == NULL || profile->annulus == NULL) {
        mn_profile_free(profile);
        return mn_fail(MN_ERR_MEMORY, "Out of memory for a tool profile of %d cells.", (int)(side * side));
    }

    for (int dy = -reach; dy <= reach; dy++) {
        for (int dx = -reach; dx <= reach; dx++) {
            float d = cell_size * sqrtf((float)(dx * dx + dy * dy));
            if (d <= r + MN_RADIUS_TOLERANCE) {
                mn_offset* o = &profile->offsets[profile->offset_count++];
                o->dx = dx;
                o->dy = dy;
                o->dz = mn_bottom_height(tool->tip_type, r, d);
            } else if (d <= head + MN_RADIUS_TOLERANCE) {
                mn_offset* o = &profile->annulus[profile->annulus_count++];
                o->dx = dx;
                o->dy = dy;
                o->dz = head_underside(tool, d);
            }
        }
    }

    return MN_OK;
}

void mn_profile_free(mn_profile* profile)
{
    free(profile->offsets);
    free(profile->annulus);
    profile->offsets = NULL;
    profile->annulus = NULL;
    profile->offset_count = 0;
    profile->annulus_count = 0;
}

MN_API int32_t mn_profile_create(const mn_tool* tool, float cell_size, mn_offset** offsets, int32_t* offset_count, mn_offset** annulus, int32_t* annulus_count)
{
    mn_profile profile;
    MN_CHECK(mn_profile_build(tool, cell_size, &profile));
    *offsets = profile.offsets;
    *offset_count = profile.offset_count;
    *annulus = profile.annulus;
    *annulus_count = profile.annulus_count;
    return MN_OK;
}
