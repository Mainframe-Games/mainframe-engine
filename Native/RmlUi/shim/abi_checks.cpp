// Compile-time checks that the C ABI constants and structs match RmlUi 6.3. A failure here means the RmlUi
// submodule changed in an ABI-relevant way: update mfrmlui.h and bump MFRMLUI_ABI_VERSION_MAJOR.
#include "mfrmlui_internal.h"

#include <RmlUi/Core/DecorationTypes.h>
#include <RmlUi/Core/Input.h>

#include <cstddef>
#include <type_traits>

// Handles and indices.
static_assert(sizeof(Rml::CompiledGeometryHandle) == sizeof(uint64_t), "geometry handle size");
static_assert(sizeof(Rml::TextureHandle) == sizeof(uint64_t), "texture handle size");
static_assert(sizeof(Rml::LayerHandle) == sizeof(uint64_t), "layer handle size");
static_assert(sizeof(Rml::CompiledFilterHandle) == sizeof(uint64_t), "filter handle size (filters array is passed as uint64_t*)");
static_assert(sizeof(Rml::CompiledShaderHandle) == sizeof(uint64_t), "shader handle size");
static_assert(sizeof(Rml::FileHandle) == sizeof(uint64_t), "file handle size");
static_assert(sizeof(int) == sizeof(int32_t), "indices are passed as int32_t*");
static_assert(sizeof(Rml::byte) == sizeof(uint8_t), "byte size");

// Vertex layout (passed to compile_geometry without copying).
static_assert(sizeof(mfrmlui_vertex) == 20, "mfrmlui_vertex must be 20 bytes");
static_assert(sizeof(Rml::Vertex) == sizeof(mfrmlui_vertex), "Rml::Vertex size changed");
static_assert(offsetof(Rml::Vertex, position) == offsetof(mfrmlui_vertex, position), "Rml::Vertex::position offset");
static_assert(offsetof(Rml::Vertex, colour) == offsetof(mfrmlui_vertex, colour), "Rml::Vertex::colour offset");
static_assert(offsetof(Rml::Vertex, tex_coord) == offsetof(mfrmlui_vertex, tex_coord), "Rml::Vertex::tex_coord offset");
static_assert(sizeof(Rml::Vector2f) == 2 * sizeof(float), "Vector2f layout");
static_assert(sizeof(Rml::ColourbPremultiplied) == 4, "colour layout");
static_assert(sizeof(Rml::Matrix4f) == 16 * sizeof(float), "Matrix4f layout (set_transform passes 16 floats)");

// Blittable public structs: standard layout, natural alignment.
static_assert(std::is_standard_layout<mfrmlui_vertex>::value && std::is_trivially_copyable<mfrmlui_vertex>::value, "blittable");
static_assert(std::is_standard_layout<mfrmlui_rectf>::value && sizeof(mfrmlui_rectf) == 16, "mfrmlui_rectf layout");
static_assert(std::is_standard_layout<mfrmlui_color_stop>::value && sizeof(mfrmlui_color_stop) == 8, "mfrmlui_color_stop layout");
static_assert(offsetof(mfrmlui_system_callbacks, user_data) == 8, "callback struct header layout");
static_assert(offsetof(mfrmlui_file_callbacks, user_data) == 8, "callback struct header layout");
static_assert(offsetof(mfrmlui_render_callbacks, user_data) == 8, "callback struct header layout");
static_assert(offsetof(mfrmlui_variable_callbacks, user_data) == 8, "callback struct header layout");
static_assert(sizeof(mfrmlui_system_callbacks) == 16 + 9 * sizeof(void*), "ABI 1.0 system callbacks size");
static_assert(sizeof(mfrmlui_file_callbacks) == 16 + 7 * sizeof(void*), "ABI 1.0 file callbacks size");
static_assert(sizeof(mfrmlui_render_callbacks) == 16 + 21 * sizeof(void*), "ABI 1.0 render callbacks size");
static_assert(sizeof(mfrmlui_variable_callbacks) == 16 + 5 * sizeof(void*), "ABI 1.0 variable callbacks size");

// Enumerations mirrored as constants.
static_assert(MFRMLUI_LOG_ALWAYS == Rml::Log::LT_ALWAYS && MFRMLUI_LOG_ERROR == Rml::Log::LT_ERROR &&
		MFRMLUI_LOG_ASSERT == Rml::Log::LT_ASSERT && MFRMLUI_LOG_WARNING == Rml::Log::LT_WARNING && MFRMLUI_LOG_INFO == Rml::Log::LT_INFO &&
		MFRMLUI_LOG_DEBUG == Rml::Log::LT_DEBUG,
	"log types");
static_assert(MFRMLUI_KM_CTRL == Rml::Input::KM_CTRL && MFRMLUI_KM_SHIFT == Rml::Input::KM_SHIFT && MFRMLUI_KM_ALT == Rml::Input::KM_ALT &&
		MFRMLUI_KM_META == Rml::Input::KM_META && MFRMLUI_KM_CAPSLOCK == Rml::Input::KM_CAPSLOCK &&
		MFRMLUI_KM_NUMLOCK == Rml::Input::KM_NUMLOCK && MFRMLUI_KM_SCROLLLOCK == Rml::Input::KM_SCROLLLOCK,
	"key modifiers");
static_assert(MFRMLUI_MODAL_NONE == static_cast<int>(Rml::ModalFlag::None) && MFRMLUI_MODAL_MODAL == static_cast<int>(Rml::ModalFlag::Modal) &&
		MFRMLUI_MODAL_KEEP == static_cast<int>(Rml::ModalFlag::Keep),
	"modal flags");
static_assert(MFRMLUI_FOCUS_NONE == static_cast<int>(Rml::FocusFlag::None) &&
		MFRMLUI_FOCUS_DOCUMENT == static_cast<int>(Rml::FocusFlag::Document) && MFRMLUI_FOCUS_KEEP == static_cast<int>(Rml::FocusFlag::Keep) &&
		MFRMLUI_FOCUS_AUTO == static_cast<int>(Rml::FocusFlag::Auto),
	"focus flags");
static_assert(MFRMLUI_SCROLL_NONE == static_cast<int>(Rml::ScrollFlag::None) && MFRMLUI_SCROLL_AUTO == static_cast<int>(Rml::ScrollFlag::Auto),
	"scroll flags");
static_assert(MFRMLUI_EVENT_PHASE_NONE == static_cast<int>(Rml::EventPhase::None) &&
		MFRMLUI_EVENT_PHASE_CAPTURE == static_cast<int>(Rml::EventPhase::Capture) &&
		MFRMLUI_EVENT_PHASE_TARGET == static_cast<int>(Rml::EventPhase::Target) &&
		MFRMLUI_EVENT_PHASE_BUBBLE == static_cast<int>(Rml::EventPhase::Bubble),
	"event phases");
static_assert(MFRMLUI_CLIP_MASK_SET == static_cast<int>(Rml::ClipMaskOperation::Set) &&
		MFRMLUI_CLIP_MASK_SET_INVERSE == static_cast<int>(Rml::ClipMaskOperation::SetInverse) &&
		MFRMLUI_CLIP_MASK_INTERSECT == static_cast<int>(Rml::ClipMaskOperation::Intersect),
	"clip mask operations");
static_assert(MFRMLUI_BLEND_MODE_BLEND == static_cast<int>(Rml::BlendMode::Blend) &&
		MFRMLUI_BLEND_MODE_REPLACE == static_cast<int>(Rml::BlendMode::Replace),
	"blend modes");
static_assert(MFRMLUI_FONT_STYLE_NORMAL == static_cast<int>(Rml::Style::FontStyle::Normal) &&
		MFRMLUI_FONT_STYLE_ITALIC == static_cast<int>(Rml::Style::FontStyle::Italic),
	"font styles");
static_assert(MFRMLUI_FONT_WEIGHT_AUTO == static_cast<int>(Rml::Style::FontWeight::Auto) &&
		MFRMLUI_FONT_WEIGHT_NORMAL == static_cast<int>(Rml::Style::FontWeight::Normal) &&
		MFRMLUI_FONT_WEIGHT_BOLD == static_cast<int>(Rml::Style::FontWeight::Bold),
	"font weights");

// Key identifiers (generated from RmlUi 6.3 Include/RmlUi/Core/Input.h).
static_assert(MFRMLUI_KI_UNKNOWN == Rml::Input::KI_UNKNOWN, "key identifier mismatch");
static_assert(MFRMLUI_KI_SPACE == Rml::Input::KI_SPACE, "key identifier mismatch");
static_assert(MFRMLUI_KI_0 == Rml::Input::KI_0, "key identifier mismatch");
static_assert(MFRMLUI_KI_1 == Rml::Input::KI_1, "key identifier mismatch");
static_assert(MFRMLUI_KI_2 == Rml::Input::KI_2, "key identifier mismatch");
static_assert(MFRMLUI_KI_3 == Rml::Input::KI_3, "key identifier mismatch");
static_assert(MFRMLUI_KI_4 == Rml::Input::KI_4, "key identifier mismatch");
static_assert(MFRMLUI_KI_5 == Rml::Input::KI_5, "key identifier mismatch");
static_assert(MFRMLUI_KI_6 == Rml::Input::KI_6, "key identifier mismatch");
static_assert(MFRMLUI_KI_7 == Rml::Input::KI_7, "key identifier mismatch");
static_assert(MFRMLUI_KI_8 == Rml::Input::KI_8, "key identifier mismatch");
static_assert(MFRMLUI_KI_9 == Rml::Input::KI_9, "key identifier mismatch");
static_assert(MFRMLUI_KI_A == Rml::Input::KI_A, "key identifier mismatch");
static_assert(MFRMLUI_KI_B == Rml::Input::KI_B, "key identifier mismatch");
static_assert(MFRMLUI_KI_C == Rml::Input::KI_C, "key identifier mismatch");
static_assert(MFRMLUI_KI_D == Rml::Input::KI_D, "key identifier mismatch");
static_assert(MFRMLUI_KI_E == Rml::Input::KI_E, "key identifier mismatch");
static_assert(MFRMLUI_KI_F == Rml::Input::KI_F, "key identifier mismatch");
static_assert(MFRMLUI_KI_G == Rml::Input::KI_G, "key identifier mismatch");
static_assert(MFRMLUI_KI_H == Rml::Input::KI_H, "key identifier mismatch");
static_assert(MFRMLUI_KI_I == Rml::Input::KI_I, "key identifier mismatch");
static_assert(MFRMLUI_KI_J == Rml::Input::KI_J, "key identifier mismatch");
static_assert(MFRMLUI_KI_K == Rml::Input::KI_K, "key identifier mismatch");
static_assert(MFRMLUI_KI_L == Rml::Input::KI_L, "key identifier mismatch");
static_assert(MFRMLUI_KI_M == Rml::Input::KI_M, "key identifier mismatch");
static_assert(MFRMLUI_KI_N == Rml::Input::KI_N, "key identifier mismatch");
static_assert(MFRMLUI_KI_O == Rml::Input::KI_O, "key identifier mismatch");
static_assert(MFRMLUI_KI_P == Rml::Input::KI_P, "key identifier mismatch");
static_assert(MFRMLUI_KI_Q == Rml::Input::KI_Q, "key identifier mismatch");
static_assert(MFRMLUI_KI_R == Rml::Input::KI_R, "key identifier mismatch");
static_assert(MFRMLUI_KI_S == Rml::Input::KI_S, "key identifier mismatch");
static_assert(MFRMLUI_KI_T == Rml::Input::KI_T, "key identifier mismatch");
static_assert(MFRMLUI_KI_U == Rml::Input::KI_U, "key identifier mismatch");
static_assert(MFRMLUI_KI_V == Rml::Input::KI_V, "key identifier mismatch");
static_assert(MFRMLUI_KI_W == Rml::Input::KI_W, "key identifier mismatch");
static_assert(MFRMLUI_KI_X == Rml::Input::KI_X, "key identifier mismatch");
static_assert(MFRMLUI_KI_Y == Rml::Input::KI_Y, "key identifier mismatch");
static_assert(MFRMLUI_KI_Z == Rml::Input::KI_Z, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_1 == Rml::Input::KI_OEM_1, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_PLUS == Rml::Input::KI_OEM_PLUS, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_COMMA == Rml::Input::KI_OEM_COMMA, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_MINUS == Rml::Input::KI_OEM_MINUS, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_PERIOD == Rml::Input::KI_OEM_PERIOD, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_2 == Rml::Input::KI_OEM_2, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_3 == Rml::Input::KI_OEM_3, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_4 == Rml::Input::KI_OEM_4, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_5 == Rml::Input::KI_OEM_5, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_6 == Rml::Input::KI_OEM_6, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_7 == Rml::Input::KI_OEM_7, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_8 == Rml::Input::KI_OEM_8, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_102 == Rml::Input::KI_OEM_102, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD0 == Rml::Input::KI_NUMPAD0, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD1 == Rml::Input::KI_NUMPAD1, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD2 == Rml::Input::KI_NUMPAD2, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD3 == Rml::Input::KI_NUMPAD3, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD4 == Rml::Input::KI_NUMPAD4, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD5 == Rml::Input::KI_NUMPAD5, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD6 == Rml::Input::KI_NUMPAD6, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD7 == Rml::Input::KI_NUMPAD7, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD8 == Rml::Input::KI_NUMPAD8, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPAD9 == Rml::Input::KI_NUMPAD9, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMPADENTER == Rml::Input::KI_NUMPADENTER, "key identifier mismatch");
static_assert(MFRMLUI_KI_MULTIPLY == Rml::Input::KI_MULTIPLY, "key identifier mismatch");
static_assert(MFRMLUI_KI_ADD == Rml::Input::KI_ADD, "key identifier mismatch");
static_assert(MFRMLUI_KI_SEPARATOR == Rml::Input::KI_SEPARATOR, "key identifier mismatch");
static_assert(MFRMLUI_KI_SUBTRACT == Rml::Input::KI_SUBTRACT, "key identifier mismatch");
static_assert(MFRMLUI_KI_DECIMAL == Rml::Input::KI_DECIMAL, "key identifier mismatch");
static_assert(MFRMLUI_KI_DIVIDE == Rml::Input::KI_DIVIDE, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_NEC_EQUAL == Rml::Input::KI_OEM_NEC_EQUAL, "key identifier mismatch");
static_assert(MFRMLUI_KI_BACK == Rml::Input::KI_BACK, "key identifier mismatch");
static_assert(MFRMLUI_KI_TAB == Rml::Input::KI_TAB, "key identifier mismatch");
static_assert(MFRMLUI_KI_CLEAR == Rml::Input::KI_CLEAR, "key identifier mismatch");
static_assert(MFRMLUI_KI_RETURN == Rml::Input::KI_RETURN, "key identifier mismatch");
static_assert(MFRMLUI_KI_PAUSE == Rml::Input::KI_PAUSE, "key identifier mismatch");
static_assert(MFRMLUI_KI_CAPITAL == Rml::Input::KI_CAPITAL, "key identifier mismatch");
static_assert(MFRMLUI_KI_KANA == Rml::Input::KI_KANA, "key identifier mismatch");
static_assert(MFRMLUI_KI_HANGUL == Rml::Input::KI_HANGUL, "key identifier mismatch");
static_assert(MFRMLUI_KI_JUNJA == Rml::Input::KI_JUNJA, "key identifier mismatch");
static_assert(MFRMLUI_KI_FINAL == Rml::Input::KI_FINAL, "key identifier mismatch");
static_assert(MFRMLUI_KI_HANJA == Rml::Input::KI_HANJA, "key identifier mismatch");
static_assert(MFRMLUI_KI_KANJI == Rml::Input::KI_KANJI, "key identifier mismatch");
static_assert(MFRMLUI_KI_ESCAPE == Rml::Input::KI_ESCAPE, "key identifier mismatch");
static_assert(MFRMLUI_KI_CONVERT == Rml::Input::KI_CONVERT, "key identifier mismatch");
static_assert(MFRMLUI_KI_NONCONVERT == Rml::Input::KI_NONCONVERT, "key identifier mismatch");
static_assert(MFRMLUI_KI_ACCEPT == Rml::Input::KI_ACCEPT, "key identifier mismatch");
static_assert(MFRMLUI_KI_MODECHANGE == Rml::Input::KI_MODECHANGE, "key identifier mismatch");
static_assert(MFRMLUI_KI_PRIOR == Rml::Input::KI_PRIOR, "key identifier mismatch");
static_assert(MFRMLUI_KI_NEXT == Rml::Input::KI_NEXT, "key identifier mismatch");
static_assert(MFRMLUI_KI_END == Rml::Input::KI_END, "key identifier mismatch");
static_assert(MFRMLUI_KI_HOME == Rml::Input::KI_HOME, "key identifier mismatch");
static_assert(MFRMLUI_KI_LEFT == Rml::Input::KI_LEFT, "key identifier mismatch");
static_assert(MFRMLUI_KI_UP == Rml::Input::KI_UP, "key identifier mismatch");
static_assert(MFRMLUI_KI_RIGHT == Rml::Input::KI_RIGHT, "key identifier mismatch");
static_assert(MFRMLUI_KI_DOWN == Rml::Input::KI_DOWN, "key identifier mismatch");
static_assert(MFRMLUI_KI_SELECT == Rml::Input::KI_SELECT, "key identifier mismatch");
static_assert(MFRMLUI_KI_PRINT == Rml::Input::KI_PRINT, "key identifier mismatch");
static_assert(MFRMLUI_KI_EXECUTE == Rml::Input::KI_EXECUTE, "key identifier mismatch");
static_assert(MFRMLUI_KI_SNAPSHOT == Rml::Input::KI_SNAPSHOT, "key identifier mismatch");
static_assert(MFRMLUI_KI_INSERT == Rml::Input::KI_INSERT, "key identifier mismatch");
static_assert(MFRMLUI_KI_DELETE == Rml::Input::KI_DELETE, "key identifier mismatch");
static_assert(MFRMLUI_KI_HELP == Rml::Input::KI_HELP, "key identifier mismatch");
static_assert(MFRMLUI_KI_LWIN == Rml::Input::KI_LWIN, "key identifier mismatch");
static_assert(MFRMLUI_KI_RWIN == Rml::Input::KI_RWIN, "key identifier mismatch");
static_assert(MFRMLUI_KI_APPS == Rml::Input::KI_APPS, "key identifier mismatch");
static_assert(MFRMLUI_KI_POWER == Rml::Input::KI_POWER, "key identifier mismatch");
static_assert(MFRMLUI_KI_SLEEP == Rml::Input::KI_SLEEP, "key identifier mismatch");
static_assert(MFRMLUI_KI_WAKE == Rml::Input::KI_WAKE, "key identifier mismatch");
static_assert(MFRMLUI_KI_F1 == Rml::Input::KI_F1, "key identifier mismatch");
static_assert(MFRMLUI_KI_F2 == Rml::Input::KI_F2, "key identifier mismatch");
static_assert(MFRMLUI_KI_F3 == Rml::Input::KI_F3, "key identifier mismatch");
static_assert(MFRMLUI_KI_F4 == Rml::Input::KI_F4, "key identifier mismatch");
static_assert(MFRMLUI_KI_F5 == Rml::Input::KI_F5, "key identifier mismatch");
static_assert(MFRMLUI_KI_F6 == Rml::Input::KI_F6, "key identifier mismatch");
static_assert(MFRMLUI_KI_F7 == Rml::Input::KI_F7, "key identifier mismatch");
static_assert(MFRMLUI_KI_F8 == Rml::Input::KI_F8, "key identifier mismatch");
static_assert(MFRMLUI_KI_F9 == Rml::Input::KI_F9, "key identifier mismatch");
static_assert(MFRMLUI_KI_F10 == Rml::Input::KI_F10, "key identifier mismatch");
static_assert(MFRMLUI_KI_F11 == Rml::Input::KI_F11, "key identifier mismatch");
static_assert(MFRMLUI_KI_F12 == Rml::Input::KI_F12, "key identifier mismatch");
static_assert(MFRMLUI_KI_F13 == Rml::Input::KI_F13, "key identifier mismatch");
static_assert(MFRMLUI_KI_F14 == Rml::Input::KI_F14, "key identifier mismatch");
static_assert(MFRMLUI_KI_F15 == Rml::Input::KI_F15, "key identifier mismatch");
static_assert(MFRMLUI_KI_F16 == Rml::Input::KI_F16, "key identifier mismatch");
static_assert(MFRMLUI_KI_F17 == Rml::Input::KI_F17, "key identifier mismatch");
static_assert(MFRMLUI_KI_F18 == Rml::Input::KI_F18, "key identifier mismatch");
static_assert(MFRMLUI_KI_F19 == Rml::Input::KI_F19, "key identifier mismatch");
static_assert(MFRMLUI_KI_F20 == Rml::Input::KI_F20, "key identifier mismatch");
static_assert(MFRMLUI_KI_F21 == Rml::Input::KI_F21, "key identifier mismatch");
static_assert(MFRMLUI_KI_F22 == Rml::Input::KI_F22, "key identifier mismatch");
static_assert(MFRMLUI_KI_F23 == Rml::Input::KI_F23, "key identifier mismatch");
static_assert(MFRMLUI_KI_F24 == Rml::Input::KI_F24, "key identifier mismatch");
static_assert(MFRMLUI_KI_NUMLOCK == Rml::Input::KI_NUMLOCK, "key identifier mismatch");
static_assert(MFRMLUI_KI_SCROLL == Rml::Input::KI_SCROLL, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_FJ_JISHO == Rml::Input::KI_OEM_FJ_JISHO, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_FJ_MASSHOU == Rml::Input::KI_OEM_FJ_MASSHOU, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_FJ_TOUROKU == Rml::Input::KI_OEM_FJ_TOUROKU, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_FJ_LOYA == Rml::Input::KI_OEM_FJ_LOYA, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_FJ_ROYA == Rml::Input::KI_OEM_FJ_ROYA, "key identifier mismatch");
static_assert(MFRMLUI_KI_LSHIFT == Rml::Input::KI_LSHIFT, "key identifier mismatch");
static_assert(MFRMLUI_KI_RSHIFT == Rml::Input::KI_RSHIFT, "key identifier mismatch");
static_assert(MFRMLUI_KI_LCONTROL == Rml::Input::KI_LCONTROL, "key identifier mismatch");
static_assert(MFRMLUI_KI_RCONTROL == Rml::Input::KI_RCONTROL, "key identifier mismatch");
static_assert(MFRMLUI_KI_LMENU == Rml::Input::KI_LMENU, "key identifier mismatch");
static_assert(MFRMLUI_KI_RMENU == Rml::Input::KI_RMENU, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_BACK == Rml::Input::KI_BROWSER_BACK, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_FORWARD == Rml::Input::KI_BROWSER_FORWARD, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_REFRESH == Rml::Input::KI_BROWSER_REFRESH, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_STOP == Rml::Input::KI_BROWSER_STOP, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_SEARCH == Rml::Input::KI_BROWSER_SEARCH, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_FAVORITES == Rml::Input::KI_BROWSER_FAVORITES, "key identifier mismatch");
static_assert(MFRMLUI_KI_BROWSER_HOME == Rml::Input::KI_BROWSER_HOME, "key identifier mismatch");
static_assert(MFRMLUI_KI_VOLUME_MUTE == Rml::Input::KI_VOLUME_MUTE, "key identifier mismatch");
static_assert(MFRMLUI_KI_VOLUME_DOWN == Rml::Input::KI_VOLUME_DOWN, "key identifier mismatch");
static_assert(MFRMLUI_KI_VOLUME_UP == Rml::Input::KI_VOLUME_UP, "key identifier mismatch");
static_assert(MFRMLUI_KI_MEDIA_NEXT_TRACK == Rml::Input::KI_MEDIA_NEXT_TRACK, "key identifier mismatch");
static_assert(MFRMLUI_KI_MEDIA_PREV_TRACK == Rml::Input::KI_MEDIA_PREV_TRACK, "key identifier mismatch");
static_assert(MFRMLUI_KI_MEDIA_STOP == Rml::Input::KI_MEDIA_STOP, "key identifier mismatch");
static_assert(MFRMLUI_KI_MEDIA_PLAY_PAUSE == Rml::Input::KI_MEDIA_PLAY_PAUSE, "key identifier mismatch");
static_assert(MFRMLUI_KI_LAUNCH_MAIL == Rml::Input::KI_LAUNCH_MAIL, "key identifier mismatch");
static_assert(MFRMLUI_KI_LAUNCH_MEDIA_SELECT == Rml::Input::KI_LAUNCH_MEDIA_SELECT, "key identifier mismatch");
static_assert(MFRMLUI_KI_LAUNCH_APP1 == Rml::Input::KI_LAUNCH_APP1, "key identifier mismatch");
static_assert(MFRMLUI_KI_LAUNCH_APP2 == Rml::Input::KI_LAUNCH_APP2, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_AX == Rml::Input::KI_OEM_AX, "key identifier mismatch");
static_assert(MFRMLUI_KI_ICO_HELP == Rml::Input::KI_ICO_HELP, "key identifier mismatch");
static_assert(MFRMLUI_KI_ICO_00 == Rml::Input::KI_ICO_00, "key identifier mismatch");
static_assert(MFRMLUI_KI_PROCESSKEY == Rml::Input::KI_PROCESSKEY, "key identifier mismatch");
static_assert(MFRMLUI_KI_ICO_CLEAR == Rml::Input::KI_ICO_CLEAR, "key identifier mismatch");
static_assert(MFRMLUI_KI_ATTN == Rml::Input::KI_ATTN, "key identifier mismatch");
static_assert(MFRMLUI_KI_CRSEL == Rml::Input::KI_CRSEL, "key identifier mismatch");
static_assert(MFRMLUI_KI_EXSEL == Rml::Input::KI_EXSEL, "key identifier mismatch");
static_assert(MFRMLUI_KI_EREOF == Rml::Input::KI_EREOF, "key identifier mismatch");
static_assert(MFRMLUI_KI_PLAY == Rml::Input::KI_PLAY, "key identifier mismatch");
static_assert(MFRMLUI_KI_ZOOM == Rml::Input::KI_ZOOM, "key identifier mismatch");
static_assert(MFRMLUI_KI_PA1 == Rml::Input::KI_PA1, "key identifier mismatch");
static_assert(MFRMLUI_KI_OEM_CLEAR == Rml::Input::KI_OEM_CLEAR, "key identifier mismatch");
static_assert(MFRMLUI_KI_LMETA == Rml::Input::KI_LMETA, "key identifier mismatch");
static_assert(MFRMLUI_KI_RMETA == Rml::Input::KI_RMETA, "key identifier mismatch");
