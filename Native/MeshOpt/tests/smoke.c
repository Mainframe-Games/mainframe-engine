/* mfmeshopt smoke test: a flat 16 × 16 grid simplifies far below half its triangles, a bumpy one keeps a positive error,
 * locked borders keep the outline, invalid arguments return 0, and the vertex cache order keeps every triangle. */
#include "mfmeshopt.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

#define N 16
#define VERTS ((N + 1) * (N + 1))
#define INDICES (N * N * 6)

static float positions[VERTS * 3];
static uint32_t indices[INDICES];
static uint32_t out[INDICES];

static void grid(float bump)
{
	for (int z = 0; z <= N; ++z)
		for (int x = 0; x <= N; ++x)
		{
			float *p = &positions[(z * (N + 1) + x) * 3];
			p[0] = (float)x;
			p[1] = bump * sinf((float)x * 0.9f) * cosf((float)z * 0.7f);
			p[2] = (float)z;
		}
	int i = 0;
	for (int z = 0; z < N; ++z)
		for (int x = 0; x < N; ++x)
		{
			uint32_t a = (uint32_t)(z * (N + 1) + x), b = a + 1, c = a + N + 1, d = c + 1;
			indices[i++] = a; indices[i++] = c; indices[i++] = b;
			indices[i++] = b; indices[i++] = c; indices[i++] = d;
		}
}

static int border_vertex(uint32_t v)
{
	uint32_t x = v % (N + 1), z = v / (N + 1);
	return x == 0 || z == 0 || x == N || z == N;
}

int main(void)
{
	float error = -1.0f;
	if (mfmeshopt_abi_version() != MFMESHOPT_ABI_VERSION) { puts("abi mismatch"); return 1; }
	if (mfmeshopt_library_version() != 1030) { printf("library %u\n", mfmeshopt_library_version()); return 1; }

	grid(0.0f);
	if (fabsf(mfmeshopt_simplify_scale(positions, VERTS, 12) - (float)N) > 1e-4f) { puts("scale"); return 1; }
	size_t flat = mfmeshopt_simplify(out, indices, INDICES, positions, VERTS, 12, NULL, 0, NULL, 0, 6, 1e9f, MFMESHOPT_ERROR_ABSOLUTE, &error);
	if (flat == 0 || flat % 3 != 0 || flat > INDICES / 8 || error > 1e-2f) { printf("flat %zu error %g\n", flat, error); return 1; }

	size_t locked = mfmeshopt_simplify(out, indices, INDICES, positions, VERTS, 12, NULL, 0, NULL, 0, 6, 1e9f,
		MFMESHOPT_ERROR_ABSOLUTE | MFMESHOPT_LOCK_BORDER, &error);
	int seen[VERTS] = {0};
	for (size_t i = 0; i < locked; ++i) seen[out[i]] = 1;
	for (uint32_t v = 0; v < VERTS; ++v)
		if (border_vertex(v) && !seen[v]) { printf("border vertex %u dropped\n", v); return 1; }
	if (locked <= flat || locked >= INDICES) { printf("locked %zu\n", locked); return 1; }

	grid(0.5f);
	float normals[VERTS * 3];
	for (int v = 0; v < VERTS; ++v) { normals[v * 3] = 0.0f; normals[v * 3 + 1] = 1.0f; normals[v * 3 + 2] = 0.0f; }
	const float weights[3] = {1.0f, 1.0f, 1.0f};
	size_t bumpy = mfmeshopt_simplify(out, indices, INDICES, positions, VERTS, 12, normals, 12, weights, 3, INDICES / 2, 1e9f,
		MFMESHOPT_ERROR_ABSOLUTE, &error);
	if (bumpy == 0 || bumpy > INDICES / 2 || !(error > 0.0f)) { printf("bumpy %zu error %g\n", bumpy, error); return 1; }

	mfmeshopt_optimize_vertex_cache(out, indices, INDICES, VERTS);
	unsigned long long a = 0, b = 0;
	for (int i = 0; i < INDICES; ++i) { a += indices[i] * 2654435761ull; b += out[i] * 2654435761ull; }
	if (a != b) { puts("vertex cache order lost indices"); return 1; }

	indices[0] = VERTS; /* out of range */
	if (mfmeshopt_simplify(out, indices, INDICES, positions, VERTS, 12, NULL, 0, NULL, 0, 6, 1e9f, 0, &error) != 0) { puts("range"); return 1; }
	if (mfmeshopt_simplify(out, indices, 5, positions, VERTS, 12, NULL, 0, NULL, 0, 3, 1e9f, 0, &error) != 0) { puts("count"); return 1; }
	if (mfmeshopt_simplify(out, indices, INDICES, positions, VERTS, 8, NULL, 0, NULL, 0, 6, 1e9f, 0, &error) != 0) { puts("stride"); return 1; }

	printf("mfmeshopt ok (flat %zu, locked %zu, bumpy %zu of %d indices)\n", flat, locked, bumpy, INDICES);
	return 0;
}
