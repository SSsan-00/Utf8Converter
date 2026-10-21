$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    dotnet publish src/Utf8Converter/Utf8Converter.csproj -c Release --self-contained false -o ./publish
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
    $fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('Utf8ConverterPerf-' + [Guid]::NewGuid().ToString('N'))
    $inputFolder = Join-Path $fixtureRoot 'project'
    [IO.Directory]::CreateDirectory($inputFolder) | Out-Null
    $encoding = [Text.Encoding]::GetEncoding(932, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
    $text = [string][char]0x65e5 + [char]0x672c + [char]0x8a9e
    $small = $encoding.GetBytes(($text + "`r`n") * 40)
    for ($i = 0; $i -lt 5000; $i++) {
        [IO.File]::WriteAllBytes((Join-Path $inputFolder ('file{0}.cs' -f $i)), $small)
    }
    $large = $encoding.GetBytes(($text + "`r`n") * 8192)
    $stream = [IO.File]::Create((Join-Path $inputFolder 'large.cs'))
    try { for ($i = 0; $i -lt 256; $i++) { $stream.Write($large, 0, $large.Length) } }
    finally { $stream.Dispose() }
    $stream = [IO.File]::Create((Join-Path $inputFolder 'asset.bin'))
    try { $stream.SetLength(32MB) } finally { $stream.Dispose() }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath (Join-Path $projectRoot 'publish/Utf8Converter.exe') -ArgumentList ('"' + $inputFolder + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixtureRoot 'stdout.txt') -RedirectStandardError (Join-Path $fixtureRoot 'stderr.txt')
    $null = $process.Handle
    $peak = 0L
    while (-not $process.WaitForExit(50)) {
        $process.Refresh()
        $peak = [Math]::Max($peak, $process.PeakWorkingSet64)
    }
    $timer.Stop()
    $process.Refresh()
    $peak = [Math]::Max($peak, $process.PeakWorkingSet64)
    [PSCustomObject]@{
        Files = 5002
        Seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
        PeakWorkingSetMB = [Math]::Round($peak / 1MB, 2)
        ExitCode = $process.ExitCode
        Fixture = $fixtureRoot
    } | Format-List
    if ($process.ExitCode -ne 0) { throw 'Performance run failed; inspect fixture logs.' }
} finally { Pop-Location }
