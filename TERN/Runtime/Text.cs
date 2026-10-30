using System;

namespace TERN.Runtime
{
    /// <summary>
    /// Character boundaries for strings.
    ///
    /// TERN indexes strings by UTF-16 code unit, like .NET, so `len()` is O(1)
    /// and an index means the same thing in both back ends. A code point above
    /// U+FFFF — an emoji, a rare CJK glyph — occupies two of those units, and
    /// cutting between them produces a lone surrogate, which is not text: it
    /// renders as U+FFFD and does not survive a round trip through UTF-8.
    ///
    /// So every operation that returns part of a string snaps its bounds to a
    /// character boundary first. An index that lands inside a pair resolves to
    /// the start of that character, which is what
    /// <c>str_offset_of_unit</c> in the native runtime does, and what makes
    /// <c>"🙂".slice(0, 1)</c> the empty string in both back ends rather than
    /// half a character.
    /// </summary>
    internal static class Text
    {
        public static bool IsHighSurrogate(char c) => c >= '\uD800' && c <= '\uDBFF';

        public static bool IsLowSurrogate(char c) => c >= '\uDC00' && c <= '\uDFFF';

        /// <summary>
        /// The start of the character that contains <paramref name="index"/>,
        /// clamped to [0, Length].
        /// </summary>
        public static int Snap(string text, int index)
        {
            if (index <= 0)
            {
                return 0;
            }

            if (index >= text.Length)
            {
                return text.Length;
            }

            return IsLowSurrogate(text[index]) && IsHighSurrogate(text[index - 1]) ? index - 1 : index;
        }

        /// <summary>How many code units the character at <paramref name="index"/> occupies.</summary>
        public static int Width(string text, int index)
        {
            if (index < 0 || index >= text.Length)
            {
                return 0;
            }

            return index + 1 < text.Length && IsHighSurrogate(text[index]) && IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
        }

        /// <summary>
        /// The characters of a string, each with its code points intact, in
        /// order. Allocates one string per character, which is what `chars()`
        /// promises.
        /// </summary>
        public static string[] Characters(string text)
        {
            if (!HasSurrogates(text))
            {
                var simple = new string[text.Length];
                for (var i = 0; i < text.Length; i++)
                {
                    simple[i] = text[i].ToString();
                }

                return simple;
            }

            var result = new System.Collections.Generic.List<string>(text.Length);
            for (var i = 0; i < text.Length;)
            {
                var width = Width(text, i);
                result.Add(text.Substring(i, width));
                i += width;
            }

            return result.ToArray();
        }

        /// <summary>True when the string contains any surrogate code unit.</summary>
        public static bool HasSurrogates(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsSurrogate(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A substring whose bounds are snapped to character boundaries, so a
        /// pair is never split.
        /// </summary>
        public static string Substring(string text, int start, int end)
        {
            start = Snap(text, start);
            end = Snap(text, end);
            if (end <= start)
            {
                return string.Empty;
            }

            return text.Substring(start, end - start);
        }
    }
}
