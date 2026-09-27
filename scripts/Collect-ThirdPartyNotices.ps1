#requires -Version 7.0
<#
.SYNOPSIS
Copies bundled NuGet license/notice documents for the packages resolved in an assets file.
.DESCRIPTION
Reads project.assets.json and the existing NuGet package caches only. It never downloads
documents, interprets license terms, or changes package contents. Original notice files
and nuspec metadata are copied byte-for-byte into package/version subdirectories.
.EXAMPLE
pwsh -File scripts/Collect-ThirdPartyNotices.ps1 -AssetsFile app/obj/project.assets.json -Destination publish/ThirdPartyNotices
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AssetsFile,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Get-ConfinedPath {
    param([string]$Root, [string]$RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Expected a relative package path, got '$RelativePath'."
    }
    $parts = $RelativePath.Replace('\', '/').Split('/')
    if ($parts -contains '..' -or $parts -contains '.') {
        throw "Package path traversal is not allowed: '$RelativePath'."
    }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath([IO.Path]::Combine($rootPath, ($parts -join [IO.Path]::DirectorySeparatorChar)))
    if (-not $fullPath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw "Package path escapes its root: '$RelativePath'."
    }
    return $fullPath
}

function Test-NoticeName {
    param([string]$Name)
    # Match document names, never implementation files such as LicenseManager.dll.
    # Permit explicit variants such as LICENSE-MIT, NOTICE.thirdparty and LICENSES.md.
    return $Name -match '^(?i:licen[cs]es?|notices?|copying|copyright|third[-_. ]?party[-_. ]?(?:notices?|licen[cs]es?))(?:$|[._ -])' -and
        [IO.Path]::GetExtension($Name) -notmatch '^(?i:\.(?:dll|exe|pdb|winmd|pri|nupkg|sha512|cs|vb|cpp|h|targets|props))$'
}

$assetsPath = (Resolve-Path -LiteralPath $AssetsFile).Path
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
if (-not $assets.ContainsKey('libraries') -or -not $assets.ContainsKey('packageFolders')) {
    throw 'The assets file does not contain resolved libraries and packageFolders.'
}
$destinationPath = [IO.Path]::GetFullPath($Destination)
$packageFolders = @($assets.packageFolders.Keys | ForEach-Object { [IO.Path]::GetFullPath($_) })
if ($packageFolders.Count -eq 0) { throw 'The assets file does not declare a NuGet package cache.' }
foreach ($folder in $packageFolders) {
    $cacheRoot = $folder.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($destinationPath.Equals($cacheRoot, $pathComparison) -or
        $destinationPath.StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
        throw 'The notice destination must not be inside a NuGet package cache.'
    }
}

$resolvedPackages = @{}
foreach ($library in $assets.libraries.GetEnumerator()) {
    if ($library.Value.type -eq 'package') { $resolvedPackages[$library.Key] = $library.Value }
}
# Self-contained runtime/targeting packs are downloadDependencies, not libraries.
# NuGet records their exact range rather than a library file inventory.
if ($assets.ContainsKey('project') -and $assets.project.ContainsKey('frameworks')) {
    foreach ($framework in $assets.project.frameworks.Values) {
        if (-not $framework.ContainsKey('downloadDependencies')) { continue }
        foreach ($dependency in $framework.downloadDependencies) {
            $range = [string]$dependency.version
            $version = if ($range -match '^\[([^,\[\]()\s]+),\s*([^,\[\]()\s]+)\]$' -and $Matches[1] -ceq $Matches[2]) {
                $Matches[1]
            } elseif ($range -match '^\[([^,\[\]()\s]+)\]$') {
                $Matches[1]
            } elseif ($range -match '^\d[^,\[\]()\s]*$') {
                $range
            } else {
                throw "Download dependency '$($dependency.name)' has no concrete resolved version: '$range'."
            }
            $identity = "$($dependency.name)/$version"
            if (-not $resolvedPackages.ContainsKey($identity)) {
                $resolvedPackages[$identity] = @{ path = $identity.ToLowerInvariant(); downloadDependency = $true }
            }
        }
    }
}
$packages = @($resolvedPackages.GetEnumerator() | Sort-Object Key)
$index = [Collections.Generic.List[string]]::new()
$index.Add('# Third-party package notices')
$index.Add('')
$index.Add('This directory contains original documents bundled with the NuGet packages resolved in project.assets.json. Files are copied without modification. This index does not interpret or replace their terms.')
$index.Add('')
$index.Add('Packages include resolved libraries and exact-version download dependencies, including self-contained .NET runtime packs and build/targeting packs. When multiple runtime architectures were restored, their notices are all included.')
$index.Add('')
$index.Add('A package with no matching bundled document is listed explicitly. Its copied nuspec may declare a license expression, file reference or URL. No external license documents were downloaded or inferred.')
$index.Add('')
$copiedCount = 0
$withoutNotices = 0

foreach ($package in $packages) {
    $identity = [string]$package.Key
    $library = $package.Value
    if (-not $library.ContainsKey('path') -or
        (-not $library.ContainsKey('files') -and -not $library.ContainsKey('downloadDependency'))) {
        throw "Resolved package '$identity' is missing its cache path or file inventory."
    }
    # Use the path NuGet actually resolved, rather than guessing package ID casing.
    $packageRoot = $null
    foreach ($folder in $packageFolders) {
        $candidate = Get-ConfinedPath $folder $library.path
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = $candidate; break }
    }
    if ($null -eq $packageRoot) { throw "The resolved NuGet package '$identity' is missing from every declared package cache." }

    $inventory = if ($library.ContainsKey('files')) {
        @($library.files | Sort-Object -Unique)
    } else {
        @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force |
            ForEach-Object { [IO.Path]::GetRelativePath($packageRoot, $_.FullName) } | Sort-Object -Unique)
    }
    $nuspecs = @($inventory | Where-Object { [IO.Path]::GetExtension($_) -ieq '.nuspec' })
    if ($nuspecs.Count -eq 0) { throw "Resolved package '$identity' has no nuspec metadata in its file inventory." }
    $notices = @($inventory | Where-Object { Test-NoticeName ([IO.Path]::GetFileName($_.Replace('\', '/'))) })
    $selected = @(@($nuspecs) + @($notices) | Sort-Object -Unique)
    $packageDestination = Get-ConfinedPath $destinationPath $identity
    $index.Add("## $identity")
    $index.Add('')
    foreach ($relative in $selected) {
        $source = Get-ConfinedPath $packageRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Expected package document '$identity/$relative' is missing from the local cache."
        }
        # Reject links before copying: documents must come from this resolved package.
        $item = Get-Item -LiteralPath $source -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Package document '$identity/$relative' is a reparse point, not a regular bundled file."
        }
        $target = Get-ConfinedPath $packageDestination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($source, $target, $true)
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Length -ne $item.Length) {
            throw "Copy verification failed for '$identity/$relative'."
        }
        $link = ($identity + '/' + $relative.Replace('\', '/')).Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }
        $kind = if ([IO.Path]::GetExtension($relative) -ieq '.nuspec') { 'Package metadata' } else { 'Bundled document' }
        $index.Add("- ${kind}: [$relative](<$($link -join '/')>)")
        $copiedCount++
    }
    if ($notices.Count -eq 0) {
        $withoutNotices++
        $index.Add('- No bundled license/notice document matched the filename rules; consult the package metadata above.')
    }
    $index.Add('')
}

$index.Insert(2, "Resolved packages: $($packages.Count). Copied documents and metadata: $copiedCount. Packages without a matching bundled notice: $withoutNotices.")
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null
[IO.File]::WriteAllLines((Join-Path $destinationPath 'INDEX.md'), $index, [Text.UTF8Encoding]::new($false))
Write-Host "Collected $copiedCount bundled documents/metadata for $($packages.Count) resolved packages; $withoutNotices have no matching bundled notice."
Write-Host "Index: $(Join-Path $destinationPath 'INDEX.md')"
