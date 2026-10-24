param([Parameter(Mandatory = $true)][string]$Packages)
$ErrorActionPreference = 'Stop'
$Packages = (Resolve-Path $Packages).Path
$root = Join-Path $env:RUNNER_TEMP ('DAYDRIVE package check ' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $root | Out-Null
Remove-Item Env:INTELLIGENT_SCHEDULER_DATA_DIR -ErrorAction SilentlyContinue
function Run-Daydrive([string]$Executable, [string]$DataDirectory) {
    $arguments = @('--seed-demo=7')
    if ($DataDirectory) { $arguments += ('"--data-dir=' + $DataDirectory + '"') }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WorkingDirectory $root -PassThru
    if (-not $process.WaitForExit(180000)) {
        $process.Kill()
        throw 'DAYDRIVE startup timed out'
    }
    if ($process.ExitCode -ne 0) { throw "DAYDRIVE exited with $($process.ExitCode)" }
}
try {
    $archive = Get-ChildItem $Packages -Filter '*win-x64-portable.zip' | Select-Object -First 1
    Expand-Archive $archive.FullName -DestinationPath $root
    $portable = Get-ChildItem $root -Directory -Filter '*win-x64-portable' | Select-Object -First 1
    Run-Daydrive (Join-Path $portable.FullName 'DAYDRIVE.exe') ''
    $portableDb = Join-Path $portable.FullName 'Data/intelligent-scheduler.db'
    if (-not (Test-Path $portableDb)) { throw 'Portable data was not created beside the executable' }
    if ((Get-Item $portableDb).Length -eq 0) { throw 'Portable database is empty' }
    if (Test-Path (Join-Path $root 'Data')) { throw 'Portable data leaked into working directory' }
    $installer = Get-ChildItem $Packages -Filter '*win-x64-setup.exe' | Select-Object -First 1
    $install = Join-Path $root 'Installed DAYDRIVE'
    $setup = Start-Process $installer.FullName -ArgumentList @('/S', "/D=$install") -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw 'Installer failed' }
    if (Test-Path (Join-Path $install 'daydrive.portable')) { throw 'Installer includes portable marker' }
    $installedData = Join-Path $root 'installed user data'
    Run-Daydrive (Join-Path $install 'DAYDRIVE.exe') $installedData
    if (-not (Test-Path (Join-Path $installedData 'intelligent-scheduler.db'))) { throw 'Installed startup failed' }
    # Uninstallation must preserve files it does not own and the user's database.
    Set-Content (Join-Path $install 'keep-my-file.txt') 'user file'
    $uninstall = Start-Process (Join-Path $install 'Uninstall.exe') -ArgumentList @('/S', "_?=$install") -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) { throw 'Uninstaller failed' }
    if (Test-Path (Join-Path $install 'DAYDRIVE.exe')) { throw 'Uninstaller left application executable' }
    if (-not (Test-Path (Join-Path $install 'keep-my-file.txt'))) { throw 'Uninstaller removed user file' }
    if (-not (Test-Path (Join-Path $installedData 'intelligent-scheduler.db'))) { throw 'Uninstaller removed user data' }
    Write-Host 'Windows portable, installer, database startup, and uninstall passed.'
} finally {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
