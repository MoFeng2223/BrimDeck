#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$SdkDirectory,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [string]$RuntimeTestRunner
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkDirectory).Path
$work = [IO.Path]::GetFullPath($WorkDirectory)
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (Test-Path -LiteralPath $work) { throw 'Choose a new authoring verification directory.' }
if ($work.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $work -eq $repository) {
    throw 'Authoring verification must run outside the host repository.'
}
if (Test-Path -LiteralPath (Join-Path $sdk 'src')) { throw 'The developer SDK must not contain host sources.' }
$metadata = Get-Content -LiteralPath (Join-Path $sdk 'sdk.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $work | Out-Null
$packages = Join-Path $work 'packages'
New-Item -ItemType Directory -Path $packages | Out-Null
$reports = @()
foreach ($id in $metadata.Templates) {
    $project = Join-Path $work "projects/$id"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $project) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sdk "templates/$id") -Destination $project -Recurse
    $projectFiles = @(Get-ChildItem -LiteralPath $project -Filter '*.csproj' -File)
    if ($projectFiles.Count -ne 1) { throw 'A standalone template must contain exactly one uniquely named project.' }
    $projectPath = $projectFiles[0].FullName
    & dotnet msbuild $projectPath -restore '-t:GetModulePackage' '-p:Configuration=Release' "-p:BrimDeckSdk=$sdk" "-p:ArtifactsPath=$work/artifacts" '-warnaserror' '-nologo' '-verbosity:quiet'
    if ($LASTEXITCODE -ne 0) { throw "The $id documentation example did not compile independently." }
    $projectReferences = & dotnet msbuild $projectPath "-p:BrimDeckSdk=$sdk" '-getItem:ProjectReference' '-nologo'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect project references.' }
    $referenceReport = ($projectReferences -join [Environment]::NewLine) | ConvertFrom-Json
    if (@($referenceReport.Items.ProjectReference).Count -ne 0) { throw 'An authoring example depends on host projects.' }
    $manifest = Get-Content -LiteralPath (Join-Path $project 'module.json') -Raw | ConvertFrom-Json
    $guide = [IO.File]::ReadAllText((Join-Path $sdk 'docs/zh-CN/modules/contributing.md'))
    foreach ($part in @(@{Name='project';Language='xml';File=$projectPath},@{Name='manifest';Language='json';File=(Join-Path $project 'module.json')},@{Name='source';Language='csharp';File=(Join-Path $project 'Module.cs')})) {
        $pattern = '(?s)<!-- module-example: ' + [regex]::Escape($id + ' ' + $part.Name) + ' -->\s*```' + $part.Language + '\r?\n(.*?)\r?\n```'
        $blocks = [regex]::Matches($guide,$pattern)
        if ($blocks.Count -ne 1 -or $blocks[0].Groups[1].Value.Trim() -ne [IO.File]::ReadAllText($part.File).Trim()) {
            throw "The $id template differs from its documented $($part.Name)."
        }
    }
    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $assembly = $projectXml.Project.PropertyGroup.AssemblyName
    $package = Join-Path $work "artifacts/bin/$assembly/release/package/$id"
    $shared = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -in @('BrimDeck.dll','BrimDeck.UI.dll','BrimDeck.Modules.Abstractions.dll','BrimDeck.Core.dll','BrimDeck.Modules.dll') })
    if ($shared.Count -ne 0 -or $manifest.id -ne $id) { throw 'Invalid module package identity or duplicated host DLL.' }
    $json = & dotnet (Join-Path $sdk 'tools/checker/BrimDeck.ModuleCheck.dll') $package --json --warnings-as-errors
    if ($LASTEXITCODE -ne 0) { throw "The $id package failed strict static inspection: $json" }
    $report = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    if (!$report.Accepted -or $report.ExecutesModuleCode) { throw 'The checker did not accept the static-only result.' }
    [IO.File]::WriteAllText((Join-Path $work "$id-check.json"), ($json -join [Environment]::NewLine))
    Copy-Item -LiteralPath $package -Destination (Join-Path $packages $id) -Recurse
    $reports += [ordered]@{Id=$id;Built=$true;DocumentMatches=$true;ProjectReferences=0;SharedDlls=0;Accepted=$report.Accepted;Errors=@($report.Issues | Where-Object Severity -eq 'Error').Count;Warnings=@($report.Issues | Where-Object Severity -eq 'Warning').Count}
}
$links = 0
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $sdk 'docs'),(Join-Path $sdk 'mods') -Recurse -Filter '*.md' -File) {
    foreach ($match in [regex]::Matches([IO.File]::ReadAllText($file.FullName), '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value.Split('#')[0]
        if (!$target -or $target -match '^[a-z]+:') { continue }
        $path = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $target))
        if (!(Test-Path -LiteralPath $path)) { throw "Broken exported document link: $($file.Name) -> $target" }
        $links++
    }
}
if ($RuntimeTestRunner) {
    & $RuntimeTestRunner --authoring-packages $packages
    if ($LASTEXITCODE -ne 0) { throw 'The standalone documentation modules failed host runtime checks.' }
}
$result = [ordered]@{Sdk=$sdk;HostSourcesPresent=$false;Contract="$($metadata.ContractMajor).$($metadata.ContractMinor)";Examples=$reports;LocalLinks=$links;RuntimeChecked=[bool]$RuntimeTestRunner}
[IO.File]::WriteAllText((Join-Path $work 'results.json'), ($result | ConvertTo-Json -Depth 6))
$result | ConvertTo-Json -Depth 6
