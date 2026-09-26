param([switch]$SkipTests, [switch]$DebugBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $localCargo = Join-Path $projectRoot '.tools\cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $localCargo) {
        $env:CARGO_HOME = Join-Path $projectRoot '.tools\cargo'
        $env:RUSTUP_HOME = Join-Path $projectRoot '.tools\rustup'
        $env:PATH = (Split-Path -Parent $localCargo) + ';' + $env:PATH
        $cargo = $localCargo
    } else {
        $cargo = (Get-Command cargo -ErrorAction Stop).Source
    }
    $targetArgs = @()
    $targetSubdir = ''
    $sysroot = & rustc --print sysroot
    $hostTriple = (& rustc -vV | Select-String '^host: ').ToString().Substring(6)
    if ($hostTriple -eq 'x86_64-pc-windows-gnu') {
        $bundledLinker = Join-Path $sysroot "lib\rustlib\$hostTriple\bin\self-contained"
        if (Test-Path -LiteralPath $bundledLinker) { $env:PATH = $bundledLinker + ';' + $env:PATH }
        $llvmFolder = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.tools') -Directory -Filter 'llvm-mingw-*-ucrt-x86_64' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($llvmFolder) {
            $llvmBin = Join-Path $llvmFolder.FullName 'bin'
            $env:PATH = $llvmBin + ';' + $env:PATH
            $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNULLVM_LINKER = Join-Path $llvmBin 'x86_64-w64-mingw32-clang.exe'
            # Build scripts run on the GNU host; the delivered binary uses LLVM/UCRT throughout.
            $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = Join-Path $bundledLinker 'x86_64-w64-mingw32-gcc.exe'
            $targetSubdir = 'x86_64-pc-windows-gnullvm'
            $targetArgs = @('--target', $targetSubdir)
        }
    }
    if (-not $SkipTests) {
        & $cargo test --locked @targetArgs
        if ($LASTEXITCODE -ne 0) { throw 'Les tests Rust ont échoué.' }
    }
    $profile = if ($DebugBuild) { 'debug' } else { 'release' }
    if ($DebugBuild) { & $cargo build --locked @targetArgs } else { & $cargo build --release --locked @targetArgs }
    if ($LASTEXITCODE -ne 0) { throw 'La compilation Rust a échoué.' }
    $builtExe = Join-Path $projectRoot "target\$targetSubdir\$profile\ProcedurePilot.exe"
    if ($targetSubdir -eq 'x86_64-pc-windows-gnullvm') {
        $imports = & (Join-Path $llvmBin 'llvm-objdump.exe') -p $builtExe
        if ($LASTEXITCODE -ne 0) { throw "Impossible de vérifier les dépendances de l’exécutable." }
        if ($imports -match 'DLL Name:\s*libunwind\.dll') {
            throw "L’exécutable dépend encore de libunwind.dll : vérifier la liaison statique."
        }
    }
    $dist = Join-Path $projectRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    Copy-Item -LiteralPath $builtExe -Destination (Join-Path $dist 'ProcedurePilot.exe') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $dist 'README.md') -Force
    $packageFiles = @((Join-Path $dist 'ProcedurePilot.exe'), (Join-Path $dist 'README.md'))
    if ($targetSubdir -eq 'x86_64-pc-windows-gnullvm') {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\LLVM-LICENSE.txt') -Destination $dist -Force
        $packageFiles += Join-Path $dist 'LLVM-LICENSE.txt'
    }
    # Remove the obsolete DLL left by earlier packages, after validating the new build.
    $oldUnwind = Join-Path $dist 'libunwind.dll'
    if (Test-Path -LiteralPath $oldUnwind) { Remove-Item -LiteralPath $oldUnwind }
    $zip = Join-Path $dist 'ProcedurePilot-rust-windows-x64-v2.0.0.zip'
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $zip -Force
    Get-Item -LiteralPath (Join-Path $dist 'ProcedurePilot.exe'),$zip | Select-Object FullName,Length
} finally { Pop-Location }
