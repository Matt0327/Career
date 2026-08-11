namespace Callsign.SimConnect;

/// <summary>Lifecycle of the connection to the flight simulator.</summary>
public enum SimConnectionState
{
    /// <summary>Not connected and not trying.</summary>
    Disconnected,

    /// <summary>Attempting to open a connection (sim may not be running yet).</summary>
    Connecting,

    /// <summary>Connected and receiving data.</summary>
    Connected,

    /// <summary>The sim signalled it is quitting; a reconnect loop is now waiting for it to return.</summary>
    SimExited,
}
