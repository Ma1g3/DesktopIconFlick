param([switch]$Test)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '需要 Windows x64 和 .NET Framework 4.8。'
}

$output = Join-Path $PSScriptRoot 'bin'
$null = New-Item -Path $output -ItemType Directory -Force
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$references = @('System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$application = Join-Path $output 'DesktopIconFlick.exe'
$manifest = Join-Path $PSScriptRoot 'src\app.manifest'
& $compiler /nologo /utf8output /codepage:65001 /warnaserror+ /platform:x64 /optimize+ /target:winexe "/win32manifest:$manifest" "/out:$application" @references @sources
if ($LASTEXITCODE -ne 0) { throw '应用构建失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\app.config') -Destination "$application.config" -Force
Write-Output $application

if ($Test) {
    $runner = Join-Path $output 'DesktopIconFlick.Tests.exe'
    $tests = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' -File | ForEach-Object FullName)
    & $compiler /nologo /utf8output /codepage:65001 /warnaserror+ /platform:x64 /target:exe /main:DesktopIconFlick.Tests "/out:$runner" @references @sources @tests
    if ($LASTEXITCODE -ne 0) { throw '测试构建失败。' }
    & $runner
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
}
