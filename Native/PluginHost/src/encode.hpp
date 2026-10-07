// Ogg Vorbis encoding (libvorbisenc, VBR).
#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

namespace mfph {

struct EncodeResult {
	std::uint64_t frames = 0;
	int sampleRate = 0;
	int channels = 0;
};

// UTF-8 bytes -> a path (protocol strings and the command line are UTF-8).
std::filesystem::path pathFromUtf8(const std::string& utf8);

// Encodes the WAV at `wavPath` to `oggPath` at VBR quality `quality` (0..10, as libvorbis' q / 10). The file is
// written to a temporary name next to `oggPath` and renamed over it, so a failure leaves any previous file intact.
// Returns false with a message in `error` on failure.
bool encodeWavToOgg(const std::filesystem::path& wavPath, const std::filesystem::path& oggPath, float quality,
	EncodeResult& result, std::string& error);

} // namespace mfph
