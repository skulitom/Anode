# Save one rectangle of the current session's screen as a PNG. Run inside the seat (through Anode), it captures the seat.
# Usage: powershell -NoProfile -File grab_window.ps1 -X 482 -Y 216 -W 316 -H 239 -Out C:\path\shot.png
param([int]$X, [int]$Y, [int]$W, [int]$H, [string]$Out)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"saved $Out session $((Get-Process -Id $PID).SessionId)"
