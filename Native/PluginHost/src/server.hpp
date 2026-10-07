// `mfplughost --serve <socket-path>`: listens on the socket, serves one editor connection, exits when it closes.
#pragma once

#include "protocol.hpp"

#include <ostream>
#include <string>

namespace mfph {

// How long the helper waits for the editor to connect before giving up.
inline constexpr int kAcceptTimeoutMs = 30000;

// Handles one request. Sets `shutdown` when the helper should exit after sending `reply`.
void dispatch(const Frame& request, Frame& reply, bool& shutdown);

// Runs the helper's server loop. Returns the process exit code (0: the editor shut it down or disconnected).
int serve(const std::string& socketPath, std::ostream& log, int acceptTimeoutMs = kAcceptTimeoutMs);

} // namespace mfph
