# Builds and verifies the Windows x64 distribution. Run with PowerShell 7 on a Windows desktop.
[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
[void][IO.Directory]::CreateDirectory($artifacts)
$id = [guid]::NewGuid().ToString('N')
$stage = Join-Path $artifacts ".publish-$id"
$previous = Join-Path $artifacts ".previous-$id"
$current = Join-Path $artifacts 'current'

function Assert-ArtifactDirectory([string]$path) {
    $item = Get-Item -LiteralPath $path -Force
    if (-not $item.PSIsContainer -or $item.Parent.FullName -ne $artifacts -or
        $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected artifact directory: $path"
    }
}

# Serialize promotions. A failed build or verification leaves current available.
$lock = [IO.File]::Open((Join-Path $artifacts '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $next = Join-Path $stage 'next'
    $app = Join-Path $next 'win-x64'
    [void][IO.Directory]::CreateDirectory($app)
    $project = Join-Path $repo 'src/Superb.Desktop/Superb.Desktop.csproj'
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=embedded', '-o', $app)
    if ($NoRestore) { $publishArgs += '--no-restore' }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $docs = New-Item -ItemType Directory -Path (Join-Path $app 'docs')
    foreach ($name in 'README.md', 'LICENSE', 'CHANGELOG.md') {
        Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $docs.FullName
    }
    $notices = Get-Item -LiteralPath (Join-Path $app 'ThirdPartyNotices')
    if ($notices.Parent.FullName -ne $app -or -not $notices.PSIsContainer -or
        $notices.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected notices path.' }
    Move-Item -LiteralPath $notices.FullName -Destination $docs.FullName
    $zip = Join-Path $next 'Superb-windows-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($app, $zip)
    $verify = Join-Path $stage 'unzipped'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $verify)
    $entries = @(Get-ChildItem -LiteralPath $verify -Force)
    if ($entries.Count -ne 2 -or !(Test-Path -LiteralPath "$verify/Superb.exe" -PathType Leaf) -or
        !(Test-Path -LiteralPath "$verify/docs" -PathType Container)) { throw 'Unexpected ZIP layout.' }
    foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $repo 'src/Superb.Desktop/ThirdPartyNotices') -File) {
        if (!(Test-Path -LiteralPath "$verify/docs/ThirdPartyNotices/$($notice.Name)" -PathType Leaf)) {
            throw "Missing license: $($notice.Name)"
        }
    }
    $log = Join-Path $stage 'verification.log'
    $process = Start-Process -FilePath "$verify/Superb.exe" -ArgumentList '--verify-package' -WorkingDirectory $verify -WindowStyle Hidden -RedirectStandardError $log -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); $process.WaitForExit(); throw 'Package verification timed out.' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Package verification failed ($($process.ExitCode)): $(Get-Content -LiteralPath $log -Raw)" }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256", "$hash  Superb-windows-x64.zip`n")

    if (Test-Path -LiteralPath $current) {
        Assert-ArtifactDirectory $current
        [IO.Directory]::Move($current, $previous)
    }
    try { [IO.Directory]::Move($next, $current) }
    catch {
        if (Test-Path -LiteralPath $previous) {
            Assert-ArtifactDirectory $previous
            [IO.Directory]::Move($previous, $current)
        }
        throw
    }
    if (Test-Path -LiteralPath $previous) {
        Assert-ArtifactDirectory $previous
        Remove-Item -LiteralPath $previous -Recurse -Force
    }
    Write-Output "Verified application: $current/win-x64/Superb.exe"
    Write-Output "Verified archive: $current/Superb-windows-x64.zip"
}
finally {
    try {
        if (Test-Path -LiteralPath $stage) {
            Assert-ArtifactDirectory $stage
            Remove-Item -LiteralPath $stage -Recurse -Force
        }
    }
    finally { $lock.Dispose() }
}
