param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\BrimDeck-DLL')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if ($dotnet) {
    $installedSdks = & $dotnet --list-sdks 2>$null
    if (-not $installedSdks -and (Test-Path 'D:\Temp\dotnet-sdk\dotnet.exe')) { $dotnet = 'D:\Temp\dotnet-sdk\dotnet.exe' }
}
if (-not $dotnet -and (Test-Path 'D:\Temp\dotnet-sdk\dotnet.exe')) { $dotnet = 'D:\Temp\dotnet-sdk\dotnet.exe' }
if (-not $dotnet) { throw 'The .NET 10 SDK is required to build the DLL plugin preview.' }

& $dotnet publish (Join-Path $repo 'src\BrimDeck\BrimDeck.csproj') -c Release -r win-x64 --self-contained false -p:Platform=x64 -o $output
if ($LASTEXITCODE -ne 0) { throw "BrimDeck publish failed with exit code $LASTEXITCODE." }

$sample = Join-Path $repo 'samples\BrimDeck.Island.Clock'
& $dotnet build $sample -c Release
if ($LASTEXITCODE -ne 0) { throw "Sample plugin build failed with exit code $LASTEXITCODE." }
$sampleOutput = Join-Path $sample 'bin\Release\net10.0-windows10.0.19041.0'
$sampleDestination = Join-Path $output 'sample-plugins\clock'
New-Item -ItemType Directory -Force -Path $sampleDestination | Out-Null
Copy-Item -Path (Join-Path $sampleOutput '*') -Destination $sampleDestination -Recurse -Force
Write-Host "Preview: $output\BrimDeck.exe"
Write-Host "Sample DLL plugin: $sampleDestination"
