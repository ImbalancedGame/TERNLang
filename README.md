# Introduction
Tern is a simple language that can be used by support professionals, and extended by them.

# Scopes
Scopes are similar to C# static classes. They group together methods that can be used (i.e. File operations, network operations).

An example scope is the following:

```
scope MyConsoleApp {

	OnStart {
		debugPrefix = "[Debug] > ";
		errorPrefix = "[Error] > ";

		EnableErrors(true);
		EnableDebug(true);
		RemoveMethodFromScope(Name, "OnStart");
	}

	EnableErrors {
		enableErrors = arg0;
	}

	EnableDebug {
		enableDebug = arg0;
	}

	OnLoad {
		// Do something spectacular.

		DebugLog("Debug Test" + enableDebug + "example");
		ErrorLog("Error Test");
	}

	DebugLog {
		msg = arg0;

		@If(enableDebug, {
			Console.SetColor("Blue");
			Console.WriteLine(String.Concat(debugPrefix, msg));
			Console.SetColor("Gray");
		});
	}

	ErrorLog {
		msg = arg0;

		@If(enableErrors, {
			Console.SetColor("Red");
			Console.WriteLine(String.Concat(errorPrefix, msg));
			Console.SetColor("Gray");
		});
	}

}
```

**It's important to note that this code will not run itself and it's required you add the following code somewhere:**
```
MyConsoleApp.OnStart();
MyConsoleApp.OnLoad();
```

# Why does this language exist?
Most languages are unsafe, unlimited and simply uncontrollable. A system admin can allow the runtime for this language to execute without worry about their system being exposed to bad actors. It's expandable in a way that you can almost achieve anything a system admin may need. You choose what functions are required for your application in plain text. Easy.

#
