/*
 * Resolves every function declared in mfrmlui.h from the built library, loading it the way the .NET runtime does
 * (dynamic load by path, then lookup by exact name). Also prints the ABI version reported by the library.
 *
 * Usage: mfrmlui_exports <path-to-library>
 */
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "mfrmlui_exports.h"

#if defined(_WIN32)
#	define WIN32_LEAN_AND_MEAN
#	include <windows.h>
typedef HMODULE mf_lib;
typedef FARPROC mf_sym;
static mf_lib mf_open(const char* path) { return LoadLibraryA(path); }
static mf_sym mf_find(mf_lib lib, const char* name) { return GetProcAddress(lib, name); }
static void mf_close(mf_lib lib) { FreeLibrary(lib); }
#else
#	include <dlfcn.h>
typedef void* mf_lib;
typedef void* mf_sym;
static mf_lib mf_open(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static mf_sym mf_find(mf_lib lib, const char* name) { return dlsym(lib, name); }
static void mf_close(mf_lib lib) { dlclose(lib); }
#endif

typedef uint32_t (*abi_version_fn)(void);

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
	for (int i = 0; i < MFRMLUI_EXPORT_COUNT; ++i)
	{
		if (!mf_find(lib, mfrmlui_exports[i]))
		{
			fprintf(stderr, "FAIL: missing export %s\n", mfrmlui_exports[i]);
			++missing;
		}
	}

	mf_sym sym = mf_find(lib, "mfrmlui_abi_version");
	if (sym)
	{
		abi_version_fn abi_version;
		memcpy(&abi_version, &sym, sizeof abi_version);
		const uint32_t v = abi_version();
		printf("library ABI %u.%u\n", (unsigned)(v >> 16), (unsigned)(v & 0xFFFFu));
	}
	mf_close(lib);

	if (missing)
		return 1;
	printf("OK: all %d functions declared in mfrmlui.h are exported by %s\n", MFRMLUI_EXPORT_COUNT, argv[1]);
	return 0;
}
