/*
 * mfsvg: a flat C ABI over ThorVG 1.0.3 (the copy Godot 4.7.2 vendors, thirdparty/thorvg) that rasterises SVG documents
 * exactly as Godot's ImageLoaderSVG does: ThorVG's software canvas, straight-alpha RGBA8 (ABGR8888S), the picture
 * resized to round(size × scale). Part of Mainframe Engine (docs/design/natives.md, memory/decisions/0112-svg-via-thorvg.md).
 */
#ifndef MFSVG_H
#define MFSVG_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#  ifdef MFSVG_BUILDING
#    define MFSVG_API __declspec(dllexport)
#  else
#    define MFSVG_API __declspec(dllimport)
#  endif
#else
#  define MFSVG_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ABI version: major << 16 | minor. */
#define MFSVG_ABI_VERSION ((1 << 16) | 0)

MFSVG_API uint32_t mfsvg_abi_version(void);

/* Result codes. */
#define MFSVG_OK 0
#define MFSVG_ERROR_INIT 1
#define MFSVG_ERROR_PARSE 2
#define MFSVG_ERROR_RENDER 3
#define MFSVG_ERROR_ARGUMENT 4

/*
 * Rasterises an SVG document (UTF-8, not NUL-terminated) at `scale` into a new straight-alpha RGBA8 buffer, rows top to
 * bottom, `*width` × `*height` pixels (each at least 1, at most 16384). Free the buffer with mfsvg_free. Thread-safe.
 */
MFSVG_API int32_t mfsvg_rasterize(const char *svg, size_t length, float scale, uint8_t **rgba, uint32_t *width, uint32_t *height);

/* The document's natural size (ThorVG's picture size) without rasterising. */
MFSVG_API int32_t mfsvg_size(const char *svg, size_t length, float *width, float *height);

MFSVG_API void mfsvg_free(uint8_t *rgba);

#ifdef __cplusplus
}
#endif

#endif /* MFSVG_H */
