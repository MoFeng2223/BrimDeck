#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$ArtifactsDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new SDK output directory; existing directories are never overwritten.' }
if (!(Test-Path -LiteralPath (Split-Path -Parent $output) -PathType Container)) { throw 'The SDK output parent directory must exist.' }
$stage = $output + '.staging-' + [Guid]::NewGuid().ToString('N')

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}
function Get-ProjectProperty([string]$Project, [string]$Property) {
    $value = & dotnet msbuild (Join-Path $repository $Project) '-p:Configuration=Release' "-p:ArtifactsPath=$artifacts" "-getProperty:$Property" '-nologo'
    if ($LASTEXITCODE -ne 0 -or !($value -join '').Trim()) { throw "Cannot resolve $Property for $Project." }
    return ($value -join [Environment]::NewLine).Trim()
}
function Export-Example([string]$Id) {
    $guide = [IO.File]::ReadAllText((Join-Path $repository 'docs/zh-CN/modules/contributing.md'))
    $directory = Join-Path $stage "templates/$Id"
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($part in @(@{Name='project';Language='xml';File='Module.csproj'},@{Name='manifest';Language='json';File='module.json'},@{Name='source';Language='csharp';File='Module.cs'})) {
        $pattern = '(?s)<!-- module-example: ' + [regex]::Escape($Id + ' ' + $part.Name) + ' -->\s*```' + $part.Language + '\r?\n(.*?)\r?\n```'
        $matches = [regex]::Matches($guide, $pattern)
        if ($matches.Count -ne 1) { throw "Expected exactly one $Id $($part.Name) example in the development guide." }
        [IO.File]::WriteAllText((Join-Path $directory $part.File), $matches[0].Groups[1].Value + [Environment]::NewLine)
    }
    $manifest = Get-Content -LiteralPath (Join-Path $directory 'module.json') -Raw | ConvertFrom-Json
    $projectName = [IO.Path]::ChangeExtension($manifest.assembly, '.csproj')
    if ($projectName -ne [IO.Path]::GetFileName($projectName)) { throw 'The documented assembly must be a root filename.' }
    Move-Item -LiteralPath (Join-Path $directory 'Module.csproj') -Destination (Join-Path $directory $projectName)
}

# Maintainer command. The exported bundle contains binaries, docs and examples only.
Invoke-Dotnet @('build',(Join-Path $repository 'src/BrimDeck.UI/BrimDeck.UI.csproj'),'-c','Release','--artifacts-path',$artifacts,'--nologo','-v:q')
Invoke-Dotnet @('build',(Join-Path $repository 'tools/BrimDeck.ModuleDocs/BrimDeck.ModuleDocs.csproj'),'-c','Release','--artifacts-path',$artifacts,'--nologo','-v:q')
$docsTool = Get-ProjectProperty 'tools/BrimDeck.ModuleDocs/BrimDeck.ModuleDocs.csproj' 'TargetPath'
Invoke-Dotnet @($docsTool,'--check',$repository)
Invoke-Dotnet @('publish',(Join-Path $repository 'tools/BrimDeck.ModuleCheck/BrimDeck.ModuleCheck.csproj'),'-c','Release','--artifacts-path',$artifacts,'-o',(Join-Path $stage 'tools/checker'),'--no-self-contained','--nologo','-v:q')
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'ref'),(Join-Path $stage 'build') | Out-Null
foreach ($project in @('BrimDeck.Modules.Abstractions','BrimDeck.UI','BrimDeck.Core')) {
    $reference = Get-ProjectProperty "src/$project/$project.csproj" 'TargetRefPath'
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference assembly: $project" }
    Copy-Item -LiteralPath $reference -Destination (Join-Path $stage "ref/$project.dll")
}
foreach ($name in @('ModuleSdk.props','ModulePackage.targets')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $stage "build/$name") }
foreach ($document in @('mods/README.md','docs/zh-CN/modules.md','docs/en-US/modules.md')) {
    $destination = Join-Path $stage $document
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $document) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repository 'docs/zh-CN/modules') -Destination (Join-Path $stage 'docs/zh-CN/modules') -Recurse
foreach ($example in @('notes','timers','usage-alerts')) {
    $files = Get-ChildItem -LiteralPath (Join-Path $repository "mods/$example") -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Select-Object -ExpandProperty FullName
    foreach ($file in $files) {
        $destination = Join-Path $stage ([IO.Path]::GetRelativePath($repository,$file))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Export-Example 'clock-demo'
Export-Example 'counter-demo'
Export-Example 'usage-guard'

$contractSource = [IO.File]::ReadAllText((Join-Path $repository 'src/BrimDeck.Modules.Abstractions/HostApis.cs'))
$contract = [regex]::Match($contractSource, 'const int Major = (\d+), Minor = (\d+);')
if (!$contract.Success) { throw 'Cannot read the current contract version.' }
$metadata = [ordered]@{ FormatVersion=1; ContractMajor=[int]$contract.Groups[1].Value; ContractMinor=[int]$contract.Groups[2].Value; TargetFramework='net10.0-windows10.0.19041.0'; Configuration='Release'; ReferenceOnly=$true; Docs=@('docs/zh-CN/modules/contributing.md','docs/zh-CN/modules/api.md','mods/README.md'); Templates=@('clock-demo','counter-demo','usage-guard') }
[IO.File]::WriteAllText((Join-Path $stage 'sdk.json'), ($metadata | ConvertTo-Json -Depth 4))
$readme = @'
# BrimDeck 模组开发包

从 [开发指南](docs/zh-CN/modules/contributing.md) 开始，再查阅 [API](docs/zh-CN/modules/api.md) 和 [平台规范](mods/README.md)。这三篇文档及三个完整模板足以开始开发，不需要宿主源码。

`ref/` 只用于编译，不能执行或安装到模组目录。运行时使用匹配契约的 BrimDeck。`tools/checker/` 是静态检查工具的私有运行依赖，不是模组依赖。

使用 .NET 10 SDK，把 templates 中一个目录复制到自己的工程目录，按指南传入 BrimDeckSdk 路径构建。模板内容直接从文档提取，不另维护一份示例。
'@
[IO.File]::WriteAllText((Join-Path $stage 'README.md'), $readme)
$files = Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/'); Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
[IO.File]::WriteAllText((Join-Path $stage 'files.json'), (@($files) | ConvertTo-Json -Depth 4))
Move-Item -LiteralPath $stage -Destination $output
Write-Output $output
