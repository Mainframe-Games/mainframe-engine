// mfplughost version and protocol version (docs/design/natives.md, ADR 0146).
#pragma once

#include <cstdint>

namespace mfph {

// The helper's version (CMake project version).
inline constexpr const char* kVersion = MFPH_VERSION;

// Bumped on any incompatible protocol change; the editor refuses a helper whose version differs.
inline constexpr std::uint32_t kProtocolVersion = 1;

} // namespace mfph
