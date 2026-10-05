// mfsvg: Godot 4.7.2's ImageLoaderSVG::create_image_from_utf8_buffer over the same ThorVG (see include/mfsvg.h).
#include "mfsvg.h"

#include <thorvg.h>

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>

namespace
{
std::once_flag g_init_once;
bool g_initialized = false;

bool ensure_initialized()
{
	// Godot: tvg::Initializer::init(TVG_THREADS) with TVG_THREADS = 1 when threads are enabled.
	std::call_once(g_init_once, [] { g_initialized = tvg::Initializer::init(1) == tvg::Result::Success; });
	return g_initialized;
}

constexpr uint32_t max_dimension = 16384;
} // namespace

extern "C" MFSVG_API uint32_t mfsvg_abi_version(void)
{
	return MFSVG_ABI_VERSION;
}

extern "C" MFSVG_API int32_t mfsvg_size(const char *svg, size_t length, float *width, float *height)
{
	if (svg == nullptr || width == nullptr || height == nullptr || length == 0 || length > 0x7FFFFFFF)
		return MFSVG_ERROR_ARGUMENT;
	if (!ensure_initialized())
		return MFSVG_ERROR_INIT;
	tvg::Picture *picture = tvg::Picture::gen();
	if (picture->load(svg, static_cast<uint32_t>(length), "svg", nullptr, true) != tvg::Result::Success)
	{
		tvg::Paint::rel(picture);
		return MFSVG_ERROR_PARSE;
	}
	picture->size(width, height);
	tvg::Paint::rel(picture);
	return MFSVG_OK;
}

extern "C" MFSVG_API int32_t mfsvg_rasterize(const char *svg, size_t length, float scale, uint8_t **rgba, uint32_t *width, uint32_t *height)
{
	if (svg == nullptr || rgba == nullptr || width == nullptr || height == nullptr || length == 0 || length > 0x7FFFFFFF ||
		!(std::fabs(scale) > 0.00001f))
		return MFSVG_ERROR_ARGUMENT;
	*rgba = nullptr;
	*width = *height = 0;
	if (!ensure_initialized())
		return MFSVG_ERROR_INIT;

	tvg::Picture *picture = tvg::Picture::gen();
	if (picture->load(svg, static_cast<uint32_t>(length), "svg", nullptr, true) != tvg::Result::Success)
	{
		tvg::Paint::rel(picture);
		return MFSVG_ERROR_PARSE;
	}

	float fw = 0, fh = 0;
	picture->size(&fw, &fh);
	uint32_t w = static_cast<uint32_t>(std::fmax(1.0, std::round(fw * scale)));
	uint32_t h = static_cast<uint32_t>(std::fmax(1.0, std::round(fh * scale)));
	if (w > max_dimension)
		w = max_dimension;
	if (h > max_dimension)
		h = max_dimension;
	picture->size(static_cast<float>(w), static_cast<float>(h));

	auto *buffer = static_cast<uint8_t *>(std::calloc(static_cast<size_t>(w) * h, 4));
	if (buffer == nullptr)
	{
		tvg::Paint::rel(picture);
		return MFSVG_ERROR_RENDER;
	}

	std::unique_ptr<tvg::SwCanvas> canvas(tvg::SwCanvas::gen());
	if (canvas->target(reinterpret_cast<uint32_t *>(buffer), w, w, h, tvg::ColorSpace::ABGR8888S) != tvg::Result::Success)
	{
		tvg::Paint::rel(picture);
		std::free(buffer);
		return MFSVG_ERROR_RENDER;
	}

	// From here the canvas owns the picture.
	if (canvas->add(picture) != tvg::Result::Success)
	{
		std::free(buffer);
		return MFSVG_ERROR_RENDER;
	}

	if (canvas->draw(true) != tvg::Result::Success || canvas->sync() != tvg::Result::Success)
	{
		std::free(buffer);
		return MFSVG_ERROR_RENDER;
	}

	*rgba = buffer;
	*width = w;
	*height = h;
	return MFSVG_OK;
}

extern "C" MFSVG_API void mfsvg_free(uint8_t *rgba)
{
	std::free(rgba);
}
