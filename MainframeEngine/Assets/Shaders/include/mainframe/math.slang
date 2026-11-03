// Constants and GLSL-style helpers game shaders share (ADR 0237), re-exported by mainframe.spatial and mainframe.canvas.
module mainframe_math;

/// pi.
public static const float PI = 3.1415926535897932384626433833;

/// 2 pi.
public static const float TAU = 6.2831853071795864769252867666;

/// Euler's number.
public static const float E = 2.7182818284590452353602874714;

/// GLSL's mod: x - y * floor(x / y) (Slang's fmod rounds towards zero instead).
public T mod<T : __BuiltinFloatingPointType>(T x, T y)
{
    return x - y * floor(x / y);
}

/// GLSL's mod, per component.
public vector<T, N> mod<T : __BuiltinFloatingPointType, let N : int>(vector<T, N> x, vector<T, N> y)
{
    return x - y * floor(x / y);
}

/// GLSL's mod by a scalar, per component.
public vector<T, N> mod<T : __BuiltinFloatingPointType, let N : int>(vector<T, N> x, T y)
{
    return x - y * floor(x / y);
}
