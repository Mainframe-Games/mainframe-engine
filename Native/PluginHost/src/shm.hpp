// The file-backed shared memory the editor and the helper exchange audio and note events through (ADR 0147). The
// editor creates and sizes the file, writes the header and sends its path (PluginSetupShm); both sides map it.
//
//   header (64 bytes): u32 magic 'MFSM', u32 version, u32 maxBlockFrames, u32 slotCount, u32 maxEvents, u32 slotStride,
//                      u32 currentInstance (the helper writes the instance it is calling into, 0 when none: after a
//                      crash the editor reads it to blame the right plugin), u32 reserved[9]
//   slot i at 64 + i * slotStride (one per loaded instance, chosen by the editor at load):
//                      f32 inL[maxBlock], inR[maxBlock], outL[maxBlock], outR[maxBlock],
//                      u32 eventCount, u32 reserved[3],
//                      event[maxEvents] (16 bytes: i32 sampleOffset, u8 type (0 off, 1 on), u8 channel, u8 pitch,
//                      u8 velocity (0..127), u32 reserved[2])
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace mfph {

inline constexpr std::uint32_t kShmMagic = 0x4D53464Du; // "MFSM"
inline constexpr std::uint32_t kShmVersion = 1;
inline constexpr std::size_t kShmHeaderSize = 64;
inline constexpr std::size_t kShmEventSize = 16;

struct ShmEvent {
	std::int32_t sampleOffset;
	std::uint8_t type;
	std::uint8_t channel;
	std::uint8_t pitch;
	std::uint8_t velocity;
	std::uint32_t reserved[2];
};
static_assert(sizeof(ShmEvent) == kShmEventSize);

class SharedMemory {
public:
	SharedMemory() = default;
	~SharedMemory() { close(); }
	SharedMemory(const SharedMemory&) = delete;
	SharedMemory& operator=(const SharedMemory&) = delete;

	// Maps an existing file of `size` bytes and validates its header.
	bool open(const std::string& path, std::size_t size, std::string& error);
	void close();
	bool isOpen() const { return data_ != nullptr; }

	std::uint32_t maxBlock() const { return maxBlock_; }
	std::uint32_t slotCount() const { return slotCount_; }
	std::uint32_t maxEvents() const { return maxEvents_; }

	// Channel 0..3: inL, inR, outL, outR.
	float* channel(std::uint32_t slot, int which) const;
	std::uint32_t* eventCount(std::uint32_t slot) const;
	const ShmEvent* events(std::uint32_t slot) const;
	void setCurrentInstance(std::uint32_t id) const;

private:
	unsigned char* slot(std::uint32_t index) const { return data_ + kShmHeaderSize + static_cast<std::size_t>(index) * stride_; }
	unsigned char* data_ = nullptr;
	std::size_t size_ = 0;
	std::uint32_t maxBlock_ = 0, slotCount_ = 0, maxEvents_ = 0, stride_ = 0;
#ifdef _WIN32
	void* file_ = nullptr;
	void* mapping_ = nullptr;
#endif
};

} // namespace mfph
