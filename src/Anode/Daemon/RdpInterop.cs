using System.Reflection;
using System.Runtime.InteropServices;

namespace Anode.Daemon;

/// <summary>
/// The two COM interfaces of the Remote Desktop ActiveX control that Anode needs, and
/// nothing else. Both are documented: <c>IMsRdpExtendedSettings</c> carries the
/// "ConnectToChildSession" property that turns an ordinary loopback connection into a
/// child session, and <c>IMsTscAxEvents</c> is the control's event dispinterface.
///
/// They are declared by hand rather than through an interop assembly so the build has
/// no dependency on tlbimp or on a Visual Studio install.
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsRdpExtendedSettings
{
    [DispId(1)]
    object this[[MarshalAs(UnmanagedType.BStr)] string propertyName] { get; set; }
}

/// <summary>Event dispinterface of the Remote Desktop ActiveX control.</summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(7)] void OnChannelReceivedData(string channelName, string data);
    [DispId(8)] void OnRequestGoFullScreen();
    [DispId(9)] void OnRequestLeaveFullScreen();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(13)] void OnIdleTimeoutNotification();
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(15)] void OnConfirmClose(ref bool allowClose);
    [DispId(16)] void OnReceivedTSPublicKey(string publicKey, ref bool allowConnect);
    [DispId(17)] void OnAutoReconnecting(int disconnectReason, int attemptCount, ref int continueStatus);
    [DispId(18)] void OnAuthenticationWarningDisplayed();
    [DispId(19)] void OnAuthenticationWarningDismissed();
    [DispId(20)] void OnRemoteProgramResult(string remoteProgramName, int result, bool displayErrorDialog);
    [DispId(21)] void OnRemoteProgramDisplayed(bool displayed, uint exeStyle);
    [DispId(22)] void OnLogonError(int errorCode);
    [DispId(23)] void OnFocusReleased(int direction);
    [DispId(24)] void OnUserNameAcquired(string userName);
    [DispId(26)] void OnMouseInputModeChanged(bool absoluteMouseMode);
    [DispId(28)] void OnServiceMessageReceived(string serviceMessage);
    [DispId(29)] void OnRemoteWindowDisplayed(bool displayed, IntPtr hwnd, int windowState);
    [DispId(30)] void OnConnectionBarPullDown();
    [DispId(32)] void OnNetworkStatusChanged(uint quality, int bandwidth, int rtt);
    [DispId(33)] void OnAutoReconnected();
    [DispId(34)] void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttempts);
    [DispId(35)] void OnDevicesButtonPressed();
}

/// <summary>Late-bound helpers for the parts of the control that have no hand-written interface.</summary>
internal static class Dispatch
{
    public static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    public static void Set(object target, string name, object? value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

    public static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);

    /// <summary>
    /// Sets a property, swallowing the failure. Used for the settings that are nice to
    /// have but differ between Windows builds; a missing one must not stop the seat.
    /// </summary>
    public static bool TrySet(object target, string name, object? value)
    {
        try { Set(target, name, value); return true; }
        catch { return false; }
    }
}

/// <summary>
/// Turns the numeric disconnect codes the control reports into something a person can
/// act on. Only the codes that actually show up for a loopback child session are
/// spelled out; the rest fall back to the control's own description.
/// </summary>
internal static class RdpDisconnect
{
    public static string Explain(int reason) => reason switch
    {
        0 => "No reason given.",
        1 => "Anode closed the connection.",
        2 => "The seat closed the connection.",
        3 => "The seat was signed out.",
        260 => "Could not resolve localhost.",
        264 => "The connection timed out. Remote Desktop Services may be starting.",
        516 or 520 => "Could not reach the Remote Desktop host on localhost. Run `anode doctor`; Remote Desktop is probably still off.",
        1030 or 1032 => "The Remote Desktop client hit an internal error.",
        1800 => "The Remote Desktop host refused the loopback connection. This is what you get when Remote Desktop is disabled or child sessions were never enabled. Run `anode setup`.",
        2055 => "The seat could not sign in. If you sign in to Windows with a PIN, sign in with your password once and try again.",
        2056 => "Another child session already exists. Only one may be connected at a time.",
        3591 => "Your account is not allowed to sign in locally, which a child session requires.",
        _ => $"Remote Desktop reported disconnect reason {reason}."
    };
}
