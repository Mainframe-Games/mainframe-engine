// VST3 plugin hosting (MF_PLUGINHOST_VST3; ADR 0147): the 0x0100-0x01FF messages, the --scan command line.
#pragma once

#include "protocol.hpp"

#include <functional>
#include <string>

namespace mfph {

// Built with VST3 support.
bool pluginsSupported();

// Handles a plugin message (protocol thread). Returns false when `request` is not one.
bool dispatchPlugin(const Frame& request, Frame& reply);

// Where unsolicited notifications (PluginEditorClosed) go; called from the main thread.
void setPluginNotify(std::function<void(const Frame&)> notify);

// Unloads every instance and unmaps the shared memory (before the helper exits).
void shutdownPlugins();

// One bundle's audio classes as JSON: {"bundle":..,"classes":[{"classId","name","vendor","version","sdkVersion",
// "subCategories","kind":"instrument"|"effect"}],"error":null|".."}. Returns false when the module cannot be loaded.
bool scanBundle(const std::string& bundlePath, std::string& json);

} // namespace mfph
