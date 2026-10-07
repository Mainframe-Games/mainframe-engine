// The helper's test plugins (CTest fixture, never shipped): see CMakeLists.txt in this folder.
#include "public.sdk/source/main/pluginfactory.h"
#include "public.sdk/source/vst/vstsinglecomponenteffect.h"
#include "pluginterfaces/base/ibstream.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivstparameterchanges.h"

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

bool InitModule() { return true; }
bool DeinitModule() { return true; }

namespace {

enum class Kind { Gain, Synth, Crasher };
constexpr uint32 kCrasherLatency = 128;
constexpr ParamID kGainParam = 0;

struct Voice {
	int16 pitch;
	float velocity;
	double phase;
};

class TestPlugin : public SingleComponentEffect {
public:
	explicit TestPlugin(Kind kind) : kind_(kind) {}

	tresult PLUGIN_API initialize(FUnknown* context) SMTG_OVERRIDE {
		const tresult result = SingleComponentEffect::initialize(context);
		if (result != kResultOk)
			return result;
		if (kind_ == Kind::Synth)
			addEventInput(STR16("Events"), 1);
		else
			addAudioInput(STR16("Stereo In"), SpeakerArr::kStereo);
		addAudioOutput(STR16("Stereo Out"), SpeakerArr::kStereo);
		if (kind_ == Kind::Gain)
			parameters.addParameter(STR16("Gain"), nullptr, 0, 0.5, ParameterInfo::kCanAutomate, kGainParam);
		return kResultOk;
	}

	tresult PLUGIN_API setBusArrangements(SpeakerArrangement* inputs, int32 numIns, SpeakerArrangement* outputs, int32 numOuts) SMTG_OVERRIDE {
		if (numOuts != 1 || outputs[0] != SpeakerArr::kStereo)
			return kResultFalse;
		if (kind_ == Kind::Synth ? numIns != 0 : (numIns != 1 || inputs[0] != SpeakerArr::kStereo))
			return kResultFalse;
		return SingleComponentEffect::setBusArrangements(inputs, numIns, outputs, numOuts);
	}

	tresult PLUGIN_API canProcessSampleSize(int32 size) SMTG_OVERRIDE { return size == kSample32 ? kResultTrue : kResultFalse; }

	tresult PLUGIN_API setupProcessing(ProcessSetup& setup) SMTG_OVERRIDE {
		sampleRate_ = setup.sampleRate;
		return SingleComponentEffect::setupProcessing(setup);
	}

	tresult PLUGIN_API setActive(TBool) SMTG_OVERRIDE {
		voices_.clear();
		delay_.assign(kCrasherLatency * 2, 0.0f);
		delayPos_ = 0;
		return kResultOk;
	}

	tresult PLUGIN_API setProcessing(TBool) SMTG_OVERRIDE { return kResultOk; }

	uint32 PLUGIN_API getLatencySamples() SMTG_OVERRIDE { return kind_ == Kind::Crasher ? kCrasherLatency : 0; }

	tresult PLUGIN_API process(ProcessData& data) SMTG_OVERRIDE {
		if (kind_ == Kind::Crasher && armed_)
			std::abort();
		if (auto* changes = data.inputParameterChanges) {
			for (int32 i = 0; i < changes->getParameterCount(); ++i) {
				IParamValueQueue* queue = changes->getParameterData(i);
				int32 offset;
				ParamValue value;
				if (queue && queue->getParameterId() == kGainParam && queue->getPoint(queue->getPointCount() - 1, offset, value) == kResultOk)
					gain_ = static_cast<float>(value);
			}
		}
		if (data.numOutputs < 1 || data.numSamples <= 0)
			return kResultOk;
		float** out = data.outputs[0].channelBuffers32;
		float** in = data.numInputs > 0 ? data.inputs[0].channelBuffers32 : nullptr;
		const int32 frames = data.numSamples;
		switch (kind_) {
		case Kind::Gain:
			for (int32 c = 0; c < 2; ++c)
				for (int32 f = 0; f < frames; ++f)
					out[c][f] = in[c][f] * gain_;
			break;
		case Kind::Crasher:
			for (int32 f = 0; f < frames; ++f) {
				for (int32 c = 0; c < 2; ++c) {
					float& slot = delay_[delayPos_ * 2 + static_cast<std::size_t>(c)];
					const float delayed = slot;
					slot = in[c][f];
					out[c][f] = delayed;
				}
				delayPos_ = (delayPos_ + 1) % kCrasherLatency;
			}
			break;
		case Kind::Synth: {
			IEventList* events = data.inputEvents;
			const int32 eventCount = events ? events->getEventCount() : 0;
			int32 next = 0;
			for (int32 f = 0; f < frames; ++f) {
				Event e{};
				while (next < eventCount && events->getEvent(next, e) == kResultOk && e.sampleOffset <= f) {
					++next;
					if (e.type == Event::kNoteOnEvent && e.noteOn.velocity > 0.0f)
						voices_.push_back({e.noteOn.pitch, e.noteOn.velocity, 0.0});
					else if (e.type == Event::kNoteOnEvent || e.type == Event::kNoteOffEvent) {
						const int16 pitch = e.type == Event::kNoteOnEvent ? e.noteOn.pitch : e.noteOff.pitch;
						for (std::size_t v = 0; v < voices_.size(); ++v)
							if (voices_[v].pitch == pitch) {
								voices_.erase(voices_.begin() + static_cast<std::ptrdiff_t>(v));
								break;
							}
					}
				}
				float sample = 0.0f;
				for (Voice& v : voices_) {
					sample += static_cast<float>(0.2 * v.velocity * std::sin(v.phase));
					v.phase += 2.0 * 3.14159265358979 * 440.0 * std::pow(2.0, (v.pitch - 69) / 12.0) / sampleRate_;
				}
				out[0][f] = sample;
				out[1][f] = sample;
			}
			break;
		}
		}
		data.outputs[0].silenceFlags = 0;
		return kResultOk;
	}

	tresult PLUGIN_API setState(IBStream* state) SMTG_OVERRIDE {
		char buffer[16] = {};
		int32 read = 0;
		state->read(buffer, sizeof(buffer) - 1, &read);
		if (kind_ == Kind::Crasher) {
			if (std::strncmp(buffer, "CRASHNOW", 8) == 0)
				std::abort();
			armed_ = std::strncmp(buffer, "CRASH", 5) == 0;
		} else if (kind_ == Kind::Gain && read >= 4) {
			std::memcpy(&gain_, buffer, 4);
			setParamNormalized(kGainParam, gain_);
		}
		return kResultOk;
	}

	tresult PLUGIN_API getState(IBStream* state) SMTG_OVERRIDE {
		if (kind_ == Kind::Gain)
			return state->write(&gain_, 4, nullptr);
		if (kind_ == Kind::Crasher)
			return state->write(const_cast<char*>(armed_ ? "CRASH" : "OK"), armed_ ? 5 : 2, nullptr);
		return state->write(const_cast<char*>("synth"), 5, nullptr);
	}

	static FUnknown* createGain(void*) { return static_cast<IAudioProcessor*>(new TestPlugin(Kind::Gain)); }
	static FUnknown* createSynth(void*) { return static_cast<IAudioProcessor*>(new TestPlugin(Kind::Synth)); }
	static FUnknown* createCrasher(void*) { return static_cast<IAudioProcessor*>(new TestPlugin(Kind::Crasher)); }

private:
	Kind kind_;
	float gain_ = 0.5f;
	bool armed_ = false;
	double sampleRate_ = 48000.0;
	std::vector<Voice> voices_;
	std::vector<float> delay_ = std::vector<float>(kCrasherLatency * 2, 0.0f);
	std::size_t delayPos_ = 0;
};

} // namespace

BEGIN_FACTORY_DEF("Mainframe Games", "https://github.com/Mainframe-Games", "mailto:noreply@example.com")
DEF_CLASS2(INLINE_UID(0x4D465447, 0x41494E00, 0x00000000, 0x00000001), PClassInfo::kManyInstances, kVstAudioEffectClass,
	"MF Test Gain", 0, "Fx", "1.0.0", kVstVersionString, TestPlugin::createGain)
DEF_CLASS2(INLINE_UID(0x4D465453, 0x594E5400, 0x00000000, 0x00000002), PClassInfo::kManyInstances, kVstAudioEffectClass,
	"MF Test Synth", 0, "Instrument|Synth", "1.0.0", kVstVersionString, TestPlugin::createSynth)
DEF_CLASS2(INLINE_UID(0x4D464352, 0x41534800, 0x00000000, 0x00000003), PClassInfo::kManyInstances, kVstAudioEffectClass,
	"MF Test Crasher", 0, "Fx|Delay", "1.0.0", kVstVersionString, TestPlugin::createCrasher)
END_FACTORY
