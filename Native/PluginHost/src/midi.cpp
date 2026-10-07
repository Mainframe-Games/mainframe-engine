#include "midi.hpp"

#include "mainthread.hpp"

#include <algorithm>
#include <chrono>

#ifdef MFPH_MIDI
#include <RtMidi.h>

#include <condition_variable>
#include <memory>
#include <mutex>
#include <thread>
#endif

namespace mfph {

std::uint64_t steadyNowNs() {
	return static_cast<std::uint64_t>(
		std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

void writeMidiPorts(PayloadWriter& w, const std::vector<MidiPortInfo>& ports) {
	w.u32(static_cast<std::uint32_t>(ports.size()));
	for (const auto& p : ports) {
		w.u32(p.id);
		w.str(p.name);
		w.u16(p.online ? 1 : 0);
	}
}

Frame midiEventFrame(std::uint32_t deviceId, std::uint64_t timestampNs, const unsigned char* bytes, std::size_t size) {
	Frame f;
	f.type = static_cast<std::uint16_t>(MessageType::MidiEvent);
	f.id = 0;
	PayloadWriter w;
	w.u32(deviceId);
	w.u64(timestampNs);
	w.u16(static_cast<std::uint16_t>(size));
	w.bytes(bytes, size);
	f.payload = std::move(w.data());
	return f;
}

#ifndef MFPH_MIDI

bool midiSupported() { return false; }
int runMidiTestSource(const std::string&, int) { return 1; }
bool dispatchMidi(const Frame&, Frame&) { return false; }
void setMidiNotify(std::function<void(const Frame&)>) {}
void shutdownMidi() {}

#else

namespace {

struct Port {
	MidiPortInfo info;
	std::unique_ptr<RtMidiIn> in; // open
};

struct Midi {
	std::mutex mutex; // ports, enumerator
	std::vector<Port> ports;
	std::unique_ptr<RtMidiIn> enumerator;
	int supported = -1; // -1: not probed

	std::mutex notifyMutex;
	std::function<void(const Frame&)> notify;

	std::thread poll;
	std::mutex pollMutex;
	std::condition_variable pollWake;
	bool pollStop = false;
};

Midi& midi() {
	static Midi m;
	return m;
}

void ignoreErrors(RtMidiError::Type, const std::string&, void*) {}

void send(const Frame& f) {
	auto& m = midi();
	std::lock_guard lock(m.notifyMutex);
	if (m.notify)
		m.notify(f);
}

void onMessage(double, std::vector<unsigned char>* message, void* user) {
	if (message == nullptr || message->empty() || message->front() < 0x80 || message->front() >= 0xF0)
		return; // running status is expanded by RtMidi; system messages are not for the editor
	const auto id = static_cast<std::uint32_t>(reinterpret_cast<std::uintptr_t>(user));
	send(midiEventFrame(id, steadyNowNs(), message->data(), message->size()));
}

// Locked. The current port names, duplicates suffixed " #2", " #3", ...
std::vector<std::string> currentNames(Midi& m) {
	std::vector<std::string> names;
	if (!m.enumerator)
		return names;
	const unsigned count = m.enumerator->getPortCount();
	for (unsigned i = 0; i < count; ++i) {
		std::string name = m.enumerator->getPortName(i);
		std::string unique = name;
		for (int n = 2; std::find(names.begin(), names.end(), unique) != names.end(); ++n)
			unique = name + " #" + std::to_string(n);
		names.push_back(unique);
	}
	return names;
}

// Locked. Re-reads the port list; closes open ports that went away. Returns true when anything changed.
bool refresh(Midi& m) {
	const auto names = currentNames(m);
	bool changed = false;
	for (auto& p : m.ports) {
		const bool online = std::find(names.begin(), names.end(), p.info.name) != names.end();
		if (online != p.info.online) {
			p.info.online = online;
			changed = true;
		}
		if (!online && p.in) {
			p.in->cancelCallback();
			p.in.reset();
		}
	}
	for (const auto& name : names) {
		auto it = std::find_if(m.ports.begin(), m.ports.end(), [&](const Port& p) { return p.info.name == name; });
		if (it == m.ports.end()) {
			Port p;
			p.info.id = static_cast<std::uint32_t>(m.ports.size() + 1);
			p.info.name = name;
			p.info.online = true;
			m.ports.push_back(std::move(p));
			changed = true;
		}
	}
	return changed;
}

std::vector<MidiPortInfo> snapshot(Midi& m) {
	std::vector<MidiPortInfo> out;
	for (const auto& p : m.ports)
		out.push_back(p.info);
	return out;
}

void startPoll(Midi& m) {
	if (m.poll.joinable())
		return;
	m.pollStop = false;
	m.poll = std::thread([&m] {
		std::unique_lock wait(m.pollMutex);
		while (!m.pollWake.wait_for(wait, std::chrono::seconds(1), [&] { return m.pollStop; })) {
			std::vector<MidiPortInfo> ports;
			bool changed = false;
			runOnMainThread([&] {
				std::lock_guard lock(m.mutex);
				changed = refresh(m);
				ports = snapshot(m);
			});
			if (changed) {
				Frame f;
				f.type = static_cast<std::uint16_t>(MessageType::MidiDevicesChanged);
				PayloadWriter w;
				writeMidiPorts(w, ports);
				f.payload = std::move(w.data());
				send(f);
			}
		}
	});
}

void errorReply(Frame& reply, ErrorCode code, const std::string& message) {
	reply.type = static_cast<std::uint16_t>(MessageType::Error);
	PayloadWriter w;
	w.u32(static_cast<std::uint32_t>(code));
	w.str(message);
	reply.payload = std::move(w.data());
}

// Locked, main thread. Opens port `p` (by its current index). Returns false with `error` set.
bool openPort(Midi& m, Port& p, std::string& error) {
	const auto names = currentNames(m);
	const auto it = std::find(names.begin(), names.end(), p.info.name);
	if (it == names.end()) {
		error = "MIDI device '" + p.info.name + "' is offline";
		return false;
	}
	try {
		auto in = std::make_unique<RtMidiIn>(RtMidi::UNSPECIFIED, "Mainframe Engine");
		in->setErrorCallback(ignoreErrors, nullptr);
		in->ignoreTypes(true, true, true);
		in->setCallback(onMessage, reinterpret_cast<void*>(static_cast<std::uintptr_t>(p.info.id)));
		in->openPort(static_cast<unsigned>(it - names.begin()), "Mainframe Engine In");
		if (!in->isPortOpen()) {
			error = "could not open MIDI device '" + p.info.name + "'";
			return false;
		}
		p.in = std::move(in);
		return true;
	} catch (const RtMidiError& e) {
		error = e.getMessage();
		return false;
	}
}

} // namespace

bool midiSupported() {
	auto& m = midi();
	bool result = false;
	runOnMainThread([&] { // CoreMIDI delivers port changes to the run loop of the thread that created the client
		std::lock_guard lock(m.mutex);
		if (m.supported < 0) {
			try {
				m.enumerator = std::make_unique<RtMidiIn>(RtMidi::UNSPECIFIED, "Mainframe Engine");
				m.enumerator->setErrorCallback(ignoreErrors, nullptr);
				m.supported = m.enumerator->getCurrentApi() != RtMidi::RTMIDI_DUMMY ? 1 : 0;
			} catch (const RtMidiError&) {
				m.enumerator.reset();
				m.supported = 0;
			}
			if (m.supported == 0)
				m.enumerator.reset();
		}
		result = m.supported == 1;
	});
	return result;
}

bool dispatchMidi(const Frame& request, Frame& reply) {
	const auto type = static_cast<MessageType>(request.type);
	if (type != MessageType::MidiListInputs && type != MessageType::MidiOpen && type != MessageType::MidiClose)
		return false;
	if (!midiSupported()) {
		errorReply(reply, ErrorCode::Failed, "MIDI input is not available in this helper");
		return true;
	}
	auto& m = midi();
	PayloadReader in(request.payload);
	PayloadWriter out;
	std::string error;
	bool failed = false;
	std::uint32_t id = 0;
	if (type != MessageType::MidiListInputs && !in.u32(id)) {
		errorReply(reply, ErrorCode::BadPayload, "midi: bad payload");
		return true;
	}
	runOnMainThread([&] {
		std::lock_guard lock(m.mutex);
		refresh(m);
		if (type == MessageType::MidiListInputs) {
			writeMidiPorts(out, snapshot(m));
			return;
		}
		auto it = std::find_if(m.ports.begin(), m.ports.end(), [&](const Port& p) { return p.info.id == id; });
		if (it == m.ports.end()) {
			error = "unknown MIDI device " + std::to_string(id);
			failed = true;
		} else if (type == MessageType::MidiOpen) {
			if (!it->in)
				failed = !openPort(m, *it, error);
		} else if (it->in) {
			it->in->cancelCallback();
			it->in.reset();
		}
	});
	startPoll(m);
	if (failed) {
		errorReply(reply, ErrorCode::Failed, error);
		return true;
	}
	reply.payload = std::move(out.data());
	return true;
}

int runMidiTestSource(const std::string& name, int seconds) {
#ifdef _WIN32
	(void)name;
	(void)seconds;
	return 1;
#else
	try {
		RtMidiOut out(RtMidi::UNSPECIFIED, name);
		out.openVirtualPort(name);
		const auto end = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
		const std::vector<unsigned char> on{0x90, 60, 100}, off{0x80, 60, 0};
		while (std::chrono::steady_clock::now() < end) {
			out.sendMessage(&on);
			std::this_thread::sleep_for(std::chrono::milliseconds(100));
			out.sendMessage(&off);
			std::this_thread::sleep_for(std::chrono::milliseconds(100));
		}
		return 0;
	} catch (const RtMidiError&) {
		return 1;
	}
#endif
}

void setMidiNotify(std::function<void(const Frame&)> notify) {
	auto& m = midi();
	std::lock_guard lock(m.notifyMutex);
	m.notify = std::move(notify);
}

void shutdownMidi() {
	auto& m = midi();
	{
		std::lock_guard lock(m.pollMutex);
		m.pollStop = true;
	}
	m.pollWake.notify_all();
	if (m.poll.joinable())
		m.poll.join();
	runOnMainThread([&] {
		std::lock_guard lock(m.mutex);
		for (auto& p : m.ports)
			if (p.in) {
				p.in->cancelCallback();
				p.in.reset();
			}
	});
}

#endif

} // namespace mfph
