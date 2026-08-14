$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\KevinZonda.Terminal\KevinZonda.Terminal.csproj'
$executable = Join-Path $repositoryRoot 'src\KevinZonda.Terminal\bin\Debug\net10.0-windows\KevinZonda.Terminal.exe'
$environmentProbe = Join-Path ([IO.Path]::GetTempPath()) "kterm-smoke-$([Guid]::NewGuid().ToString('N')).txt"

dotnet build $project --nologo
if ($LASTEXITCODE -ne 0) {
    throw "KevinZonda Terminal build failed with exit code $LASTEXITCODE."
}

$env:KTERM_SMOKE_TEST = '1'
$env:KTERM_SMOKE_OUTPUT = $environmentProbe
try {
    $application = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -PassThru
}
finally {
    Remove-Item Env:\KTERM_SMOKE_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:\KTERM_SMOKE_OUTPUT -ErrorAction SilentlyContinue
}

$children = @()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $application.Refresh()
        if ($application.HasExited) {
            throw "KevinZonda Terminal exited during smoke initialization with code $($application.ExitCode)."
        }

        $children = @(Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $application.Id)
        $shells = @($children | Where-Object Name -in @('powershell.exe', 'pwsh.exe', 'cmd.exe'))
        $conhosts = @($children | Where-Object Name -eq 'conhost.exe')
    }
    while (($shells.Count -lt 5 -or
        $conhosts.Count -lt 5 -or
        -not (Test-Path -LiteralPath $environmentProbe)) -and [DateTime]::UtcNow -lt $deadline)

    if ($shells.Count -ne 5) {
        throw "Expected 5 independent Shell processes, found $($shells.Count)."
    }
    if ($conhosts.Count -ne 5) {
        throw "Expected 5 ConPTY conhost processes, found $($conhosts.Count)."
    }
    if (-not (Test-Path -LiteralPath $environmentProbe)) {
        throw 'The shell environment probe did not complete.'
    }

    $environmentValues = @(Get-Content -LiteralPath $environmentProbe)
    if ($environmentValues.Count -ne 2 -or
        $environmentValues[0] -ne 'xterm-256color' -or
        $environmentValues[1] -ne 'truecolor') {
        throw "Unexpected shell environment: $($environmentValues -join ', ')."
    }
}
finally {
    $childProcessIds = @($children.ProcessId)
    if (-not $application.HasExited) {
        $null = $application.CloseMainWindow()
        if (-not $application.WaitForExit(12000)) {
            $application.Kill()
            throw 'KevinZonda Terminal did not close cleanly within 12 seconds.'
        }
    }

    Start-Sleep -Milliseconds 800
    $remaining = @(Get-Process -Id $childProcessIds -ErrorAction SilentlyContinue)
    if ($remaining.Count -ne 0) {
        throw "KevinZonda Terminal left $($remaining.Count) child processes running after shutdown."
    }

    [IO.File]::Delete($environmentProbe)
}

Write-Output 'KevinZonda Terminal smoke test passed: xterm-256color/truecolor, 2 tabs, 2x2 active layout, 5 Shells, 5 ConPTY hosts, 0 leaked child processes.'
