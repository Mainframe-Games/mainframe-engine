// The editor <-> helper control protocol (ADR 0146). Every message is one frame:
//
//   u32 payloadLength | u16 type | u16 flags (0) | u32 requestId | payload[payloadLength]
//
// little-endian, payload at most kMaxPayload bytes. A reply carries its request's id and type | kReplyBit, or kError.
// Payload fields are little-endian u16/u32/u64/f32 and strings as u32 byte length + UTF-8 (no terminator).
// Type ranges: 0x0001-0x00FF core, 0x0100-0x01FF plugins (VST3 phase), 0x0200-0x02FF MIDI (MIDI phase).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace mfph {

inline constexpr std::uint32_t kMaxPayload = 64u * 1024u * 1024u;
inline constexpr std::size_t kHeaderSize = 12;
inline constexpr std::uint16_t kReplyBit = 0x8000;

enum class MessageType : std::uint16_t {
	Hello = 0x0001,    // u32 protocolVersion, str client -> u32 protocolVersion, str helperVersion, u32 capabilities, u32 pid
	Ping = 0x0002,     // bytes -> the same bytes
	Shutdown = 0x0003, // -> (empty), then the helper exits
	Sleep = 0x0004,    // u32 milliseconds -> (empty) after that long (diagnostics: timeout tests)
	Clock = 0x0005,    // -> u64 the helper's steady clock now, in ns (MidiEvent timestamps; the editor maps them once at
	                   //   connect and on each ping — same machine, so only the clocks' origins differ)
	Encode = 0x0010,   // str wavPath, str oggPath, f32 quality (0..10) -> u64 frames, u32 sampleRate, u16 channels
	// 0x0100..0x01FF: VST3 plugins (ADR 0147). Instances are u32 ids; audio and note events travel through the shared
	// memory (shm.hpp), control calls run on the helper's main thread.
	PluginSetupShm = 0x0100,  // str path, u64 size -> (empty): maps the editor's shared memory file
	PluginScan = 0x0101,      // str bundlePath -> str json (the same JSON as `mfplughost --scan`)
	PluginLoad = 0x0102,      // str bundlePath, str classId (32 hex), u32 slot, u32 sampleRate, u32 maxBlock
	                          //   -> u32 instanceId, u32 latency, u16 inChannels, u16 outChannels, u32 flags (1: editor), str name
	PluginUnload = 0x0103,    // u32 instanceId -> (empty)
	PluginGetState = 0x0104,  // u32 instanceId -> u32 componentLength, bytes, u32 controllerLength, bytes
	PluginSetState = 0x0105,  // u32 instanceId, u32 componentLength, bytes, u32 controllerLength, bytes -> (empty)
	PluginLatency = 0x0106,   // u32 instanceId -> u32 latency frames
	PluginSetOffline = 0x0107, // u32 offline (0/1) -> (empty): kOffline/kRealtime process mode for every instance
	PluginOpenEditor = 0x0108, // u32 instanceId, str title -> (empty)
	PluginCloseEditor = 0x0109, // u32 instanceId -> (empty)
	PluginProcess = 0x010A,   // u32 frames, u32 flags (1: playing), f64 tempo, i64 projectFrame, u32 count,
	                          //   count x (u32 instanceId, u32 inputSlot (0xFFFFFFFF: the slot's own input)) -> (empty)
	                          // runs the instances in order; inputSlot copies that slot's output into the input first
	PluginEditorClosed = 0x010B, // helper -> editor notification (request id 0, no reply): u32 instanceId
	// 0x0200..0x02FF: MIDI input devices (ADR 0148; midi.hpp). Device ids are stable per port name for the helper's life.
	MidiListInputs = 0x0200, // -> u32 count, count x (u32 id, str name, u16 online)
	MidiOpen = 0x0201,       // u32 id -> (empty); idempotent; an error when the device is offline
	MidiClose = 0x0202,      // u32 id -> (empty)
	MidiEvent = 0x0203,      // helper -> editor notification (request id 0): u32 deviceId, u64 timestampNs (Clock's
	                         //   clock), u16 length, bytes (one channel message: note on/off, CC, pitch bend, ...)
	MidiDevicesChanged = 0x0204, // helper -> editor notification: the MidiListInputs reply payload (polled ~1 s)
	Error = 0xFFFF,    // reply only: u32 ErrorCode, str message
};

enum class ErrorCode : std::uint32_t {
	UnknownMessage = 1,
	BadPayload = 2,
	Failed = 3,
	VersionMismatch = 4,
};

enum Capability : std::uint32_t {
	CapabilityEncode = 1u << 0,
	CapabilityVst3 = 1u << 1,
	CapabilityMidi = 1u << 2,
};

struct Frame {
	std::uint16_t type = 0;
	std::uint16_t flags = 0;
	std::uint32_t id = 0;
	std::vector<unsigned char> payload;
};

// Serialises a frame (header + payload).
std::vector<unsigned char> encodeFrame(const Frame& frame);

// Parses a 12-byte header. Returns false if the payload length exceeds kMaxPayload.
bool decodeHeader(const unsigned char* header, Frame& frame, std::uint32_t& payloadLength);

class PayloadWriter {
public:
	void u16(std::uint16_t v);
	void u32(std::uint32_t v);
	void u64(std::uint64_t v);
	void f32(float v);
	void str(const std::string& v);
	void bytes(const unsigned char* data, std::size_t size);
	std::vector<unsigned char>& data() { return data_; }

private:
	std::vector<unsigned char> data_;
};

class PayloadReader {
public:
	explicit PayloadReader(const std::vector<unsigned char>& data) : data_(data) {}
	bool u16(std::uint16_t& v);
	bool u32(std::uint32_t& v);
	bool u64(std::uint64_t& v);
	bool f32(float& v);
	bool str(std::string& v);
	bool bytes(std::size_t n, const unsigned char*& p) { return take(n, p); }
	bool atEnd() const { return pos_ == data_.size(); }

private:
	bool take(std::size_t n, const unsigned char*& p);
	const std::vector<unsigned char>& data_;
	std::size_t pos_ = 0;
};

} // namespace mfph
