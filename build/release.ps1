param(
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts'
$stage = Join-Path $out 'stage'
$app = Join-Path $root 'Kebuz.App\Kebuz.App.csproj'
$marker = 'kebuz.portable'

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

if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
}

New-Item -ItemType Directory -Force $stage | Out-Null

$portable = Join-Path $stage 'portable\KebuzReplay'
$lite = Join-Path $stage 'lite\KebuzReplay'
$setup = Join-Path $stage 'setup'

Publish $portable @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
Publish $lite @('--self-contained', 'false', '-p:PublishSingleFile=true')
Publish $setup @('--self-contained', 'false')

New-Item -ItemType File -Force (Join-Path $portable $marker) | Out-Null
New-Item -ItemType File -Force (Join-Path $lite $marker) | Out-Null

$portableZip = Join-Path $out 'KebuzReplay-win-x64-portable.zip'
$liteZip = Join-Path $out 'KebuzReplay-win-x64-lite.zip'

Compress-Archive -Path $portable -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -Path $lite -DestinationPath $liteZip -CompressionLevel Optimal

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
