using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Anode.Core.Util;
using Microsoft.Win32;

namespace Anode.Core.Steam;

/// <summary>Whether a game could use the Steam client now.</summary>
internal enum SteamReady
{
    /// <summary>An account is signed in and connected to Steam; a game can start.</summary>
    Online = 0,
    /// <summary>An account is signed in but Steam is not connected, as in offline mode.</summary>
    Offline = 1,
    /// <summary>No client answers, or none has an account signed in yet.</summary>
    Unavailable = 2
}

/// <summary>
/// Asks the Steam client what a game's SteamAPI_Init asks: a pipe, then the signed-in user. Steam's
/// own records say an account is active as soon as one is chosen, and after an unclean exit they keep
/// the last one, while a game started then fails to initialize. The check runs as <c>anode
/// __steam-ready</c> in the game's session, so it takes the game's route to the client, and it sets no
/// app id, so Steam does not count it as a game.
/// </summary>
internal static class SteamReadiness
{
    private const uint LoadWithAlteredSearchPath = 0x8;

    /// <summary>Runs the check in a helper process that reaches the client through <paramref name="name"/>.</summary>
    public static SteamReady Ask(string name, int timeoutMs = 10_000)
    {
        var info = new ProcessStartInfo
        {
            FileName = Env.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
            ArgumentList = { "__steam-ready" }
        };
        info.Environment[SteamBridge.OverrideVariable] = name;
        foreach (string variable in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }) info.Environment.Remove(variable);
        using var helper = Process.Start(info);
        if (helper is null) return SteamReady.Unavailable;
        // Steam's library may write to either stream; drain both so it never blocks.
        _ = helper.StandardOutput.ReadToEndAsync();
        _ = helper.StandardError.ReadToEndAsync();
        if (!helper.WaitForExit(timeoutMs))
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            return SteamReady.Unavailable;
        }
        return helper.ExitCode is 0 or 1 ? (SteamReady)helper.ExitCode : SteamReady.Unavailable;
    }

    /// <summary><c>anode __steam-ready</c>: the exit code is a <see cref="SteamReady"/>.</summary>
    public static unsafe int Probe()
    {
        try
        {
            if (ClientLibrary() is not { } path) return (int)SteamReady.Unavailable;
            IntPtr library = LoadLibraryEx(path, IntPtr.Zero, LoadWithAlteredSearchPath);
            if (library == IntPtr.Zero) return (int)SteamReady.Unavailable;
            var create = (delegate* unmanaged<byte*, int*, IntPtr>)NativeLibrary.GetExport(library, "CreateInterface");
            IntPtr client = IntPtr.Zero;
            foreach (string version in new[] { "SteamClient021", "SteamClient020", "SteamClient017" })
                fixed (byte* text = Ascii(version))
                    if ((client = create(text, null)) != IntPtr.Zero) break;
            if (client == IntPtr.Zero) return (int)SteamReady.Unavailable;

            // ISteamClient: 0 CreateSteamPipe, 1 BReleaseSteamPipe, 2 ConnectToGlobalUser, 4 ReleaseUser, 5 GetISteamUser.
            IntPtr* methods = *(IntPtr**)client;
            int pipe = ((delegate* unmanaged<IntPtr, int>)methods[0])(client);
            if (pipe == 0) return (int)SteamReady.Unavailable;
            try
            {
                int user = ((delegate* unmanaged<IntPtr, int, int>)methods[2])(client, pipe);
                if (user == 0) return (int)SteamReady.Unavailable;
                try
                {
                    var getUser = (delegate* unmanaged<IntPtr, int, int, byte*, IntPtr>)methods[5];
                    IntPtr steamUser = IntPtr.Zero;
                    foreach (string version in new[] { "SteamUser023", "SteamUser021", "SteamUser020" })
                        fixed (byte* text = Ascii(version))
                            if ((steamUser = getUser(client, user, pipe, text)) != IntPtr.Zero) break;
                    if (steamUser == IntPtr.Zero) return (int)SteamReady.Offline;
                    // ISteamUser: 1 BLoggedOn.
                    bool online = ((delegate* unmanaged<IntPtr, byte>)(*(IntPtr**)steamUser)[1])(steamUser) != 0;
                    return (int)(online ? SteamReady.Online : SteamReady.Offline);
                }
                finally { ((delegate* unmanaged<IntPtr, int, int, void>)methods[4])(client, pipe, user); }
            }
            finally { ((delegate* unmanaged<IntPtr, int, byte>)methods[1])(client, pipe); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
        {
            return (int)SteamReady.Unavailable;
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + "\0");

    /// <summary>The 64-bit client library of the running Steam, as Steam records it.</summary>
    private static string? ClientLibrary()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        if (key?.GetValue("SteamClientDll64") is string recorded && File.Exists(recorded)) return recorded;
        if (Steam.FindExecutable() is not { } steam) return null;
        string beside = Path.Combine(Path.GetDirectoryName(steam)!, "steamclient64.dll");
        return File.Exists(beside) ? beside : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryExW")]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
}
