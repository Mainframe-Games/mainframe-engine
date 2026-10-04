// Variants, dictionaries and data models (data binding).
#include "mfrmlui_internal.h"

#include <RmlUi/Core/DecorationTypes.h>

#include <iterator>
#include <limits>

using namespace mfrmlui;

namespace {

constexpr const char* kInvalidVariant = "NULL variant handle";
constexpr const char* kInvalidDictionary = "NULL dictionary handle";
constexpr const char* kInvalidModel = "invalid data model handle (NULL, removed, or library not initialised)";

mfrmlui_data_model* LiveModel(mfrmlui_data_model* model)
{
	State& state = GetState();
	if (!model || !state.initialised || !state.data_models.count(model))
		return nullptr;
	return model;
}

template <typename F>
int32_t WithModel(mfrmlui_data_model* model, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!LiveModel(model))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidModel);
		return body(*model);
	});
}

template <typename F>
int32_t WithVariant(const mfrmlui_variant* variant, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!variant)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidVariant);
		return body(*ToRml(variant));
	});
}

template <typename F>
int32_t WithMutableVariant(mfrmlui_variant* variant, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!variant)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidVariant);
		body(*ToRml(variant));
		return MFRMLUI_OK;
	});
}

int32_t TypeMismatch()
{
	return Fail(MFRMLUI_ERROR_TYPE_MISMATCH, "variant value cannot be converted to the requested type");
}

const Rml::Variant* DictionaryValueAt(const Rml::Dictionary& dictionary, int32_t index)
{
	if (index < 0 || static_cast<size_t>(index) >= dictionary.size())
		return nullptr;
	auto it = dictionary.begin();
	std::advance(it, index);
	return &it->second;
}

} // namespace

// ----- dynamic variables ------------------------------------------------------------------------------------------------

// One binding of mfrmlui_data_model_bind_variable: the caller's callbacks plus one VariableDefinition per kind. Every
// node of the bound graph is a DataVariable(definition-of-its-kind, node-token).
struct mfrmlui_data_model::Variable {
	class Definition final : public Rml::VariableDefinition {
	public:
		Definition(Rml::DataVariableType in_type, Variable& in_owner) : Rml::VariableDefinition(in_type), owner(in_owner) {}

		bool Get(void* ptr, Rml::Variant& variant) override
		{
			const mfrmlui_variable_callbacks& cb = owner.callbacks;
			if (Type() != Rml::DataVariableType::Scalar || !cb.get)
				return false;
			return cb.get(cb.user_data, PointerToNode(ptr), ToHandle(&variant)) != 0;
		}

		bool Set(void* ptr, const Rml::Variant& variant) override
		{
			const mfrmlui_variable_callbacks& cb = owner.callbacks;
			if (Type() != Rml::DataVariableType::Scalar || !cb.set)
				return false;
			return cb.set(cb.user_data, PointerToNode(ptr), ToHandle(&variant)) != 0;
		}

		int Size(void* ptr) override
		{
			const mfrmlui_variable_callbacks& cb = owner.callbacks;
			if (Type() != Rml::DataVariableType::Array || !cb.size)
				return 0;
			const int32_t size = cb.size(cb.user_data, PointerToNode(ptr));
			return size < 0 ? 0 : size;
		}

		Rml::DataVariable Child(void* ptr, const Rml::DataAddressEntry& address) override
		{
			const mfrmlui_variable_callbacks& cb = owner.callbacks;
			if (!cb.child)
				return Rml::DataVariable();

			int32_t kind = -1;
			uint64_t child_node = 0;
			if (Type() == Rml::DataVariableType::Array)
			{
				if (address.index < 0)
				{
					if (address.name == "size")
						return Rml::MakeLiteralIntVariable(Size(ptr));
					return Rml::DataVariable();
				}
				kind = cb.child(cb.user_data, PointerToNode(ptr), address.index, nullptr, &child_node);
			}
			else if (Type() == Rml::DataVariableType::Struct)
			{
				kind = cb.child(cb.user_data, PointerToNode(ptr), -1, address.name.c_str(), &child_node);
			}

			if (kind < MFRMLUI_VARIABLE_SCALAR || kind > MFRMLUI_VARIABLE_STRUCT)
				return Rml::DataVariable();
			return Rml::DataVariable(owner.definitions[kind].get(), NodeToPointer(child_node));
		}

	private:
		Variable& owner;
	};

	explicit Variable(const mfrmlui_variable_callbacks& in_callbacks) : callbacks(in_callbacks)
	{
		definitions[MFRMLUI_VARIABLE_SCALAR] = std::make_unique<Definition>(Rml::DataVariableType::Scalar, *this);
		definitions[MFRMLUI_VARIABLE_ARRAY] = std::make_unique<Definition>(Rml::DataVariableType::Array, *this);
		definitions[MFRMLUI_VARIABLE_STRUCT] = std::make_unique<Definition>(Rml::DataVariableType::Struct, *this);
	}

	mfrmlui_variable_callbacks callbacks;
	std::unique_ptr<Definition> definitions[3];
};

mfrmlui_data_model::mfrmlui_data_model(mfrmlui_context* in_owner, std::string in_name, Rml::DataModelConstructor in_constructor) :
	owner(in_owner), name(std::move(in_name)), constructor(in_constructor), handle(in_constructor.GetModelHandle())
{}

mfrmlui_data_model::~mfrmlui_data_model()
{
	// The RmlUi data model is destroyed before this wrapper, so nothing references the bindings any more.
	for (const Release& release : releases)
	{
		if (release.callback)
			release.callback(release.user_data);
	}
	for (const auto& variable : variables)
	{
		if (variable->callbacks.release)
			variable->callbacks.release(variable->callbacks.user_data);
	}
}

extern "C" {

// ----- variants -------------------------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_type(const mfrmlui_variant* variant)
{
	return WithVariant(variant, [](const Rml::Variant& v) -> int32_t {
		switch (v.GetType())
		{
		case Rml::Variant::NONE: return MFRMLUI_VARIANT_NONE;
		case Rml::Variant::BOOL: return MFRMLUI_VARIANT_BOOL;
		case Rml::Variant::BYTE:
		case Rml::Variant::INT:
		case Rml::Variant::INT64:
		case Rml::Variant::UINT:
		case Rml::Variant::UINT64: return MFRMLUI_VARIANT_INT;
		case Rml::Variant::FLOAT:
		case Rml::Variant::DOUBLE: return MFRMLUI_VARIANT_FLOAT;
		case Rml::Variant::CHAR:
		case Rml::Variant::STRING: return MFRMLUI_VARIANT_STRING;
		case Rml::Variant::VECTOR2: return MFRMLUI_VARIANT_VECTOR2;
		case Rml::Variant::VECTOR3: return MFRMLUI_VARIANT_VECTOR3;
		case Rml::Variant::VECTOR4: return MFRMLUI_VARIANT_VECTOR4;
		case Rml::Variant::COLOURF: return MFRMLUI_VARIANT_COLOURF;
		case Rml::Variant::COLOURB: return MFRMLUI_VARIANT_COLOURB;
		case Rml::Variant::COLORSTOPLIST: return MFRMLUI_VARIANT_COLOR_STOP_LIST;
		default: return MFRMLUI_VARIANT_OTHER;
		}
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_bool(const mfrmlui_variant* variant, mfrmlui_bool* out_value)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (!out_value)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_bool: NULL output");
		bool value = false;
		if (!v.GetInto(value))
			return TypeMismatch();
		*out_value = value ? 1 : 0;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_int64(const mfrmlui_variant* variant, int64_t* out_value)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (!out_value)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_int64: NULL output");
		int64_t value = 0;
		if (!v.GetInto(value))
			return TypeMismatch();
		*out_value = value;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_double(const mfrmlui_variant* variant, double* out_value)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (!out_value)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_double: NULL output");
		double value = 0.0;
		if (!v.GetInto(value))
			return TypeMismatch();
		*out_value = value;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_string(const mfrmlui_variant* variant, char* buffer, int32_t capacity)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		Rml::String value;
		if (v.GetType() != Rml::Variant::NONE && !v.GetInto(value))
			return TypeMismatch();
		return CopyOut(value, buffer, capacity);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_float4(const mfrmlui_variant* variant, float* out_values4)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (!out_values4)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_float4: NULL output");
		float r[4] = {0.f, 0.f, 0.f, 0.f};
		switch (v.GetType())
		{
		case Rml::Variant::VECTOR2:
		{
			const auto& x = v.GetReference<Rml::Vector2f>();
			r[0] = x.x;
			r[1] = x.y;
			break;
		}
		case Rml::Variant::VECTOR3:
		{
			const auto& x = v.GetReference<Rml::Vector3f>();
			r[0] = x.x;
			r[1] = x.y;
			r[2] = x.z;
			break;
		}
		case Rml::Variant::VECTOR4:
		{
			const auto& x = v.GetReference<Rml::Vector4f>();
			r[0] = x.x;
			r[1] = x.y;
			r[2] = x.z;
			r[3] = x.w;
			break;
		}
		case Rml::Variant::COLOURF:
		{
			const auto& x = v.GetReference<Rml::Colourf>();
			r[0] = x.red;
			r[1] = x.green;
			r[2] = x.blue;
			r[3] = x.alpha;
			break;
		}
		case Rml::Variant::COLOURB:
		{
			const auto& x = v.GetReference<Rml::Colourb>();
			r[0] = static_cast<float>(x.red);
			r[1] = static_cast<float>(x.green);
			r[2] = static_cast<float>(x.blue);
			r[3] = static_cast<float>(x.alpha);
			break;
		}
		default: return TypeMismatch();
		}
		std::memcpy(out_values4, r, sizeof r);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_colourb(const mfrmlui_variant* variant, uint8_t* out_rgba4)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (!out_rgba4)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_colourb: NULL output");
		if (v.GetType() != Rml::Variant::COLOURB)
			return TypeMismatch();
		const auto& x = v.GetReference<Rml::Colourb>();
		out_rgba4[0] = x.red;
		out_rgba4[1] = x.green;
		out_rgba4[2] = x.blue;
		out_rgba4[3] = x.alpha;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_color_stops(const mfrmlui_variant* variant, mfrmlui_color_stop* out_stops, int32_t capacity)
{
	return WithVariant(variant, [&](const Rml::Variant& v) -> int32_t {
		if (capacity < 0 || (!out_stops && capacity > 0))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_get_color_stops: invalid output array");
		if (v.GetType() != Rml::Variant::COLORSTOPLIST)
			return TypeMismatch();
		const auto& stops = v.GetReference<Rml::ColorStopList>();
		if (stops.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max()))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_variant_get_color_stops: too many stops");
		const int32_t count = static_cast<int32_t>(stops.size());
		for (int32_t i = 0; i < count && i < capacity; ++i)
		{
			const Rml::ColorStop& stop = stops[static_cast<size_t>(i)];
			mfrmlui_color_stop& out = out_stops[i];
			out.colour[0] = stop.color.red;
			out.colour[1] = stop.color.green;
			out.colour[2] = stop.color.blue;
			out.colour[3] = stop.color.alpha;
			out.position = stop.position.number;
		}
		return count;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_none(mfrmlui_variant* variant)
{
	return WithMutableVariant(variant, [](Rml::Variant& v) { v.Clear(); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_bool(mfrmlui_variant* variant, mfrmlui_bool value)
{
	return WithMutableVariant(variant, [&](Rml::Variant& v) { v = (value != 0); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_int64(mfrmlui_variant* variant, int64_t value)
{
	return WithMutableVariant(variant, [&](Rml::Variant& v) { v = value; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_double(mfrmlui_variant* variant, double value)
{
	return WithMutableVariant(variant, [&](Rml::Variant& v) { v = value; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_string(mfrmlui_variant* variant, const char* utf8, int32_t length)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!variant || (!utf8 && length != 0))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_variant_set_string: NULL variant or text");
		Rml::Variant& v = *ToRml(variant);
		if (!utf8)
			v = Rml::String();
		else if (length < 0)
			v = Rml::String(utf8);
		else
			v = Rml::String(utf8, static_cast<size_t>(length));
		return MFRMLUI_OK;
	});
}

// ----- dictionaries -------------------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_dictionary_get_count(const mfrmlui_dictionary* dictionary)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!dictionary)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidDictionary);
		const size_t size = ToRml(dictionary)->size();
		if (size > static_cast<size_t>(std::numeric_limits<int32_t>::max()))
			return Fail(MFRMLUI_ERROR_FAILED, "dictionary too large");
		return static_cast<int32_t>(size);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_dictionary_get_key(const mfrmlui_dictionary* dictionary, int32_t index, char* buffer,
	int32_t capacity)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!dictionary)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidDictionary);
		const Rml::Dictionary& d = *ToRml(dictionary);
		if (index < 0 || static_cast<size_t>(index) >= d.size())
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_dictionary_get_key: index out of range");
		auto it = d.begin();
		std::advance(it, index);
		return CopyOut(it->first, buffer, capacity);
	});
}

MFRMLUI_API const mfrmlui_variant* MFRMLUI_CALL mfrmlui_dictionary_get_value(const mfrmlui_dictionary* dictionary, int32_t index)
{
	return Guard<const mfrmlui_variant*>(nullptr, [&]() -> const mfrmlui_variant* {
		if (!dictionary)
			return FailNull<const mfrmlui_variant>(kInvalidDictionary);
		const Rml::Variant* value = DictionaryValueAt(*ToRml(dictionary), index);
		if (!value)
			return FailNull<const mfrmlui_variant>("mfrmlui_dictionary_get_value: index out of range");
		return ToHandle(value);
	});
}

MFRMLUI_API const mfrmlui_variant* MFRMLUI_CALL mfrmlui_dictionary_find(const mfrmlui_dictionary* dictionary, const char* key)
{
	return Guard<const mfrmlui_variant*>(nullptr, [&]() -> const mfrmlui_variant* {
		if (!dictionary || !key)
			return FailNull<const mfrmlui_variant>("mfrmlui_dictionary_find: NULL dictionary or key");
		const Rml::Dictionary& d = *ToRml(dictionary);
		auto it = d.find(Rml::String(key));
		if (it == d.end())
			return nullptr;
		return ToHandle(&it->second);
	});
}

// ----- data models ----------------------------------------------------------------------------------------------------------

MFRMLUI_API mfrmlui_data_model* MFRMLUI_CALL mfrmlui_data_model_create(mfrmlui_context* context, const char* name)
{
	return Guard<mfrmlui_data_model*>(nullptr, [&]() -> mfrmlui_data_model* {
		State& state = GetState();
		if (!context || !state.initialised || !state.contexts.count(context) || !context->context)
			return FailNull<mfrmlui_data_model>("mfrmlui_data_model_create: invalid context handle");
		if (!name || !*name)
			return FailNull<mfrmlui_data_model>("mfrmlui_data_model_create: empty name");

		Rml::DataModelConstructor constructor = context->context->CreateDataModel(name);
		if (!constructor)
			return FailNull<mfrmlui_data_model>("mfrmlui_data_model_create: RmlUi refused the model (name already in use?)");

		auto model = std::make_unique<mfrmlui_data_model>(context, std::string(name), constructor);
		mfrmlui_data_model* raw = model.get();
		context->data_models.push_back(std::move(model));
		state.data_models.insert(raw);
		return raw;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_remove(mfrmlui_data_model* model)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		mfrmlui_context* context = m.owner;
		if (context->context)
			context->context->RemoveDataModel(m.name);
		GetState().data_models.erase(&m);
		auto& models = context->data_models;
		for (auto it = models.begin(); it != models.end(); ++it)
		{
			if (it->get() == &m)
			{
				models.erase(it); // runs the bindings' release callbacks
				break;
			}
		}
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_func(mfrmlui_data_model* model, const char* name, mfrmlui_data_get_callback get,
	mfrmlui_data_set_callback set, mfrmlui_release_callback release, void* user_data)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		if (!name || !*name || !get)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_bind_func: empty name or NULL getter");

		Rml::DataGetFunc get_func = [get, user_data](Rml::Variant& value) { get(user_data, ToHandle(&value)); };
		Rml::DataSetFunc set_func;
		if (set)
			set_func = [set, user_data](const Rml::Variant& value) { set(user_data, ToHandle(&value)); };

		m.releases.reserve(m.releases.size() + 1); // never fail after a successful bind
		if (!m.constructor.BindFunc(name, std::move(get_func), std::move(set_func)))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_data_model_bind_func: RmlUi refused the binding (name already bound?)");
		m.releases.push_back({release, user_data});
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_event_callback(mfrmlui_data_model* model, const char* name,
	mfrmlui_data_event_callback callback, mfrmlui_release_callback release, void* user_data)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		if (!name || !*name || !callback)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_bind_event_callback: empty name or NULL callback");

		mfrmlui_data_model* handle = &m;
		Rml::DataEventFunc func = [callback, user_data, handle](Rml::DataModelHandle, Rml::Event& event, const Rml::VariantList& arguments) {
			std::vector<const mfrmlui_variant*> pointers;
			pointers.reserve(arguments.size());
			for (const Rml::Variant& argument : arguments)
				pointers.push_back(ToHandle(&argument));
			const int32_t count = static_cast<int32_t>(pointers.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max())
					? std::numeric_limits<int32_t>::max()
					: pointers.size());
			callback(user_data, handle, ToHandle(&event), pointers.empty() ? nullptr : pointers.data(), count);
		};

		m.releases.reserve(m.releases.size() + 1);
		if (!m.constructor.BindEventCallback(name, std::move(func)))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_data_model_bind_event_callback: RmlUi refused the binding (name already bound?)");
		m.releases.push_back({release, user_data});
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_variable(mfrmlui_data_model* model, const char* name, int32_t root_kind,
	uint64_t root_node, const mfrmlui_variable_callbacks* callbacks)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		if (!name || !*name || root_kind < MFRMLUI_VARIABLE_SCALAR || root_kind > MFRMLUI_VARIABLE_STRUCT)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_bind_variable: empty name or invalid root kind");
		mfrmlui_variable_callbacks copy;
		if (!CopyCallbacks(callbacks, copy, sizeof(mfrmlui_variable_callbacks)))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_bind_variable: NULL callbacks or struct_size too small");
		if (!copy.get || !copy.child || !copy.size)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_bind_variable: get, size and child callbacks are required");

		auto variable = std::make_unique<mfrmlui_data_model::Variable>(copy);
		const Rml::DataVariable root(variable->definitions[root_kind].get(), NodeToPointer(root_node));
		m.variables.reserve(m.variables.size() + 1);
		if (!m.constructor.BindCustomDataVariable(name, root))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_data_model_bind_variable: RmlUi refused the binding (name already bound?)");
		m.variables.push_back(std::move(variable));
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_dirty_variable(mfrmlui_data_model* model, const char* name)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		if (!name)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_data_model_dirty_variable: NULL name");
		m.handle.DirtyVariable(name);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_dirty_all_variables(mfrmlui_data_model* model)
{
	return WithModel(model, [&](mfrmlui_data_model& m) -> int32_t {
		m.handle.DirtyAllVariables();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_data_model_is_variable_dirty(mfrmlui_data_model* model, const char* name)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool {
		if (!LiveModel(model) || !name)
		{
			SetLastError("mfrmlui_data_model_is_variable_dirty: invalid model or NULL name");
			return 0;
		}
		return model->handle.IsVariableDirty(name) ? 1 : 0;
	});
}

} // extern "C"
