#requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Reader
)
$ErrorActionPreference = 'Stop'
$applicationPath = (Resolve-Path -LiteralPath $Executable).Path
$readerPath = (Resolve-Path -LiteralPath $Reader).Path
$profile = Join-Path ([IO.Path]::GetTempPath()) ('BetterSignalRGB-native-smoke-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($profile) | Out-Null
@{ SavedSources='[]'; AutoStartRecordingOnBoot='false'; BootInTray='true'; NativeOutputEnabled='true' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'LocalSettings.json')
$previousProfile = $env:LocalSettingsOptions__ApplicationDataFolder
$appProcess = $null
$nativeReader = $null
try {
    try {
        $env:LocalSettingsOptions__ApplicationDataFolder = $profile
        $appProcess = Start-Process -FilePath $applicationPath -WorkingDirectory (Split-Path $applicationPath) -WindowStyle Hidden -PassThru
    } finally { $env:LocalSettingsOptions__ApplicationDataFolder = $previousProfile }
    $descriptorPath = Join-Path $profile 'NativeOutput/connection.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!(Test-Path -LiteralPath $descriptorPath)) {
        $appProcess.Refresh()
        if ($appProcess.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw "Isolated application did not start native output. Logs: $profile" }
        Start-Sleep -Milliseconds 100
    }
    $descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    if ($descriptor.processId -ne $appProcess.Id -or ([uri]$descriptor.baseUrl).Host -ne '127.0.0.1') { throw 'Wrong isolated connection identity.' }
    $authorization = @{ Authorization='Bearer ' + $descriptor.token }
    $api = $descriptor.baseUrl + '/api/native/v1'
    $discovery = Invoke-RestMethod -Uri ($api + '/discovery') -Headers $authorization -TimeoutSec 5
    if ($discovery.transport.name -cne 'ORGBFRM1' -or $discovery.outputs.Count -ne 2) { throw 'Wrong native discovery.' }
    $sceneList = Invoke-RestMethod -Uri ($api + '/scenes') -Headers $authorization -TimeoutSec 5
    if ($sceneList.scenes.Count -ne 0) { throw 'Smoke profile unexpectedly contains user scenes.' }
    $request = @{ clientId='isolated-winui-smoke'; ttlSeconds=30; overrides=@{ AmbilightStyle='Contours'; ScreenWidth=240; ScreenHeight=160; ScreenX=40; ScreenY=20 } } | ConvertTo-Json -Depth 4
    $acquired = Invoke-RestMethod -Uri ($api + '/leases') -Method Post -Headers $authorization -ContentType 'application/json' -Body $request -TimeoutSec 10
    if (!$acquired.control.lease.id) { throw 'WinUI did not grant the synthetic lease.' }
    $lease = $acquired.control.lease.id
    do {
        $status = Invoke-RestMethod -Uri ($api + '/status') -Headers $authorization -TimeoutSec 5
        $effective = $status.output.effectiveState
        if ($effective -and $effective.controlRevision -eq $acquired.targetControlRevision) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'No frame-effective native settings in the isolated WinUI app.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    if ($status.control.scene.isRecording -or $effective.rendering.sources.Count -ne 0 -or
        $effective.rendering.effectiveSettings.ambilightStyle -cne 'Contours') { throw 'Unexpected isolated source/appearance state.' }
    if ($status.control.lease.PSObject.Properties.Name -contains 'id') { throw 'Status disclosed the ownership capability.' }
    $start = [Diagnostics.ProcessStartInfo]::new($readerPath)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.ArgumentList.Add($effective.image.channel)
    $nativeReader = [Diagnostics.Process]::Start($start)
    $hello = $nativeReader.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
    if (!$hello.StartsWith('READY ')) { throw 'Actual C++ Reader did not start.' }
    $nativeReader.StandardInput.WriteLine('read 2000 5'); $nativeReader.StandardInput.Flush()
    $frame = $nativeReader.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult().Split(' ')
    if ($frame[0] -cne 'NewFrame' -or $frame[1] -cne '320' -or $frame[2] -cne '200' -or $frame[5] -cne $effective.image.generation) { throw 'Actual C++ Reader rejected the isolated WinUI output.' }
    Start-Sleep -Milliseconds 2300
    $nativeReader.StandardInput.WriteLine('read 2000 5'); $nativeReader.StandardInput.Flush()
    $static = $nativeReader.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult().Split(' ')
    if ($static[0] -cne 'Unchanged') { throw 'Static WinUI output lost its live heartbeat.' }
    $released = Invoke-RestMethod -Uri ($api + '/leases/' + $lease) -Method Delete -Headers $authorization -TimeoutSec 10
    if ($released.control.lease) { throw 'Synthetic lease was not released.' }
    $settings = Get-Content -LiteralPath (Join-Path $profile 'LocalSettings.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($settings.ContainsKey('SignalRgbEffect')) { throw 'Temporary appearance unexpectedly persisted.' }
    $log = Get-Content -LiteralPath (Join-Path $profile 'Logs/ApplicationErrors.log') -Raw
    if ($log -notmatch '\[Launch complete\]' -or $log -match 'UnhandledException|Launch failed') { throw 'WinUI launch failed; inspect isolated profile logs.' }
    [pscustomobject]@{ Result='PASS'; Application='real WinUI build'; Capture='disabled, zero sources'; NativeReader='actual C++ FrameSurface Reader'; StaticHeartbeat='PASS'; TemporaryOverrides='released without persisted preferences'; Profile=$profile } | ConvertTo-Json
} finally {
    if ($nativeReader) {
        if (!$nativeReader.HasExited) {
            $nativeReader.StandardInput.WriteLine('quit'); $nativeReader.StandardInput.Flush()
            if (!$nativeReader.WaitForExit(3000)) { $nativeReader.Kill(); $nativeReader.WaitForExit() }
        }
        $nativeReader.Dispose()
    }
    if ($appProcess) {
        $appProcess.Refresh()
        if (!$appProcess.HasExited -and $appProcess.Path -ieq $applicationPath) { Stop-Process -InputObject $appProcess; $appProcess.WaitForExit(10000) | Out-Null }
        $appProcess.Dispose()
    }
}
