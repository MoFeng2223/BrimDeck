[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta)\.\d+)?$')][string]$Version,
    [string]$ReleaseNotes,
    [string]$OutputDirectory = 'artifacts/releases'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
Push-Location $projectRoot
try {
    if (!$ReleaseNotes) { $ReleaseNotes = "releases/$Version.md" }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
    $outputPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }))
    $publishPath = Join-Path $projectRoot ".artifacts/release-build/$Version-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $publishPath, $outputPath | Out-Null
    # Each version gets a clean output folder, so files of an earlier release never end up in this one.
    Get-ChildItem -LiteralPath $outputPath -File | Remove-Item
    dotnet publish src/BrimDeck/BrimDeck.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishSingleFile=false -o $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    $iscc = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
    & $iscc -qp "-dAppVersion=$Version" "-dSourceDir=$publishPath" "-o$outputPath" installer/BrimDeck.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Get-Item -LiteralPath (Join-Path $outputPath "BrimDeck-$Version-Setup.exe")
    # The portable package is the same publish output in a zip: extract it anywhere and run BrimDeck.exe. It registers no
    # uninstall entry and creates no shortcuts, so it keeps its settings in %LOCALAPPDATA%\BrimDeck like an installed copy
    # does (the two share one set of settings), and it updates by downloading this file again: AppUpdates offers the
    # installer only to a copy the installer registered (GitHubUpdates.IsInstalled).
    $portable = Join-Path $outputPath "BrimDeck-$Version-Portable.zip"
    # Every entry sits in one top-level folder, so extracting into Downloads or the root of a drive keeps the files together.
    $zip = [IO.Compression.ZipFile]::Open($portable, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $publishPath -File -Recurse | ForEach-Object {
            $entry = 'BrimDeck/' + [IO.Path]::GetRelativePath($publishPath, $_.FullName).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
    # The app reads update.json from the latest release and verifies the installer against it before running it.
    $manifest = [ordered]@{
        version = $Version
        notes = Read-ReleaseNotes $notesPath
        file = $installer.Name
        size = $installer.Length
        sha256 = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [IO.File]::WriteAllText((Join-Path $outputPath 'update.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $outputPath -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding utf8
    Write-Output "Release artifacts: $outputPath"
    Write-Output "Published application for validation: $publishPath"
}
finally { Pop-Location }
