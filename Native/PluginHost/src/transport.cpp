#include "transport.hpp"

#include "encode.hpp"

#include <cstring>
#include <filesystem>
#include <system_error>
#include <utility>

#ifdef _WIN32
#include <winsock2.h>
#include <afunix.h>
#else
#include <cerrno>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

namespace mfph {
namespace {

#ifdef _WIN32
using RawSocket = SOCKET;
constexpr RawSocket kInvalid = INVALID_SOCKET;
void closeRaw(RawSocket s) { closesocket(s); }
std::string lastError() { return "socket error " + std::to_string(WSAGetLastError()); }
int pollOne(RawSocket s, int timeoutMs) {
	WSAPOLLFD fd{};
	fd.fd = s;
	fd.events = POLLRDNORM;
	return WSAPoll(&fd, 1, timeoutMs);
}
#else
using RawSocket = int;
constexpr RawSocket kInvalid = -1;
void closeRaw(RawSocket s) { ::close(s); }
std::string lastError() { return std::strerror(errno); }
int pollOne(RawSocket s, int timeoutMs) {
	pollfd fd{};
	fd.fd = s;
	fd.events = POLLIN;
	int r;
	do {
		r = ::poll(&fd, 1, timeoutMs);
	} while (r < 0 && errno == EINTR);
	return r;
}
#endif

RawSocket raw(SocketHandle h) { return static_cast<RawSocket>(h); }

#ifdef _WIN32
constexpr int kAddressLength = static_cast<int>(sizeof(sockaddr_un));
#else
constexpr socklen_t kAddressLength = static_cast<socklen_t>(sizeof(sockaddr_un));
#endif

bool fillAddress(const std::string& path, sockaddr_un& address, std::string& error) {
	std::memset(&address, 0, sizeof address);
	address.sun_family = AF_UNIX;
	if (path.empty() || path.size() >= sizeof address.sun_path) {
		error = "socket path is empty or longer than " + std::to_string(sizeof address.sun_path - 1) + " bytes: " + path;
		return false;
	}
	std::memcpy(address.sun_path, path.data(), path.size());
	return true;
}

void removePath(const std::string& path) {
	std::error_code ec;
	std::filesystem::remove(pathFromUtf8(path), ec);
}

} // namespace

bool initSockets(std::string& error) {
#ifdef _WIN32
	WSADATA data;
	if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
		error = "WSAStartup failed";
		return false;
	}
#else
	(void)error;
#endif
	return true;
}

Connection::~Connection() { close(); }

Connection::Connection(Connection&& other) noexcept : handle_(other.handle_), open_(std::exchange(other.open_, false)) {}

Connection& Connection::operator=(Connection&& other) noexcept {
	if (this != &other) {
		close();
		handle_ = other.handle_;
		open_ = std::exchange(other.open_, false);
	}
	return *this;
}

void Connection::close() {
	if (open_) {
		closeRaw(raw(handle_));
		open_ = false;
	}
}

bool Connection::connect(const std::string& path, Connection& out, std::string& error) {
	sockaddr_un address;
	if (!fillAddress(path, address, error))
		return false;
	RawSocket s = ::socket(AF_UNIX, SOCK_STREAM, 0);
	if (s == kInvalid) {
		error = lastError();
		return false;
	}
	if (::connect(s, reinterpret_cast<const sockaddr*>(&address), kAddressLength) != 0) {
		error = "connect " + path + ": " + lastError();
		closeRaw(s);
		return false;
	}
	out = Connection(static_cast<SocketHandle>(s));
	return true;
}

bool Connection::readExact(unsigned char* data, std::size_t size, bool& closed) {
	closed = false;
	std::size_t done = 0;
	while (done < size) {
#ifdef _WIN32
		int n = ::recv(raw(handle_), reinterpret_cast<char*>(data + done), static_cast<int>(size - done), 0);
#else
		ssize_t n = ::recv(raw(handle_), data + done, size - done, 0);
		if (n < 0 && errno == EINTR)
			continue;
#endif
		if (n == 0) {
			closed = true;
			return false;
		}
		if (n < 0)
			return false;
		done += static_cast<std::size_t>(n);
	}
	return true;
}

ReadResult Connection::readFrame(Frame& frame, std::string& error) {
	unsigned char header[kHeaderSize];
	bool closed;
	if (!readExact(header, kHeaderSize, closed)) {
		if (closed)
			return ReadResult::Closed;
		error = lastError();
		return ReadResult::Error;
	}
	std::uint32_t length;
	if (!decodeHeader(header, frame, length)) {
		error = "frame payload of " + std::to_string(length) + " bytes exceeds the limit";
		return ReadResult::Error;
	}
	frame.payload.resize(length);
	if (length > 0 && !readExact(frame.payload.data(), length, closed)) {
		if (closed)
			return ReadResult::Closed;
		error = lastError();
		return ReadResult::Error;
	}
	return ReadResult::Ok;
}

bool Connection::writeFrame(const Frame& frame, std::string& error) {
	const std::vector<unsigned char> bytes = encodeFrame(frame);
	std::size_t done = 0;
	while (done < bytes.size()) {
#ifdef _WIN32
		int n = ::send(raw(handle_), reinterpret_cast<const char*>(bytes.data() + done), static_cast<int>(bytes.size() - done), 0);
#else
		int flags = 0;
#ifdef MSG_NOSIGNAL
		flags = MSG_NOSIGNAL; // a closed peer is an error return, not SIGPIPE (Linux)
#endif
		ssize_t n = ::send(raw(handle_), bytes.data() + done, bytes.size() - done, flags);
		if (n < 0 && errno == EINTR)
			continue;
#endif
		if (n <= 0) {
			error = lastError();
			return false;
		}
		done += static_cast<std::size_t>(n);
	}
	return true;
}

Listener::~Listener() { close(); }

void Listener::close() {
	if (open_) {
		closeRaw(raw(handle_));
		open_ = false;
		removePath(path_);
	}
}

bool Listener::listen(const std::string& path, std::string& error) {
	sockaddr_un address;
	if (!fillAddress(path, address, error))
		return false;
	removePath(path); // a stale socket file from a crashed helper
	RawSocket s = ::socket(AF_UNIX, SOCK_STREAM, 0);
	if (s == kInvalid) {
		error = lastError();
		return false;
	}
	if (::bind(s, reinterpret_cast<const sockaddr*>(&address), kAddressLength) != 0 || ::listen(s, 1) != 0) {
		error = "listen " + path + ": " + lastError();
		closeRaw(s);
		return false;
	}
	handle_ = static_cast<SocketHandle>(s);
	open_ = true;
	path_ = path;
	return true;
}

bool Listener::accept(int timeoutMs, Connection& out, std::string& error) {
	const int ready = pollOne(raw(handle_), timeoutMs);
	if (ready == 0)
		return false;
	if (ready < 0) {
		error = lastError();
		return false;
	}
	RawSocket s = ::accept(raw(handle_), nullptr, nullptr);
	if (s == kInvalid) {
		error = lastError();
		return false;
	}
	out = Connection(static_cast<SocketHandle>(s));
	return true;
}

} // namespace mfph
