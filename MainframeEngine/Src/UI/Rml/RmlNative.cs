using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine.UI.Rml;

/// <summary>
/// Raw P/Invokes over <c>Native/RmlUi/include/mfrmlui.h</c> (library <c>mfrmlui</c>, ABI 1.0). Source-generated
/// (<see cref="LibraryImportAttribute"/>) with blittable signatures only — handles are <see cref="nint"/>, strings are
/// NUL-terminated UTF-8 <c>byte*</c> — so every call is a direct cdecl call with no marshalling stub or allocation.
/// The managed API (<see cref="RmlCore"/>, <see cref="RmlContext"/>, <see cref="RmlElement"/>, ...) is built on top;
/// use this class only to extend it.
/// </summary>
/// <remarks>
/// Naming: <c>mfrmlui_context_update</c> → <see cref="ContextUpdate"/>. Status codes: <see cref="Ok"/> or a negative
/// <c>Error*</c> value; handle-returning functions return 0 on failure (details in <see cref="GetLastError"/>).
/// </remarks>
internal static unsafe partial class RmlNative
{
    public const string Library = "mfrmlui";

    /// <summary>ABI this binding was written against: <c>(MAJOR &lt;&lt; 16) | MINOR</c>.</summary>
    public const int AbiMajor = 1;
    public const int AbiMinor = 0;
    public const uint AbiVersion = (AbiMajor << 16) | AbiMinor;

    // Status codes.
    public const int Ok = 0;
    public const int ErrorInvalidArgument = -1;
    public const int ErrorNotInitialised = -2;
    public const int ErrorAlreadyInitialised = -3;
    public const int ErrorFailed = -4;
    public const int ErrorException = -5;
    public const int ErrorInUse = -6;
    public const int ErrorNotFound = -7;
    public const int ErrorTypeMismatch = -8;

    // Input results (the shim already resolved RmlUi's inverted bool).
    public const int InputPropagate = 0;
    public const int InputConsumed = 1;

    // Variable kinds for DataModelBindVariable.
    public const int VariableScalar = 0;
    public const int VariableArray = 1;
    public const int VariableStruct = 2;

    // ── Library ─────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint GetAbiVersion();

    [LibraryImport(Library, EntryPoint = "mfrmlui_get_rmlui_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetRmlUiVersion(byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_get_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int GetLastError(byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_initialise")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int Initialise(SystemCallbacks* system, FileCallbacks* file);

    [LibraryImport(Library, EntryPoint = "mfrmlui_shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int Shutdown();

    [LibraryImport(Library, EntryPoint = "mfrmlui_is_initialised")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int IsInitialised();

    [LibraryImport(Library, EntryPoint = "mfrmlui_load_font_face")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int LoadFontFace(byte* path, int fallbackFace, int weight);

    [LibraryImport(Library, EntryPoint = "mfrmlui_load_font_face_from_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int LoadFontFaceFromMemory(byte* data, long size, byte* family, int style, int weight, int fallbackFace);

    [LibraryImport(Library, EntryPoint = "mfrmlui_clear_style_sheet_cache")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ClearStyleSheetCache();

    [LibraryImport(Library, EntryPoint = "mfrmlui_clear_template_cache")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ClearTemplateCache();

    [LibraryImport(Library, EntryPoint = "mfrmlui_release_textures")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ReleaseTextures(nint renderInterface);

    [LibraryImport(Library, EntryPoint = "mfrmlui_release_compiled_geometry")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ReleaseCompiledGeometry(nint renderInterface);

    [LibraryImport(Library, EntryPoint = "mfrmlui_string_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int StringSet(nint stringSink, byte* utf8, int length);

    // ── Render interface ────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_render_interface_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint RenderInterfaceCreate(RenderCallbacks* callbacks);

    [LibraryImport(Library, EntryPoint = "mfrmlui_render_interface_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int RenderInterfaceDestroy(nint renderInterface);

    // ── Context ─────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextCreate(byte* name, int width, int height, nint renderInterface);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_set_dimensions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextSetDimensions(nint context, int width, int height);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_set_density_independent_pixel_ratio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextSetDensityIndependentPixelRatio(nint context, float ratio);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_update")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextUpdate(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextRender(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_next_update_delay")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial double ContextGetNextUpdateDelay(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_enable_mouse_cursor")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextEnableMouseCursor(nint context, int enable);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_activate_theme")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextActivateTheme(nint context, byte* themeName, int activate);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_root_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextGetRootElement(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_hover_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextGetHoverElement(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_focus_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextGetFocusElement(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_element_at_point")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextGetElementAtPoint(nint context, float x, float y);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_num_documents")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextGetNumDocuments(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_get_document")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextGetDocument(nint context, int index);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_is_mouse_interacting")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextIsMouseInteracting(nint context);

    // ── Input ───────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_mouse_move")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessMouseMove(nint context, int x, int y, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_mouse_button_down")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessMouseButtonDown(nint context, int button, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_mouse_button_up")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessMouseButtonUp(nint context, int button, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_mouse_wheel")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessMouseWheel(nint context, float deltaX, float deltaY, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_mouse_leave")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessMouseLeave(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_key_down")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessKeyDown(nint context, int key, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_key_up")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessKeyUp(nint context, int key, int keyModifiers);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_process_text_input")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ContextProcessTextInput(nint context, byte* utf8);

    // ── Documents ───────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_load_document")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextLoadDocument(nint context, byte* path);

    [LibraryImport(Library, EntryPoint = "mfrmlui_context_load_document_from_memory")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ContextLoadDocumentFromMemory(nint context, byte* rml, byte* sourceUrl);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_show")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentShow(nint document, int modal, int focus, int scroll);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_hide")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentHide(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentClose(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_reload")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DocumentReload(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_reload_style_sheet")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentReloadStyleSheet(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_is_visible")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentIsVisible(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_is_modal")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentIsModal(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_pull_to_front")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentPullToFront(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_push_to_back")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentPushToBack(nint document);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_get_title")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentGetTitle(nint document, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_get_source_url")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DocumentGetSourceUrl(nint document, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_document_as_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DocumentAsElement(nint document);

    // ── Elements ────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_owner_document")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementGetOwnerDocument(nint element);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_parent")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementGetParent(nint element);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_num_children")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetNumChildren(nint element);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_child")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementGetChild(nint element, int index);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_element_by_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementGetElementById(nint element, byte* id);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_query_selector")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementQuerySelector(nint element, byte* selector);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_query_selector_all")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementQuerySelectorAll(nint element, byte* selector, nint* outElements, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_tag_name")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetTagName(nint element, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_id")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetId(nint element, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_attribute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetAttribute(nint element, byte* name, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_attribute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetAttribute(nint element, byte* name, byte* value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_remove_attribute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementRemoveAttribute(nint element, byte* name);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_has_attribute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementHasAttribute(nint element, byte* name);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_class")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetClass(nint element, byte* className, int activate);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_is_class_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementIsClassSet(nint element, byte* className);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_class_names")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetClassNames(nint element, byte* classNames);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_pseudo_class")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetPseudoClass(nint element, byte* pseudoClass, int activate);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_is_pseudo_class_set")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementIsPseudoClassSet(nint element, byte* pseudoClass);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_property")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetProperty(nint element, byte* name, byte* value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_remove_property")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementRemoveProperty(nint element, byte* name);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_inner_rml")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetInnerRml(nint element, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_inner_rml")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetInnerRml(nint element, byte* rml);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetValue(nint element, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_set_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementSetValue(nint element, byte* value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_focus")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementFocus(nint element, int focusVisible);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_blur")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementBlur(nint element);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_click")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementClick(nint element);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_scroll_into_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementScrollIntoView(nint element, int alignWithTop);

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_get_bounds")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ElementGetBounds(nint element, RmlRect* outBounds);

    // ── Events ──────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_element_add_event_listener")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint ElementAddEventListener(nint element, byte* eventType, int inCapturePhase,
        delegate* unmanaged[Cdecl]<nint, nint, void> callback, delegate* unmanaged[Cdecl]<nint, void> onDetach, nint userData);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_listener_remove")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EventListenerRemove(nint listener);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_get_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EventGetType(nint evt, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_get_phase")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EventGetPhase(nint evt);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_get_target_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EventGetTargetElement(nint evt);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_get_current_element")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EventGetCurrentElement(nint evt);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_stop_propagation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EventStopPropagation(nint evt);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_stop_immediate_propagation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int EventStopImmediatePropagation(nint evt);

    [LibraryImport(Library, EntryPoint = "mfrmlui_event_get_parameters")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint EventGetParameters(nint evt);

    // ── Variants and dictionaries ───────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_type")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetType(nint variant);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_bool")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetBool(nint variant, int* outValue);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_int64")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetInt64(nint variant, long* outValue);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_double")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetDouble(nint variant, double* outValue);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetString(nint variant, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_float4")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetFloat4(nint variant, float* outValues4);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_colourb")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetColourb(nint variant, byte* outRgba4);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_get_color_stops")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantGetColorStops(nint variant, RmlColorStop* outStops, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_set_none")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantSetNone(nint variant);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_set_bool")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantSetBool(nint variant, int value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_set_int64")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantSetInt64(nint variant, long value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_set_double")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantSetDouble(nint variant, double value);

    [LibraryImport(Library, EntryPoint = "mfrmlui_variant_set_string")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int VariantSetString(nint variant, byte* utf8, int length);

    [LibraryImport(Library, EntryPoint = "mfrmlui_dictionary_get_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DictionaryGetCount(nint dictionary);

    [LibraryImport(Library, EntryPoint = "mfrmlui_dictionary_get_key")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DictionaryGetKey(nint dictionary, int index, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "mfrmlui_dictionary_get_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DictionaryGetValue(nint dictionary, int index);

    [LibraryImport(Library, EntryPoint = "mfrmlui_dictionary_find")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DictionaryFind(nint dictionary, byte* key);

    // ── Data models ─────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint DataModelCreate(nint context, byte* name);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_remove")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelRemove(nint model);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_bind_func")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelBindFunc(nint model, byte* name,
        delegate* unmanaged[Cdecl]<nint, nint, void> get, delegate* unmanaged[Cdecl]<nint, nint, void> set,
        delegate* unmanaged[Cdecl]<nint, void> release, nint userData);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_bind_event_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelBindEventCallback(nint model, byte* name,
        delegate* unmanaged[Cdecl]<nint, nint, nint, nint*, int, void> callback,
        delegate* unmanaged[Cdecl]<nint, void> release, nint userData);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_bind_variable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelBindVariable(nint model, byte* name, int rootKind, ulong rootNode, VariableCallbacks* callbacks);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_dirty_variable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelDirtyVariable(nint model, byte* name);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_dirty_all_variables")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelDirtyAllVariables(nint model);

    [LibraryImport(Library, EntryPoint = "mfrmlui_data_model_is_variable_dirty")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DataModelIsVariableDirty(nint model, byte* name);

    // ── Debugger ────────────────────────────────────────────────────────────────────────────────────────────

    [LibraryImport(Library, EntryPoint = "mfrmlui_debugger_initialise")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DebuggerInitialise(nint hostContext);

    [LibraryImport(Library, EntryPoint = "mfrmlui_debugger_set_context")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DebuggerSetContext(nint context);

    [LibraryImport(Library, EntryPoint = "mfrmlui_debugger_set_visible")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DebuggerSetVisible(int visible);

    [LibraryImport(Library, EntryPoint = "mfrmlui_debugger_is_visible")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DebuggerIsVisible();

    [LibraryImport(Library, EntryPoint = "mfrmlui_debugger_shutdown")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int DebuggerShutdown();

    // ── Callback structs (layouts identical to mfrmlui.h; 64-bit only) ─────────────────────────────────────

    /// <summary><c>mfrmlui_system_callbacks</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemCallbacks
    {
        public uint StructSize;
        public nint UserData;
        public delegate* unmanaged[Cdecl]<nint, double> GetElapsedTime;
        public delegate* unmanaged[Cdecl]<nint, byte*, nint, int> TranslateString;
        public delegate* unmanaged[Cdecl]<nint, int, byte*, int> LogMessage;
        public delegate* unmanaged[Cdecl]<nint, byte*, void> SetMouseCursor;
        public delegate* unmanaged[Cdecl]<nint, byte*, void> SetClipboardText;
        public delegate* unmanaged[Cdecl]<nint, nint, void> GetClipboardText;
        public delegate* unmanaged[Cdecl]<nint, float, float, float, void> ActivateKeyboard;
        public delegate* unmanaged[Cdecl]<nint, void> DeactivateKeyboard;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*, nint, void> JoinPath;
    }

    /// <summary><c>mfrmlui_file_callbacks</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileCallbacks
    {
        public uint StructSize;
        public nint UserData;
        public delegate* unmanaged[Cdecl]<nint, byte*, ulong> Open;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> Close;
        public delegate* unmanaged[Cdecl]<nint, ulong, byte*, ulong, ulong> Read;
        public delegate* unmanaged[Cdecl]<nint, ulong, long, int, int> Seek;
        public delegate* unmanaged[Cdecl]<nint, ulong, ulong> Tell;
        public delegate* unmanaged[Cdecl]<nint, ulong, ulong> Length;
        public delegate* unmanaged[Cdecl]<nint, byte*, nint, int> LoadFile;
    }

    /// <summary><c>mfrmlui_render_callbacks</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenderCallbacks
    {
        public uint StructSize;
        public nint UserData;
        // required
        public delegate* unmanaged[Cdecl]<nint, RmlVertex*, int, int*, int, ulong> CompileGeometry;
        public delegate* unmanaged[Cdecl]<nint, ulong, float, float, ulong, void> RenderGeometry;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> ReleaseGeometry;
        public delegate* unmanaged[Cdecl]<nint, byte*, int*, int*, ulong> LoadTexture;
        public delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, ulong> GenerateTexture;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> ReleaseTexture;
        public delegate* unmanaged[Cdecl]<nint, int, void> EnableScissorRegion;
        public delegate* unmanaged[Cdecl]<nint, int, int, int, int, void> SetScissorRegion;
        // optional
        public delegate* unmanaged[Cdecl]<nint, float*, void> SetTransform;
        public delegate* unmanaged[Cdecl]<nint, int, void> EnableClipMask;
        public delegate* unmanaged[Cdecl]<nint, int, ulong, float, float, void> RenderToClipMask;
        public delegate* unmanaged[Cdecl]<nint, ulong> PushLayer;
        public delegate* unmanaged[Cdecl]<nint, ulong, ulong, int, ulong*, int, void> CompositeLayers;
        public delegate* unmanaged[Cdecl]<nint, void> PopLayer;
        public delegate* unmanaged[Cdecl]<nint, ulong> SaveLayerAsTexture;
        public delegate* unmanaged[Cdecl]<nint, ulong> SaveLayerAsMaskImage;
        public delegate* unmanaged[Cdecl]<nint, byte*, nint, ulong> CompileFilter;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> ReleaseFilter;
        public delegate* unmanaged[Cdecl]<nint, byte*, nint, ulong> CompileShader;
        public delegate* unmanaged[Cdecl]<nint, ulong, ulong, float, float, ulong, void> RenderShader;
        public delegate* unmanaged[Cdecl]<nint, ulong, void> ReleaseShader;
    }

    /// <summary><c>mfrmlui_variable_callbacks</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VariableCallbacks
    {
        public uint StructSize;
        public nint UserData;
        public delegate* unmanaged[Cdecl]<nint, ulong, nint, int> Get;
        public delegate* unmanaged[Cdecl]<nint, ulong, nint, int> Set;
        public delegate* unmanaged[Cdecl]<nint, ulong, int> Size;
        public delegate* unmanaged[Cdecl]<nint, ulong, int, byte*, ulong*, int> Child;
        public delegate* unmanaged[Cdecl]<nint, void> Release;
    }

    /// <summary>The size C code sees for a callback struct (<c>struct_size</c>).</summary>
    public static uint SizeOf<T>() where T : unmanaged => (uint)sizeof(T);
}

/// <summary>An RmlUi vertex (<c>mfrmlui_vertex</c> = <c>Rml::Vertex</c>, 20 bytes): position, premultiplied RGBA8, UV.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct RmlVertex
{
    public float X, Y;
    public uint ColorRgba; // bytes in memory: R, G, B, A (premultiplied)
    public float U, V;
}

/// <summary>A rectangle in context pixels (<c>mfrmlui_rectf</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RmlRect : IEquatable<RmlRect>
{
    public float X, Y, Width, Height;

    public RmlRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public readonly bool Equals(RmlRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override readonly bool Equals(object? obj) => obj is RmlRect r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(RmlRect a, RmlRect b) => a.Equals(b);
    public static bool operator !=(RmlRect a, RmlRect b) => !a.Equals(b);
    public override readonly string ToString() => $"({X}, {Y}, {Width}×{Height})";
}

/// <summary>One gradient stop (<c>mfrmlui_color_stop</c>): premultiplied RGBA8 colour and resolved position.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RmlColorStop
{
    public uint ColorRgba;
    public float Position;
}
