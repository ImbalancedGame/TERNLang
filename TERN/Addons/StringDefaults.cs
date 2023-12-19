using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace TERN.Addons
{
    internal static class StringDefaults
    {

        public static void Load()
        {
            Scope s = new ("String");

            Scope.AddScope(s, out var key, out s);
             
            s.AddMethod("Concat", out _, out var instructions);

            instructions.Add(Concat);
             
            s.AddMethod("Join", out _, out instructions);

            instructions.Add(Join);
             
            s.AddMethod("Trim", out _, out instructions);

            instructions.Add(Trim);
             
            s.AddMethod("StartsWith", out _, out instructions);

            instructions.Add(StartsWith);
        }

        private static object Concat(Scope scope, in object[] input)
        {
            StringBuilder builder = new StringBuilder();

            foreach (object o in input)
            {
                builder.Append(o?.ToString());
            }

            return builder.ToString();
        }

        private static object Join(Scope scope, in object[] input)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 1; i < input.Length; i++)
            {
                object o = input[i];
                builder.Append(o.ToString());

                if (i < input.Length - 1)
                {
                    builder.Append(input[0].ToString());
                }
            }

            return builder.ToString();
        }

        private static object Trim(Scope scope, in object[] input)
        {
            return (input[0] as string).Trim();
        }

        private static object StartsWith(Scope scope, in object[] input)
        {
            return (input[1] as string).StartsWith(input[0] as string);
        }
    }
}
