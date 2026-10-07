#include "shm.hpp"

#include <atomic>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace mfph {
namespace {

std::uint32_t readU32(const unsigned char* p) {
	std::uint32_t v;
	std::memcpy(&v, p, 4);
	return v;
}

#ifdef _WIN32
std::wstring widen(const std::string& s) {
	int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
	std::wstring w(static_cast<std::size_t>(n > 0 ? n - 1 : 0), L'\0');
	if (n > 1)
		MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
	return w;
}
#endif

} // namespace

bool SharedMemory::open(const std::string& path, std::size_t size, std::string& error) {
	close();
	if (size < kShmHeaderSize) {
		error = "shared memory: too small";
		return false;
	}
#ifdef _WIN32
	file_ = CreateFileW(widen(path).c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
		nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
	if (file_ == INVALID_HANDLE_VALUE) {
		file_ = nullptr;
		error = "shared memory: cannot open " + path;
		return false;
	}
	mapping_ = CreateFileMappingW(file_, nullptr, PAGE_READWRITE, 0, 0, nullptr);
	void* view = mapping_ ? MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, size) : nullptr;
	if (view == nullptr) {
		close();
		error = "shared memory: cannot map " + path;
		return false;
	}
	data_ = static_cast<unsigned char*>(view);
#else
	const int fd = ::open(path.c_str(), O_RDWR);
	if (fd < 0) {
		error = "shared memory: cannot open " + path;
		return false;
	}
	struct stat st {};
	if (::fstat(fd, &st) != 0 || static_cast<std::size_t>(st.st_size) < size) {
		::close(fd);
		error = "shared memory: " + path + " is smaller than " + std::to_string(size) + " bytes";
		return false;
	}
	void* view = ::mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
	::close(fd);
	if (view == MAP_FAILED) {
		error = "shared memory: cannot map " + path;
		return false;
	}
	data_ = static_cast<unsigned char*>(view);
#endif
	size_ = size;
	maxBlock_ = readU32(data_ + 8);
	slotCount_ = readU32(data_ + 12);
	maxEvents_ = readU32(data_ + 16);
	stride_ = readU32(data_ + 20);
	const std::size_t needed = 16u * maxBlock_ + 16u + kShmEventSize * maxEvents_;
	if (readU32(data_) != kShmMagic || readU32(data_ + 4) != kShmVersion || maxBlock_ == 0 || stride_ < needed || stride_ % 16 != 0 ||
		kShmHeaderSize + static_cast<std::size_t>(slotCount_) * stride_ > size) {
		close();
		error = "shared memory: bad header";
		return false;
	}
	return true;
}

void SharedMemory::close() {
#ifdef _WIN32
	if (data_)
		UnmapViewOfFile(data_);
	if (mapping_)
		CloseHandle(mapping_);
	if (file_)
		CloseHandle(file_);
	mapping_ = file_ = nullptr;
#else
	if (data_)
		::munmap(data_, size_);
#endif
	data_ = nullptr;
	size_ = 0;
	maxBlock_ = slotCount_ = maxEvents_ = stride_ = 0;
}

float* SharedMemory::channel(std::uint32_t index, int which) const {
	return reinterpret_cast<float*>(slot(index)) + static_cast<std::size_t>(which) * maxBlock_;
}

std::uint32_t* SharedMemory::eventCount(std::uint32_t index) const {
	return reinterpret_cast<std::uint32_t*>(slot(index) + 16u * maxBlock_);
}

const ShmEvent* SharedMemory::events(std::uint32_t index) const {
	return reinterpret_cast<const ShmEvent*>(slot(index) + 16u * maxBlock_ + 16u);
}

void SharedMemory::setCurrentInstance(std::uint32_t id) const {
	if (data_) {
		std::memcpy(data_ + 24, &id, 4);
		std::atomic_thread_fence(std::memory_order_seq_cst);
	}
}

} // namespace mfph
