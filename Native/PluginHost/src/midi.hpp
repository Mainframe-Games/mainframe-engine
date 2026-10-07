// MIDI input devices (MF_PLUGINHOST_MIDI; RtMidi; ADR 0148): the 0x0200-0x02FF messages.
//
// Devices are identified by u32 ids the helper assigns per port name for its lifetime (a second port with the same
// name gets "name #2"), so an unplugged keyboard keeps its id when it comes back. A poll thread re-reads the port list
// about once a second and sends MidiDevicesChanged when it changes; an open device that goes away is closed, and the
// editor opens it again when it is back. Incoming messages (note on/off, control change, pitch bend, aftertouch;
// sysex/clock/active sensing ignored) are sent as MidiEvent notifications stamped with the helper's steady clock.
#pragma once

#include "protocol.hpp"

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace mfph {

// Built with RtMidi and a MIDI API that initialised (Linux without ALSA, or without /dev/snd/seq: false).
bool midiSupported();

// Handles a MIDI message (protocol thread). Returns false when `request` is not one.
bool dispatchMidi(const Frame& request, Frame& reply);

// Where MidiEvent / MidiDevicesChanged notifications go; called from RtMidi's and the poll thread.
void setMidiNotify(std::function<void(const Frame&)> notify);

// Closes every port and stops the poll thread.
void shutdownMidi();

// The helper's monotonic clock (std::chrono::steady_clock) in nanoseconds: MidiEvent timestamps and the Clock reply.
std::uint64_t steadyNowNs();

// `mfplughost --midi-test-source <name> <seconds>` (tests only): a virtual MIDI output named <name> that plays C4
// (note on, velocity 100, then note off) every 200 ms. Returns 1 where the platform has no virtual ports (Windows).
int runMidiTestSource(const std::string& name, int seconds);

struct MidiPortInfo {
	std::uint32_t id = 0;
	std::string name;
	bool online = false;
};

// The wire encodings (shared with the tests).
void writeMidiPorts(PayloadWriter& w, const std::vector<MidiPortInfo>& ports);
Frame midiEventFrame(std::uint32_t deviceId, std::uint64_t timestampNs, const unsigned char* bytes, std::size_t size);

} // namespace mfph
