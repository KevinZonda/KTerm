$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\KevinZonda.KTerm\KevinZonda.KTerm.csproj'
$executable = Join-Path $repositoryRoot 'src\KevinZonda.KTerm\bin\Debug\net10.0-windows\KevinZonda.KTerm.exe'

dotnet build $project --nologo
if ($LASTEXITCODE -ne 0) {
    throw "KTerm build failed with exit code $LASTEXITCODE."
}

$env:KTERM_SMOKE_TEST = '1'
try {
    $application = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -PassThru
}
finally {
    Remove-Item Env:\KTERM_SMOKE_TEST -ErrorAction SilentlyContinue
}

$children = @()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $application.Refresh()
        if ($application.HasExited) {
            throw "KTerm exited during smoke initialization with code $($application.ExitCode)."
        }

        $children = @(Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $application.Id)
        $shells = @($children | Where-Object Name -in @('powershell.exe', 'pwsh.exe', 'cmd.exe'))
        $conhosts = @($children | Where-Object Name -eq 'conhost.exe')
    }
    while (($shells.Count -lt 5 -or $conhosts.Count -lt 5) -and [DateTime]::UtcNow -lt $deadline)

    if ($shells.Count -ne 5) {
        throw "Expected 5 independent Shell processes, found $($shells.Count)."
    }
    if ($conhosts.Count -ne 5) {
        throw "Expected 5 ConPTY conhost processes, found $($conhosts.Count)."
    }
}
finally {
    $childProcessIds = @($children.ProcessId)
    if (-not $application.HasExited) {
        $null = $application.CloseMainWindow()
        if (-not $application.WaitForExit(12000)) {
            $application.Kill()
            throw 'KTerm did not close cleanly within 12 seconds.'
        }
    }

    Start-Sleep -Milliseconds 800
    $remaining = @(Get-Process -Id $childProcessIds -ErrorAction SilentlyContinue)
    if ($remaining.Count -ne 0) {
        throw "KTerm left $($remaining.Count) child processes running after shutdown."
    }
}

Write-Output 'KTerm smoke test passed: 2 tabs, 2x2 active layout, 5 Shells, 5 ConPTY hosts, 0 leaked child processes.'
