using Callsign.Host;

// The standalone web host: build the Callsign app and run it. The desktop shell
// (app/Callsign.Desktop) builds the same app in-process via CallsignWebApp.Build.
CallsignWebApp.Build(args).Run();

public partial class Program { }
