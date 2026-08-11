# vendor/simconnect

The SimConnect binaries live here at build time but are **never committed**
(see `.gitignore`). They come from the official MSFS 2024 SDK and are Microsoft's
to distribute, so we keep them out of source control and fetch them locally.

Expected contents after setup:

- `Microsoft.FlightSimulator.SimConnect.dll` — the managed interop the adapter references
- `SimConnect.dll` — the native library it P/Invokes (copied next to the built exe)

To populate this folder:

1. Install the MSFS 2024 SDK from inside the sim
   (Options → General → Developers → Developer Mode → Help → SDK Installer).
2. Run `scripts/fetch-simconnect.ps1`.
3. Rebuild: `dotnet build Callsign.slnx`.

Until then, `Callsign.SimConnect.Windows` builds as a throwing stub and the PoC
runs in synthetic (`--fake`) mode.
