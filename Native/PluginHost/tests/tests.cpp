// mfplughost tests: WAV reading, Vorbis encoding (decoded back through libvorbisfile), the --encode command line and
// a protocol round trip over a real socket (hello, ping, unknown message, encode, disconnect, shutdown).
//
//   mfplughost_tests <path-to-mfplughost> <scratch-dir>
#include "encode.hpp"
#include "protocol.hpp"
#include "server.hpp"
#include "transport.hpp"
#include "version.hpp"
#include "wav.hpp"

#include <vorbis/vorbisfile.h>

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

namespace fs = std::filesystem;

static int g_failures = 0;

#define CHECK(cond)                                                                       \
	do {                                                                                  \
		if (!(cond)) {                                                                    \
			std::cerr << __FILE__ << ":" << __LINE__ << ": CHECK failed: " #cond << "\n"; \
			++g_failures;                                                                 \
		}                                                                                 \
	} while (0)

static std::vector<float> sine(int frames, int channels, int rate, double hz) {
	std::vector<float> s(static_cast<std::size_t>(frames * channels));
	for (int i = 0; i < frames; ++i)
		for (int c = 0; c < channels; ++c)
			s[static_cast<std::size_t>(i * channels + c)] = static_cast<float>(0.5 * std::sin(2.0 * 3.14159265358979 * hz * (c + 1) * i / rate));
	return s;
}

static void writePcm16(const fs::path& path, const std::vector<float>& samples, int channels, int rate) {
	std::ofstream f(path, std::ios::binary);
	auto u32 = [&](std::uint32_t v) { f.write(reinterpret_cast<const char*>(&v), 4); }; // little-endian hosts only
	auto u16 = [&](std::uint16_t v) { f.write(reinterpret_cast<const char*>(&v), 2); };
	const auto bytes = static_cast<std::uint32_t>(samples.size() * 2);
	f.write("RIFF", 4); u32(36 + bytes); f.write("WAVEfmt ", 8); u32(16); u16(1); u16(static_cast<std::uint16_t>(channels));
	u32(static_cast<std::uint32_t>(rate)); u32(static_cast<std::uint32_t>(rate * channels * 2)); u16(static_cast<std::uint16_t>(channels * 2)); u16(16);
	f.write("data", 4); u32(bytes);
	for (float s : samples)
		u16(static_cast<std::uint16_t>(static_cast<std::int16_t>(std::lround(s * 32767.0f))));
}

// Decodes an .ogg; returns frames (or -1) and the RMS of the decoded signal.
static long long decodeOgg(const fs::path& path, int& channels, long& rate, double& rms) {
	OggVorbis_File vf;
	if (ov_fopen(path.string().c_str(), &vf) != 0)
		return -1;
	vorbis_info* info = ov_info(&vf, -1);
	channels = info->channels;
	rate = info->rate;
	long long total = ov_pcm_total(&vf, -1);
	double sum = 0;
	long long decoded = 0;
	for (;;) {
		float** pcm;
		int section;
		long n = ov_read_float(&vf, &pcm, 4096, &section);
		if (n <= 0)
			break;
		for (long i = 0; i < n; ++i)
			for (int c = 0; c < channels; ++c)
				sum += static_cast<double>(pcm[c][i]) * pcm[c][i];
		decoded += n;
	}
	ov_clear(&vf);
	rms = decoded > 0 ? std::sqrt(sum / static_cast<double>(decoded * channels)) : 0;
	return decoded == total ? total : -2;
}

static void testEncode(const fs::path& dir) {
	const int rate = 44100, frames = rate * 2;
	std::string error;
	// 32-bit float stereo
	const fs::path wav = dir / "sine.wav", ogg = dir / "sine.ogg";
	CHECK(mfph::writeWavFloat(wav, sine(frames, 2, rate, 440), 2, rate, error));
	mfph::EncodeResult result;
	CHECK(mfph::encodeWavToOgg(wav, ogg, 6.0f, result, error));
	CHECK(result.frames == static_cast<std::uint64_t>(frames) && result.channels == 2 && result.sampleRate == rate);
	CHECK(!fs::exists(dir / "sine.ogg.encoding.tmp"));
	int channels = 0;
	long decodedRate = 0;
	double rms = 0;
	CHECK(decodeOgg(ogg, channels, decodedRate, rms) == frames);
	CHECK(channels == 2 && decodedRate == rate);
	CHECK(std::fabs(rms - 0.5 / std::sqrt(2.0)) < 0.02); // a 0.5-amplitude sine
	const auto size = fs::file_size(ogg);
	CHECK(size > 4000 && size < fs::file_size(wav) / 8);

	// 16-bit PCM mono
	const fs::path wav16 = dir / "mono16.wav", ogg16 = dir / "mono16.ogg";
	writePcm16(wav16, sine(rate / 2, 1, rate, 220), 1, rate);
	mfph::WavData data;
	CHECK(mfph::readWav(wav16, data, error) && data.channels == 1 && data.frames() == static_cast<std::uint64_t>(rate / 2));
	CHECK(mfph::encodeWavToOgg(wav16, ogg16, 0.0f, result, error));
	CHECK(decodeOgg(ogg16, channels, decodedRate, rms) == rate / 2 && channels == 1);

	// Failure leaves the previous output intact.
	const auto before = fs::file_size(ogg);
	std::ofstream(dir / "bad.wav") << "not a wav";
	CHECK(!mfph::encodeWavToOgg(dir / "bad.wav", ogg, 6.0f, result, error));
	CHECK(error.find("RIFF") != std::string::npos);
	CHECK(fs::file_size(ogg) == before);
}

static int run(const std::string& command) {
#ifdef _WIN32
	return std::system(("\"" + command + "\"").c_str()); // cmd.exe strips one outer pair of quotes
#else
	return std::system(command.c_str());
#endif
}

static void testCommandLine(const std::string& exe, const fs::path& dir) {
	const fs::path wav = dir / "sine.wav", ogg = dir / "cli.ogg";
	CHECK(run("\"" + exe + "\" --encode \"" + wav.string() + "\" \"" + ogg.string() + "\" --quality 3") == 0);
	CHECK(fs::exists(ogg) && fs::file_size(ogg) > 1000);
	CHECK(run("\"" + exe + "\" --encode \"" + (dir / "missing.wav").string() + "\" \"" + (dir / "x.ogg").string() + "\"") != 0);
	CHECK(!fs::exists(dir / "x.ogg"));
	CHECK(run("\"" + exe + "\" --version") == 0);
}

static bool request(mfph::Connection& c, mfph::MessageType type, std::uint32_t id, const std::vector<unsigned char>& payload, mfph::Frame& reply) {
	mfph::Frame frame;
	frame.type = static_cast<std::uint16_t>(type);
	frame.id = id;
	frame.payload = payload;
	std::string error;
	return c.writeFrame(frame, error) && c.readFrame(reply, error) == mfph::ReadResult::Ok && reply.id == id;
}

static bool connectWithRetry(const std::string& path, mfph::Connection& c) {
	std::string error;
	for (int i = 0; i < 200; ++i) {
		if (mfph::Connection::connect(path, c, error))
			return true;
		std::this_thread::sleep_for(std::chrono::milliseconds(10));
	}
	std::cerr << error << "\n";
	return false;
}

static void testProtocol(const fs::path& dir) {
	std::string error;
	CHECK(mfph::initSockets(error));
	const std::string path = (fs::temp_directory_path() / ("mfph-test-" + std::to_string(std::rand()) + ".sock")).string();
	std::ostringstream log;
	auto server = std::async(std::launch::async, [&] { return mfph::serve(path, log, 10000); });
	mfph::Connection c;
	CHECK(connectWithRetry(path, c));

	mfph::Frame reply;
	mfph::PayloadWriter hello;
	hello.u32(mfph::kProtocolVersion);
	hello.str("tests");
	CHECK(request(c, mfph::MessageType::Hello, 1, hello.data(), reply));
	CHECK(reply.type == (static_cast<std::uint16_t>(mfph::MessageType::Hello) | mfph::kReplyBit));
	{
		mfph::PayloadReader r(reply.payload);
		std::uint32_t version = 0, caps = 0, pid = 0;
		std::string helper;
		CHECK(r.u32(version) && r.str(helper) && r.u32(caps) && r.u32(pid) && r.atEnd());
		CHECK(version == mfph::kProtocolVersion && helper == mfph::kVersion && (caps & mfph::CapabilityEncode) != 0);
	}

	mfph::PayloadWriter wrong;
	wrong.u32(999);
	wrong.str("tests");
	CHECK(request(c, mfph::MessageType::Hello, 2, wrong.data(), reply));
	CHECK(reply.type == static_cast<std::uint16_t>(mfph::MessageType::Error));

	CHECK(request(c, mfph::MessageType::Ping, 3, {1, 2, 3}, reply));
	CHECK((reply.payload == std::vector<unsigned char>{1, 2, 3}));

	CHECK(request(c, static_cast<mfph::MessageType>(0x0123), 4, {}, reply));
	CHECK(reply.type == static_cast<std::uint16_t>(mfph::MessageType::Error));
	{
		mfph::PayloadReader r(reply.payload);
		std::uint32_t code = 0;
		CHECK(r.u32(code) && code == static_cast<std::uint32_t>(mfph::ErrorCode::UnknownMessage));
	}

	mfph::PayloadWriter encode;
	encode.str((dir / "sine.wav").string());
	encode.str((dir / "socket.ogg").string());
	encode.f32(4.0f);
	CHECK(request(c, mfph::MessageType::Encode, 5, encode.data(), reply));
	{
		mfph::PayloadReader r(reply.payload);
		std::uint64_t frames = 0;
		std::uint32_t rate = 0;
		std::uint16_t channels = 0;
		CHECK(r.u64(frames) && r.u32(rate) && r.u16(channels));
		CHECK(frames == 88200 && rate == 44100 && channels == 2);
	}

	CHECK(request(c, mfph::MessageType::Shutdown, 6, {}, reply));
	CHECK(server.wait_for(std::chrono::seconds(5)) == std::future_status::ready && server.get() == 0);
	CHECK(!fs::exists(path));

	// A dropped connection ends the helper too (the editor died).
	const std::string path2 = path + "2";
	auto server2 = std::async(std::launch::async, [&] { return mfph::serve(path2, log, 10000); });
	mfph::Connection c2;
	CHECK(connectWithRetry(path2, c2));
	c2.close();
	CHECK(server2.wait_for(std::chrono::seconds(5)) == std::future_status::ready && server2.get() == 0);
}

int main(int argc, char** argv) {
	if (argc != 3) {
		std::cerr << "usage: mfplughost_tests <mfplughost> <scratch-dir>\n";
		return 2;
	}
	const fs::path dir = argv[2];
	fs::remove_all(dir);
	fs::create_directories(dir);
	std::srand(static_cast<unsigned>(std::chrono::steady_clock::now().time_since_epoch().count()));
	testEncode(dir);
	testCommandLine(argv[1], dir);
	testProtocol(dir);
	if (g_failures > 0) {
		std::cerr << g_failures << " check(s) failed\n";
		return 1;
	}
	std::cout << "mfplughost_tests: all checks passed\n";
	return 0;
}
