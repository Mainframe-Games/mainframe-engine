// VST3 hosting (ADR 0147) on the VST 3 SDK's hosting classes. Control calls (load, state, editor windows) run on the
// main thread (runOnMainThread); PluginProcess runs on the protocol thread, which is the plugins' audio thread. The
// editor serialises requests, so the two never run at the same time except an editor window the user closes.
#include "plugins.hpp"

#include "mainthread.hpp"
#include "shm.hpp"

#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"

#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <sstream>
#include <vector>

#ifdef _WIN32
#include <io.h>
#else
#include <unistd.h>
#endif

namespace mfph {
namespace {

using namespace Steinberg;
using namespace Steinberg::Vst;

constexpr std::uint32_t kOwnInput = 0xFFFFFFFFu;
constexpr int32 kMaxEventsPerBlock = 1024;

struct Instance {
	std::uint32_t id = 0;
	std::uint32_t slot = 0;
	std::string name;
	VST3::Hosting::Module::Ptr module;
	IPtr<PlugProvider> provider;
	IPtr<IComponent> component;
	IPtr<IAudioProcessor> processor;
	IPtr<IEditController> controller;
	int32 inChannels = 0;
	int32 outChannels = 0;
	std::vector<AudioBusBuffers> inBuses, outBuses;
	std::vector<std::vector<float*>> inPtrs, outPtrs;
	std::vector<float> scratch;
	EventList events{kMaxEventsPerBlock};
	ParameterChanges inChanges{0};
	ParameterChanges outChanges{256};
	EditorWindow* editor = nullptr;
};

struct Host {
	std::map<std::uint32_t, std::unique_ptr<Instance>> instances;
	std::map<std::string, VST3::Hosting::Module::Ptr> modules;
	SharedMemory shm;
	IPtr<HostApplication> app;
	std::uint32_t nextId = 1;
	double sampleRate = 48000.0;
	int32 maxBlock = 256;
	bool offline = false;
	std::function<void(const Frame&)> notify;
	ProcessContext context{};
};

Host& host() {
	static Host* h = new Host(); // never destroyed: plugins must not be torn down by static destructors
	return *h;
}

void ensureHostApp() {
	Host& h = host();
	if (!h.app) {
		h.app = owned(new HostApplication());
		PluginContextFactory::instance().setPluginContext(h.app);
	}
}

void errorReply(Frame& reply, ErrorCode code, const std::string& message) {
	reply.type = static_cast<std::uint16_t>(MessageType::Error);
	PayloadWriter w;
	w.u32(static_cast<std::uint32_t>(code));
	w.str(message);
	reply.payload = std::move(w.data());
}

std::string jsonEscape(const std::string& s) {
	std::string out;
	for (char c : s) {
		switch (c) {
		case '"': out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:
			if (static_cast<unsigned char>(c) < 0x20) {
				char buf[8];
				std::snprintf(buf, sizeof(buf), "\\u%04x", static_cast<unsigned>(static_cast<unsigned char>(c)));
				out += buf;
			} else {
				out += c;
			}
		}
	}
	return out;
}

// Plugins print to stdout while loading; --scan prints its JSON there, so stdout goes to stderr meanwhile.
class StdoutToStderr {
public:
	StdoutToStderr() {
		std::fflush(stdout);
#ifdef _WIN32
		saved_ = _dup(1);
		_dup2(2, 1);
#else
		saved_ = ::dup(1);
		::dup2(2, 1);
#endif
	}
	~StdoutToStderr() {
		std::fflush(stdout);
		if (saved_ >= 0) {
#ifdef _WIN32
			_dup2(saved_, 1);
			_close(saved_);
#else
			::dup2(saved_, 1);
			::close(saved_);
#endif
		}
	}
	StdoutToStderr(const StdoutToStderr&) = delete;
	StdoutToStderr& operator=(const StdoutToStderr&) = delete;

private:
	int saved_ = -1;
};

VST3::Hosting::Module::Ptr moduleFor(const std::string& path, std::string& error) {
	Host& h = host();
	if (auto it = h.modules.find(path); it != h.modules.end())
		return it->second;
	auto module = VST3::Hosting::Module::create(path, error);
	if (module)
		h.modules[path] = module;
	else if (error.empty())
		error = "cannot load " + path;
	return module;
}

bool isInstrument(const VST3::Hosting::ClassInfo& info) {
	return info.subCategoriesString().find("Instrument") != std::string::npos;
}

int32 channelCount(IAudioProcessor* processor, BusDirection dir, int32 bus) {
	SpeakerArrangement arr = 0;
	if (processor->getBusArrangement(dir, bus, arr) != kResultTrue)
		return 0;
	return SpeakerArr::getChannelCount(arr);
}

// (Re)starts processing in the current mode: setupProcessing, setActive(true), setProcessing(true).
bool startProcessing(Instance& inst, std::string& error) {
	Host& h = host();
	ProcessSetup setup{h.offline ? kOffline : kRealtime, kSample32, h.maxBlock, h.sampleRate};
	h.shm.setCurrentInstance(inst.id);
	const bool ok = inst.processor->setupProcessing(setup) == kResultOk && inst.component->setActive(true) == kResultOk;
	if (ok)
		inst.processor->setProcessing(true); // kNotImplemented is fine
	h.shm.setCurrentInstance(0);
	if (!ok)
		error = "the plugin rejected " + std::to_string(static_cast<int>(h.sampleRate)) + " Hz / " + std::to_string(h.maxBlock) +
				" frames";
	return ok;
}

void stopProcessing(Instance& inst) {
	inst.processor->setProcessing(false);
	inst.component->setActive(false);
}

// Bus buffers: the main stereo buses point into the shared memory slot, everything else at scratch.
void bindBuffers(Instance& inst) {
	Host& h = host();
	const int32 inCount = inst.component->getBusCount(kAudio, kInput);
	const int32 outCount = inst.component->getBusCount(kAudio, kOutput);
	std::size_t scratchChannels = 0;
	std::vector<int32> inCh(static_cast<std::size_t>(inCount)), outCh(static_cast<std::size_t>(outCount));
	for (int32 i = 0; i < inCount; ++i)
		scratchChannels += static_cast<std::size_t>(inCh[static_cast<std::size_t>(i)] = channelCount(inst.processor, kInput, i));
	for (int32 i = 0; i < outCount; ++i)
		scratchChannels += static_cast<std::size_t>(outCh[static_cast<std::size_t>(i)] = channelCount(inst.processor, kOutput, i));
	inst.scratch.assign(scratchChannels * static_cast<std::size_t>(h.maxBlock), 0.0f);
	std::size_t next = 0;
	auto scratch = [&] { return inst.scratch.data() + (next++) * static_cast<std::size_t>(h.maxBlock); };
	auto bind = [&](std::vector<AudioBusBuffers>& buses, std::vector<std::vector<float*>>& ptrs, const std::vector<int32>& counts,
					int firstShmChannel) {
		buses.assign(counts.size(), AudioBusBuffers{});
		ptrs.assign(counts.size(), {});
		for (std::size_t b = 0; b < counts.size(); ++b) {
			for (int32 c = 0; c < counts[b]; ++c)
				ptrs[b].push_back(b == 0 && c < 2 ? h.shm.channel(inst.slot, firstShmChannel + c) : scratch());
			buses[b].numChannels = counts[b];
			buses[b].channelBuffers32 = ptrs[b].empty() ? nullptr : ptrs[b].data();
		}
	};
	bind(inst.inBuses, inst.inPtrs, inCh, 0);
	bind(inst.outBuses, inst.outPtrs, outCh, 2);
	inst.inChannels = inCount > 0 ? inCh[0] : 0;
	inst.outChannels = outCount > 0 ? outCh[0] : 0;
}

std::unique_ptr<Instance> load(const std::string& path, const std::string& classId, std::uint32_t slot, std::string& error) {
	Host& h = host();
	ensureHostApp();
	auto module = moduleFor(path, error);
	if (!module)
		return nullptr;
	auto uid = VST3::UID::fromString(classId);
	if (!uid) {
		error = "bad class id " + classId;
		return nullptr;
	}
	const auto& factory = module->getFactory();
	for (const auto& info : factory.classInfos()) {
		if (info.ID() != *uid || info.category() != kVstAudioEffectClass)
			continue;
		auto inst = std::make_unique<Instance>();
		inst->id = h.nextId++;
		inst->slot = slot;
		inst->name = info.name();
		inst->module = module;
		h.shm.setCurrentInstance(inst->id);
		inst->provider = owned(new PlugProvider(factory, info, true));
		const bool created = inst->provider->initialize();
		h.shm.setCurrentInstance(0);
		if (!created || !inst->provider->getComponentPtr()) {
			error = "the plugin '" + info.name() + "' could not be created";
			return nullptr;
		}
		inst->component = inst->provider->getComponentPtr();
		inst->controller = inst->provider->getControllerPtr();
		inst->processor = U::cast<IAudioProcessor>(inst->component);
		if (!inst->processor || inst->processor->canProcessSampleSize(kSample32) != kResultTrue) {
			error = "the plugin '" + info.name() + "' cannot process 32-bit float audio";
			return nullptr;
		}
		const int32 inCount = inst->component->getBusCount(kAudio, kInput);
		const int32 outCount = inst->component->getBusCount(kAudio, kOutput);
		std::vector<SpeakerArrangement> ins(static_cast<std::size_t>(inCount)), outs(static_cast<std::size_t>(outCount));
		for (int32 i = 0; i < inCount; ++i)
			inst->processor->getBusArrangement(kInput, i, ins[static_cast<std::size_t>(i)]);
		for (int32 i = 0; i < outCount; ++i)
			inst->processor->getBusArrangement(kOutput, i, outs[static_cast<std::size_t>(i)]);
		if (inCount > 0)
			ins[0] = SpeakerArr::kStereo;
		if (outCount > 0)
			outs[0] = SpeakerArr::kStereo;
		inst->processor->setBusArrangements(ins.data(), inCount, outs.data(), outCount); // a refusal keeps its own layout
		if (inCount > 0)
			inst->component->activateBus(kAudio, kInput, 0, true);
		if (outCount > 0)
			inst->component->activateBus(kAudio, kOutput, 0, true);
		if (inst->component->getBusCount(kEvent, kInput) > 0)
			inst->component->activateBus(kEvent, kInput, 0, true);
		bindBuffers(*inst);
		if (outCount == 0 || inst->outChannels == 0) {
			error = "the plugin '" + info.name() + "' has no audio output";
			return nullptr;
		}
		if (!startProcessing(*inst, error))
			return nullptr;
		return inst;
	}
	error = "class " + classId + " is not an audio plugin in " + path;
	return nullptr;
}

void unload(Instance& inst) {
	if (inst.editor) {
		closeEditorWindow(inst.editor);
		inst.editor = nullptr;
	}
	stopProcessing(inst);
	inst.processor = nullptr;
	inst.controller = nullptr;
	inst.component = nullptr;
	inst.provider = nullptr; // terminates and disconnects
}

Instance* find(std::uint32_t id) {
	auto& instances = host().instances;
	auto it = instances.find(id);
	return it == instances.end() ? nullptr : it->second.get();
}

std::vector<unsigned char> streamBytes(MemoryStream& s) {
	const auto* data = reinterpret_cast<const unsigned char*>(s.getData());
	return std::vector<unsigned char>(data, data + s.getSize());
}

bool sameObject(FUnknown* a, FUnknown* b) {
	if (!a || !b)
		return false;
	FUnknownPtr<FUnknown> ua(a), ub(b);
	return ua.get() == ub.get();
}

void processBlock(PayloadReader& in, Frame& reply) {
	Host& h = host();
	std::uint32_t frames, flags, count;
	std::uint64_t tempoBits, projectFrame;
	if (!in.u32(frames) || !in.u32(flags) || !in.u64(tempoBits) || !in.u64(projectFrame) || !in.u32(count))
		return errorReply(reply, ErrorCode::BadPayload, "process: bad payload");
	if (!h.shm.isOpen())
		return errorReply(reply, ErrorCode::Failed, "process: no shared memory");
	if (frames == 0 || frames > h.shm.maxBlock() || static_cast<int32>(frames) > h.maxBlock)
		return errorReply(reply, ErrorCode::BadPayload, "process: bad block size");
	double tempo;
	std::memcpy(&tempo, &tempoBits, 8);
	ProcessContext& ctx = h.context;
	ctx = ProcessContext{};
	ctx.state = ProcessContext::kTempoValid | ProcessContext::kProjectTimeMusicValid | ProcessContext::kTimeSigValid |
				((flags & 1u) ? static_cast<uint32>(ProcessContext::kPlaying) : 0u);
	ctx.sampleRate = h.sampleRate;
	ctx.projectTimeSamples = static_cast<TSamples>(projectFrame);
	ctx.tempo = tempo > 0 ? tempo : 120.0;
	ctx.projectTimeMusic = static_cast<double>(ctx.projectTimeSamples) / h.sampleRate * ctx.tempo / 60.0;
	ctx.timeSigNumerator = 4;
	ctx.timeSigDenominator = 4;

	for (std::uint32_t i = 0; i < count; ++i) {
		std::uint32_t id, inputSlot;
		if (!in.u32(id) || !in.u32(inputSlot))
			return errorReply(reply, ErrorCode::BadPayload, "process: bad payload");
		Instance* inst = find(id);
		if (!inst)
			return errorReply(reply, ErrorCode::Failed, "process: unknown instance " + std::to_string(id));
		if (inputSlot != kOwnInput) {
			if (inputSlot >= h.shm.slotCount())
				return errorReply(reply, ErrorCode::BadPayload, "process: bad input slot");
			std::memcpy(h.shm.channel(inst->slot, 0), h.shm.channel(inputSlot, 2), frames * sizeof(float));
			std::memcpy(h.shm.channel(inst->slot, 1), h.shm.channel(inputSlot, 3), frames * sizeof(float));
		}
		inst->events.clear();
		std::uint32_t* eventCount = h.shm.eventCount(inst->slot);
		const std::uint32_t n = std::min<std::uint32_t>(*eventCount, h.shm.maxEvents());
		const ShmEvent* events = h.shm.events(inst->slot);
		for (std::uint32_t e = 0; e < n; ++e) {
			Event ev{};
			ev.busIndex = 0;
			ev.sampleOffset = std::min<int32>(events[e].sampleOffset, static_cast<int32>(frames) - 1);
			if (events[e].type == 1) {
				ev.type = Event::kNoteOnEvent;
				ev.noteOn = {static_cast<int16>(events[e].channel), static_cast<int16>(events[e].pitch), 0.0f,
					static_cast<float>(events[e].velocity) / 127.0f, 0, -1};
			} else {
				ev.type = Event::kNoteOffEvent;
				ev.noteOff = {static_cast<int16>(events[e].channel), static_cast<int16>(events[e].pitch), 0.0f, -1, 0.0f};
			}
			inst->events.addEvent(ev);
		}
		*eventCount = 0;
		inst->outChanges.clearQueue();

		ProcessData data;
		data.processMode = h.offline ? kOffline : kRealtime;
		data.symbolicSampleSize = kSample32;
		data.numSamples = static_cast<int32>(frames);
		data.numInputs = static_cast<int32>(inst->inBuses.size());
		data.numOutputs = static_cast<int32>(inst->outBuses.size());
		data.inputs = inst->inBuses.empty() ? nullptr : inst->inBuses.data();
		data.outputs = inst->outBuses.empty() ? nullptr : inst->outBuses.data();
		data.inputEvents = &inst->events;
		data.inputParameterChanges = &inst->inChanges;
		data.outputParameterChanges = &inst->outChanges;
		data.processContext = &ctx;
		h.shm.setCurrentInstance(inst->id);
		inst->processor->process(data);
		h.shm.setCurrentInstance(0);
		if (inst->outChannels == 1)
			std::memcpy(h.shm.channel(inst->slot, 3), h.shm.channel(inst->slot, 2), frames * sizeof(float));
	}
}

} // namespace

bool pluginsSupported() { return true; }

void setPluginNotify(std::function<void(const Frame&)> notify) { host().notify = std::move(notify); }

void shutdownPlugins() {
	runOnMainThread([] {
		Host& h = host();
		for (auto& [id, inst] : h.instances)
			unload(*inst);
		h.instances.clear();
		h.shm.close();
	});
}

bool scanBundle(const std::string& bundlePath, std::string& json) {
	std::ostringstream out;
	out << "{\"bundle\":\"" << jsonEscape(bundlePath) << "\",\"classes\":[";
	std::string error;
	VST3::Hosting::Module::Ptr module;
	{
		StdoutToStderr quiet;
		module = VST3::Hosting::Module::create(bundlePath, error);
	}
	bool first = true;
	if (module) {
		for (const auto& info : module->getFactory().classInfos()) {
			if (info.category() != kVstAudioEffectClass)
				continue;
			out << (first ? "" : ",") << "{\"classId\":\"" << info.ID().toString() << "\",\"name\":\"" << jsonEscape(info.name())
				<< "\",\"vendor\":\"" << jsonEscape(info.vendor()) << "\",\"version\":\"" << jsonEscape(info.version())
				<< "\",\"sdkVersion\":\"" << jsonEscape(info.sdkVersion()) << "\",\"subCategories\":\""
				<< jsonEscape(info.subCategoriesString()) << "\",\"kind\":\"" << (isInstrument(info) ? "instrument" : "effect") << "\"}";
			first = false;
		}
	}
	out << "],\"error\":";
	if (module)
		out << "null}";
	else
		out << "\"" << jsonEscape(error.empty() ? "cannot load the module" : error) << "\"}";
	json = out.str();
	return module != nullptr;
}

bool dispatchPlugin(const Frame& request, Frame& reply) {
	const auto type = static_cast<MessageType>(request.type);
	if (request.type < 0x0100 || request.type > 0x01FF)
		return false;
	reply = Frame{};
	reply.id = request.id;
	reply.type = static_cast<std::uint16_t>(request.type | kReplyBit);
	PayloadReader in(request.payload);
	PayloadWriter out;
	Host& h = host();

	if (type == MessageType::PluginProcess) {
		processBlock(in, reply);
		return true;
	}

	runOnMainThread([&] {
		switch (type) {
		case MessageType::PluginSetupShm: {
			std::string path, error;
			std::uint64_t size;
			if (!in.str(path) || !in.u64(size))
				return errorReply(reply, ErrorCode::BadPayload, "setupShm: bad payload");
			if (!h.instances.empty())
				return errorReply(reply, ErrorCode::Failed, "setupShm: unload every plugin first");
			if (!h.shm.open(path, static_cast<std::size_t>(size), error))
				return errorReply(reply, ErrorCode::Failed, error);
			break;
		}
		case MessageType::PluginScan: {
			std::string path, json;
			if (!in.str(path))
				return errorReply(reply, ErrorCode::BadPayload, "scan: bad payload");
			scanBundle(path, json);
			out.str(json);
			break;
		}
		case MessageType::PluginLoad: {
			std::string path, classId, error;
			std::uint32_t slot, rate, block;
			if (!in.str(path) || !in.str(classId) || !in.u32(slot) || !in.u32(rate) || !in.u32(block))
				return errorReply(reply, ErrorCode::BadPayload, "load: bad payload");
			if (!h.shm.isOpen() || slot >= h.shm.slotCount() || block == 0 || block > h.shm.maxBlock() || rate < 8000)
				return errorReply(reply, ErrorCode::BadPayload, "load: no shared memory, or bad slot/rate/block size");
			if (h.instances.empty()) {
				h.sampleRate = rate;
				h.maxBlock = static_cast<int32>(block);
			} else if (static_cast<int32>(block) != h.maxBlock || static_cast<double>(rate) != h.sampleRate) {
				return errorReply(reply, ErrorCode::BadPayload, "load: every plugin runs at one rate and block size");
			}
			auto inst = load(path, classId, slot, error);
			if (!inst)
				return errorReply(reply, ErrorCode::Failed, error);
			out.u32(inst->id);
			out.u32(inst->processor->getLatencySamples());
			out.u16(static_cast<std::uint16_t>(inst->inChannels));
			out.u16(static_cast<std::uint16_t>(inst->outChannels));
			out.u32(inst->controller ? 1u : 0u);
			out.str(inst->name);
			h.instances[inst->id] = std::move(inst);
			break;
		}
		case MessageType::PluginUnload:
		case MessageType::PluginGetState:
		case MessageType::PluginSetState:
		case MessageType::PluginLatency:
		case MessageType::PluginOpenEditor:
		case MessageType::PluginCloseEditor: {
			std::uint32_t id;
			if (!in.u32(id))
				return errorReply(reply, ErrorCode::BadPayload, "plugin: bad payload");
			Instance* inst = find(id);
			if (!inst)
				return errorReply(reply, ErrorCode::Failed, "unknown plugin instance " + std::to_string(id));
			h.shm.setCurrentInstance(id);
			switch (type) {
			case MessageType::PluginUnload:
				unload(*inst);
				h.instances.erase(id);
				break;
			case MessageType::PluginGetState: {
				MemoryStream component, controller;
				inst->component->getState(&component);
				if (inst->controller && !sameObject(inst->controller, inst->component))
					inst->controller->getState(&controller);
				auto c = streamBytes(component), k = streamBytes(controller);
				out.u32(static_cast<std::uint32_t>(c.size()));
				out.bytes(c.data(), c.size());
				out.u32(static_cast<std::uint32_t>(k.size()));
				out.bytes(k.data(), k.size());
				break;
			}
			case MessageType::PluginSetState: {
				std::uint32_t cl, kl;
				const unsigned char *c = nullptr, *k = nullptr;
				if (!in.u32(cl) || !in.bytes(cl, c) || !in.u32(kl) || !in.bytes(kl, k))
					return errorReply(reply, ErrorCode::BadPayload, "setState: bad payload");
				if (cl > 0) {
					MemoryStream component(const_cast<unsigned char*>(c), static_cast<TSize>(cl));
					if (inst->component->setState(&component) != kResultOk)
						return errorReply(reply, ErrorCode::Failed, "the plugin rejected its saved state");
					if (inst->controller && !sameObject(inst->controller, inst->component)) {
						int64 pos = 0;
						component.seek(0, IBStream::kIBSeekSet, &pos);
						inst->controller->setComponentState(&component);
					}
				}
				if (kl > 0 && inst->controller && !sameObject(inst->controller, inst->component)) {
					MemoryStream controller(const_cast<unsigned char*>(k), static_cast<TSize>(kl));
					inst->controller->setState(&controller);
				}
				break;
			}
			case MessageType::PluginLatency:
				out.u32(inst->processor->getLatencySamples());
				break;
			case MessageType::PluginOpenEditor: {
				std::string title, error;
				if (!in.str(title))
					return errorReply(reply, ErrorCode::BadPayload, "openEditor: bad payload");
				if (inst->editor)
					break; // already open
				if (!inst->controller)
					return errorReply(reply, ErrorCode::Failed, "the plugin has no editor");
				IPtr<IPlugView> view = owned(inst->controller->createView(ViewType::kEditor));
				if (!view)
					return errorReply(reply, ErrorCode::Failed, "the plugin has no editor");
				inst->editor = openEditorWindow(view, title, [id](bool byUser) {
					Host& hh = host();
					if (Instance* i = find(id))
						i->editor = nullptr;
					if (byUser && hh.notify) {
						Frame note;
						note.type = static_cast<std::uint16_t>(MessageType::PluginEditorClosed);
						PayloadWriter w;
						w.u32(id);
						note.payload = std::move(w.data());
						hh.notify(note);
					}
				}, error);
				if (!inst->editor)
					return errorReply(reply, ErrorCode::Failed, error);
				break;
			}
			case MessageType::PluginCloseEditor:
				if (inst->editor)
					closeEditorWindow(inst->editor);
				break;
			default:
				break;
			}
			h.shm.setCurrentInstance(0);
			break;
		}
		case MessageType::PluginSetOffline: {
			std::uint32_t offline;
			if (!in.u32(offline))
				return errorReply(reply, ErrorCode::BadPayload, "setOffline: bad payload");
			if ((offline != 0) != h.offline) {
				h.offline = offline != 0;
				std::string error;
				for (auto& [id, inst] : h.instances) {
					stopProcessing(*inst);
					if (!startProcessing(*inst, error))
						return errorReply(reply, ErrorCode::Failed, error);
				}
			}
			break;
		}
		default:
			return errorReply(reply, ErrorCode::UnknownMessage, "unknown plugin message " + std::to_string(request.type));
		}
		reply.payload = std::move(out.data());
	});
	return true;
}

} // namespace mfph
