# Throwaway test window for the Windows-MCP spike. Runs inside the Anode seat.
# A plain WinForms text box (not Notepad: Windows 11 Notepad shares per-user tab state with the user's own Notepad).
# Every second it writes the box's text to run\typed.txt, and it closes itself after 10 minutes.
param([string]$Out = (Join-Path $PSScriptRoot 'run\typed.txt'))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = 'C-074 test pad'
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point(40, 40)
$form.Size = New-Object System.Drawing.Size(640, 400)
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.Dock = 'Fill'
$box.Font = New-Object System.Drawing.Font('Segoe UI', 14)
$box.AccessibleName = 'Test pad text'
$form.Controls.Add($box)
$started = Get-Date
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({
    [System.IO.File]::WriteAllText($Out, $box.Text)
    if (((Get-Date) - $started).TotalMinutes -ge 10) { $form.Close() }
})
$form.Add_FormClosing({ [System.IO.File]::WriteAllText($Out, $box.Text) })
$timer.Start()
[void]$form.ShowDialog()
