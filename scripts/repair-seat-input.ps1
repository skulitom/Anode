#requires -Version 5.1
#requires -RunAsAdministrator
<# One-time, opt-in repair for a SYSTEM GameInput helper holding the seat foreground.
   Stops only the explicitly identified helper in the verified child session.
   Does not stop/configure the machine-wide GameInput services. They may recreate it.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory=$true)][int]$HelperPid)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System.Runtime.InteropServices;
public static class AnodeRepairChildIdentity {
    [DllImport("wtsapi32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSGetChildSessionId(out uint session);
}
'@
[uint32]$childSession = 0
$parentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
if (-not [AnodeRepairChildIdentity]::WTSGetChildSessionId([ref]$childSession) -or $childSession -eq 0 -or $childSession -eq [uint32]::MaxValue -or $childSession -eq $parentSession) { throw 'Windows did not verify a child session for this parent; nothing changed.' }
$helper = Get-Process -Id $HelperPid -ErrorAction Stop
$details = Get-CimInstance Win32_Process -Filter "ProcessId = $HelperPid"
$service = Get-CimInstance Win32_Service -Filter "Name = 'GameInputSvc'"
if ($helper.SessionId -ne $childSession -or $helper.ProcessName -ne 'GameInputSvc' -or $details.ParentProcessId -ne $service.ProcessId -or $service.State -ne 'Running') {
    throw 'The target is not a GameInputSvc helper in the verified child session. Nothing changed.'
}
Write-Host "GameInput helper PID $HelperPid, child session $childSession, started $($helper.StartTime)."
if ($PSCmdlet.ShouldProcess("GameInput helper PID $HelperPid in child session $childSession", 'Stop this helper; leave parent-session processes and services running')) {
    $helper.Kill()
    if (-not $helper.WaitForExit(5000)) { throw 'The helper did not exit within five seconds.' }
    Write-Host 'Helper stopped. Retest focus and input inside the seat; the service may recreate it.'
}
