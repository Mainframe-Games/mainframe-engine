/*
 * mfmeshopt: a flat C ABI over meshoptimizer 1.3 (https://github.com/zeux/meshoptimizer, MIT) for the engine's generated
 * mesh levels of detail. Part of Mainframe Engine (docs/design/natives.md,
 * memory/decisions/0256-meshoptimizer-shim-for-generated-lods.md). Indices are 32-bit triangle lists; positions and
 * attributes are float arrays with a byte stride. Every function is thread-safe (no global state).
 */
#ifndef MFMESHOPT_H
#define MFMESHOPT_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#  ifdef MFMESHOPT_BUILDING
#    define MFMESHOPT_API __declspec(dllexport)
#  else
#    define MFMESHOPT_API __declspec(dllimport)
#  endif
#else
#  define MFMESHOPT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI version: major << 16 | minor. */
#define MFMESHOPT_ABI_VERSION ((1 << 16) | 0)

MFMESHOPT_API uint32_t mfmeshopt_abi_version(void);

/* meshoptimizer's MESHOPTIMIZER_VERSION the library was built with (1030 = 1.3). */
MFMESHOPT_API uint32_t mfmeshopt_library_version(void);

/* Simplify options (meshoptimizer's meshopt_Simplify* bits). */
#define MFMESHOPT_LOCK_BORDER (1u << 0)    /* keep open borders: no cracks between surfaces of one mesh */
#define MFMESHOPT_ERROR_ABSOLUTE (1u << 2) /* target and result errors in mesh units, not relative to the mesh size */
#define MFMESHOPT_PRUNE (1u << 3)          /* drop small disconnected parts */

/*
 * meshopt_simplifyWithAttributes: writes at most `index_count` indices of a coarser triangle list to `destination` and
 * returns their count (a multiple of 3, ≤ index_count; 0 for invalid arguments). Stops at `target_index_count` or when
 * the error would exceed `target_error`. `attributes` (may be NULL with `attribute_count` 0) holds `attribute_count`
 * floats per vertex weighted by `attribute_weights`. `*result_error` (may be NULL) receives the error reached. Only the
 * options above are accepted; others are cleared.
 */
MFMESHOPT_API size_t mfmeshopt_simplify(uint32_t *destination, const uint32_t *indices, size_t index_count,
	const float *positions, size_t vertex_count, size_t position_stride,
	const float *attributes, size_t attribute_stride, const float *attribute_weights, size_t attribute_count,
	size_t target_index_count, float target_error, uint32_t options, float *result_error);

/* meshopt_simplifyScale: the factor that turns a relative error into mesh units (the mesh's extent). */
MFMESHOPT_API float mfmeshopt_simplify_scale(const float *positions, size_t vertex_count, size_t position_stride);

/* meshopt_optimizeVertexCache: reorders a triangle list for the post-transform cache (`destination` may be `indices`). */
MFMESHOPT_API void mfmeshopt_optimize_vertex_cache(uint32_t *destination, const uint32_t *indices, size_t index_count, size_t vertex_count);

#ifdef __cplusplus
}
#endif

#endif /* MFMESHOPT_H */
