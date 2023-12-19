using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TERN.Extensions
{
    internal static class StreamHelpers
    {
        public static string ReadToEnd(Stream stream)
        {
            using StreamReader sr = new StreamReader(stream);

            return sr.ReadToEnd();
        }
    }
}
