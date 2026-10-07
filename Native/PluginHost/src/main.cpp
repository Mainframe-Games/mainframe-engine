// mfplughost: the music editor's helper process. See docs/design/natives.md and ADR 0146.
#include "encode.hpp"
#include "mainthread.hpp"
#include "midi.hpp"
#include "plugins.hpp"
#include "server.hpp"
#include "version.hpp"

#include <cstdlib>
#include <iostream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#include <shellapi.h>
#endif

namespace {

// The command line as UTF-8 (Windows' narrow argv is in the ANSI code page).
std::vector<std::string> utf8Args(int argc, char** argv) {
	std::vector<std::string> args;
#ifdef _WIN32
	(void)argc;
	(void)argv;
	int count = 0;
	LPWSTR* wide = CommandLineToArgvW(GetCommandLineW(), &count);
	for (int i = 0; wide != nullptr && i < count; ++i) {
		int size = WideCharToMultiByte(CP_UTF8, 0, wide[i], -1, nullptr, 0, nullptr, nullptr);
		std::string s(static_cast<std::size_t>(size > 0 ? size - 1 : 0), '\0');
		if (size > 1)
			WideCharToMultiByte(CP_UTF8, 0, wide[i], -1, s.data(), size, nullptr, nullptr);
		args.push_back(std::move(s));
	}
	LocalFree(wide);
#else
	for (int i = 0; i < argc; ++i)
		args.emplace_back(argv[i]);
#endif
	return args;
}

int usage() {
	std::cerr << "usage: mfplughost --encode <in.wav> <out.ogg> [--quality <0..10>]\n"
				 "       mfplughost --serve <socket-path>\n"
				 "       mfplughost --scan <bundle.vst3>\n"
				 "       mfplughost --version\n";
	return 2;
}

} // namespace

int main(int argc, char** argv) {
	const std::vector<std::string> args = utf8Args(argc, argv);
	if (args.size() == 2 && args[1] == "--version") {
		std::cout << "mfplughost " << mfph::kVersion << " (protocol " << mfph::kProtocolVersion << ")\n";
		return 0;
	}
	if (args.size() == 3 && args[1] == "--serve") {
		const std::string socketPath = args[2];
		return mfph::runWithMainLoop([socketPath] { return mfph::serve(socketPath, std::cerr); });
	}
	if (args.size() == 4 && args[1] == "--midi-test-source") // tests: a virtual MIDI keyboard playing C4
		return mfph::runMidiTestSource(args[2], std::atoi(args[3].c_str()));
	if (args.size() == 3 && args[1] == "--scan") {
		std::string json;
		const bool ok = mfph::scanBundle(args[2], json);
		std::cout << json << std::endl;
		return ok ? 0 : 1;
	}
	if ((args.size() == 4 || args.size() == 6) && args[1] == "--encode") {
		float quality = 6.0f;
		if (args.size() == 6) {
			if (args[4] != "--quality")
				return usage();
			char* end = nullptr;
			quality = std::strtof(args[5].c_str(), &end);
			if (end == args[5].c_str() || *end != '\0' || quality < 0.0f || quality > 10.0f) {
				std::cerr << "mfplughost: --quality must be a number from 0 to 10\n";
				return 1;
			}
		}
		mfph::EncodeResult result;
		std::string error;
		if (!mfph::encodeWavToOgg(mfph::pathFromUtf8(args[2]), mfph::pathFromUtf8(args[3]), quality, result, error)) {
			std::cerr << "mfplughost: " << error << "\n";
			return 1;
		}
		return 0;
	}
	return usage();
}
