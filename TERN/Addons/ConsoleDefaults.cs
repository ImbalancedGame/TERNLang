using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TERN.Addons
{
    internal static class ConsoleDefaults
    {
        public static void Load()
        {
            Scope s = new ("Console");

            Scope.AddScope(s, out var key, out s);

            s.AddMethod("Write", out _, out var instructions);

            instructions.Add(PrintToConsole);

            s.AddMethod("WriteLine", out _, out instructions);

            instructions.Add(PrintLineToConsole);

            s.AddMethod("ReadLine", out _, out instructions);

            instructions.Add(ReadLine);

            s.AddMethod("SetColor", out _, out instructions);

            instructions.Add(SetColor);
        }

        private static object PrintToConsole(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            Console.Write(input[0]);

            return null;
        }

        private static object PrintLineToConsole(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            Console.WriteLine(input[0]);

            return null;
        }

        private static object ReadLine(Scope scope, in object[] input)
        {
            return Console.ReadLine();
        }

        private static object SetColor(Scope scope, in object[] input)
        {
            if(Enum.TryParse<ConsoleColor>(input[0] as string, out ConsoleColor color))
            {
                Console.ForegroundColor = color;
            }

            return null;
        }
    }
}
