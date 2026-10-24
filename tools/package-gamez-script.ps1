param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$extension = Join-Path $repository 'vscode/gamez-script'
Push-Location $repository
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $repository 'artifacts'))
    if (!$SkipBuild) {
        dotnet publish src/zStudio.LanguageServer/zStudio.LanguageServer.csproj -c Release -r win-x64 -p:RestoreLockedMode=true -o (Join-Path $extension 'server')
        if ($LASTEXITCODE -ne 0) { throw 'Language server publish failed.' }
    }
    Copy-Item -LiteralPath LICENSE -Destination (Join-Path $extension 'LICENSE')
    Push-Location $extension
    try {
        npm ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Extension dependency restore failed.' }
        npm run compile
        if ($LASTEXITCODE -ne 0) { throw 'Extension compile failed.' }
        npm test
        if ($LASTEXITCODE -ne 0) { throw 'Extension validation failed.' }
        npm run package
        if ($LASTEXITCODE -ne 0) { throw 'VSIX packaging failed.' }
    } finally { Pop-Location }
    $package = Join-Path $repository 'artifacts/gamez-script-0.9.8-win32-x64.vsix'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach ($required in @('extension/package.json','extension/dist/extension.js','extension/server/zStudio.LanguageServer.exe','extension/LICENSE.txt','extension/THIRD-PARTY-NOTICES.md','extension/readme.md')) {
            if (!$zip.GetEntry($required)) { throw "VSIX is missing $required" }
        }
        if (@($zip.Entries | Where-Object { $_.FullName -match '(^|/)(node_modules|data|gamegen|test|src)/' }).Count) { throw 'VSIX contains development files or project data.' }
    } finally { $zip.Dispose() }
    Get-FileHash -LiteralPath $package -Algorithm SHA256
} finally { Pop-Location }
