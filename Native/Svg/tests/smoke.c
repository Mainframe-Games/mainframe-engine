/* mfsvg smoke test: a 4×2 document with a red left half rasterises to the expected pixels at scale 1 and 2. */
#include "mfsvg.h"

#include <stdio.h>
#include <string.h>

static const char svg[] = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"2\" viewBox=\"0 0 4 2\">"
                          "<rect x=\"0\" y=\"0\" width=\"2\" height=\"2\" fill=\"#ff0000\"/></svg>";

int main(void)
{
	uint8_t *rgba = NULL;
	uint32_t w = 0, h = 0;
	float fw = 0, fh = 0;
	if (mfsvg_abi_version() != MFSVG_ABI_VERSION) { puts("abi mismatch"); return 1; }
	if (mfsvg_size(svg, strlen(svg), &fw, &fh) != MFSVG_OK || fw != 4.0f || fh != 2.0f) { puts("size"); return 1; }
	if (mfsvg_rasterize(svg, strlen(svg), 1.0f, &rgba, &w, &h) != MFSVG_OK || w != 4 || h != 2) { puts("rasterize"); return 1; }
	/* Pixel (0,0): opaque red; pixel (3,0): transparent. */
	if (rgba[0] != 255 || rgba[1] != 0 || rgba[2] != 0 || rgba[3] != 255) { printf("p0 %u %u %u %u\n", rgba[0], rgba[1], rgba[2], rgba[3]); return 1; }
	if (rgba[3 * 4 + 3] != 0) { puts("p3 alpha"); return 1; }
	mfsvg_free(rgba);
	if (mfsvg_rasterize(svg, strlen(svg), 2.0f, &rgba, &w, &h) != MFSVG_OK || w != 8 || h != 4) { puts("scale"); return 1; }
	mfsvg_free(rgba);
	if (mfsvg_rasterize("<svg", 4, 1.0f, &rgba, &w, &h) != MFSVG_ERROR_PARSE) { puts("bad svg"); return 1; }
	puts("mfsvg ok");
	return 0;
}
