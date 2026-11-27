# Build artifacts only. Does not install or change Windows services.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/windows-agent'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../src/ControlSystem.Agent/ControlSystem.Agent.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:EnableWindowsService=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed (exit $LASTEXITCODE)." }
Write-Host "Published to $OutputDirectory. No service was installed."
