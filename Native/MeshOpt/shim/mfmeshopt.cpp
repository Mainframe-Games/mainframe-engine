// mfmeshopt: the C ABI in include/mfmeshopt.h over meshoptimizer (external/meshoptimizer, compiled into this library).
#include "mfmeshopt.h"

#include "meshoptimizer.h"

namespace
{
constexpr uint32_t AcceptedOptions = MFMESHOPT_LOCK_BORDER | MFMESHOPT_ERROR_ABSOLUTE | MFMESHOPT_PRUNE;

static_assert(MFMESHOPT_LOCK_BORDER == meshopt_SimplifyLockBorder, "option bits follow meshoptimizer");
static_assert(MFMESHOPT_ERROR_ABSOLUTE == meshopt_SimplifyErrorAbsolute, "option bits follow meshoptimizer");
static_assert(MFMESHOPT_PRUNE == meshopt_SimplifyPrune, "option bits follow meshoptimizer");

// meshoptimizer asserts on these; a bad call from managed code must return 0 instead of aborting.
bool ValidStride(size_t stride, size_t minimum)
{
	return stride >= minimum && stride <= 256 && stride % sizeof(float) == 0;
}
} // namespace

extern "C" {

MFMESHOPT_API uint32_t mfmeshopt_abi_version(void)
{
	return MFMESHOPT_ABI_VERSION;
}

MFMESHOPT_API uint32_t mfmeshopt_library_version(void)
{
	return MESHOPTIMIZER_VERSION;
}

MFMESHOPT_API size_t mfmeshopt_simplify(uint32_t *destination, const uint32_t *indices, size_t index_count,
	const float *positions, size_t vertex_count, size_t position_stride,
	const float *attributes, size_t attribute_stride, const float *attribute_weights, size_t attribute_count,
	size_t target_index_count, float target_error, uint32_t options, float *result_error)
{
	if (result_error != nullptr)
		*result_error = 0.0f;
	if (destination == nullptr || indices == nullptr || positions == nullptr || index_count % 3 != 0 ||
		target_index_count > index_count || !ValidStride(position_stride, 3 * sizeof(float)) || attribute_count > 32 ||
		(attribute_count > 0 && (attributes == nullptr || attribute_weights == nullptr ||
								 !ValidStride(attribute_stride, attribute_count * sizeof(float)))))
		return 0;
	for (size_t i = 0; i < index_count; ++i)
		if (indices[i] >= vertex_count)
			return 0;

	options &= AcceptedOptions;
	if (attribute_count == 0)
		return meshopt_simplify(destination, indices, index_count, positions, vertex_count, position_stride,
			target_index_count, target_error, options, result_error);
	return meshopt_simplifyWithAttributes(destination, indices, index_count, positions, vertex_count, position_stride,
		attributes, attribute_stride, attribute_weights, attribute_count, nullptr, target_index_count, target_error, options,
		result_error);
}

MFMESHOPT_API float mfmeshopt_simplify_scale(const float *positions, size_t vertex_count, size_t position_stride)
{
	if (positions == nullptr || !ValidStride(position_stride, 3 * sizeof(float)))
		return 0.0f;
	return meshopt_simplifyScale(positions, vertex_count, position_stride);
}

MFMESHOPT_API void mfmeshopt_optimize_vertex_cache(uint32_t *destination, const uint32_t *indices, size_t index_count, size_t vertex_count)
{
	if (destination == nullptr || indices == nullptr || index_count % 3 != 0)
		return;
	for (size_t i = 0; i < index_count; ++i)
		if (indices[i] >= vertex_count)
			return;
	meshopt_optimizeVertexCache(destination, indices, index_count, vertex_count);
}

} // extern "C"
