// mfplughost tests: WAV reading, Vorbis encoding (decoded back through libvorbisfile), the --encode command line and
// a protocol round trip over a real socket (hello, ping, unknown message, encode, disconnect, shutdown).
//
//   mfplughost_tests <path-to-mfplughost> <scratch-dir>
#include "encode.hpp"
#include "midi.hpp"
#include "protocol.hpp"
#include "server.hpp"
#include "transport.hpp"
#include "version.hpp"
#include "wav.hpp"

#include <vorbis/vorbisfile.h>
#ifdef MFPH_MIDI
#include <RtMidi.h>
#endif

#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <memory>
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


// MIDI: the wire encodings, the Clock request and — where the platform has virtual ports (CoreMIDI, ALSA) — a virtual
// output in this process seen as a helper input: list, open, a note arrives as a timestamped MidiEvent, closing the
// virtual port marks the device offline (MidiDevicesChanged).
static void testMidi() {
	{
		mfph::PayloadWriter w;
		mfph::writeMidiPorts(w, {{3, "Keys", true}, {7, "Pads", false}});
		mfph::PayloadReader r(w.data());
		std::uint32_t n = 0, id = 0;
		std::string name;
		std::uint16_t online = 9;
		CHECK(r.u32(n) && n == 2 && r.u32(id) && id == 3 && r.str(name) && name == "Keys" && r.u16(online) && online == 1);
		const unsigned char on[] = {0x90, 60, 100};
		const auto f = mfph::midiEventFrame(3, 123456789ull, on, 3);
		mfph::PayloadReader e(f.payload);
		std::uint64_t ts = 0;
		std::uint16_t len = 0;
		const unsigned char* bytes = nullptr;
		CHECK(f.type == static_cast<std::uint16_t>(mfph::MessageType::MidiEvent) && f.id == 0);
		CHECK(e.u32(id) && id == 3 && e.u64(ts) && ts == 123456789ull && e.u16(len) && len == 3 && e.bytes(3, bytes) && e.atEnd());
		CHECK(bytes[0] == 0x90 && bytes[1] == 60 && bytes[2] == 100);
	}

	const std::string path = (fs::temp_directory_path() / ("mfph-midi-" + std::to_string(std::rand()) + ".sock")).string();
	std::ostringstream log;
	auto server = std::async(std::launch::async, [&] { return mfph::serve(path, log, 10000); });
	mfph::Connection c;
	CHECK(connectWithRetry(path, c));
	mfph::Frame reply;
	const std::uint64_t before = mfph::steadyNowNs();
	CHECK(request(c, mfph::MessageType::Clock, 1, {}, reply));
	{
		mfph::PayloadReader r(reply.payload);
		std::uint64_t now = 0;
		CHECK(r.u64(now) && now >= before && now <= mfph::steadyNowNs());
	}
	mfph::PayloadWriter hello;
	hello.u32(mfph::kProtocolVersion);
	hello.str("tests");
	CHECK(request(c, mfph::MessageType::Hello, 2, hello.data(), reply));
	std::uint32_t caps = 0;
	{
		mfph::PayloadReader r(reply.payload);
		std::uint32_t version = 0;
		std::string helper;
		CHECK(r.u32(version) && r.str(helper) && r.u32(caps));
	}
#if defined(MFPH_MIDI) && !defined(_WIN32)
	if ((caps & mfph::CapabilityMidi) != 0) {
		const std::string portName = "mfph-test-" + std::to_string(std::rand());
		auto out = std::make_unique<RtMidiOut>(RtMidi::UNSPECIFIED, portName);
		out->openVirtualPort(portName);
		std::this_thread::sleep_for(std::chrono::milliseconds(100));
		CHECK(request(c, mfph::MessageType::MidiListInputs, 3, {}, reply));
		std::uint32_t device = 0;
		{
			mfph::PayloadReader r(reply.payload);
			std::uint32_t n = 0;
			CHECK(r.u32(n));
			for (std::uint32_t i = 0; i < n; ++i) {
				std::uint32_t id = 0;
				std::string name;
				std::uint16_t online = 0;
				CHECK(r.u32(id) && r.str(name) && r.u16(online));
				if (name.find(portName) != std::string::npos && online == 1)
					device = id;
			}
		}
		CHECK(device != 0);
		mfph::PayloadWriter open;
		open.u32(device);
		CHECK(request(c, mfph::MessageType::MidiOpen, 4, open.data(), reply));
		CHECK(reply.type == (static_cast<std::uint16_t>(mfph::MessageType::MidiOpen) | mfph::kReplyBit));

		// Notifications arrive on the connection; a reader with a deadline (Shutdown unblocks it on failure).
		const std::uint64_t sent = mfph::steadyNowNs();
		const std::vector<unsigned char> note{0x90, 64, 99};
		out->sendMessage(&note);
		auto reader = std::async(std::launch::async, [&] {
			int found = 0; // 1: the note, 2: then the device went offline
			for (;;) {
				mfph::Frame f;
				std::string error;
				if (c.readFrame(f, error) != mfph::ReadResult::Ok)
					return found;
				mfph::PayloadReader r(f.payload);
				if (f.type == static_cast<std::uint16_t>(mfph::MessageType::MidiEvent) && found == 0) {
					std::uint32_t id = 0;
					std::uint64_t ts = 0;
					std::uint16_t len = 0;
					const unsigned char* b = nullptr;
					if (r.u32(id) && id == device && r.u64(ts) && ts >= sent && r.u16(len) && len == 3 && r.bytes(3, b) && b[0] == 0x90 && b[1] == 64 && b[2] == 99) {
						found = 1;
						out.reset(); // unplug
					}
				} else if (f.type == static_cast<std::uint16_t>(mfph::MessageType::MidiDevicesChanged) && found == 1) {
					std::uint32_t n = 0;
					r.u32(n);
					for (std::uint32_t i = 0; i < n; ++i) {
						std::uint32_t id = 0;
						std::string name;
						std::uint16_t online = 0;
						r.u32(id);
						r.str(name);
						r.u16(online);
						if (id == device && online == 0)
							return 2;
					}
				}
			}
		});
		const bool done = reader.wait_for(std::chrono::seconds(6)) == std::future_status::ready;
		if (!done) {
			mfph::Frame shutdown;
			shutdown.type = static_cast<std::uint16_t>(mfph::MessageType::Shutdown);
			shutdown.id = 99;
			std::string error;
			c.writeFrame(shutdown, error);
		}
		const int found = reader.get();
		CHECK(done && found == 2);
		if (!done) {
			server.wait_for(std::chrono::seconds(5));
			return;
		}
	} else {
		std::cout << "mfplughost_tests: no MIDI API at run time, virtual port test skipped\n";
	}
#endif
	CHECK(request(c, mfph::MessageType::Shutdown, 9, {}, reply));
	CHECK(server.wait_for(std::chrono::seconds(5)) == std::future_status::ready && server.get() == 0);
}

#ifdef MFPH_VST3
#include "plugins.hpp"
#include "shm.hpp"

// VST3 hosting against the test bundle (tests/plugins), through dispatch() on this thread (no main loop: control calls
// run inline): scan, load, process (synth makes sound, gain scales, a chain feeds one into the next), state round trip,
// latency, offline mode, unload.
namespace {
constexpr std::uint32_t kBlock = 256, kSlots = 4, kEvents = 64;
constexpr std::uint32_t kStride = ((16 * kBlock + 16 + 16 * kEvents) + 63) / 64 * 64;

mfph::Frame call(mfph::MessageType type, const std::vector<unsigned char>& payload) {
	mfph::Frame request, reply;
	request.type = static_cast<std::uint16_t>(type);
	request.id = 1;
	request.payload = payload;
	bool shutdown;
	mfph::dispatch(request, reply, shutdown);
	if (reply.type == static_cast<std::uint16_t>(mfph::MessageType::Error)) {
		mfph::PayloadReader r(reply.payload);
		std::uint32_t code;
		std::string message;
		r.u32(code);
		r.str(message);
		std::cerr << "  error reply: " << message << "\n";
	}
	return reply;
}

bool ok(const mfph::Frame& reply, mfph::MessageType type) {
	return reply.type == (static_cast<std::uint16_t>(type) | mfph::kReplyBit);
}

std::uint32_t loadPlugin(const std::string& classId, std::uint32_t slot, std::uint32_t& latency) {
	mfph::PayloadWriter w;
	w.str(MFPH_TEST_PLUGINS);
	w.str(classId);
	w.u32(slot);
	w.u32(48000);
	w.u32(kBlock);
	auto reply = call(mfph::MessageType::PluginLoad, w.data());
	CHECK(ok(reply, mfph::MessageType::PluginLoad));
	mfph::PayloadReader r(reply.payload);
	std::uint32_t id = 0, flags = 0;
	std::uint16_t ins = 0, outs = 0;
	std::string name;
	CHECK(r.u32(id) && r.u32(latency) && r.u16(ins) && r.u16(outs) && r.u32(flags) && r.str(name));
	CHECK(outs == 2);
	return id;
}

void process(std::uint32_t frames, const std::vector<std::pair<std::uint32_t, std::uint32_t>>& chain) {
	mfph::PayloadWriter w;
	w.u32(frames);
	w.u32(1);
	double tempo = 120.0;
	std::uint64_t bits;
	std::memcpy(&bits, &tempo, 8);
	w.u64(bits);
	w.u64(0);
	w.u32(static_cast<std::uint32_t>(chain.size()));
	for (auto [id, input] : chain) {
		w.u32(id);
		w.u32(input);
	}
	CHECK(ok(call(mfph::MessageType::PluginProcess, w.data()), mfph::MessageType::PluginProcess));
}

float peak(const float* p, std::uint32_t n) {
	float m = 0;
	for (std::uint32_t i = 0; i < n; ++i)
		m = std::max(m, std::fabs(p[i]));
	return m;
}
} // namespace

static void testPlugins(const fs::path& dir) {
	const std::string gainId = "4D46544741494E000000000000000001", synthId = "4D465453594E54000000000000000002",
					  crasherId = "4D464352415348000000000000000003";
	std::string json;
	CHECK(mfph::scanBundle(MFPH_TEST_PLUGINS, json));
	CHECK(json.find(synthId) != std::string::npos && json.find("\"kind\":\"instrument\"") != std::string::npos);
	CHECK(!mfph::scanBundle((dir / "missing.vst3").string(), json) && json.find("\"error\":\"") != std::string::npos);

	// The shared memory file, as the editor writes it.
	const fs::path shmPath = dir / "plugins.shm";
	const std::size_t size = mfph::kShmHeaderSize + kSlots * kStride;
	{
		std::vector<unsigned char> bytes(size, 0);
		const std::uint32_t header[] = {mfph::kShmMagic, mfph::kShmVersion, kBlock, kSlots, kEvents, kStride};
		std::memcpy(bytes.data(), header, sizeof(header));
		std::ofstream(shmPath, std::ios::binary).write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(size));
	}
	mfph::PayloadWriter setup;
	setup.str(shmPath.string());
	setup.u64(size);
	CHECK(ok(call(mfph::MessageType::PluginSetupShm, setup.data()), mfph::MessageType::PluginSetupShm));
	mfph::SharedMemory shm;
	std::string error;
	CHECK(shm.open(shmPath.string(), size, error));
	if (!shm.isOpen())
		return;

	std::uint32_t latency = 99;
	const std::uint32_t synth = loadPlugin(synthId, 0, latency);
	CHECK(latency == 0);
	const std::uint32_t gain = loadPlugin(gainId, 1, latency);
	const std::uint32_t crasher = loadPlugin(crasherId, 2, latency);
	CHECK(latency == 128);
	mfph::PayloadWriter lat;
	lat.u32(crasher);
	auto latReply = call(mfph::MessageType::PluginLatency, lat.data());
	mfph::PayloadReader latReader(latReply.payload);
	CHECK(ok(latReply, mfph::MessageType::PluginLatency) && latReader.u32(latency) && latency == 128);

	// A note on the synth, the synth's output chained into the gain (0.5 by default).
	*shm.eventCount(0) = 1;
	auto* ev = const_cast<mfph::ShmEvent*>(shm.events(0));
	ev[0] = mfph::ShmEvent{10, 1, 0, 69, 100, {0, 0}};
	process(kBlock, {{synth, 0xFFFFFFFFu}, {gain, 0}});
	const float synthPeak = peak(shm.channel(0, 2), kBlock);
	CHECK(synthPeak > 0.05f && peak(shm.channel(0, 2), 10) == 0.0f);
	CHECK(std::fabs(peak(shm.channel(1, 2), kBlock) - synthPeak * 0.5f) < 1e-4f);
	CHECK(*shm.eventCount(0) == 0);

	// Gain state round trip: set 0.25, read it back, process scales by it.
	const float quarter = 0.25f;
	mfph::PayloadWriter st;
	st.u32(gain);
	st.u32(4);
	st.bytes(reinterpret_cast<const unsigned char*>(&quarter), 4);
	st.u32(0);
	CHECK(ok(call(mfph::MessageType::PluginSetState, st.data()), mfph::MessageType::PluginSetState));
	mfph::PayloadWriter gs;
	gs.u32(gain);
	auto state = call(mfph::MessageType::PluginGetState, gs.data());
	mfph::PayloadReader sr(state.payload);
	std::uint32_t cl = 0;
	const unsigned char* cp = nullptr;
	float restored = 0;
	CHECK(ok(state, mfph::MessageType::PluginGetState) && sr.u32(cl) && cl == 4 && sr.bytes(4, cp));
	if (cp)
		std::memcpy(&restored, cp, 4);
	CHECK(restored == 0.25f);
	for (std::uint32_t i = 0; i < kBlock; ++i)
		shm.channel(1, 0)[i] = shm.channel(1, 1)[i] = 1.0f;
	process(kBlock, {{gain, 0xFFFFFFFFu}});
	CHECK(std::fabs(shm.channel(1, 2)[5] - 0.25f) < 1e-6f && std::fabs(shm.channel(1, 3)[200] - 0.25f) < 1e-6f);

	// Offline and back; the crasher delays by its latency.
	mfph::PayloadWriter off;
	off.u32(1);
	CHECK(ok(call(mfph::MessageType::PluginSetOffline, off.data()), mfph::MessageType::PluginSetOffline));
	for (std::uint32_t i = 0; i < kBlock; ++i)
		shm.channel(2, 0)[i] = shm.channel(2, 1)[i] = i == 0 ? 1.0f : 0.0f;
	process(kBlock, {{crasher, 0xFFFFFFFFu}});
	CHECK(shm.channel(2, 2)[128] == 1.0f && shm.channel(2, 2)[0] == 0.0f);
	off.data().clear();
	off.u32(0);
	CHECK(ok(call(mfph::MessageType::PluginSetOffline, off.data()), mfph::MessageType::PluginSetOffline));

	// Editors: the test plugins have none (single component effects without a view).
	mfph::PayloadWriter oe;
	oe.u32(gain);
	oe.str("Gain - Track - Song");
	CHECK(call(mfph::MessageType::PluginOpenEditor, oe.data()).type == static_cast<std::uint16_t>(mfph::MessageType::Error));

	for (std::uint32_t id : {synth, gain, crasher}) {
		mfph::PayloadWriter u;
		u.u32(id);
		CHECK(ok(call(mfph::MessageType::PluginUnload, u.data()), mfph::MessageType::PluginUnload));
	}
	mfph::PayloadWriter u;
	u.u32(gain);
	CHECK(call(mfph::MessageType::PluginUnload, u.data()).type == static_cast<std::uint16_t>(mfph::MessageType::Error));
	mfph::shutdownPlugins();
}
#endif

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
	testMidi();
#ifdef MFPH_VST3
	testPlugins(dir);
#endif
	if (g_failures > 0) {
		std::cerr << g_failures << " check(s) failed\n";
		return 1;
	}
	std::cout << "mfplughost_tests: all checks passed\n";
	return 0;
}
