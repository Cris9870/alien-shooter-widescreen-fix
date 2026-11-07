# Builds the DLL and the launcher and creates dist\AlienShooterWidescreenFix-windows.zip.
# Usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content "$root\VERSION" -Raw).Trim()

Write-Host "== d3d9.dll"
cmd /c "`"$root\upscaler\build.bat`""
if ($LASTEXITCODE) { throw "No se pudo compilar d3d9.dll" }

Write-Host "== launcher $version"
dotnet build "$root\launcher\AlienShooterLauncher.csproj" -c Release -nologo -p:Version=$version
if ($LASTEXITCODE) { throw "No se pudo compilar el lanzador" }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'AlienShooterWidescreenFix'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\instalacion-manual" | Out-Null

Copy-Item "$root\launcher\bin\Release\AlienShooterLauncher.exe" "$stage\Alien Shooter Launcher.exe"
Copy-Item "$root\LEEME.txt", "$root\LICENSE" $stage
Copy-Item "$root\upscaler\d3d9.dll", "$root\upscaler\d3d9_upscale.ini" "$stage\instalacion-manual"

$zip = Join-Path $dist 'AlienShooterWidescreenFix-windows.zip'
# Entries are written one by one with forward slashes: Compress-Archive and ZipFile in
# Windows PowerShell 5 write backslashes, which some unzip tools do not understand.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $name = $f.FullName.Substring($stage.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host "== $zip"
Write-Host "   SHA-256 $hash"
