using System.Numerics;

namespace TERN.Addons
{
    internal static class MathDefaults
    {
        public static void Load()
        {
            Scope s = new ("Math");

            Scope.AddScope(s, out _, out s);

            s.AddMethod("Add", out _, out var instructions);

            instructions.Add(Add);
        }

        private static object Add(Scope scope, in object[]? input)
        {
            if (input is null || input.Length < 2)
            {
                return input?.Length == 1 ? input[0] : 0;
            }

            return input[0] switch
            {
                int => Add<int>(input),
                float => Add<float>(input),
                double => Add<double>(input),
                long => Add<long>(input),
                _ => 0
            };
        }

        private static T? Add<T>(in object[] input) where T : INumber<T>
        {
            T? value = default;

            return value is null ? value : input.Aggregate(value, (current, o) => current + (T)o);
        }
    }
}
