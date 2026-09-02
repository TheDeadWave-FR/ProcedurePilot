$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePaths = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName
$outputDir = Join-Path $projectRoot 'dist'
$outputPath = Join-Path $outputDir 'ProcedurePilot.exe'
$manifestPath = Join-Path $projectRoot 'app.manifest'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Le compilateur .NET Framework est introuvable.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /debug- `
    /win32manifest:$manifestPath `
    /out:$outputPath `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Xml.Linq.dll `
    /reference:Microsoft.CSharp.dll `
    $sourcePaths

if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué (code $LASTEXITCODE)." }

Get-Item -LiteralPath $outputPath | Select-Object FullName, Length, LastWriteTime
