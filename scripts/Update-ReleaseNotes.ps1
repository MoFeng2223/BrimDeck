[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta)\.\d+)?$')][string]$Version,
    [string]$ReleaseNotes
)
# Replaces the notes of a published release without rebuilding it: the GitHub release text, the notes in update.json
# that the app shows, and the update.json line of SHA256SUMS.txt. The installer and its checksum stay as published.
# Requires the GitHub CLI (gh), signed in with permission to edit releases of this repository.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
Push-Location $projectRoot
$work = Join-Path ([IO.Path]::GetTempPath()) "BrimDeck-notes-$([Guid]::NewGuid().ToString('N'))"
try {
    if (!$ReleaseNotes) { $ReleaseNotes = "releases/$Version.md" }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
    $notes = Read-ReleaseNotes $notesPath
    $tag = "v$Version"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    gh release download $tag --pattern update.json --pattern SHA256SUMS.txt --dir $work
    if ($LASTEXITCODE -ne 0) { throw "Could not download the assets of $tag." }

    $manifestPath = Join-Path $work 'update.json'
    $manifest = Get-Content -Raw -Encoding utf8 -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -ne $Version) { throw "update.json of $tag names version $($manifest.version)." }
    $manifest.notes = $notes
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

    $sumsPath = Join-Path $work 'SHA256SUMS.txt'
    $hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = Get-Content -Encoding utf8 -LiteralPath $sumsPath | Where-Object { $_ -and $_ -notmatch '\s+update\.json$' }
    [IO.File]::WriteAllLines($sumsPath, [string[]](@($sums) + "$hash  update.json"), [Text.UTF8Encoding]::new($false))

    gh release edit $tag --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw "Could not edit the notes of $tag." }
    gh release upload $tag $manifestPath $sumsPath --clobber
    if ($LASTEXITCODE -ne 0) { throw "Could not upload the updated assets of $tag." }
    Write-Output "Updated the notes of $tag."
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
