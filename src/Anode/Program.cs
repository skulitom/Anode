namespace Anode;

internal static class Program
{
    /// <summary>
    /// Single entry point for every role: the CLI, the daemon that owns the seat, the
    /// agent host that runs inside the seat, the elevated setup step and the MCP server.
    /// One executable keeps the seat host guaranteed to be the same build as the daemon
    /// that launched it.
    /// </summary>
    [STAThread]
    private static int Main(string[] args) => Cli.Cli.Run(args);
}
