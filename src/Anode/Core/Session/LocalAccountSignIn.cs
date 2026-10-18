using Anode.Core.Util;
using Microsoft.Win32;

namespace Anode.Core.Session;

/// <summary>
/// The "Guest only" sharing model (Lsa ForceGuest = 1, set by some security suites and hardened images) makes Windows
/// sign every network sign-in of a local account in as the built-in Guest. A seat's sign-in is a network sign-in
/// (Network Level Authentication), so it fails with substatus 0xC0000064 whatever password is typed, while the lock
/// screen accepts the same password and Remote Desktop reports only a generic error. Anode never changes the setting.
/// </summary>
internal static class LocalAccountSignIn
{
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";
    internal const string Name = "Local account sign-in";
    internal const string Help = Links.Troubleshooting + "#windows-refuses-the-right-password-for-a-local-account";

    /// <summary>The ForceGuest value, or null when it is not set or can't be read (Windows' default, Classic, is 0).</summary>
    public static int? ForceGuest()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LsaKey);
            return key?.GetValue("ForceGuest") as int?;
        }
        catch { return null; }
    }

    private static bool GuestOnly(int? forceGuest) => forceGuest is int value && value != 0;

    /// <summary>A warning, not a failure: the setting can be a deliberate hardening choice, and domain accounts are unaffected.</summary>
    public static Check Check(int? forceGuest) => GuestOnly(forceGuest)
        ? new Check(Name, CheckLevel.Warn,
            $"ForceGuest = {forceGuest} (Guest only): Windows signs local accounts in to the seat as Guest, so their sign-in fails",
            "If your security policy allows it, set \"Network access: Sharing and security model for local accounts\" to "
            + "Classic in secpol.msc (ForceGuest = 0). Anode never changes it. See " + Help)
        : new Check(Name, CheckLevel.Pass, forceGuest is null
            ? "Classic (ForceGuest not set): local accounts sign in as themselves"
            : "ForceGuest = 0 (Classic): local accounts sign in as themselves");

    /// <summary>The sentence a failed seat sign-in adds when the Guest-only model is the likely cause, otherwise null.</summary>
    public static string? FailureHint(int? forceGuest) => GuestOnly(forceGuest)
        ? $"Windows here signs local accounts in to the seat as Guest (Lsa ForceGuest = {forceGuest}, the \"Guest only\" sharing model), "
            + "so a local account's sign-in fails even with the right password. See " + Help
        : null;
}
