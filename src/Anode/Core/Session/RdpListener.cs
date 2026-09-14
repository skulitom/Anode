using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;

namespace Anode.Core.Session;

/// <summary>The viewer and readiness checks must use the same configured loopback endpoint.</summary>
internal static class RdpListener
{
    public static int Port
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp");
            return key?.GetValue("PortNumber") is int port and > 0 and <= ushort.MaxValue ? port : 3389;
        }
    }

    public static Check Check() => CheckAsync(Port).GetAwaiter().GetResult();

    // A TCP handshake sends no RDP credentials and creates no session. Probe both
    // loopback families because localhost may resolve to either in the RDP control.
    internal static async Task<Check> CheckAsync(int port)
    {
        var probes = await Task.WhenAll(Reachable(IPAddress.Loopback, port), Reachable(IPAddress.IPv6Loopback, port))
            .ConfigureAwait(false);
        return probes.Any(reachable => reachable)
            ? new Check("Remote Desktop listener", CheckLevel.Pass, $"localhost:{port} accepts TCP connections")
            : new Check("Remote Desktop listener", CheckLevel.Fail, $"Nothing accepts TCP connections on localhost:{port}",
                "Run `anode setup` to start or restart TermService, then `anode doctor`. "
                + "If it still fails, check RDP-Tcp and Microsoft-Windows-TerminalServices-LocalSessionManager/Operational event 17. "
                + "0x80070005 means access denied; certificate/private-key permissions may prevent listener startup.");
    }

    private static async Task<bool> Reachable(IPAddress address, int port)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or NotSupportedException)
        {
            return false;
        }
    }
}
