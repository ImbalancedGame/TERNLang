#Introduction
Tern is a simple language that can be used by support professionals, and extended by them.

#Scopes
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

#
