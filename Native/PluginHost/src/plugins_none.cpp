// Built without MF_PLUGINHOST_VST3: plugin messages answer "not built with VST3".
#include "plugins.hpp"

namespace mfph {

bool pluginsSupported() { return false; }

bool dispatchPlugin(const Frame&, Frame&) { return false; }

void setPluginNotify(std::function<void(const Frame&)>) {}

void shutdownPlugins() {}

bool scanBundle(const std::string& bundlePath, std::string& json) {
	json = "{\"bundle\":\"" + bundlePath + "\",\"classes\":[],\"error\":\"this helper was built without VST3\"}";
	return false;
}

} // namespace mfph
