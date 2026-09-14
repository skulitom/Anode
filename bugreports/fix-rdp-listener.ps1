#requires -RunAsAdministrator
# Historical manual repair for the diagnosed certificate failure, not a general setup step.
# Run in an ELEVATED PowerShell. Creates the certificate the Remote Desktop listener could not create
# itself (Local Session Manager event 17, 0x80070005), lets the service read its key, binds it to the
# RDP-Tcp listener and restarts the service. Nothing else is changed. Each step prints what it did.

$ErrorActionPreference = 'Stop'

Write-Host "1. creating a self-signed certificate for the RDP listener in Cert:\LocalMachine\My"
$cert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -CertStoreLocation 'Cert:\LocalMachine\My' `
    -KeyAlgorithm RSA -KeyLength 2048 -Provider 'Microsoft Software Key Storage Provider' `
    -KeyUsage DigitalSignature, KeyEncipherment -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
    -NotAfter (Get-Date).AddYears(5) -FriendlyName 'Remote Desktop (Anode seat)'
Write-Host "   thumbprint $($cert.Thumbprint)"

Write-Host "2. granting NETWORK SERVICE read access to the private key"
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$keyName = $rsa.Key.UniqueName
$keyFile = Get-ChildItem "$env:ProgramData\Microsoft\Crypto\Keys\$keyName", "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys\$keyName" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $keyFile) { throw "private key file $keyName not found under ProgramData\Microsoft\Crypto" }
icacls $keyFile.FullName /grant '*S-1-5-20:(R)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not grant NETWORK SERVICE read access to $($keyFile.FullName)" }
$rsa.Dispose()
Write-Host "   key file $($keyFile.FullName)"

Write-Host "3. binding the certificate to the RDP-Tcp listener"
$ts = Get-CimInstance -Namespace root/cimv2/TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-Tcp'"
Set-CimInstance -InputObject $ts -Property @{ SSLCertificateSHA1Hash = $cert.Thumbprint }
Write-Host "   bound: $((Get-CimInstance -Namespace root/cimv2/TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-Tcp'").SSLCertificateSHA1Hash)"

Write-Host "4. restarting Remote Desktop Services"
Restart-Service TermService -Force
Start-Sleep -Seconds 5

Write-Host "5. result"
qwinsta | Out-Host
netstat -an | Select-String ':3389' | Select-Object -First 3 | Out-Host
Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational' -MaxEvents 2 |
    Select-Object TimeCreated, Id, @{ n = 'msg'; e = { $_.Message.Substring(0, [Math]::Min(90, $_.Message.Length)) } } | Format-Table -AutoSize | Out-Host
