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
	Encode = 0x0010,   // str wavPath, str oggPath, f32 quality (0..10) -> u64 frames, u32 sampleRate, u16 channels
	// 0x0100..0x01FF: plugins (scan, load, state, latency, process, editor windows) — VST3 phase
	// 0x0200..0x02FF: MIDI devices — MIDI phase
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
	bool atEnd() const { return pos_ == data_.size(); }

private:
	bool take(std::size_t n, const unsigned char*& p);
	const std::vector<unsigned char>& data_;
	std::size_t pos_ = 0;
};

} // namespace mfph
