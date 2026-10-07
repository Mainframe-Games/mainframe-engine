#include "protocol.hpp"

#include <cstring>

namespace mfph {
namespace {

void put(std::vector<unsigned char>& out, std::uint64_t v, int bytes) {
	for (int i = 0; i < bytes; ++i)
		out.push_back(static_cast<unsigned char>((v >> (8 * i)) & 0xFF));
}

std::uint64_t get(const unsigned char* p, int bytes) {
	std::uint64_t v = 0;
	for (int i = 0; i < bytes; ++i)
		v |= static_cast<std::uint64_t>(p[i]) << (8 * i);
	return v;
}

} // namespace

std::vector<unsigned char> encodeFrame(const Frame& frame) {
	std::vector<unsigned char> out;
	out.reserve(kHeaderSize + frame.payload.size());
	put(out, frame.payload.size(), 4);
	put(out, frame.type, 2);
	put(out, frame.flags, 2);
	put(out, frame.id, 4);
	out.insert(out.end(), frame.payload.begin(), frame.payload.end());
	return out;
}

bool decodeHeader(const unsigned char* header, Frame& frame, std::uint32_t& payloadLength) {
	payloadLength = static_cast<std::uint32_t>(get(header, 4));
	frame.type = static_cast<std::uint16_t>(get(header + 4, 2));
	frame.flags = static_cast<std::uint16_t>(get(header + 6, 2));
	frame.id = static_cast<std::uint32_t>(get(header + 8, 4));
	return payloadLength <= kMaxPayload;
}

void PayloadWriter::u16(std::uint16_t v) { put(data_, v, 2); }
void PayloadWriter::u32(std::uint32_t v) { put(data_, v, 4); }
void PayloadWriter::u64(std::uint64_t v) { put(data_, v, 8); }

void PayloadWriter::f32(float v) {
	std::uint32_t raw;
	std::memcpy(&raw, &v, sizeof raw);
	put(data_, raw, 4);
}

void PayloadWriter::str(const std::string& v) {
	u32(static_cast<std::uint32_t>(v.size()));
	data_.insert(data_.end(), v.begin(), v.end());
}

void PayloadWriter::bytes(const unsigned char* data, std::size_t size) { data_.insert(data_.end(), data, data + size); }

bool PayloadReader::take(std::size_t n, const unsigned char*& p) {
	if (data_.size() - pos_ < n)
		return false;
	p = data_.data() + pos_;
	pos_ += n;
	return true;
}

bool PayloadReader::u16(std::uint16_t& v) {
	const unsigned char* p;
	if (!take(2, p))
		return false;
	v = static_cast<std::uint16_t>(get(p, 2));
	return true;
}

bool PayloadReader::u32(std::uint32_t& v) {
	const unsigned char* p;
	if (!take(4, p))
		return false;
	v = static_cast<std::uint32_t>(get(p, 4));
	return true;
}

bool PayloadReader::u64(std::uint64_t& v) {
	const unsigned char* p;
	if (!take(8, p))
		return false;
	v = get(p, 8);
	return true;
}

bool PayloadReader::f32(float& v) {
	std::uint32_t raw;
	if (!u32(raw))
		return false;
	std::memcpy(&v, &raw, sizeof v);
	return true;
}

bool PayloadReader::str(std::string& v) {
	std::uint32_t size;
	const unsigned char* p;
	if (!u32(size) || !take(size, p))
		return false;
	v.assign(reinterpret_cast<const char*>(p), size);
	return true;
}

} // namespace mfph
