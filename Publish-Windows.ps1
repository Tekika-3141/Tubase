$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'Tubase.csproj'
$outputPath = Join-Path $PSScriptRoot 'portable\win-x64'

dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $outputPath 'Tubase.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published executable was not found: $executablePath"
}

Get-Item -LiteralPath $executablePath | Select-Object FullName, Length, LastWriteTime
