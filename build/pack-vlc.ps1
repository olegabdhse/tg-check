# Упаковывает нужную часть VLC (x64) в zip, который встраивается в exe и распаковывается при первом видео.
# Берём только то, что нужно для проигрывания локальных файлов: ядро, контейнеры, декодеры, вывод звука/картинки.
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$OutFile)
$ErrorActionPreference = 'Stop'

$stage = Join-Path ([IO.Path]::GetTempPath()) ("tgcheck-vlc-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    $include = @(
        'libvlc.dll', 'libvlccore.dll',
        'plugins\demux\*', 'plugins\packetizer\*', 'plugins\stream_filter\*',
        'plugins\audio_output\*', 'plugins\audio_mixer\*', 'plugins\audio_filter\*',
        'plugins\video_chroma\*', 'plugins\video_filter\*', 'plugins\video_output\*',
        'plugins\d3d11\*', 'plugins\d3d9\*',
        'plugins\access\libfilesystem_plugin.dll', 'plugins\access\libimem_plugin.dll',
        'plugins\codec\libavcodec_plugin.dll', 'plugins\codec\libd3d11va_plugin.dll', 'plugins\codec\libdxva2_plugin.dll',
        'plugins\codec\libdav1d_plugin.dll', 'plugins\codec\libvpx_plugin.dll', 'plugins\codec\libopus_plugin.dll',
        'plugins\codec\libvorbis_plugin.dll', 'plugins\codec\libflac_plugin.dll', 'plugins\codec\libmpg123_plugin.dll',
        'plugins\codec\libaraw_plugin.dll', 'plugins\codec\liblpcm_plugin.dll', 'plugins\codec\libadpcm_plugin.dll',
        'plugins\codec\libg711_plugin.dll', 'plugins\codec\libspeex_plugin.dll', 'plugins\codec\libtheora_plugin.dll'
    )
    foreach ($pattern in $include) {
        foreach ($file in Get-ChildItem (Join-Path $Source $pattern) -File) {
            $rel = $file.FullName.Substring($Source.TrimEnd('\').Length + 1)
            $dest = Join-Path $stage $rel
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item $file.FullName $dest
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $OutFile, [IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
