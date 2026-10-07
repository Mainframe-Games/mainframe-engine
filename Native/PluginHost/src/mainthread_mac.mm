// macOS main thread: NSApplication (accessory: no Dock icon) and plugin editor windows (an NSWindow whose content view
// hosts the plugin's IPlugView NSView).
#include "mainthread.hpp"

#import <Cocoa/Cocoa.h>

#include <atomic>
#include <cstdlib>
#include <thread>

#ifdef MFPH_VST3
#include "pluginterfaces/base/funknownimpl.h"
#include "pluginterfaces/gui/iplugview.h"
#endif

namespace {
std::atomic<bool> g_loopRunning{false};
}

#ifdef MFPH_VST3
namespace mfph {
struct EditorWindow;
}

@interface MFEditorDelegate : NSObject <NSWindowDelegate>
@property(nonatomic, assign) mfph::EditorWindow* owner;
@end

namespace mfph {

class PlugFrame : public Steinberg::U::Implements<Steinberg::U::Directly<Steinberg::IPlugFrame>> {
public:
	NSWindow* window = nil;
	Steinberg::tresult PLUGIN_API resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* newSize) override {
		if (!window || !newSize)
			return Steinberg::kInvalidArgument;
		[window setContentSize:NSMakeSize(newSize->getWidth(), newSize->getHeight())];
		view->onSize(newSize);
		return Steinberg::kResultTrue;
	}
};

struct EditorWindow {
	Steinberg::IPtr<Steinberg::IPlugView> view;
	Steinberg::IPtr<PlugFrame> frame;
	NSWindow* window = nil;
	MFEditorDelegate* delegate = nil;
	std::function<void(bool)> onClosed;
	bool closingByRequest = false;
};

} // namespace mfph

@implementation MFEditorDelegate
- (void)windowWillClose:(NSNotification*)notification {
	(void)notification;
	mfph::EditorWindow* w = self.owner;
	if (!w)
		return;
	self.owner = nullptr;
	w->view->setFrame(nullptr);
	w->view->removed();
	w->window.delegate = nil;
	auto onClosed = std::move(w->onClosed);
	const bool byUser = !w->closingByRequest;
	dispatch_async(dispatch_get_main_queue(), ^{
		delete w; // after AppKit is done closing the window
	});
	if (onClosed)
		onClosed(byUser);
}
@end
#endif

namespace mfph {

int runWithMainLoop(const std::function<int()>& worker) {
	@autoreleasepool {
		[NSApplication sharedApplication];
		[NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];
		g_loopRunning = true;
		std::thread([worker] {
			const int code = worker();
			dispatch_async(dispatch_get_main_queue(), ^{
				std::exit(code);
			});
		}).detach();
		[NSApp run];
	}
	return 0;
}

void runOnMainThread(const std::function<void()>& fn) {
	if (!g_loopRunning || [NSThread isMainThread]) {
		fn();
		return;
	}
	dispatch_sync(dispatch_get_main_queue(), ^{
		@autoreleasepool {
			fn();
		}
	});
}

#ifdef MFPH_VST3
EditorWindow* openEditorWindow(Steinberg::IPlugView* view, const std::string& title, std::function<void(bool)> onClosed,
	std::string& error) {
	if (view->isPlatformTypeSupported(Steinberg::kPlatformTypeNSView) != Steinberg::kResultTrue) {
		error = "the plugin's editor does not support NSView";
		return nullptr;
	}
	Steinberg::ViewRect rect;
	if (view->getSize(&rect) != Steinberg::kResultTrue || rect.getWidth() <= 0 || rect.getHeight() <= 0)
		rect = Steinberg::ViewRect(0, 0, 400, 300);
	NSWindowStyleMask style = NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable;
	if (view->canResize() == Steinberg::kResultTrue)
		style |= NSWindowStyleMaskResizable;
	auto* w = new EditorWindow();
	w->view = view;
	w->window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, rect.getWidth(), rect.getHeight())
											styleMask:style
											  backing:NSBackingStoreBuffered
												defer:NO];
	w->window.releasedWhenClosed = NO;
	NSString* nsTitle = [NSString stringWithUTF8String:title.c_str()];
	w->window.title = nsTitle != nil ? nsTitle : @"Plugin";
	w->frame = Steinberg::owned(new PlugFrame());
	w->frame->window = w->window;
	view->setFrame(w->frame);
	if (view->attached((__bridge void*)w->window.contentView, Steinberg::kPlatformTypeNSView) != Steinberg::kResultTrue) {
		view->setFrame(nullptr);
		[w->window close];
		delete w;
		error = "the plugin's editor could not be attached";
		return nullptr;
	}
	w->delegate = [MFEditorDelegate new];
	w->delegate.owner = w;
	w->window.delegate = w->delegate;
	w->onClosed = std::move(onClosed);
	[w->window center];
	[w->window makeKeyAndOrderFront:nil];
	[NSApp activateIgnoringOtherApps:YES];
	return w;
}

void closeEditorWindow(EditorWindow* window) {
	if (!window)
		return;
	window->closingByRequest = true;
	[window->window close];
}
#else
EditorWindow* openEditorWindow(Steinberg::IPlugView*, const std::string&, std::function<void(bool)>, std::string& error) {
	error = "this helper was built without VST3";
	return nullptr;
}
void closeEditorWindow(EditorWindow*) {}
#endif

} // namespace mfph
