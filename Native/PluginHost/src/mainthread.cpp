// Windows / Linux: no separate main loop yet; plugin editor windows are not supported on these platforms yet.
#include "mainthread.hpp"

namespace mfph {

int runWithMainLoop(const std::function<int()>& worker) { return worker(); }

void runOnMainThread(const std::function<void()>& fn) { fn(); }

EditorWindow* openEditorWindow(Steinberg::IPlugView*, const std::string&, std::function<void(bool)>, std::string& error) {
	error = "plugin editor windows are not supported on this platform yet";
	return nullptr;
}

void closeEditorWindow(EditorWindow*) {}

} // namespace mfph
