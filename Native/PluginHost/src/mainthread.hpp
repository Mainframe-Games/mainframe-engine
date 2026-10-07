// The helper's main thread. On macOS plugins expect their UI-thread calls (create, setState, createView, ...) on the
// main thread, which runs the NSApplication loop for editor windows; the protocol (and the audio it carries) runs on
// another thread and hands control calls over synchronously. Elsewhere everything runs on the protocol thread.
#pragma once

#include <functional>
#include <string>

namespace Steinberg {
class IPlugView;
}

namespace mfph {

// Runs `worker` with a main loop around it (macOS: NSApplication on this thread, `worker` on a new thread) and returns
// its result. Call from main().
int runWithMainLoop(const std::function<int()>& worker);

// Runs `fn` on the main thread and waits (inline when already there or when no main loop runs).
void runOnMainThread(const std::function<void()>& fn);

struct EditorWindow;

// Main thread. Opens a window hosting `view` (attached on success). `onClosed(byUser)` runs on the main thread when the
// window goes away (the window is freed afterwards). Returns null and sets `error` when the platform has no support.
EditorWindow* openEditorWindow(Steinberg::IPlugView* view, const std::string& title, std::function<void(bool byUser)> onClosed,
	std::string& error);

// Main thread: closes the window (its onClosed runs with byUser = false).
void closeEditorWindow(EditorWindow* window);

} // namespace mfph
