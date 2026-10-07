// WAV reading for the encoder: RIFF/WAVE with 16-bit PCM or 32-bit IEEE float samples, mono or stereo
// (WAVE_FORMAT_EXTENSIBLE with either sub-format too).
#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>

namespace mfph {

struct WavData {
	int channels = 0;
	int sampleRate = 0;
	std::vector<float> samples; // interleaved, ±1
	std::uint64_t frames() const { return channels > 0 ? samples.size() / static_cast<std::size_t>(channels) : 0; }
};

// Reads `path` into `out`. Returns false with a message in `error` on failure.
bool readWav(const std::filesystem::path& path, WavData& out, std::string& error);

// Writes interleaved float samples as a 32-bit float WAV (tests).
bool writeWavFloat(const std::filesystem::path& path, const std::vector<float>& samples, int channels, int sampleRate, std::string& error);

} // namespace mfph
