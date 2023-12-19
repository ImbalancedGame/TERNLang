using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Xml.Linq;
using TERN.Extensions;

namespace TERN
{
    internal class Scope
    {
        public delegate object Method(Scope scope, in object[] input);

        public static Hashtable Scopes { get; }

        public static Dictionary<string, ushort> ScopeNames;

        private static ushort Index;

        private List<Scope> _usableScopes;

        static Scope()
        {
            Scopes = new Hashtable();
            ScopeNames = new Dictionary<string, ushort>();
        }

        public static void AddScope(Scope scope, out ushort key, out Scope resultScope)
        {
            if(TryFindKeyByScopeName(scope.Name, out var index))
            {
                //TernScript.ErrorLog($"{scope.Name} already exists.");

                key = index.Value;

                resultScope = Scopes[index.Value] as Scope;

                return;
            }

            key = Index++;
            Scopes.Add(key, scope);
            ScopeNames[scope.Name] = key;

            resultScope = scope;
        }

        public static Scope? GetScope(ushort key) => Scopes[key] as Scope;
        public static Scope? GetScopeByName(string key)
        {
            if (!TryFindKeyByScopeName(key, out var index) || key is null)
            {
                return null;
            }

            if(index is null)
            {
                return null;
            }

            return Scopes[index.Value] as Scope;
        }

        public string Name { get; init; }
        public Scope? Parent { get; init; }

        private Hashtable _variables;

        private ushort _index;

        private Dictionary<string, ushort> _variableHashKeys;

        private Dictionary<ushort, (List<Method> Instructions, string[] argNames)> _methods;
        private Dictionary<string, ushort> _methodDictionary;

        private List<Method> scopedActions;

        private ushort _methodIndex;

        public Scope(string name, Scope? parent = null) 
        {
            _variableHashKeys = new Dictionary<string, ushort> ();
            _variables = new Hashtable ();

            Name = name;
            Parent = parent;

            _methods = new Dictionary<ushort, (List<Method> Instructions, string[] argNames)> ();
            _methodDictionary = new Dictionary<string, ushort>();
            scopedActions = new List<Method>();
            _usableScopes = new List<Scope>();
        }

        public void CanUseScope(Scope scope)
        {
            _usableScopes.Add(scope);
        }

        public void AddScopedActions(Method action)
        {
            scopedActions.Add(action);
        }

        public bool HasMethod(string name)
        {
            return _methodDictionary.ContainsKey(name);
        }

        public void AddMethod(string name, out ushort key, out List<Method> instructions, string[]? argNames = null)
        {
            key = ++_methodIndex;

            instructions = new List<Method>();

            //Console.WriteLine($"Adding {Name} + {name}");

            _methods.Add(key, (instructions, argNames)!);
            _methodDictionary.Add(name, key);
        }

        public void RemoveMethodByName(string name)
        {
            if (!TryFindKeyByMethodName(name, out var key) || key is null)
            {
                return;
            }

            _methods.Remove(key.Value);
            _methodDictionary.Remove(name);
        }

        public void AddMethodWithActions(string name, out ushort key, in List<Method> instructions, string[]? argNames = null)
        {
            key = ++_methodIndex;

            _methods.Add(key, (instructions, argNames)!);
            _methodDictionary.Add(name, key);
        }

        private ushort NextIndex {  get { return ++_index; } }

        public (List<Method> Instructions, string[] argNames)? GetMethodByName(string methodName)
        {
            if (!TryFindKeyByMethodName(methodName, out var key) || key is null)
            {
                return null;
            }

            return _methods[key.Value];
        }
        public (List<Method> Instructions, string[] argNames)? GetMethod(ushort key)
        {  
            return _methods[key];
        }

        public object ExecuteScopedInstructionSet()
        {
            return scopedActions.Execute(this);
        }

        public (Dictionary<ushort, (List<Method>, string[])> Keyed, Dictionary<string, ushort> Searchable) GetAllMethods()
        {
            return (_methods, _methodDictionary);
        }

        public void ImportMethods(Dictionary<ushort, (List<Method>, string[] argNames)> Keyed, Dictionary<string, ushort> Searchable)
        {
            foreach(var entry in Searchable)
            {
                if (!_methodDictionary.ContainsKey(entry.Key))
                { 
                    AddMethod(entry.Key, out _, out var newMethods);

                    newMethods.AddRange(Keyed[entry.Value].Item1);

                    //Console.WriteLine($"Importing {Name} + {entry.Key}");
                }
            }
        }

        public object ExecuteInstructionSet(string name, object[] param, Scope? scope = null)
        {
            var useScope = (scope ?? this);

            useScope.TryFindKeyByMethodName(name, out var temp);

            if (temp is null)
            {

                //TernScript.ErrorLog($"Could not find method {name}() in {useScope.Name}");

                while (useScope.Parent is not null)
                {
                    useScope = useScope.Parent;

                    useScope.TryFindKeyByMethodName(name, out temp);

                    if (temp is not null)
                    {
                        return useScope.ExecuteInstructionSet(temp.Value, param, scope);
                    }
                }

                TernScript.ErrorLog($"Could not find method {name}() in {useScope.Name}");
                Environment.Exit(0);

                return null;
            }

            return useScope.ExecuteInstructionSet(temp.Value, param, scope);
        }

        public object ExecuteInstructionSet(ushort name, object[] param, Scope? scope = null)
        {
            var instructions = GetMethod(name);

            Scope temp = scope ?? new Scope("");

            List<object> para = new List<object>();

            if (instructions.Value.argNames is not null)
            {
                for (var index = 0; index < instructions.Value.argNames.Length; index++)
                {
                    var valueArgName = instructions.Value.argNames[index];
                    
                    temp.AddVariable(valueArgName, temp.GetRawVariableByName("arg"+index), out _);
                }
            }

            if (param is not null)
            {
                foreach (object o in param)
                {
                    if (o is not Method m)
                    {
                        para.Add(o);
                        continue;
                    }

                    para.Add(m.Invoke(temp, null));
                }
            }

            return instructions.Value.Instructions.Execute(temp, para.ToArray());
        }

        public byte[]? GetVariableByName(string variableName)
        {
            if (!TryFindKeyByVariableName(variableName, out var key) || key is null)
            {
                foreach(var tempScope in _usableScopes)
                {
                    var result = tempScope.GetVariableByName(variableName);

                    if (result is not null)
                    {
                        return result;
                    }
                }

                return null;
            }

            return _variables[key] as byte[];
        }

        public Dictionary<string, ushort> VariableMap => _variableHashKeys;

        public object GetRawVariableByName(string variableName)
        {
            if (!TryFindKeyByVariableName(variableName, out var key) || key is null)
            {
                foreach (var tempScope in _usableScopes)
                {
                    var result = tempScope.GetRawVariableByName(variableName);

                    if (result is not null)
                    {
                        return result;
                    }
                }

                return null;
            }

            return _variables[key];
        }

        public byte[]? GetVariableByIndex(ushort index)
        {
            return _variables[index] as byte[];
        }
        public object GetRawVariableByIndex(ushort index)
        {
            return _variables[index];
        }

        public void AddVariable(string variableName, byte[] data, out ushort? key)
        {
            if(!TryFindKeyByVariableName(variableName, out key))
            {
                key = NextIndex;
            }

            if(key is null)
            {
                throw new Exception("Variable reference index is null");
            }

            _variables.Add(key.Value, data);
        }

        public List<Method> InstructionSet { get => scopedActions; }


        public void AddVariable(string variableName, object data, out ushort? key)
        {
            if (!TryFindKeyByVariableName(variableName, out key))
            {
                key = NextIndex;
            }
            else
            {
                _variables[key] = data; 

                return;
            }

            if (key is null)
            {
                throw new Exception("Variable reference index is null");
            }

            _variables.Add(key.Value, data);
            _variableHashKeys.Add(variableName, key.Value);
        }

        private bool TryFindKeyByVariableName(in string variableName, out ushort? key)
        {
            if(_variableHashKeys.ContainsKey(variableName))
            {
                key = _variableHashKeys[variableName];
                return true;
            }

            key = null;

            return false;
        }

        private bool TryFindKeyByMethodName(in string methodName, out ushort? key)
        {
            if(_methodDictionary.ContainsKey(methodName))
            {
                key = _methodDictionary[methodName];
                return true;
            }

            key = null;

            return false;
        }

        private static bool TryFindKeyByScopeName(in string scopeName, out ushort? key)
        {
            if(ScopeNames.ContainsKey(scopeName))
            {
                key = ScopeNames[scopeName];
                return true;
            }

            key = null;

            return false;
        }
    }
}
