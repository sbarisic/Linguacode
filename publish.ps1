param([string]$Output = (Join-Path $PSScriptRoot 'artifacts/linguacode-win-x64'))
$ErrorActionPreference = 'Stop'
$destination = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'src/Linguacode.Desktop/Linguacode.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $destination -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
$desktopToolsHash = (Get-FileHash -LiteralPath (Join-Path $destination 'Linguacode.Audio.dll')).Hash
& dotnet publish (Join-Path $PSScriptRoot 'src/Linguacode.AudioHost/Linguacode.AudioHost.csproj') -c Release -r win-x64 --self-contained true -o $destination -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Audio host publish failed.' }
if ((Get-FileHash -LiteralPath (Join-Path $destination 'Linguacode.Audio.dll')).Hash -ne $desktopToolsHash) { throw 'GUI and service builds differ; publish from a stable source checkout.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CALL_RECORDER.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CALL_RECORDER_ACCEPTANCE.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $destination
Write-Output (Join-Path $destination 'Linguacode.exe')
