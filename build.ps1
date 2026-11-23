# Builds and tests the Greeter. Output is also written to build-log.txt.
# Usage:  .\build.ps1            (build + test)
#         .\build.ps1 -Run       (build + test, then start the Greeter)
#         .\build.ps1 -Publish   (self-contained single-file exe in .\artifacts\TaskHubGreeter)
param([switch]$Run, [switch]$Publish)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
$log = Join-Path $PSScriptRoot 'build-log.txt'
"=== $(Get-Date -Format s) ===" | Out-File $log -Encoding utf8

function Write-Log([string]$line) { Write-Host $line; Add-Content -Path $log -Value $line -Encoding utf8 }

function Step($title, [scriptblock]$cmd) {
    Write-Log "`n### $title"
    & $cmd 2>&1 | ForEach-Object { Write-Log "$_" }
    if ($LASTEXITCODE -ne 0) {
        Write-Log "### FAILED: $title (exit $LASTEXITCODE)"
        exit $LASTEXITCODE
    }
}

# A running Greeter locks its exe; stop it so the build can overwrite it.
Get-Process TaskHubGreeter -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Step 'dotnet --version' { dotnet --version }
Step 'restore' { dotnet restore TaskHubGreeter.slnx }
Step 'build'   { dotnet build TaskHubGreeter.slnx -c Debug --no-restore -v:minimal }
Step 'test'    { dotnet test TaskHubGreeter.slnx -c Debug --no-build -v:minimal --logger "console;verbosity=normal" }

$exe = Join-Path $PSScriptRoot 'src/TaskHubGreeter.App/bin/Debug/net10.0-windows/TaskHubGreeter.exe'
if ($Publish) {
    Step 'publish' {
        dotnet publish src/TaskHubGreeter.App/TaskHubGreeter.App.csproj -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/TaskHubGreeter
    }
    $exe = Join-Path $PSScriptRoot 'artifacts/TaskHubGreeter/TaskHubGreeter.exe'
}

Write-Log "`n### ALL OK"
Write-Log "Greeter: $exe"

if ($Run) { Start-Process $exe }
