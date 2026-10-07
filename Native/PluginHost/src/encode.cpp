#include "encode.hpp"

#include "version.hpp"
#include "wav.hpp"

#include <vorbis/vorbisenc.h>

#include <algorithm>
#include <fstream>
#include <system_error>

namespace mfph {
namespace {

// Owns the libvorbis/libogg encoder state.
struct Encoder {
	vorbis_info info{};
	vorbis_comment comment{};
	vorbis_dsp_state dsp{};
	vorbis_block block{};
	ogg_stream_state stream{};
	bool dspReady = false;
	bool streamReady = false;

	Encoder() {
		vorbis_info_init(&info);
		vorbis_comment_init(&comment);
	}

	~Encoder() {
		if (streamReady)
			ogg_stream_clear(&stream);
		if (dspReady) {
			vorbis_block_clear(&block);
			vorbis_dsp_clear(&dsp);
		}
		vorbis_comment_clear(&comment);
		vorbis_info_clear(&info);
	}

	Encoder(const Encoder&) = delete;
	Encoder& operator=(const Encoder&) = delete;
};

bool writePage(std::ofstream& out, const ogg_page& page) {
	out.write(reinterpret_cast<const char*>(page.header), page.header_len);
	out.write(reinterpret_cast<const char*>(page.body), page.body_len);
	return static_cast<bool>(out);
}

} // namespace

std::filesystem::path pathFromUtf8(const std::string& utf8) {
	return std::filesystem::path(std::u8string(utf8.begin(), utf8.end()));
}

bool encodeWavToOgg(const std::filesystem::path& wavPath, const std::filesystem::path& oggPath, float quality,
	EncodeResult& result, std::string& error) {
	WavData wav;
	if (!readWav(wavPath, wav, error))
		return false;

	Encoder enc;
	const float q = std::clamp(quality, 0.0f, 10.0f) / 10.0f;
	if (vorbis_encode_init_vbr(&enc.info, wav.channels, wav.sampleRate, q) != 0) {
		error = "the Vorbis encoder does not support " + std::to_string(wav.channels) + " channel(s) at " +
			std::to_string(wav.sampleRate) + " Hz";
		return false;
	}
	vorbis_comment_add_tag(&enc.comment, "ENCODER", (std::string("Mainframe Engine mfplughost ") + kVersion).c_str());
	vorbis_analysis_init(&enc.dsp, &enc.info);
	vorbis_block_init(&enc.dsp, &enc.block);
	enc.dspReady = true;
	// A fixed serial number keeps the output deterministic for the same input.
	ogg_stream_init(&enc.stream, 0x4D46504C); // "MFPL"
	enc.streamReady = true;

	std::filesystem::path temp = oggPath;
	temp += ".encoding.tmp";
	std::error_code ec;
	if (oggPath.has_parent_path())
		std::filesystem::create_directories(oggPath.parent_path(), ec);

	bool ok = true;
	{
		std::ofstream out(temp, std::ios::binary | std::ios::trunc);
		if (!out) {
			error = "cannot write '" + temp.string() + "'";
			return false;
		}

		ogg_packet header, headerComment, headerCode;
		vorbis_analysis_headerout(&enc.dsp, &enc.comment, &header, &headerComment, &headerCode);
		ogg_stream_packetin(&enc.stream, &header);
		ogg_stream_packetin(&enc.stream, &headerComment);
		ogg_stream_packetin(&enc.stream, &headerCode);
		ogg_page page;
		while (ok && ogg_stream_flush(&enc.stream, &page) != 0)
			ok = writePage(out, page);

		const std::uint64_t frames = wav.frames();
		const auto channels = static_cast<std::size_t>(wav.channels);
		constexpr std::uint64_t kChunk = 4096;
		std::uint64_t done = 0;
		bool eos = false;
		while (ok && !eos) {
			const std::uint64_t n = std::min(kChunk, frames - done);
			if (n == 0) {
				vorbis_analysis_wrote(&enc.dsp, 0); // end of stream
			} else {
				float** buffer = vorbis_analysis_buffer(&enc.dsp, static_cast<int>(n));
				for (std::uint64_t i = 0; i < n; ++i)
					for (std::size_t c = 0; c < channels; ++c)
						buffer[c][i] = wav.samples[static_cast<std::size_t>(done + i) * channels + c];
				vorbis_analysis_wrote(&enc.dsp, static_cast<int>(n));
				done += n;
			}

			while (ok && vorbis_analysis_blockout(&enc.dsp, &enc.block) == 1) {
				vorbis_analysis(&enc.block, nullptr);
				vorbis_bitrate_addblock(&enc.block);
				ogg_packet packet;
				while (ok && vorbis_bitrate_flushpacket(&enc.dsp, &packet) != 0) {
					ogg_stream_packetin(&enc.stream, &packet);
					while (ok && ogg_stream_pageout(&enc.stream, &page) != 0) {
						ok = writePage(out, page);
						if (ogg_page_eos(&page) != 0)
							eos = true;
					}
				}
			}
			if (n == 0)
				eos = true; // flushed everything after the end-of-stream marker
		}
		while (ok && ogg_stream_flush(&enc.stream, &page) != 0)
			ok = writePage(out, page);
		out.flush();
		ok = ok && static_cast<bool>(out);
		result.frames = frames;
	}

	if (!ok) {
		error = "writing '" + temp.string() + "' failed";
		std::filesystem::remove(temp, ec);
		return false;
	}
	std::filesystem::rename(temp, oggPath, ec);
	if (ec) {
		error = "cannot replace '" + oggPath.string() + "': " + ec.message();
		std::filesystem::remove(temp, ec);
		return false;
	}
	result.sampleRate = wav.sampleRate;
	result.channels = wav.channels;
	return true;
}

} // namespace mfph
