// The part of GodotSharp's StringExtensions that Color.cs needs (Godot 4.7.2, MIT), copied verbatim.
namespace MainframeEngine
{
    internal static class StringExtensions
    {
        /// <summary>
        /// Returns the hexadecimal representation of this byte as a string.
        /// </summary>
        /// <param name="b">The byte to encode.</param>
        /// <returns>The hexadecimal representation of this byte.</returns>
        internal static string HexEncode(this byte b)
        {
            string ret = string.Empty;

            for (int i = 0; i < 2; i++)
            {
                char c;
                int lv = b & 0xF;

                if (lv < 10)
                {
                    c = (char)('0' + lv);
                }
                else
                {
                    c = (char)('a' + lv - 10);
                }

                b >>= 4;
                ret = c + ret;
            }

            return ret;
        }
    }
}
