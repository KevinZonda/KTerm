# KTerm resize artifact test.
#
# Run inside a KTerm PowerShell tab:
#   & scripts\resize-progress.ps1
#
# While the progress bar is running, repeatedly resize the window:
# make it narrower/wider and shorter/taller. After the script finishes,
# anything colored left on screen above the "Done" line is a stale
# artifact. A healthy terminal should show a clean screen.

Write-Host "KTerm resize test" -ForegroundColor Cyan
Write-Host "While the bar runs, resize the window (narrower/wider/shorter/taller)." -ForegroundColor Yellow
Write-Host ""

$activity = 'KTerm resize test'
foreach ($i in 0..400) {
    Write-Progress -Activity $activity `
        -Status "Step $i of 400 - resize the window now" `
        -PercentComplete ($i * 100 / 400)
    Start-Sleep -Milliseconds 250
}
Write-Progress -Activity $activity -Completed

Write-Host ""
Write-Host "Done. Any colored bars above this line are stale artifacts." -ForegroundColor Green
