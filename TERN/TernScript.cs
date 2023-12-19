using System.Diagnostics;
using TERN.Extensions;
using System.Text;
using System.Text.RegularExpressions;
using TERN.Addons;

namespace TERN
{
    public partial class TernScript
    {
        public static void ErrorLog(string error)
        {
            ConsoleColor color = Console.ForegroundColor;

            Console.ForegroundColor = ConsoleColor.Red;

            Debug.WriteLine(error);

            Console.ForegroundColor = color;
        }

        public TernScript(FileStream fileStream, string scopeName = "Global")
        {
            var script = StreamHelpers.ReadToEnd(fileStream);

            Globals.Load();
            ConsoleDefaults.Load();
            MathDefaults.Load();
            StringDefaults.Load();
            FileDefaults.Load();

            Process(scopeName, script);
        }

        internal TernScript(FileStream fileStream, Scope scope)
        {
            var script = StreamHelpers.ReadToEnd(fileStream);

            Process(scope, script);
        }

        public TernScript(string script, string scopeName = "Global")
        {
            Globals.Load();
            ConsoleDefaults.Load();
            MathDefaults.Load();
            StringDefaults.Load();
            FileDefaults.Load();

            Process(scopeName, script);
        }

        private void Process(string scope, string content)
        {
            Scope? main = Scope.GetScopeByName(scope);

            if(main is null)
            {
                main = new Scope(scope); 
                Scope.AddScope(main, out _, out main);
            }

            BuildStructure(main, content);

            main.ExecuteScopedInstructionSet();
        }

        private void Process(Scope scope, string content)
        {
            BuildStructure(scope, content);

            scope.ExecuteScopedInstructionSet();
        }

        public void ParseParams(ref object[] paras)
        {
            for(int i = 0; i< paras.Length; i++)
            {
                var temp = paras[i];
                
                if(temp is null)
                {
                    continue;
                }

                if (temp is string s)
                {
                    if (IsNumber(s))
                    {

                    }
                }
            }
        }

        private enum StringQuery
        {
            None,
            Quotes,
            Speech
        }

        private void BuildStructure(Scope scope, string content)
        {
            ProcessScope(scope, content, 0);
        }

        private (string methodName, List<string> argNames) ProcessMethodName(string method)
        {
            var args = new List<string>();

            if (!method.Contains(' '))
                return (method, null)!;

            var state = MethodProcessState.None;
            var endOfMethodName = method.IndexOf(' ');
            var index = endOfMethodName + 1;

            while (index < method.Length)
            {
                var endOf = method.IndexOf(' ', index);
                
                if ((endOf == -1 && state != MethodProcessState.None) || endOf > -1)
                {
                    var word = method[index..(endOf == -1 ? method.Length : endOf)];
                    index = (endOf == -1 ? method.Length : endOf) + 1;
                    
                    if (word.Equals("wants"))
                    {
                        state = MethodProcessState.Wants;
                        continue;
                    }

                    switch (state)
                    {
                        case MethodProcessState.Wants:
                            args.Add(word);
                            break;
                    }
                }
            }
            
            return (method[..endOfMethodName], args);
            
        }
        
        enum MethodProcessState
        {
            None,
            Wants
        }

        private void ProcessScope(Scope scope, string content, int a)
        {
            var b = a;

            StringBuilder builder = new();

            var state = ProcessorState.None;
            var scopeState = ScopeProcessingState.InScope;

            var stringing = StringQuery.None;

            //Console.WriteLine($"{scope.Name}");

            var currentScope = scope;

            List<Scope.Method> actionList = scope.InstructionSet;

            int line = 0;

            bool isParams = false;

            bool nextCharIsEscaped = false;

            int paramDepth = 0;

            for (; b < content.Length; b++)
            {
                var c = content[b];

                if (c == '\n')
                    line++;

                if(nextCharIsEscaped)
                {
                    nextCharIsEscaped = false;

                    builder.Append(c);

                    continue;
                }

                if(!nextCharIsEscaped && c == '\\')
                { 
                    if (stringing != StringQuery.None)
                    {
                        nextCharIsEscaped = true;
                    }

                    builder.Append(c);

                    continue;
                }

                if(stringing == StringQuery.Quotes && c != '\'' || stringing == StringQuery.Speech && c != '"')
                {
                    builder.Append(c);

                    continue;
                }
                
                if (state == ProcessorState.StartedComment || state == ProcessorState.EnteringComment)
                {
                    if (c == '\n' || (state == ProcessorState.StartedComment && c != '/'))
                    {
                        state = ProcessorState.None;
                        builder.Clear();
                    }

                    continue;
                }

                if(c != '/' && state == ProcessorState.EnteringComment)
                {
                    state = ProcessorState.None;
                    builder.Append(c);
                }

                switch (c)
                {
                    case ';':

                        if (isParams)
                        {
                            builder.Append(c);
                            break;
                        }

                        var statement = builder.ToString().Trim();

                        ProcessStatement(actionList, statement, currentScope);

                        builder.Clear();

                        break;

                    case '(':
                        builder.Append(c);

                        paramDepth++;
                        isParams = true;
                        break;

                    case ')':
                        if (!isParams)
                            continue;
                        builder.Append(c);

                        paramDepth--;

                        if(paramDepth == 0)
                        {
                            isParams = false;
                        }
                        break;

                    case '{':
                        if(isParams)
                        {
                            builder.Append(c);
                            break;
                        }

                        var scopeName = builder.ToString().Trim();

                        if(scopeName.Length > 5)
                        {
                            if(scopeName[..5] == "scope")
                            {
                                scopeName = scopeName[6..].Trim();

                                if (currentScope.Parent is not null)
                                {
                                    StringBuilder temp = new StringBuilder();

                                    temp.Append(scopeName);

                                    var parent = currentScope;

                                    while (parent != null && parent.Parent != null)
                                    {
                                        temp.Insert(0, $"{currentScope.Name}.");

                                        parent = currentScope.Parent;
                                    }

                                    scopeName = temp.ToString();
                                }

                                currentScope = new Scope(scopeName, currentScope);

                                scopeState = ScopeProcessingState.InScope;

                                Scope.AddScope(currentScope, out _, out currentScope);

                                currentScope.AddVariable("Name", scopeName, out _);

                                actionList = currentScope.InstructionSet;

                                builder.Clear();

                                break;
                            }
                        }

                        if (scopeState != ScopeProcessingState.InScope)
                        {
                            ErrorLog($"Nested method is not supported.");
                            Environment.Exit(0);
                            break;
                        }

                        scopeState = ScopeProcessingState.InMethod;

                        var tempMethodName = builder.ToString().Trim();
                        
                        ErrorLog(">>>>>>>>>>" + tempMethodName);

                        var data = ProcessMethodName(tempMethodName);
                        
                        if (data.argNames is not null)
                        {
                            ErrorLog(string.Join(", ", data.argNames));
                        }

                        currentScope.AddMethod(data.methodName, out var _, out actionList, data.argNames?.ToArray());

                        builder.Clear(); 

                        break;

                    case '}':
                        if (isParams)
                        {
                            builder.Append(c);
                            break;
                        }
                        switch (scopeState)
                        {
                            case ScopeProcessingState.InScope:
                                if(currentScope.Parent is null)
                                {
                                    return;
                                    //ErrorLog($"Unexpected end of file on line {line}. Scope {currentScope.Name} does not have a parent.");
                                    //Environment.Exit(0);
                                }

                                currentScope = currentScope.Parent;
                                actionList = currentScope.InstructionSet;
                                break;

                            case ScopeProcessingState.InMethod:
                                scopeState = ScopeProcessingState.InScope;
                                actionList = currentScope.InstructionSet;
                                break;
                        }
                        break;

                    case '/':
                        switch(state)
                        {
                            case ProcessorState.EnteringComment:
                                state = ProcessorState.StartedComment;
                                break;

                            case ProcessorState.None:
                                state = ProcessorState.EnteringComment;
                                break;
                        }
                        break;

                    case '"':
                        if(stringing == StringQuery.None)
                        {
                            stringing = StringQuery.Speech;
                            builder.Append(c);
                        }
                        else if(stringing == StringQuery.Speech)
                        {
                            builder.Append(c);
                            stringing = StringQuery.None;
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;

                    case '\'':
                        if(stringing == StringQuery.None)
                        {
                            stringing = StringQuery.Quotes;
                            builder.Append(c);
                        }
                        else if(stringing == StringQuery.Quotes)
                        {
                            builder.Append(c);
                            stringing = StringQuery.None;
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;

                    default:
                        builder.Append(c);
                        continue;
                }
            }
            //Console.WriteLine("End of file");
        }

        protected string[] SplitCalculationBy(string input, char symbol)
        {
            int depth = 0;
            bool ignoreBracketsAndCommas = false;
            bool escapeNext = false;

            List<string> split = new List<string>();
            StringBuilder builder = new StringBuilder();

            foreach (char c in input)
            {
                if (!ignoreBracketsAndCommas && !escapeNext && c == symbol && depth == 0)
                {
                    split.Add(builder.ToString().Trim());
                    builder.Clear();
                }

                if (!escapeNext && ignoreBracketsAndCommas && c == '\\')
                {
                    builder.Append(c);
                    escapeNext = true;
                    continue;
                }
                else if (c == '(' && !ignoreBracketsAndCommas)
                {
                    builder.Append(c);
                    depth++;
                }
                else if (c == ')' && !ignoreBracketsAndCommas)
                {
                    builder.Append(c);
                    depth--;
                }
                else if (!escapeNext && (c == '"' || c == '\''))
                {
                    ignoreBracketsAndCommas = !ignoreBracketsAndCommas;
                    builder.Append(c);
                }
                else if (c == '\n')
                { 
                }
                else
                {
                    builder.Append(c);
                }

                escapeNext = false;
            }

            if(builder.Length > 0)
            {
                split.Add(builder.ToString());
            } 

            return split.ToArray();
        }

        internal object SplitCalculation(Scope scope, string input)
        {
            var divide = SplitCalculationBy(input, '/');
            
            object value = null;

            foreach(var d in divide)
            {
                if (value is null)
                {
                    value = SplitCalculation2(scope, d);

                    continue;
                }

                object o = SplitCalculation2(scope, d);

                if (value is int intVal)
                {
                    value = intVal + (int)o;
                }
                else if (value is float floatVal)
                {
                    value = floatVal + (float)o;
                }
                else if (value is double doubleVal)
                {
                    value = doubleVal + (double)o;
                }
                else if (value is string stringVal)
                {
                    value = stringVal + (string)o;
                }
                else if (value is long longVal)
                {
                    value = longVal + (long)o;
                }
                else
                {
                    value = o;
                }
            }

            return value;
        }

        internal object SplitCalculation2(Scope scope, string input)
        {
            var multiply = SplitCalculationBy(input, '*');

            object value = null;

            foreach (var d in multiply)
            {
                if (value is null)
                {
                    value = SplitCalculation3(scope, d);

                    continue;
                }

                object o = SplitCalculation3(scope, d);

                if (value is int intVal)
                {
                    value = intVal + (int)o;
                }
                else if (value is float floatVal)
                {
                    value = floatVal + (float)o;
                }
                else if (value is double doubleVal)
                {
                    value = doubleVal + (double)o;
                }
                else if (value is string stringVal)
                {
                    value = stringVal + (string)o;
                }
                else if (value is long longVal)
                {
                    value = longVal + (long)o;
                }
                else
                {
                    value = o;
                }
            }

            return value;
        }

        internal object SplitCalculation3(Scope scope, string input)
        {
            var minus = SplitCalculationBy(input, '-');

            object value = null;

            foreach (var d in minus)
            {
                if (value is null)
                {
                    value = SplitCalculation4(scope, d);

                    continue;
                }

                object o = SplitCalculation4(scope, d);

                if (value is int intVal)
                {
                    value = intVal + (int)o;
                }
                else if (value is float floatVal)
                {
                    value = floatVal + (float)o;
                }
                else if (value is double doubleVal)
                {
                    value = doubleVal + (double)o;
                }
                else if (value is string stringVal)
                {
                    value = stringVal + (string)o;
                }
                else if (value is long longVal)
                {
                    value = longVal + (long)o;
                }
                else
                {
                    value = o;
                }
            }

            return value;
        }

        internal object SplitCalculation4(Scope scope, string input)
        {
            var plus = SplitCalculationBy(input, '+');

            object value = null;

            foreach (var d in plus)
            {
                if (value is null)
                {
                    value = GetValueByString(scope, d);

                    continue;
                } 

                object o = GetValueByString(scope, d);

                if (value is int intVal)
                {
                    value = intVal + (int) o;
                }
                else if (value is float floatVal)
                {
                    value = floatVal + (float)o;
                }
                else if (value is double doubleVal)
                {
                    value = doubleVal + (double)o;
                }
                else if (value is string stringVal)
                {
                    value = stringVal + (string)o;
                }
                else if (value is long longVal)
                {
                    value = longVal + (long)o;
                }
                else
                {
                    value = o;
                }
            }

            return value;
        }

        internal object GetValueByString(Scope scope, string input)
        {
            if (input.Length > 1 && (input[0] == '"' && input[^1] == '"' || input[0] == '\'' && input[^1] == '\''))
            {
                return input[1..^1];
            }

            if (input == "false" || input == "true" || input == "True" || input == "False")
            {
                return bool.Parse(input.ToLower());
            }
            
            if (IsNumber(input))
            {
                if (input.Length > int.MaxValue.ToString().Length)
                {
                    return long.Parse(input);
                }
                if (input.Contains('.'))
                {
                    return float.Parse(input);
                }

                return int.Parse(input);
            }

            var result = scope.GetRawVariableByName(input);

            if (result is not null)
            {
                return result;
            }

            var checkScope = scope.Parent;

            bool found = false;

            while (result is null && checkScope != null)
            {
                result = checkScope.GetRawVariableByName(input);

                if (result is not null)
                {
                    return result;
                }

                if (checkScope.Parent is null)
                {
                    break;
                }

                checkScope = checkScope.Parent;
            }

            return null;
        }

        protected bool NeedsCalculation(string input)
        {
            int depth = 0;
            bool ignoreBracketsAndCommas = false;
            bool escapeNext = false;

            foreach (char c in input)
            {
                if (!ignoreBracketsAndCommas && !escapeNext && depth == 0) 
                {
                    switch(c)
                    {
                        case '+':
                        case '-':
                        case '*':
                        case '/':
                            return true;
                    }
                }

                if (!escapeNext && ignoreBracketsAndCommas && c == '\\')
                {
                    escapeNext = true;
                    continue;
                }
                else if (c == ',' && depth == 0 && !ignoreBracketsAndCommas)
                {

                }
                else if (c == '(' && !ignoreBracketsAndCommas)
                {
                    depth++;
                }
                else if (c == ')' && !ignoreBracketsAndCommas)
                {
                    depth--;
                }
                else if (!escapeNext && (c == '"' || c == '\''))
                {
                    ignoreBracketsAndCommas = !ignoreBracketsAndCommas;
                }
                else if (c == '\n')
                { }
                else
                {
                }

                escapeNext = false;
            }

            return false;
        }
        
        // TODO add "IsConcat" method
        // TODO add "BreakdownConcat" method

        private void ProcessStatement(List<Scope.Method> methods, string statement, Scope currentScope)
        {
            // currentScope is where the function is being called FROM, not where the method should be scoped to. We should disregard currentScope from now on.

            Regex methodCall = MethodCall();
            Regex valueCall = ValueCall();

            statement = statement.Trim(); // Remove all pointless stuff

            //Console.WriteLine($"Processing Statement: \"{statement}\"");

            if (methodCall.IsMatch(statement))
            {
                //Console.WriteLine($"Is Method Call");
                Match methodMatch = methodCall.Match(statement);

                var scope = methodMatch.Groups.ContainsKey("scope") && !string.IsNullOrEmpty(methodMatch.Groups["scope"].Value) ? methodMatch.Groups["scope"].Value : (methodMatch.Groups.ContainsKey("scopeAlt") && !string.IsNullOrEmpty(methodMatch.Groups["scopeAlt"].Value) ? methodMatch.Groups["scopeAlt"].Value : null);
                var methodName = methodMatch.Groups["methodName"].Value;
                var methodParams = methodMatch.Groups["params"].Value;
                var suppressReturns = methodMatch.Groups["suppression"].Value == "@";

                /***
                 * tempScope should be used for scoping the method, as it's where the method is being called from.
                 ***/
                Scope tempScope = scope is null or "" ? currentScope : Scope.GetScopeByName(scope);

                //Console.WriteLine($"Confirmed scope: {tempScope.Name}");

                /*if(!tempScope.HasMethod(methodName))
                {
                    { 
                        var x = tempScope.GetAllMethods();

                        foreach (var b in x.Searchable)
                        {
                            Console.WriteLine($"{b.Key} = {b.Value}");
                        }
                    }

                    ErrorLog($"{tempScope.Name} does not contain {methodName}");
                    while (tempScope.Parent is not null)
                    {
                        tempScope = tempScope.Parent;

                        if (!tempScope.HasMethod(methodName))
                        {
                            continue;
                        }

                        break;
                    }
                }*/

                if (tempScope is null)
                {
                    ErrorLog($"Invalid call [{scope}]::{methodName}({methodParams})");

                    Environment.Exit(0);
                }

                methods.Add((Scope scope, in object[] input) =>
                {
                    var method = tempScope.GetMethodByName(methodName);
                    
                    object[] o = SplitParams(methodParams);

                    //Console.WriteLine($"\n\n\n\n------------\n\nExecuting {methodName} in {tempScope.Name} with \"{methodParams}\"");

                    for (int i = 0; i < o.Length; i++)
                    {
                        if (o[i] is string valueName)
                        {
                            valueName = EscapeChars().Replace(valueName, "").Trim();

                            string? dName = null;

                            if (IsNamedAttribute().IsMatch(valueName))
                            {
                                Match m = IsNamedAttribute().Match(valueName);

                                dName = m.Groups["variableName"].Value.Trim();
                                valueName = m.Groups["variableValue"].Value.Trim();

                                //Console.WriteLine($"Is Named Attribute {dName} = {valueName}.");
                            }

                            if(NeedsCalculation(valueName))
                            {
                                o[i] = SplitCalculation(tempScope, valueName);

                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                }

                                continue;
                            }

                            if (valueName.Length > 1 && (valueName[0] == '"' && valueName[^1] == '"' || valueName[0] == '\'' && valueName[^1] == '\''))
                            {
                                o[i] = valueName[1..^1];

                                //Console.WriteLine($"Curated string {o[i]}. Added to {tempScope.Name}");
                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                }
                                //Console.WriteLine($"Verify: " + (tempScope.GetRawVariableByName(dName is not null ? dName : "arg" + i)));

                                continue;
                            }

                            if (valueName.Length > 1 && valueName[0] == '{' && valueName[^1] == '}')
                            {
                                string contents = valueName[1..^1].Trim();

                                Scope voidScope = new Scope("temp");

                                voidScope.CanUseScope(tempScope);

                                ProcessScope(voidScope, contents, 0);

                                o[i] = voidScope.InstructionSet;

                                //Console.WriteLine($"Curated string {o[i]}. Added to {tempScope.Name}");
                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                }
                                //Console.WriteLine($"Verify: " + (tempScope.GetRawVariableByName(dName is not null ? dName : "arg" + i)));

                                continue;
                            }

                            if (methodCall.IsMatch(valueName))
                            {
                                List<Scope.Method> actions = new();

                                ProcessStatement(actions, valueName, currentScope);

                                o[i] = actions.Execute(currentScope);

                                //Console.WriteLine($"Is Method Call with result \"{o[i]}\".");

                                if (o[i] is string re && IsNumber(re))
                                {
                                    if (re.Length > int.MaxValue.ToString().Length)
                                    {
                                        o[i] = long.Parse(re);
                                        
                                        if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                        {
                                            tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                        }
                                        else
                                        {
                                            tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                        }

                                        continue;
                                    }
                                    if (re.Contains('.'))
                                    {
                                        o[i] = float.Parse(re);
                                        
                                        if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                        {
                                            tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                        }
                                        else
                                        {
                                            tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                        }

                                        continue;
                                    }

                                    o[i] = int.Parse(re);
                                    
                                    if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                    {
                                        tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                    }
                                    else
                                    {
                                        tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                    }

                                    continue;
                                }

                                // is string
                                if (o[i] is string y)
                                {
                                    if (y[0] == '"' && y[^1] == '"' || y[0] == '\'' && y[^1] == '\'')
                                    {
                                        o[i] = y[1..^1];
                                    }
                                }
                                
                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                }

                                continue;
                            }

                            //Console.WriteLine($"{methodName} ---- {currentScope.Name} ---- {scope.Name}");

                            var result = currentScope.GetRawVariableByName(valueName);

                            if (result is not null)
                            {
                                o[i] = result;

                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable(dName is not null ? dName : "arg" + i, result, out _);
                                }

                                tempScope.AddVariable(valueName, result, out _);

                                //Console.WriteLine($"Value has been found in {tempScope.Name}.");

                                continue;
                            }

                            var checkScope = currentScope.Parent;

                            bool found = false;

                            while (result is null && checkScope != null)
                            {
                                result = checkScope.GetRawVariableByName(valueName);

                                if (result is not null)
                                {
                                    o[i] = result;
                                    if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                    {
                                        tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                    }
                                    else
                                    {
                                        tempScope.AddVariable(dName is not null ? dName : "arg" + i, result, out _);
                                    }

                                    tempScope.AddVariable(valueName, result, out _);

                                    //Console.WriteLine($"Value has been found in {checkScope.Name}.");

                                    found = true;
                                    break;
                                }

                                if (checkScope.Parent is null)
                                {
                                    break;
                                }

                                checkScope = checkScope.Parent;
                            }

                            if (found == false)
                            {
                                result = o[i];

                                if (result is string re)
                                {
                                    if (re == "false" || re == "true" || re == "True" || re == "False")
                                    {
                                        o[i] = bool.Parse(re.ToLower());

                                        if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                        {
                                            tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                        }
                                        else
                                        {
                                            tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                        }

                                        continue;
                                    }
                                    else if (IsNumber(re))
                                    {
                                        if (re.Length > int.MaxValue.ToString().Length)
                                        {
                                            o[i] = long.Parse(re);
                                            
                                            if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                            {
                                                tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                            }
                                            else
                                            {
                                                tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i],
                                                    out _);
                                            }

                                            continue;
                                        }
                                        if (re.Contains('.'))
                                        {
                                            o[i] = float.Parse(re);
                                            
                                            if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                            {
                                                tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                            }
                                            else
                                            {
                                                tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i],
                                                    out _);
                                            }

                                            continue;
                                        }

                                        o[i] = int.Parse(re);
                                        
                                        if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                        {
                                            tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                        }
                                        else
                                        {
                                            tempScope.AddVariable(dName is not null ? dName : "arg" + i, o[i], out _);
                                        }

                                        continue;
                                    }
                                }

                                o[i] = null;
                                //tempScope.AddVariable(dName is not null ? dName : "arg" + i, null, out _);
                                //tempScope.AddVariable(dName is not null ? dName : "arg" + i, result, out _);
                                //Console.WriteLine($"Value has been added to {tempScope.Name}.");
                            }
                        }
                        else
                        {
                            if (o[i] is string re && IsNumber(re))
                            {
                                if (re.Length > int.MaxValue.ToString().Length)
                                {
                                    o[i] = long.Parse(re);
                                    
                                    if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                    {
                                        tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                    }
                                    else
                                    {
                                        tempScope.AddVariable("arg" + i, o[i], out _);
                                    }

                                    continue;
                                }
                                if (re.Contains('.'))
                                {
                                    o[i] = float.Parse(re);

                                    if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                    {
                                        tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                    }
                                    else
                                    {
                                        tempScope.AddVariable("arg" + i, o[i], out _);
                                    }

                                    continue;
                                }

                                o[i] = int.Parse(re);

                                if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                                {
                                    tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                                }
                                else
                                {
                                    tempScope.AddVariable("arg" + i, o[i], out _);
                                }

                                continue;
                            }

                            o[i] = null;
                            
                            if (method.HasValue && method.Value.argNames is not null && method.Value.argNames.Length > i)
                            {
                                tempScope.AddVariable(method.Value.argNames[i], o[i], out _);
                            }
                            else
                            {
                                tempScope.AddVariable("arg" + i, o[i], out _);
                            }
                        }
                    }

                    //Console.Write($"Sending to {tempScope.Name}: ");

                    //foreach (object v in o)
                    //{
                    //    Console.Write($"\"{v.ToString()}\", ");
                    //}

                    //Console.WriteLine();

                    //Console.WriteLine($"Executing {methodName} from {tempScope.Name}");
                    
                    Debug.WriteLine("@" + methodName);

                    if(suppressReturns)
                    {
                        tempScope.ExecuteInstructionSet(methodName, o);
                        return null;
                    }

                    return tempScope.ExecuteInstructionSet(methodName, o);
                });
            }
            else if (valueCall.IsMatch(statement))
            {
                Match valueMatch = valueCall.Match(statement);

                var name = valueMatch.Groups["variableName"].Value;
                var value = valueMatch.Groups["variableValue"].Value;

                methods.Add((Scope scope, in object[] input) =>
                {
                    if (value == "true" || value == "false")
                    {
                        currentScope.AddVariable(name, bool.Parse(value), out _);

                        return null;
                    }
                    if (IsNumber(value))
                    {
                        if (value.Length > int.MaxValue.ToString().Length)
                        {
                            currentScope.AddVariable(name, long.Parse(value), out _);

                            return null;
                        }
                        if (value.Contains('.'))
                        {
                            currentScope.AddVariable(name, float.Parse(value), out _);

                            return null;
                        }

                        currentScope.AddVariable(name, int.Parse(value), out _);

                        return null;
                    }

                    // If the value is a string, we can check it for being a method.

                    if (methodCall.IsMatch(value))
                    {
                        List<Scope.Method> actions = new List<Scope.Method>();
                        ProcessStatement(actions, value, currentScope);

                        currentScope.AddVariable(name, actions.Execute(currentScope), out _);

                        return null;
                    }

                    if (value.Length > 1 && value[0] == '"' && value[^1] == '"')
                    {
                        currentScope.AddVariable(name, value[1..^1], out _);

                        return null;
                    }

                    var result = currentScope.GetRawVariableByName(value);

                    if (result is not null)
                    {
                        currentScope.AddVariable(name, result, out _);
                    }
                    else
                    {
                        currentScope.AddVariable(name, null, out _);
                    }

                    return null;
                });
            }
            else if (ReturnCall().IsMatch(statement))
            {
                Match returnMatch = ReturnCall().Match(statement);

                var value = returnMatch.Groups["value"].Value;

                methods.Add((Scope scope, in object[] input) =>
                {
                    if (methodCall.IsMatch(value))
                    {
                        List<Scope.Method> actions = new();
                        ProcessStatement(actions, value, currentScope);

                        return actions.Execute(currentScope);
                    }

                    if (value == "true" || value == "false")
                    {
                        return bool.Parse(value);
                    }

                    if (IsNumber(value))
                    {
                        if (value.Length > int.MaxValue.ToString().Length)
                        {
                            return long.Parse(value);
                        }
                        if (value.Contains('.'))
                        {
                            return float.Parse(value);
                        }

                        return int.Parse(value);
                    }

                    var result = currentScope.GetRawVariableByName(value);

                    if (result is not null)
                    {
                        return result;
                    }

                    var checkScope = currentScope.Parent;

                    while (result is null && checkScope != null)
                    {
                        result = checkScope.GetRawVariableByName(value);

                        if (result is not null)
                        {
                            return result;
                        }

                        if (checkScope.Parent is null)
                        {
                            break;
                        }

                        checkScope = checkScope.Parent;
                    }

                    return value;
                });
            }
            else if (ImportCall().IsMatch(statement))
            {
                Match importMatch = ImportCall().Match(statement);

                var instruction = importMatch.Groups["instruction"].Value;
                var value = importMatch.Groups["file"].Value;

                switch (instruction)
                {
                    case "import":
                    case "include":
                        if (File.Exists($"{value}.tern"))
                        {
                            new TernScript(new FileStream($"{value}.tern", FileMode.Open, FileAccess.Read, FileShare.Read), currentScope);
                        }
                        else
                        {
                            ErrorLog($"Could not find file \"{value}.tern\"");
                            Environment.Exit(0);
                        }
                        break;
                }
            }
            else
            {
                ErrorLog("Unhandled: " + statement);
            }
        }

        internal object GetVariableFromScope(Scope currentScope, string valueName)
        {
            var result = currentScope.GetRawVariableByName(valueName);

            if (result is not null)
            {
                return result;
            }

            var checkScope = currentScope.Parent;

            bool found = false;

            while (result is null && checkScope != null)
            {
                result = checkScope.GetRawVariableByName(valueName);

                if (result is not null)
                {
                    return result;
                }

                if (checkScope.Parent is null)
                {
                    break;
                }

                checkScope = checkScope.Parent;
            }

            return null;
        }

        public bool IsNumber(string value)
        {
            return IsNumber().IsMatch(value);
        }

        private object[] SplitParams(string paramsToSplit)
        {
            Debug.WriteLine("\n-----------\n\"" + paramsToSplit.Trim() + "\"");
            List<object> strings = new List<object>();

            StringBuilder builder = new StringBuilder();

            int depth = 0;
            bool ignoreBracketsAndCommas = false;
            bool escapeNext = false;

            foreach (char c in paramsToSplit)
            {
                if(!escapeNext && ignoreBracketsAndCommas && c == '\\')
                {
                    builder.Append(c);
                    escapeNext = true;
                    continue;
                }
                else if(c == ',' && depth == 0 && !ignoreBracketsAndCommas)
                {
                    //Console.WriteLine("+ " + builder.ToString());
                    strings.Add(builder.ToString().Trim());
                    builder.Clear();
                }
                else if(c == '(' && !ignoreBracketsAndCommas)
                {
                    builder.Append(c);
                    depth++;
                }
                else if(c == ')' && !ignoreBracketsAndCommas)
                {
                    depth--;

                    if(depth < 0)
                    {
                        ErrorLog($"Invalid character found in {paramsToSplit}");
                        Environment.Exit(0);
                    }
                    builder.Append(c);
                }
                else if(!escapeNext && (c == '"' || c == '\''))
                {
                    ignoreBracketsAndCommas = !ignoreBracketsAndCommas;
                    builder.Append(c);
                }
                else if(c == '\n')
                { }
                else
                {
                    builder.Append(c);
                }

                escapeNext = false;
            }

            if(builder.Length > 0)
            {
                strings.Add(builder.ToString().Trim());
            }

            return strings.ToArray();
        }

        enum StatementState 
        { 
            None,
            Namespace,
            EnteringMethod,
            MethodName,
            VariableName,
            Parameter,
            Value
        }

        enum ScopeProcessingState 
        { 
            InScope,
            InMethod
        }

        enum ProcessorState
        {
            None,
            EnteringComment,
            StartedComment
        }

        [GeneratedRegex("^(?'suppression'[@]{0,1}|)(?:\\[(?'scope'[a-zA-Z.]*?)\\]::|(?'scopeAlt'[a-zA-Z\\\\.]*)\\.|)(?'methodName'[a-zA-Z]*)\\((?'params'[\\w\\W]*)\\)$")]
        private static partial Regex MethodCall();

        [GeneratedRegex("^(?'variableName'[a-zA-Z]*)(?:[\\s\\t]*)=(?:[\\s\\t]*)(?'variableValue'(?:@|)[\\w\\W\\s]{1,})$")]
        private static partial Regex ValueCall();

        [GeneratedRegex("return (?'value'.*)")]
        private static partial Regex ReturnCall();

        [GeneratedRegex("^#(?'instruction'[a-zA-Z]*) \"(?'file'[\\w]*)\"$")]
        private static partial Regex ImportCall();

        [GeneratedRegex("^(?:-|)[\\d.]{1,}$")]
        private static partial Regex IsNumber();

        [GeneratedRegex("^(?'variableName'[a-zA-Z]*):(?'variableValue'.*)$")]
        private static partial Regex IsNamedAttribute();

        [GeneratedRegex("(?<!\\\\)\\\\")]
        private static partial Regex EscapeChars();

        [GeneratedRegex("^(?'first'((?<!\\\\)'|)([\\w\\W\\s]{0,}?)(\\1))(?:(?:[\\s]*\\+[\\s]*)|^)")]
        private static partial Regex AddVariable(); 
    }
}