[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'deploy\publish'))

$ErrorActionPreference = 'Stop'
$npmExecutable = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'npm.cmd' } else { 'npm' }
Push-Location (Join-Path $PSScriptRoot 'src\AIBot.Web')
try {
    & $npmExecutable ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    & $npmExecutable test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend regression tests failed' }
    & $npmExecutable run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
}
finally { Pop-Location }
& dotnet publish (Join-Path $PSScriptRoot 'src\AIBot.Server\AIBot.Server.csproj') -c Release -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed' }
