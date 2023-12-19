using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TERN.Addons
{
    internal static class FileDefaults
    {
        public static void Load()
        {
            Scope s = new ("File");

            Scope.AddScope(s, out var key, out s);

            s.AddMethod("Create", out _, out var instructions);

            instructions.Add(Create);

            s.AddMethod("CreateIfNotExists", out _, out instructions);

            instructions.Add(CreateIfNotExists);

            s.AddMethod("CreateStreamReader", out _, out instructions);

            instructions.Add(CreateStreamReader);

            s.AddMethod("CreateStreamWriter", out _, out instructions);

            instructions.Add(CreateStreamWriter);

            s.AddMethod("WriteTo", out _, out instructions);

            instructions.Add(WriteTo);

            s.AddMethod("WriteLineTo", out _, out instructions);

            instructions.Add(WriteLineTo);

            s.AddMethod("FlushStream", out _, out instructions);

            instructions.Add(Flush);

            s.AddMethod("Dispose", out _, out instructions);

            instructions.Add(Dispose);
        }

        private static object Create(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            File.Create(input[0] as string);

            return null;
        }

        private static object CreateIfNotExists(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is string fileName)
            {
                if (!File.Exists(fileName))
                {
                    File.Create(fileName);
                }
            }

            return null;
        }

        private static object CreateStreamReader(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is string fileName)
            {
                var fileMode = FileMode.OpenOrCreate;

                if(input[1] is string mode)
                {
                    switch(mode)
                    {
                        case "FileMode.OpenOrCreate":
                            fileMode = FileMode.OpenOrCreate;
                            break;

                        case "FileMode.Open":
                            fileMode = FileMode.Open;
                            break;

                        case "FileMode.Append":
                            fileMode = FileMode.Append;
                            break;

                        case "FileMode.Create":
                            fileMode = FileMode.Create;
                            break;

                        case "FileMode.CreateNew":
                            fileMode = FileMode.CreateNew;
                            break;
                    }
                }

                return new StreamReader(new FileStream(fileName, fileMode, FileAccess.Read, FileShare.Read));
            }

            return null;
        }

        private static object CreateStreamWriter(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is string fileName)
            {
                var fileMode = FileMode.OpenOrCreate;

                if (input.Length > 1 && input[1] is string mode)
                {
                    switch (mode)
                    {
                        case "FileMode.OpenOrCreate":
                            fileMode = FileMode.OpenOrCreate;
                            break;

                        case "FileMode.Open":
                            fileMode = FileMode.Open;
                            break;

                        case "FileMode.Append":
                            fileMode = FileMode.Append;
                            break;

                        case "FileMode.Create":
                            fileMode = FileMode.Create;
                            break;

                        case "FileMode.CreateNew":
                            fileMode = FileMode.CreateNew;
                            break;
                    }
                }

                return new StreamWriter(new FileStream(fileName, fileMode, FileAccess.Write, FileShare.Read));
            }

            return null;
        }

        private static object Dispose(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            foreach (var o in input) 
            {
                if (o is IDisposable disp)
                {
                    disp.Dispose();
                }
            }

            return null;
        }

        private static object WriteTo(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is StreamWriter writer)
            {
                Console.WriteLine($"{input[1]}");

                writer.Write(input[1]);
            }
            else
            {
                Console.WriteLine($"{input[0].GetType()} is not StreamWriter.");
            }

            return null;
        }

        private static object WriteLineTo(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is StreamWriter writer)
            {
                writer.WriteLine(input[1]);
            }

            return null;
        }
        private static object Flush(Scope scope, in object[] input)
        {
            if (input is null)
            {
                return null;
            }

            if (input[0] is StreamWriter writer)
            {
                writer.Flush();
            }

            return null;
        }
    }
}
