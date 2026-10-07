#include "server.hpp"

#include "encode.hpp"
#include "transport.hpp"
#include "version.hpp"

#include <chrono>
#include <thread>

#ifdef _WIN32
#include <process.h>
#else
#include <csignal>
#include <unistd.h>
#endif

namespace mfph {
namespace {

std::uint32_t processId() {
#ifdef _WIN32
	return static_cast<std::uint32_t>(_getpid());
#else
	return static_cast<std::uint32_t>(::getpid());
#endif
}

void errorReply(Frame& reply, ErrorCode code, const std::string& message) {
	reply.type = static_cast<std::uint16_t>(MessageType::Error);
	PayloadWriter w;
	w.u32(static_cast<std::uint32_t>(code));
	w.str(message);
	reply.payload = std::move(w.data());
}

} // namespace

void dispatch(const Frame& request, Frame& reply, bool& shutdown) {
	shutdown = false;
	reply = Frame{};
	reply.id = request.id;
	reply.type = static_cast<std::uint16_t>(request.type | kReplyBit);
	PayloadReader in(request.payload);
	PayloadWriter out;

	switch (static_cast<MessageType>(request.type)) {
	case MessageType::Hello: {
		std::uint32_t version;
		std::string client;
		if (!in.u32(version) || !in.str(client))
			return errorReply(reply, ErrorCode::BadPayload, "hello: bad payload");
		if (version != kProtocolVersion)
			return errorReply(reply, ErrorCode::VersionMismatch,
				"protocol " + std::to_string(version) + " requested; this helper speaks " + std::to_string(kProtocolVersion));
		out.u32(kProtocolVersion);
		out.str(kVersion);
		out.u32(CapabilityEncode);
		out.u32(processId());
		break;
	}
	case MessageType::Ping:
		out.bytes(request.payload.data(), request.payload.size());
		break;
	case MessageType::Shutdown:
		shutdown = true;
		break;
	case MessageType::Sleep: {
		std::uint32_t ms;
		if (!in.u32(ms))
			return errorReply(reply, ErrorCode::BadPayload, "sleep: bad payload");
		std::this_thread::sleep_for(std::chrono::milliseconds(ms));
		break;
	}
	case MessageType::Encode: {
		std::string wav, ogg;
		float quality;
		if (!in.str(wav) || !in.str(ogg) || !in.f32(quality))
			return errorReply(reply, ErrorCode::BadPayload, "encode: bad payload");
		EncodeResult result;
		std::string error;
		if (!encodeWavToOgg(pathFromUtf8(wav), pathFromUtf8(ogg), quality, result, error))
			return errorReply(reply, ErrorCode::Failed, error);
		out.u64(result.frames);
		out.u32(static_cast<std::uint32_t>(result.sampleRate));
		out.u16(static_cast<std::uint16_t>(result.channels));
		break;
	}
	default:
		return errorReply(reply, ErrorCode::UnknownMessage, "unknown message type " + std::to_string(request.type));
	}
	reply.payload = std::move(out.data());
}

int serve(const std::string& socketPath, std::ostream& log, int acceptTimeoutMs) {
#ifndef _WIN32
	std::signal(SIGPIPE, SIG_IGN); // a vanished editor is a send error, not a signal
#endif
	std::string error;
	if (!initSockets(error)) {
		log << "mfplughost: " << error << std::endl;
		return 1;
	}
	Listener listener;
	if (!listener.listen(socketPath, error)) {
		log << "mfplughost: " << error << std::endl;
		return 1;
	}
	Connection connection;
	if (!listener.accept(acceptTimeoutMs, connection, error)) {
		log << "mfplughost: " << (error.empty() ? "no editor connected within the timeout" : error) << std::endl;
		return 1;
	}
	listener.close(); // one editor per helper: the socket file goes away once connected

	for (;;) {
		Frame request;
		switch (connection.readFrame(request, error)) {
		case ReadResult::Closed:
			return 0; // the editor closed the connection (or died)
		case ReadResult::Error:
			log << "mfplughost: " << error << std::endl;
			return 1;
		case ReadResult::Ok:
			break;
		}
		Frame reply;
		bool shutdown;
		dispatch(request, reply, shutdown);
		if (!connection.writeFrame(reply, error)) {
			log << "mfplughost: " << error << std::endl;
			return 1;
		}
		if (shutdown)
			return 0;
	}
}

} // namespace mfph
