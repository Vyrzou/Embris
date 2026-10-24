# Builds Embris-Mac.zip and Embris-Linux.zip (plus the unzipped folders) next to build.bat.
# Zips are written by hand so installers keep their Unix "executable" permission when unzipped.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-UnixZip($folder, $zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $name = Split-Path -Leaf $folder
    $stream = [IO.File]::Open($zipPath, 'CreateNew')
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in Get-ChildItem $folder -File) {
        $entry = $zip.CreateEntry("$name/$($file.Name)", [IO.Compression.CompressionLevel]::Optimal)
        $exec = $file.Extension -in '.command', '.sh'
        $mode = if ($exec) { 0x81ED } else { 0x81A4 } # regular file, rwxr-xr-x / rw-r--r--
        $entry.ExternalAttributes = $mode -shl 16
        $out = $entry.Open(); $bytes = [IO.File]::ReadAllBytes($file.FullName); $out.Write($bytes, 0, $bytes.Length); $out.Dispose()
    }
    $zip.Dispose(); $stream.Dispose()

    # Mark every central-directory entry as "made by Unix" so macOS/Linux honour the permissions.
    # Walk the directory from the end-of-central-directory record rather than scanning, so file data is never touched.
    $data = [IO.File]::ReadAllBytes($zipPath)
    $eocd = $data.Length - 22
    while ($eocd -ge 0 -and [BitConverter]::ToUInt32($data, $eocd) -ne 0x06054B50) { $eocd-- }
    if ($eocd -lt 0) { throw "No end of central directory in $zipPath" }
    $count = [BitConverter]::ToUInt16($data, $eocd + 10)
    $pos = [int][BitConverter]::ToUInt32($data, $eocd + 16)
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($data, $pos) -ne 0x02014B50) { throw "Bad central directory entry in $zipPath" }
        $data[$pos + 5] = 3
        $pos += 46 + [BitConverter]::ToUInt16($data, $pos + 28) + [BitConverter]::ToUInt16($data, $pos + 30) + [BitConverter]::ToUInt16($data, $pos + 32)
    }
    [IO.File]::WriteAllBytes($zipPath, $data)
}

$targets = @{
    'Embris-Mac'   = @("$src\mac\Install Embris.command", "$src\mac\Uninstall Embris.command", "$src\mac\How to install.txt", "$src\assets\embris.icns")
    'Embris-Linux' = @("$src\linux\install.sh", "$src\linux\uninstall.sh", "$src\linux\How to install.txt")
}
foreach ($name in $targets.Keys) {
    $dir = Join-Path $root $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse }
    New-Item -ItemType Directory $dir | Out-Null
    Copy-Item "$src\embris.py", "$src\embris_core.py", "$src\emb_script.py", "$src\backgrounds.py", "$src\timeline.py", "$src\editors.py", "$src\overlay.py", "$src\assets\embris.png" $dir
    Copy-Item $targets[$name] $dir
    New-UnixZip $dir (Join-Path $root "$name.zip")
    Write-Host "Built $name.zip"
}
