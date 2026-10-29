// TERN comparison suite: C# (compiled with the same Roslyn toolchain the
// language itself uses, see tools/build-csharp.py).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

static class BenchCsharp
{
    private static void Bench(string name, Func<object> body)
    {
        var watch = Stopwatch.StartNew();
        var result = body();
        watch.Stop();
        Console.WriteLine($"{name} {result} {watch.Elapsed.TotalMilliseconds:F0}");
    }

    private static long Fib(long n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

    private sealed class Node
    {
        public Node? Left;
        public Node? Right;
    }

    private static Node? Make(int depth) => depth == 0 ? null : new Node { Left = Make(depth - 1), Right = Make(depth - 1) };

    private static int Count(Node? node) => node is null ? 0 : 1 + Count(node.Left) + Count(node.Right);

    public static void Main()
    {
        Bench("fib", () => Fib(30));

        Bench("loop", () =>
        {
            long total = 0;
            for (long i = 0; i < 5_000_000; i++)
            {
                total = total + i % 7;
            }

            return total;
        });

        Bench("maps", () =>
        {
            var map = new Dictionary<string, long>();
            for (var i = 0; i < 200_000; i++)
            {
                map["key" + i] = i;
            }

            long sum = 0;
            for (var i = 0; i < 200_000; i++)
            {
                sum += map["key" + i];
            }

            return sum;
        });

        Bench("bintree", () => Count(Make(16)));

        Bench("strings", () =>
        {
            var parts = new List<string>(20000);
            for (var i = 0; i < 20_000; i++)
            {
                parts.Add($"item-{i}");
            }

            var text = string.Join(",", parts);
            var split = text.Split(',');
            return split.Length + text.ToUpperInvariant().Length + text.Length;
        });
    }
}
