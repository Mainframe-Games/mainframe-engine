#include "wav.hpp"

#include <cstring>
#include <fstream>
#include <iterator>

namespace mfph {
namespace {

std::uint16_t u16(const unsigned char* p) { return static_cast<std::uint16_t>(p[0] | (p[1] << 8)); }

std::uint32_t u32(const unsigned char* p) {
	return static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8) |
		(static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}

void put16(std::vector<unsigned char>& b, std::uint16_t v) {
	b.push_back(static_cast<unsigned char>(v & 0xFF));
	b.push_back(static_cast<unsigned char>(v >> 8));
}

void put32(std::vector<unsigned char>& b, std::uint32_t v) {
	for (int i = 0; i < 4; ++i)
		b.push_back(static_cast<unsigned char>((v >> (8 * i)) & 0xFF));
}

} // namespace

bool readWav(const std::filesystem::path& path, WavData& out, std::string& error) {
	std::ifstream file(path, std::ios::binary);
	if (!file) {
		error = "cannot open '" + path.string() + "'";
		return false;
	}
	std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
	if (bytes.size() < 12 || std::memcmp(bytes.data(), "RIFF", 4) != 0 || std::memcmp(bytes.data() + 8, "WAVE", 4) != 0) {
		error = "'" + path.string() + "' is not a RIFF/WAVE file";
		return false;
	}

	std::uint16_t format = 0, channels = 0, bits = 0;
	std::uint32_t rate = 0;
	const unsigned char* data = nullptr;
	std::size_t dataSize = 0;
	std::size_t pos = 12;
	while (pos + 8 <= bytes.size()) {
		const unsigned char* chunk = bytes.data() + pos;
		std::size_t size = u32(chunk + 4);
		std::size_t available = bytes.size() - pos - 8;
		if (size > available)
			size = available; // a truncated last chunk: use what is there
		if (std::memcmp(chunk, "fmt ", 4) == 0 && size >= 16) {
			format = u16(chunk + 8);
			channels = u16(chunk + 10);
			rate = u32(chunk + 12);
			bits = u16(chunk + 22);
			if (format == 0xFFFE && size >= 40)
				format = u16(chunk + 8 + 24); // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID's first two bytes
		} else if (std::memcmp(chunk, "data", 4) == 0) {
			data = chunk + 8;
			dataSize = size;
		}
		pos += 8 + size + (size & 1);
	}

	if (format == 0 || data == nullptr) {
		error = "'" + path.string() + "' has no fmt or data chunk";
		return false;
	}
	if (channels < 1 || channels > 2) {
		error = "unsupported channel count " + std::to_string(channels) + " (mono or stereo)";
		return false;
	}
	if (rate < 1000 || rate > 384000) {
		error = "unsupported sample rate " + std::to_string(rate);
		return false;
	}

	const bool pcm16 = format == 1 && bits == 16;
	const bool float32 = format == 3 && bits == 32;
	if (!pcm16 && !float32) {
		error = "unsupported sample format (format " + std::to_string(format) + ", " + std::to_string(bits) +
			" bits): 16-bit PCM or 32-bit float";
		return false;
	}

	const std::size_t bytesPerSample = pcm16 ? 2 : 4;
	const std::size_t count = dataSize / bytesPerSample / channels * channels;
	out.channels = channels;
	out.sampleRate = static_cast<int>(rate);
	out.samples.resize(count);
	for (std::size_t i = 0; i < count; ++i) {
		const unsigned char* p = data + i * bytesPerSample;
		if (pcm16) {
			out.samples[i] = static_cast<float>(static_cast<std::int16_t>(u16(p))) / 32768.0f;
		} else {
			std::uint32_t raw = u32(p);
			float value;
			std::memcpy(&value, &raw, sizeof value);
			out.samples[i] = value;
		}
	}
	return true;
}

bool writeWavFloat(const std::filesystem::path& path, const std::vector<float>& samples, int channels, int sampleRate, std::string& error) {
	std::vector<unsigned char> b;
	const auto dataBytes = static_cast<std::uint32_t>(samples.size() * 4);
	b.insert(b.end(), {'R', 'I', 'F', 'F'});
	put32(b, 36 + dataBytes);
	b.insert(b.end(), {'W', 'A', 'V', 'E', 'f', 'm', 't', ' '});
	put32(b, 16);
	put16(b, 3);
	put16(b, static_cast<std::uint16_t>(channels));
	put32(b, static_cast<std::uint32_t>(sampleRate));
	put32(b, static_cast<std::uint32_t>(sampleRate * channels * 4));
	put16(b, static_cast<std::uint16_t>(channels * 4));
	put16(b, 32);
	b.insert(b.end(), {'d', 'a', 't', 'a'});
	put32(b, dataBytes);
	for (float s : samples) {
		std::uint32_t raw;
		std::memcpy(&raw, &s, sizeof raw);
		put32(b, raw);
	}
	std::ofstream file(path, std::ios::binary | std::ios::trunc);
	file.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size()));
	if (!file) {
		error = "cannot write '" + path.string() + "'";
		return false;
	}
	return true;
}

} // namespace mfph
