var script = new TERN.TernScript(
    new FileStream(
        Environment.GetCommandLineArgs()[1], 
        FileMode.Open, 
        FileAccess.Read, 
        FileShare.Read
        )
    );