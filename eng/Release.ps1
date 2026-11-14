param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ReleaseNotes
)
$ErrorActionPreference = 'Stop'
$notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet build tools/Linguacode.Release -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release tool build failed.' }
    # dotnet run encloses children in a job which blocks detached audio-service tests.
    dotnet tools/Linguacode.Release/bin/Release/net10.0/Linguacode.Release.dll $Version $notesPath
    if ($LASTEXITCODE -ne 0) { throw 'Release preparation failed. See the output above.' }
}
finally { Pop-Location }
