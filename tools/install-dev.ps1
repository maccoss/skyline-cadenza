# Windows-only dev install: publish App, copy into Skyline-daily's external
# tools folder so a restart picks it up. No zip step.

[CmdletBinding()]
param(
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path "$PSScriptRoot\.."
$appProj  = Join-Path $repoRoot 'src\SkylineCadenza.App\SkylineCadenza.App.csproj'
$publish  = Join-Path $repoRoot "build\staging-dev"
$dest     = Join-Path $env:LOCALAPPDATA "Apps\SkylineDaily\Tools\SkylineCadenza"

dotnet publish $appProj -c $Configuration -r win-x64 --self-contained false `
    /p:PublishDir="$publish\"

if (Test-Path $dest) {
    Remove-Item -Recurse -Force $dest
}
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Recurse -Force "$publish\*" $dest

Write-Host "Installed dev build to: $dest"
Write-Host "Restart Skyline-daily and look for 'Skyline Cadenza...' in the Tools menu."
