/*
 * Resolves every P/Invoke entry point used by ENet-CSharp 2.4.8 from the built ENet library, loading it the
 * way the .NET runtime does (dynamic load by path, then symbol lookup by exact name).
 *
 * Usage: enet_exports <path-to-library>
 */
#include <stdio.h>

#include "pinvoke_exports.h"

#if defined(_WIN32)
#	define WIN32_LEAN_AND_MEAN
#	include <windows.h>
typedef HMODULE mf_lib;
static mf_lib mf_open(const char* path) { return LoadLibraryA(path); }
static int mf_has(mf_lib lib, const char* name) { return GetProcAddress(lib, name) != NULL; }
static void mf_close(mf_lib lib) { FreeLibrary(lib); }
#else
#	include <dlfcn.h>
typedef void* mf_lib;
static mf_lib mf_open(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static int mf_has(mf_lib lib, const char* name) { return dlsym(lib, name) != NULL; }
static void mf_close(mf_lib lib) { dlclose(lib); }
#endif

int main(int argc, char** argv)
{
	if (argc != 2)
	{
		fprintf(stderr, "usage: %s <library>\n", argv[0]);
		return 2;
	}

	mf_lib lib = mf_open(argv[1]);
	if (!lib)
	{
		fprintf(stderr, "FAIL: could not load %s\n", argv[1]);
		return 1;
	}

	int missing = 0;
	for (int i = 0; i < MF_ENET_PINVOKE_COUNT; ++i)
	{
		if (!mf_has(lib, mf_enet_pinvokes[i]))
		{
			fprintf(stderr, "FAIL: missing export %s\n", mf_enet_pinvokes[i]);
			++missing;
		}
	}
	mf_close(lib);

	if (missing)
		return 1;
	printf("OK: all %d ENet-CSharp P/Invoke entry points resolved from %s\n", MF_ENET_PINVOKE_COUNT, argv[1]);
	return 0;
}
