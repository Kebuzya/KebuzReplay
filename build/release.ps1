param(
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$stage = Join-Path $out 'stage'
$app = Join-Path $root 'Kebuz.App\Kebuz.App.csproj'
$marker = 'kebuz.portable'
$licenseSource = Join-Path $PSScriptRoot 'licenses'

$props = [xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $version) {
    throw 'Version is missing in Directory.Build.props'
}

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Iscc) {
    throw 'Inno Setup 6 (ISCC.exe) is not found, pass -Iscc <path>'
}

function Publish([string]$Target, [string[]]$Extra) {
    $arguments = @('publish', $app, '-c', 'Release', '-r', 'win-x64', '-p:DebugType=none', '--nologo', '-v', 'q', '-o', $Target) + $Extra
    & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Target"
    }
}

function Add-DistributionLicenses([string]$Target) {
    $licenses = New-Item -ItemType Directory -Path (Join-Path $Target 'licenses')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $Target
    Copy-Item -LiteralPath (Join-Path $licenseSource 'THIRD-PARTY-NOTICES.txt') -Destination $Target
    foreach ($file in 'Microsoft-Windows-SDK.rtf', 'CsWinRT-MIT.txt', 'CsWin32-MIT.txt') {
        Copy-Item -LiteralPath (Join-Path $licenseSource $file) -Destination $licenses.FullName
    }
}

function Add-Notice([string]$Target, [string]$Notice) {
    $text = [IO.File]::ReadAllText((Join-Path $licenseSource $Notice))
    [IO.File]::AppendAllText((Join-Path $Target 'THIRD-PARTY-NOTICES.txt'), [Environment]::NewLine + $text)
}

function Copy-RuntimePackLicenses([string]$Target) {
    $assets = Get-Content -Raw (Join-Path (Split-Path -Parent $app) 'obj\project.assets.json') | ConvertFrom-Json
    $downloads = $assets.project.frameworks.PSObject.Properties.Value | ForEach-Object downloadDependencies
    $licenseDirectory = New-Item -ItemType Directory -Path (Join-Path $Target 'licenses\dotnet')
    foreach ($name in 'Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') {
        $packs = @($downloads | Where-Object name -eq $name)
        if ($packs.Count -ne 1 -or $packs[0].version -notmatch '^\[([^,]+), \1\]$') {
            throw "Cannot determine the restored version of $name"
        }

        $version = $Matches[1]
        $paths = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            Join-Path $_ "$($name.ToLowerInvariant())\$version"
        }
        $source = $paths | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $source) {
            throw "Cannot find the restored runtime pack $name $version"
        }

        $destination = New-Item -ItemType Directory -Path (Join-Path $licenseDirectory.FullName "$name-$version")
        $files = Get-ChildItem -LiteralPath $source -File | Where-Object Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES(\.TXT)?)$'
        if (-not ($files | Where-Object Name -match '^LICENSE')) {
            throw "Runtime pack $name $version has no license file"
        }
        if ($name -eq 'Microsoft.NETCore.App.Runtime.win-x64' -and -not ($files | Where-Object Name -match '^THIRD-PARTY-NOTICES')) {
            throw "Runtime pack $name $version has no third-party notices"
        }
        Copy-Item -LiteralPath $files.FullName -Destination $destination.FullName
    }
}

function Test-ArchiveLicenses([string]$Archive, [string]$Source) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            $entries.Add($entry.FullName.Replace('\', '/'), $entry)
        }
        $files = @(Get-Item -LiteralPath (Join-Path $Source 'LICENSE'), (Join-Path $Source 'THIRD-PARTY-NOTICES.txt'))
        $files += Get-ChildItem -LiteralPath (Join-Path $Source 'licenses') -Recurse -File
        foreach ($file in $files) {
            $path = $file.FullName.Substring($Source.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = $entries["KebuzReplay/$path"]
            if (-not $entry) {
                throw "Missing license entry in $($Archive): $path"
            }
            $stream = $entry.Open()
            try {
                $archivedHash = (Get-FileHash -InputStream $stream).Hash
                if ($archivedHash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                    throw "License entry differs from its source in $($Archive): $path"
                }
            }
            finally {
                $stream.Dispose()
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
}

New-Item -ItemType Directory -Force $stage | Out-Null

$portable = Join-Path $stage 'portable\KebuzReplay'
$lite = Join-Path $stage 'lite\KebuzReplay'
$setup = Join-Path $stage 'setup'

Publish $portable @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:ArchiveDistribution=portable')
Add-DistributionLicenses $portable
Copy-RuntimePackLicenses $portable
Copy-Item -LiteralPath (Join-Path $licenseSource 'Microsoft-DotNET-Library.txt') -Destination (Join-Path $portable 'licenses')
Add-Notice $portable 'dotnet-notice.txt'

Publish $lite @('--self-contained', 'false', '-p:PublishSingleFile=true', '-p:ArchiveDistribution=lite')
Add-DistributionLicenses $lite

Publish $setup @('--self-contained', 'false')
Add-DistributionLicenses $setup
Copy-Item -LiteralPath (Join-Path $licenseSource 'Inno-Setup.txt') -Destination (Join-Path $setup 'licenses')
Add-Notice $setup 'installer-notice.txt'

New-Item -ItemType File -Force (Join-Path $portable $marker) | Out-Null
New-Item -ItemType File -Force (Join-Path $lite $marker) | Out-Null

$portableZip = Join-Path $out 'KebuzReplay-win-x64-portable.zip'
$liteZip = Join-Path $out 'KebuzReplay-win-x64-lite.zip'

Compress-Archive -Path $portable -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -Path $lite -DestinationPath $liteZip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
Test-ArchiveLicenses $portableZip $portable
Test-ArchiveLicenses $liteZip $lite

& $Iscc "/DAppVersion=$version" "/DSourceDir=$setup" "/DOutputDir=$out" '/Q' (Join-Path $PSScriptRoot 'KebuzReplay.iss')

if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup failed'
}

$installer = Join-Path $out 'KebuzReplay-Setup-win-x64.exe'
$lines = foreach ($file in @($installer, $portableZip, $liteZip)) {
    $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $file)"
}

[IO.File]::WriteAllLines((Join-Path $out 'checksums.txt'), [string[]]$lines)

Remove-Item -Recurse -Force $stage

Get-ChildItem $out | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
"KebuzReplay $version"
