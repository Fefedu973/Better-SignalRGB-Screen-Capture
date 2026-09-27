param(
    [string]$ReaderPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repository
try {
    $arguments = @('run', '--project', (Join-Path $PSScriptRoot 'BetterSignalRGB.NativeIntegrationTests.csproj'), '-c', $Configuration)
    if ($ReaderPath) {
        $reader = (Resolve-Path -LiteralPath $ReaderPath).Path
        $arguments += @('--', '--reader', $reader)
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Native integration tests failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
