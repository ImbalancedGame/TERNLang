using System;
using System.Globalization;
using System.Text;
using TERN.Runtime;

namespace TERN.Builtins
{
    /// <summary>
    /// One placeholder's format specification, as in <c>format("{:>8.2f}", 3.14159)</c>.
    ///
    /// The grammar is small on purpose - it is the part of Python's format
    /// language that programs actually use:
    ///
    /// <code>
    /// {[[fill]align][sign][0][width][,][.precision][type]}
    ///
    /// align      &lt; left, &gt; right, ^ centre (default: right for numbers, left for text)
    /// fill       the character to pad with, before an align (default: space)
    /// sign       + always show a sign, - only when negative, space for a leading space
    /// 0          pad with zeros to the width, after any sign
    /// width      minimum width in characters
    /// ,          group thousands: 1,234,567
    /// .precision digits after the point for f/e/%, or a maximum length for text
    /// type       d whole, f fixed, e scientific, x X o b bases, % percent, s text
    /// </code>
    ///
    /// Everything is rendered with the invariant culture: a TERN program's output
    /// must not depend on the machine's locale, and a decimal comma in a
    /// comma-grouped number has no way to mean anything.
    /// </summary>
    internal static class FormatSpec
    {
        /// <summary>Carries a FormatError out of a helper that has no engine to ask.</summary>
        private sealed class FormatTypeException : Exception
        {
            public FormatTypeException(string message)
                : base(message)
            {
            }
        }

        /// <summary>Renders <paramref name="value"/> as <paramref name="spec"/> asks.</summary>
        public static string Render(VM vm, in Value value, string spec)
        {
            var parsed = Parse(vm, spec, value.IsNumber);

            var type = parsed.Type;
            var number = value.IsNumber;

            // A type that only means something for one kind of value is refused
            // rather than ignored: `format("{:d}", "3")` is a mistake, not a 3.
            if (type is 'd' or 'x' or 'X' or 'o' or 'b' && !value.IsNumber)
            {
                throw vm.Error("FormatError",
                    $"format type '{type}' wants a number, not {Text(vm, value)}");
            }

            if (type is 'f' or 'e' or '%' && !number)
            {
                throw vm.Error("FormatError",
                    $"format type '{type}' wants a number, not {Text(vm, value)}");
            }

            if (parsed.Precision is not null && type is 'd' or 'x' or 'X' or 'o' or 'b' && value.IsNumber)
            {
                throw vm.Error("FormatError",
                    $"format type '{type}' does not take a precision; use 'f' for a number with decimals");
            }

            var negative = number &&
                (value.Kind == ValueKind.Int ? value.AsInt() < 0 : value.AsDouble() < 0);

            string digits;
            try
            {
                digits = RenderNumber(value, parsed, type);
            }
            catch (FormatTypeException failure)
            {
                throw vm.Error("FormatError", failure.Message);
            }
            var body = parsed.Thousands && number ? Group(digits) : digits;

            // The sign goes in front of the digits, and zero padding goes after it.
            var prefix = string.Empty;
            if (number)
            {
                if (negative)
                {
                    prefix = "-";
                }
                else if (parsed.Sign == '+')
                {
                    prefix = "+";
                }
                else if (parsed.Sign == ' ')
                {
                    prefix = " ";
                }
            }

            var suffix = type == '%' ? "%" : string.Empty;

            if (!number && type != 's' && type != '\0')
            {
                throw vm.Error("FormatError", $"format type '{type}' wants a number, not {Text(vm, value)}");
            }

            return Pad(vm, prefix, body, suffix, parsed, number);
        }

        // ------------------------------------------------------------------ spec

        private readonly struct Spec
        {
            public readonly char Fill;
            public readonly char Align;      // '\0' when not given
            public readonly char Sign;       // '\0' when not given
            public readonly bool Zero;
            public readonly int Width;       // 0 when not given
            public readonly bool Thousands;
            public readonly int? Precision;
            public readonly char Type;

            public Spec(char fill, char align, char sign, bool zero, int width, bool thousands,
                int? precision, char type)
            {
                Fill = fill;
                Align = align;
                Sign = sign;
                Zero = zero;
                Width = width;
                Thousands = thousands;
                Precision = precision;
                Type = type;
            }
        }

        private static Spec Parse(VM vm, string text, bool number)
        {
            var fill = ' ';
            var align = '\0';
            var sign = '\0';
            var zero = false;
            var width = 0;
            var thousands = false;
            int? precision = null;
            var type = '\0';

            var index = 0;

            // [fill]align: a fill character is only a fill when an align follows it.
            if (text.Length >= 2 && IsAlign(text[1]))
            {
                fill = text[0];
                align = text[1];
                index = 2;
            }
            else if (text.Length >= 1 && IsAlign(text[0]))
            {
                align = text[0];
                index = 1;
            }

            if (index < text.Length && (text[index] == '+' || text[index] == '-' || text[index] == ' '))
            {
                sign = text[index++];
            }

            if (index < text.Length && text[index] == '0')
            {
                zero = true;
                index++;
            }

            var widthStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                width = width * 10 + (text[index] - '0');
                index++;
            }

            if (width > 4096)
            {
                throw vm.Error("FormatError", $"format width {width} is too wide; the limit is 4096");
            }

            _ = widthStart;

            if (index < text.Length && text[index] == ',')
            {
                thousands = true;
                index++;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                var value = 0;
                var any = false;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                {
                    value = value * 10 + (text[index] - '0');
                    any = true;
                    index++;
                }

                if (!any)
                {
                    throw vm.Error("FormatError",
                        $"the precision in '{{{text}}}' needs a number, as in '.2f'");
                }

                if (value > 100)
                {
                    throw vm.Error("FormatError",
                        $"the precision in '{{{text}}}' is {value}; the limit is 100 digits");
                }

                precision = value;
            }

            if (index < text.Length)
            {
                type = text[index++];
                if (!IsType(type))
                {
                    var shown = type == '\0' ? "nothing" : $"'{type}'";
                    throw vm.Error("FormatError",
                        $"unknown format type {shown} in '{{{text}}}'; the types are d, f, e, x, X, o, b, %, s");
                }
            }

            if (index < text.Length)
            {
                throw vm.Error("FormatError",
                    $"'{text}' is not a format specification: the shape is {{[fill]align[sign][0][width][,][.precision][type]}}, such as '>8.2f'");
            }

            // `d` on a float, or `f` on an int, is fine: the value decides the
            // digits and the type decides the shape. What is not fine is a
            // precision where it cannot mean anything, which Render reports.
            _ = number;
            return new Spec(fill, align, sign, zero, width, thousands, precision, type);
        }

        private static bool IsAlign(char c) => c is '<' or '>' or '^';

        private static bool IsType(char c) => c is 'd' or 'f' or 'e' or 'x' or 'X' or 'o' or 'b' or '%' or 's';

        // ---------------------------------------------------------------- values

        private static string RenderNumber(in Value value, in Spec spec, char type)
        {
            var precision = spec.Precision;

            switch (type)
            {
                case 'x':
                case 'X':
                case 'o':
                case 'b':
                    return InBase(Whole(value, type), type);

                case 'd':
                    return Whole(value, 'd').ToString(CultureInfo.InvariantCulture);

                case 'e':
                {
                    var magnitude = Math.Abs(value.Kind == ValueKind.Int ? value.AsInt() : value.AsDouble());
                    return magnitude.ToString("0." + new string('0', precision ?? 6) + "e+00",
                        CultureInfo.InvariantCulture);
                }

                case '%':
                {
                    var number = Math.Abs(value.Kind == ValueKind.Int ? value.AsInt() : value.AsDouble()) * 100.0;
                    return number.ToString("F" + (precision ?? 1).ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture);
                }

                case 'f':
                {
                    var number = Math.Abs(value.Kind == ValueKind.Int ? value.AsInt() : value.AsDouble());
                    return number.ToString("F" + (precision ?? 6).ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture);
                }

                case 's':
                    return value.Kind == ValueKind.Str ? value.AsString() : value.ToDisplayString();

                default:
                {
                    // No type: the text is exactly what the value prints as, so
                    // `format("{:>6}", x)` is `format("{}", x)` with padding and
                    // never a different number. Precision is the one addition -
                    // it rounds a number and truncates text.
                    if (value.IsNumber)
                    {
                        if (precision is { } places)
                        {
                            var magnitude = Math.Abs(value.Kind == ValueKind.Int ? value.AsInt() : value.AsDouble());
                            return magnitude.ToString("F" + places.ToString(CultureInfo.InvariantCulture),
                                CultureInfo.InvariantCulture);
                        }

                        // The sign is the prefix's job, so take the one the value
                        // prints with off again: `{:>6}` of -3.0 is "  -3.0".
                        var printed = value.ToDisplayString();
                        return printed.StartsWith('-') ? printed[1..] : printed;
                    }

                    var rendered = value.ToDisplayString();
                    if (precision is { } length && rendered.Length > length)
                    {
                        return rendered[..length];
                    }

                    return rendered;
                }
            }
        }

        /// <summary>
        /// The value as a whole number for a type that has no fractional digits.
        /// A float that is not whole is refused rather than quietly truncated:
        /// losing 0.6 without a word is how a total comes out wrong.
        /// </summary>
        private static long Whole(in Value value, char type)
        {
            if (value.Kind != ValueKind.Float)
            {
                return Math.Abs(value.AsInt());
            }

            var number = value.AsDouble();
            if (number != Math.Floor(number))
            {
                throw new FormatTypeException(
                    $"format type '{type}' wants a whole number, and {number.ToString("R", CultureInfo.InvariantCulture)} is not one; " +
                    "use '{:.0f}' to round it or int() to cut off the fraction");
            }

            return (long)Math.Abs(number);
        }

        private static string InBase(long value, char type)
        {
            var digits = type switch
            {
                'x' => "0123456789abcdef",
                'X' => "0123456789ABCDEF",
                'o' => "01234567",
                _ => "01",
            };

            var radix = type switch
            {
                'x' or 'X' => 16,
                'o' => 8,
                _ => 2,
            };

            if (value == 0)
            {
                return "0";
            }

            var builder = new StringBuilder();
            var remaining = value;
            while (remaining > 0)
            {
                builder.Insert(0, digits[(int)(remaining % radix)]);
                remaining /= radix;
            }

            return builder.ToString();
        }

        /// <summary>Inserts a comma every three digits, leaving a fractional part alone.</summary>
        private static string Group(string digits)
        {
            var point = digits.IndexOf('.');
            var whole = point < 0 ? digits : digits[..point];
            var tail = point < 0 ? string.Empty : digits[point..];

            var builder = new StringBuilder(whole.Length + whole.Length / 3 + 1);
            for (var i = 0; i < whole.Length; i++)
            {
                if (i > 0 && (whole.Length - i) % 3 == 0)
                {
                    builder.Append(',');
                }

                builder.Append(whole[i]);
            }

            return builder.Append(tail).ToString();
        }

        private static string Pad(VM vm, string prefix, string body, string suffix, in Spec spec, bool number)
        {
            var content = prefix + body + suffix;
            if (content.Length >= spec.Width)
            {
                return content;
            }

            var fill = spec.Zero && spec.Align == '\0' ? '0' : spec.Fill;
            // A number that was not asked to be left- or centred sits on the right,
            // because that is what makes a column of figures line up.
            var align = spec.Align != '\0'
                ? spec.Align
                : spec.Zero ? '=' : (number ? '>' : '<');

            var padding = spec.Width - content.Length;

            switch (align)
            {
                case '<':
                    return content + new string(fill, padding);

                case '^':
                {
                    var left = padding / 2;
                    var right = padding - left;
                    return new string(fill, left) + content + new string(fill, right);
                }

                case '=':
                    // Zero padding sits between the sign and the digits.
                    return prefix + new string(fill, padding) + body + suffix;

                default:
                    return new string(fill, padding) + content;
            }
        }

        /// <summary>The value as the message should name it: quoted when it is text.</summary>
        private static string Text(VM vm, in Value value) => value.ToDebugString();
    }
}
