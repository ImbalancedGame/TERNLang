using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TERN.Extensions
{
    internal static class DataTypeExtensions
    {
        public static byte[] ToBytes(this string input, Encoding encoding)
        {
            return encoding.GetBytes(input);
        }
        public static string GetString(this byte[] input, Encoding encoding)
        {
            return encoding.GetString(input);
        }

        public static object Execute(this List<Scope.Method> actions, Scope scope, object[] withVariables = null)
        {
            foreach(Scope.Method action in actions)
            {
                var value = action.Invoke(scope, withVariables);

                if(value is not null)
                {
                    //Console.WriteLine(">>>>>>>"
                    //value.ToString());

                    return value;
                }
            }

            return null;
        }

        public static object Execute(this List<Scope.Method> actions, Scope scope)
        {
            object[] withVariables = null;

            foreach (Scope.Method action in actions)
            {
                var value = action.Invoke(scope, withVariables);

                if (value is not null)
                {
                    return value;
                }
            }

            return withVariables;
        }
    }
}
