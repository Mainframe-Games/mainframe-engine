using System.Globalization;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

// One interpolated-string handler per level: the C# compiler builds the handler before calling Log.X, and its
// constructor checks the level, so a filtered-out $"..." message formats (and allocates) nothing. Messages are
// formatted with the invariant culture so logs read the same on every machine.
public static partial class Log
{
    /// <summary>Builds a <see cref="Level.Debug"/> message only when that level is enabled.</summary>
    [InterpolatedStringHandler]
    public ref struct DebugInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        public DebugInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Enabled = IsEnabled(Level.Debug);
            shouldAppend = Enabled;
            _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
        }

        /// <summary>Whether the level was enabled when the message was built.</summary>
        public bool Enabled { get; }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => Enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    /// <summary>Builds a <see cref="Level.Info"/> message only when that level is enabled.</summary>
    [InterpolatedStringHandler]
    public ref struct InfoInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        public InfoInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Enabled = IsEnabled(Level.Info);
            shouldAppend = Enabled;
            _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
        }

        /// <summary>Whether the level was enabled when the message was built.</summary>
        public bool Enabled { get; }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => Enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    /// <summary>Builds a <see cref="Level.Warning"/> message only when that level is enabled.</summary>
    [InterpolatedStringHandler]
    public ref struct WarningInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        public WarningInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Enabled = IsEnabled(Level.Warning);
            shouldAppend = Enabled;
            _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
        }

        /// <summary>Whether the level was enabled when the message was built.</summary>
        public bool Enabled { get; }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => Enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    /// <summary>Builds a <see cref="Level.Error"/> message only when that level is enabled.</summary>
    [InterpolatedStringHandler]
    public ref struct ErrorInterpolatedStringHandler
    {
        private DefaultInterpolatedStringHandler _inner;

        public ErrorInterpolatedStringHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            Enabled = IsEnabled(Level.Error);
            shouldAppend = Enabled;
            _inner = Enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture) : default;
        }

        /// <summary>Whether the level was enabled when the message was built.</summary>
        public bool Enabled { get; }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format) => _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(scoped ReadOnlySpan<char> value) => _inner.AppendFormatted(value);

        public void AppendFormatted(scoped ReadOnlySpan<char> value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            _inner.AppendFormatted(value, alignment, format);

        internal string ToStringAndClear() => Enabled ? _inner.ToStringAndClear() : string.Empty;
    }
}
