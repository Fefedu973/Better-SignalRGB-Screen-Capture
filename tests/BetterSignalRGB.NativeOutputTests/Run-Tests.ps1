param(
    [string]$OpenRgbRoot = (Join-Path $PSScriptRoot '..\..\..\OpenRGB-Room'),
    [string]$Cxx = 'g++.exe',
    [switch]$SkipBenchmark
)
$ErrorActionPreference = 'Stop'
$OpenRgbRoot = (Resolve-Path -LiteralPath $OpenRgbRoot).Path
$header = Join-Path $OpenRgbRoot 'FrameSurface\FrameSurface.h'
$hash = (Get-FileHash -LiteralPath $header -Algorithm SHA256).Hash
$output = Join-Path $PSScriptRoot 'obj\native'
[void](New-Item -ItemType Directory -Path $output -Force)
$reader = Join-Path $output 'FrameSurfaceReader.exe'
$source = Join-Path $PSScriptRoot 'reader.cpp'
$provenanceHeader = Join-Path $output 'reader-provenance.h'
Set-Content -LiteralPath $provenanceHeader -Value ('#define HEADER_SHA256 "' + $hash + '"') -Encoding ASCII
$compiler = (Get-Command $Cxx -ErrorAction Stop).Source
if ([IO.Path]::GetFileNameWithoutExtension($compiler) -eq 'cl') {
    & $compiler /nologo /std:c++17 /EHsc /O2 "/I$OpenRgbRoot" "/FI$provenanceHeader" $source "/Fe:$reader" "/Fo:$output\reader.obj" /link advapi32.lib
} else {
    & $compiler -std=c++17 -O2 -static "-I$OpenRgbRoot" -include $provenanceHeader $source -o $reader -ladvapi32
}
if ($LASTEXITCODE -ne 0) { throw 'Native reader compilation failed.' }
$revision = (& git -C $OpenRgbRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot record OpenRGB source revision.' }
$provenance = [ordered]@{ header = $header; sha256 = $hash; revision = $revision; compiler = $compiler; reader = $reader; builtAtUtc = [DateTime]::UtcNow.ToString('o') }
$provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding UTF8
Write-Output ($provenance | ConvertTo-Json -Compress)
$arguments = @('run', '--project', (Join-Path $PSScriptRoot 'BetterSignalRGB.NativeOutputTests.csproj'), '-c', 'Release', '--', '--reader', $reader, '--header-hash', $hash)
if ($SkipBenchmark) { $arguments += '--skip-benchmark' }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native output regression tests failed.' }
if ((Get-FileHash -LiteralPath $header -Algorithm SHA256).Hash -ne $hash) { throw 'Authoritative header changed during verification.' }
