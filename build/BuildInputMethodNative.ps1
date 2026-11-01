param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$env:VSCMD_SKIP_SENDTELEMETRY = '1'
$finder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $finder)) { throw 'The input-method adapter requires the Visual Studio C++ build tools and a Windows SDK.' }
$installation = & $finder -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Install the Visual Studio Desktop development with C++ workload to build the input-method adapter.' }
$setup = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$source = Join-Path $PSScriptRoot '..\src\BrimDeck.InputMethods.Native\InputMethods.cpp'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$dll = Join-Path $output 'BrimDeck.InputMethods.Native.dll'
$obj = Join-Path $output 'InputMethods.obj'
$pdb = Join-Path $output 'InputMethods.pdb'
# One cmd invocation is required to use the compiler environment established by vcvars64.
$build = 'call "{0}" >nul && cl.exe /nologo /W4 /WX /O2 /MT /EHsc /std:c++17 /LD /Fo"{1}" /Fd"{2}" "{3}" /link /OUT:"{4}" /IMPLIB:"{5}" user32.lib ole32.lib uuid.lib' -f $setup,$obj,$pdb,$source,$dll,(Join-Path $output 'InputMethods.lib')
& $env:ComSpec /d /s /c $build
if ($LASTEXITCODE -ne 0) { throw 'The input-method native adapter did not compile.' }
