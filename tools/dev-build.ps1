<#
.SYNOPSIS
    Builds a copy of Loungepad to test: dist\v<version>-<branch>\Loungepad.exe.

.DESCRIPTION
    The same single-file, self-contained exe package.ps1 makes for a release, but unsigned, with
    no zip, and in a folder named after the branch, so it never touches a release in dist\v<version>.
    The Stop hook in .claude\settings.local.json runs it with -IfChanged -Hook at the end of every
    turn, so there is a build to test after every change (the user's ask, Oct 13 2026).

    -IfChanged skips the build when nothing that goes into the exe has changed since the last one:
    every file under Loungepad\ and Loungepad.Input\ (bin and obj left out) and release-notes\, by
    path, size and write time. The fingerprint is kept in dev-build.txt beside the exe, with the
    commit and the time of the build.

    A copy of the build that is running cannot be replaced, but Windows lets it be renamed: it is
    set aside as Loungepad.exe.<n>.loungepad-old and the new exe put in its place. The next start
    deletes the set-aside file (UpdateService.FinishPreviousUpdate), and so does the next build.
    Close the running copy before starting the new one: a second copy only brings the first one
    to the front.

    Like any single-file build it updates itself from GitHub when a newer release than its version
    is out, so it is only a dev build until the csproj's version is behind the latest release.

.PARAMETER IfChanged
    Build only when the source has changed since the last dev build of this branch.

.PARAMETER Hook
    Read the hook's input from stdin and answer with the JSON a Claude Code hook prints: a message
    for the user, or, when the build fails, a block that sends the errors back to be fixed.

.EXAMPLE
    .\tools\dev-build.ps1
#>
[CmdletBinding()]
param([switch]$IfChanged, [switch]$Hook)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'Loungepad\Loungepad.csproj'
$started = Get-Date

$hookInput = $null
if ($Hook) {
    try { $hookInput = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { $hookInput = $null }
}

function Say([string]$message) {
    if ($Hook) { @{ systemMessage = $message } | ConvertTo-Json -Compress | Write-Output }
    else { Write-Host $message }
}

function Fail([string]$message) {
    # Blocked once, so the errors go back to be fixed; never twice in a row, or a build that
    # cannot be fixed in one go would hold the turn open for good.
    if ($Hook -and -not ($hookInput -and $hookInput.stop_hook_active)) {
        @{ decision = 'block'; reason = $message } | ConvertTo-Json -Compress | Write-Output
    }
    elseif ($Hook) { Say $message }
    else { Write-Host $message -ForegroundColor Red; exit 1 }
    exit 0
}

# ---- what goes into the exe, and whether it has changed ----
$files = foreach ($name in 'Loungepad', 'Loungepad.Input', 'release-notes') {
    $root = Join-Path $repo $name
    if (-not (Test-Path -LiteralPath $root)) { continue }
    Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -notin 'bin', 'obj' } | ForEach-Object {
        if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force } else { $_ }
    }
}
$listing = ($files | Sort-Object FullName | ForEach-Object {
    "$($_.FullName.Substring($repo.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
}) -join "`n"
$sha = [Security.Cryptography.SHA256]::Create()
$fingerprint = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($listing)) | ForEach-Object { $_.ToString('x2') })

$csproj = [xml](Get-Content -LiteralPath $project)
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$branch = (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null)
$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
if (-not $branch -or $branch -eq 'HEAD') { $branch = $commit }
$branch = $branch -replace '[^A-Za-z0-9._-]', '-'
$dirty = if (& git -C $repo status --porcelain -- Loungepad Loungepad.Input release-notes 2>$null) { ' with uncommitted changes' } else { '' }

$dist = Join-Path $repo "dist\v$version-$branch"
$target = Join-Path $dist 'Loungepad.exe'
$stamp = Join-Path $dist 'dev-build.txt'
$relative = $target.Substring($repo.Length + 1)

if ($IfChanged -and (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $stamp)) {
    $last = Get-Content -LiteralPath $stamp | Where-Object { $_ -like 'fingerprint: *' } | Select-Object -First 1
    if ($last -eq "fingerprint: $fingerprint") { exit 0 }
}

# ---- publish ----
$work = Join-Path $repo 'artifacts\dev-build'
$staging = Join-Path $work 'publish'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$arguments = "publish `"$project`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true " +
    "-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none " +
    "-o `"$staging`" --nologo"
$psi = New-Object System.Diagnostics.ProcessStartInfo 'dotnet', $arguments
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::Start($psi)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$log = $stdout.Result + $stderr.Result
Set-Content -LiteralPath (Join-Path $work 'publish.log') -Value $log -Encoding UTF8

if ($process.ExitCode -ne 0) {
    $errors = ($log -split "`r?`n" | Where-Object { $_ -match ': error ' } | Select-Object -Unique -First 12) -join "`n"
    if (-not $errors) { $errors = ($log -split "`r?`n" | Select-Object -Last 15) -join "`n" }
    Fail "The dev build failed (dotnet publish exited $($process.ExitCode)); nothing in $relative was replaced.`n$errors"
}

$built = Join-Path $staging 'Loungepad.exe'
if (-not (Test-Path -LiteralPath $built)) { Fail "The dev build produced no Loungepad.exe in $staging." }

# ---- put it in place, beside a running copy if there is one ----
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Get-ChildItem -LiteralPath $dist -Filter '*.loungepad-old' -Force -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { } }
$setAside = $false
if (Test-Path -LiteralPath $target) {
    try { Remove-Item -LiteralPath $target -Force -ErrorAction Stop }
    catch {
        Move-Item -LiteralPath $target -Destination "$target.$([DateTime]::UtcNow.Ticks).loungepad-old"
        $setAside = $true
    }
}
Copy-Item -LiteralPath $built -Destination $target
@(
    "Loungepad $version dev build of $branch at $commit$dirty"
    "built: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))"
    "fingerprint: $fingerprint"
) | Set-Content -LiteralPath $stamp -Encoding UTF8

$seconds = [int]((Get-Date) - $started).TotalSeconds
$note = if ($setAside) { ' A copy was running: close it, then start the new one.' } else { '' }
Say "Dev build ready: $relative ($commit$dirty, $seconds s).$note"
