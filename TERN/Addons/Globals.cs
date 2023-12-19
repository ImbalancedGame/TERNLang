using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TERN.Extensions;

namespace TERN.Addons
{
    internal static class Globals
    {
        private static Scope _globalScope;

        public static void Load()
        {
            _globalScope = new ("Global");

            Scope.AddScope(_globalScope, out var key, out _globalScope);

            _globalScope.AddMethod("Import", out _, out var instructions);

            instructions.Add(ImportScopes);

            _globalScope.AddMethod("ImportTo", out _, out instructions);

            instructions.Add(ImportScopesTo);

            _globalScope.AddMethod("RemoveMethod", out _, out instructions);

            instructions.Add(RemoveMethod);

            _globalScope.AddMethod("RemoveMethodFromScope", out _, out instructions);

            instructions.Add(RemoveMethodFromScope);

            _globalScope.AddMethod("ScopeInfo", out _, out instructions);

            instructions.Add(ScopeInfo);

            _globalScope.AddMethod("LoadScript", out _, out instructions);

            instructions.Add(LoadScript);

            _globalScope.AddMethod("And", out _, out instructions);

            instructions.Add(And);

            _globalScope.AddMethod("Or", out _, out instructions);

            instructions.Add(Or);

            _globalScope.AddMethod("If", out _, out instructions);

            instructions.Add(If);

            _globalScope.AddMethod("IsNull", out _, out instructions);

            instructions.Add(IsNull);

            _globalScope.AddMethod("Not", out _, out instructions);

            instructions.Add(Not);
        }

        private static object ScopeInfo(Scope scope, in object[] input)
        {
            foreach (var scope2 in Scope.ScopeNames)
            {
                Console.WriteLine($"{scope2.Key}");

                var methods = Scope.GetScope(scope2.Value).GetAllMethods().Searchable;

                foreach (var method in methods)
                {
                    Console.WriteLine($"\t{method.Key}");
                }
            }

            return null;
        }
        
        private static object LoadScript(Scope scope, in object[] input)
        {
            if(input.Length < 2)
            {
                Console.WriteLine("Failed to run LoadScript() due to incorrect parameters, expecting two strings.");
                return null;
            }

            if (input[0] is string s)
            {
                scope = Scope.GetScopeByName(s);
            }

            if (input[1] is string s2)
            {
                //Console.WriteLine($"Loading {s2}");
                new TernScript(new FileStream(s2, FileMode.Open, FileAccess.Read, FileShare.Read), scope);
            }

            return null;
        }

        private static object ImportScopes(Scope scope, in object[] input)
        {
            if (input is null) { return null; }

            for (int i1 = 0; i1 < input.Length; i1++)
            {
                object? i = input[i1];

                if(i is not string scopeName)
                {
                    TernScript.ErrorLog($"Cannot import invalid scope ({i})");
                    Environment.Exit(0);

                    return null;
                }

                var sco2 = Scope.GetScopeByName(scopeName);

                if(sco2 is null) 
                {
                    TernScript.ErrorLog($"Could not find scope \"{scopeName}\"");
                    Environment.Exit(0);
                    continue; 
                }

                var result = sco2.GetAllMethods();

                _globalScope.ImportMethods(result.Keyed, result.Searchable);
            }

            return null;
        }

        private static object ImportScopesTo(Scope scope, in object[] input)
        {
            if (input is null) { return null; }

            var sco = Scope.GetScopeByName(input[0] as string);

            if(sco is null)
            {
                TernScript.ErrorLog($"Scope \"{input[0] as string}\" does not exist.");

                return null;
            }

            for (int i1 = 1; i1 < input.Length; i1++)
            {
                object? i = input[i1];

                var sco2 = Scope.GetScopeByName(i as string);

                if (sco2 is null)
                {
                    TernScript.ErrorLog($"Scope \"{input[0] as string}\" does not exist.");

                    return null;
                }

                var (Keyed, Searchable) = sco2.GetAllMethods();

                sco.ImportMethods(Keyed, Searchable);
            }


            return null;
        }

        private static object RemoveMethod(Scope scope, in object[] input)
        {
            if (input is null) { return null; }

            for (int i1 = 0; i1 < input.Length; i1++)
            {
                if (input[i1] is string method)
                {
                    scope.RemoveMethodByName(method);
                }
            }

            return null;
        }

        private static object And(Scope scope, in object[] input)
        {
            foreach(var o in input)
            {
                if(o is bool b)
                {
                    if (!b)
                        return false;
                }
            }

            return true;
        }

        private static object Or(Scope scope, in object[] input)
        {
            foreach(var o in input)
            {
                if(o is bool b)
                {
                    if (b)
                        return true;
                }
            }

            return false;
        }

        private static object If(Scope scope, in object[] input)
        {
            if(input[0] is bool b)
            {
                if (input.Length >= 2)
                {
                    if (b)
                    {
                        if (input[1] is List<Scope.Method> method)
                        {
                            return method.Execute(scope);
                        }

                        return input[1];
                    }
                    else if (input.Length == 3)
                    {
                        if (input[2] is List<Scope.Method> method)
                        {
                            return method.Execute(scope);
                        }

                        return input[2];
                    }
                }

                return b;
            }

            return false;
        }

        private static object RemoveMethodFromScope(Scope scope, in object[] input)
        {
            if (input is null) { return null; }

            var sco = Scope.GetScopeByName(input[0] as string);

            for (int i1 = 1; i1 < input.Length; i1++)
            {
                if (input[i1] is string method)
                {
                    sco.RemoveMethodByName(method);
                }
            }

            return null;
        }

        private static object IsNull(Scope scope, in object[] input)
        {
            return input is null || input[0] is null;
        }

        private static object Not(Scope scope, in object[] input)
        {
            return input is null || input[0] is not bool b || !b;
        }
    }
}
