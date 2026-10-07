// Stream-socket transport for the control protocol: a Unix domain socket on every OS (Windows 10 1803+ has AF_UNIX,
// which .NET's UnixDomainSocketEndPoint also uses), so the editor and the helper share one code path.
#pragma once

#include "protocol.hpp"

#include <cstdint>
#include <string>

namespace mfph {

#ifdef _WIN32
using SocketHandle = std::uintptr_t;
#else
using SocketHandle = int;
#endif

// Once per process before any socket call (WSAStartup on Windows; a no-op elsewhere).
bool initSockets(std::string& error);

enum class ReadResult { Ok, Closed, Error };

// One connected stream socket (owning).
class Connection {
public:
	Connection() = default;
	explicit Connection(SocketHandle handle) : handle_(handle), open_(true) {}
	~Connection();
	Connection(Connection&& other) noexcept;
	Connection& operator=(Connection&& other) noexcept;
	Connection(const Connection&) = delete;
	Connection& operator=(const Connection&) = delete;

	// Connects to the socket at `path` (tests; the editor connects from .NET).
	static bool connect(const std::string& path, Connection& out, std::string& error);

	bool isOpen() const { return open_; }
	ReadResult readFrame(Frame& frame, std::string& error);
	bool writeFrame(const Frame& frame, std::string& error);
	void close();

private:
	bool readExact(unsigned char* data, std::size_t size, bool& closed);
	SocketHandle handle_{};
	bool open_ = false;
};

// A listening Unix domain socket at a path (removed again on close).
class Listener {
public:
	Listener() = default;
	~Listener();
	Listener(const Listener&) = delete;
	Listener& operator=(const Listener&) = delete;

	bool listen(const std::string& path, std::string& error);
	// Waits up to `timeoutMs` for a client. Returns false on timeout or error (`error` is empty on timeout).
	bool accept(int timeoutMs, Connection& out, std::string& error);
	void close();

private:
	SocketHandle handle_{};
	bool open_ = false;
	std::string path_;
};

} // namespace mfph
