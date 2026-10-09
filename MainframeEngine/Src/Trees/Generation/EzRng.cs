// A port of Ez Tree's seeded random number generator (src/lib/rng.js; Ez Tree 1.1.0,
// https://github.com/dgreenheck/ez-tree at commit dcf309bd86bd521083d9c70f01f2de45fdc7c457).
//
// MIT License
//
// Copyright (c) 2024 Daniel Greenheck
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace MainframeEngine.Trees;

/// <summary>
/// Ez Tree's multiply-with-carry generator (<c>rng.js</c>), bit-exact. It is not a textbook MWC: in JavaScript
/// <c>&amp; 0xffffffff</c> yields a <b>signed</b> int32 and <c>&gt;&gt; 16</c> is an arithmetic shift, so negative
/// states shift in ones. Both are kept (the state is <see cref="int"/>, the shifts are C#'s signed shifts), and the
/// final <c>&gt;&gt;&gt; 0</c> is a wrap to <see cref="uint"/>.
/// </summary>
/// <remarks>Seeds are integers. Ez Tree's demo forest uses fractional seeds, which the port does not support.</remarks>
internal struct EzRng
{
    private int _w;
    private int _z;

    /// <summary>Seeds the generator like <c>new RNG(seed)</c>.</summary>
    public EzRng(int seed)
    {
        // JS: (123456789 + seed) & mask: the sum is exact in a double, then ToInt32 wraps it modulo 2^32.
        _w = unchecked((int)(123456789L + seed));
        _z = unchecked((int)(987654321L - seed));
    }

    /// <summary>
    /// <c>rng.random(max, min)</c>: a value in [<paramref name="min"/>, <paramref name="max"/>). Note Ez Tree's argument
    /// order (max first).
    /// </summary>
    public double Next(double max = 1, double min = 0)
    {
        unchecked
        {
            // 36969 * 65535 + 32767 does not fit an int: compute in long, then wrap like ToInt32.
            _z = (int)(36969L * (_z & 65535) + (_z >> 16));
            _w = (int)(18000L * (_w & 65535) + (_w >> 16));

            // JS: ((m_z << 16) + (m_w & 65535)) >>> 0. The int shift wraps like JS's; the sum is taken modulo 2^32.
            var result = (uint)((_z << 16) + (_w & 65535));
            var unit = result / 4294967296.0;
            return (max - min) * unit + min;
        }
    }
}
