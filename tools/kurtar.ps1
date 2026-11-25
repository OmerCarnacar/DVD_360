# CD/DVD kurtarma araci (cizik diskler icin)
# Kullanim:  powershell -ExecutionPolicy Bypass -File kurtar.ps1 [-Source E:\] [-Dest klasor] [-Retries 5]
# Tekrar calistirilirsa sadece okunamayan sektorleri yeniden dener (kaldigi yerden devam eder).
param(
    [string]$Source = 'E:\',
    [string]$Dest = (Join-Path $PSScriptRoot 'Kurtarilan'),
    [int]$Retries = 4,
    [int]$BlockUnits = 32
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$logFile = Join-Path $Dest 'kurtarma_log.txt'

function Write-Log([string]$m) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $m
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

function Read-Range($fs, [long]$pos, [byte[]]$buf, [int]$count) {
    try {
        $fs.Position = $pos
        $total = 0
        while ($total -lt $count) {
            $n = $fs.Read($buf, $total, $count - $total)
            if ($n -le 0) { break }
            $total += $n
        }
        if ($total -ne $count) { return $false }
        return $true
    } catch { return $false }
}

function Save-State($path, $next, $bad) {
    $obj = @{ next = $next; bad = @($bad) }
    ($obj | ConvertTo-Json -Compress) | Set-Content -Path $path -Encoding ASCII
}

function Convert-CdxaToMpg([string]$datPath, [string]$mpgPath) {
    # VCD .DAT (RIFF/CDXA, 2352 bayt ham sektor) -> oynatilabilir .mpg
    $in = [System.IO.File]::OpenRead($datPath)
    $out = [System.IO.File]::Create($mpgPath)
    try {
        $in.Position = 44
        $sec = New-Object byte[] 2352
        while ($true) {
            $n = $in.Read($sec, 0, 2352)
            if ($n -lt 2352) { break }
            $out.Write($sec, 24, 2324)
        }
    } finally { $in.Close(); $out.Close() }
}

$summary = @()
$files = Get-ChildItem -Path $Source -Recurse -File -Force
Write-Log ("Kaynak: {0}  Hedef: {1}  Dosya sayisi: {2}" -f $Source, $Dest, $files.Count)

foreach ($f in $files) {
    $rel = $f.FullName.Substring($Source.Length).TrimStart('\')
    $outPath = Join-Path $Dest $rel
    $statePath = $outPath + '.kurtarma'
    New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
    [long]$len = $f.Length

    # Birim (sektor) duzeni
    $hdr = 0; $unit = 2048
    if ($len -ge 2396 -and (($len - 44) % 2352) -eq 0) { $hdr = 44; $unit = 2352 }
    if ($hdr -gt 0) { [long]$count = 1 + ($len - 44) / 2352 } else { [long]$count = [math]::Ceiling($len / 2048) }
    if ($count -lt 1) { $count = 1 }

    $unitStart = { param([long]$k) if ($hdr -gt 0) { if ($k -eq 0) { 0 } else { 44 + ($k - 1) * 2352 } } else { $k * 2048 } }
    $unitEnd   = { param([long]$k) [math]::Min($len, (& $unitStart ($k + 1))) }

    # Durum: tamamlanmis mi / yarim mi?
    $next = 0; $bad = New-Object System.Collections.Generic.List[long]
    if ((Test-Path $outPath) -and -not (Test-Path $statePath) -and (Get-Item $outPath).Length -eq $len) {
        Write-Log "TAMAM (onceden kurtarilmis): $rel"
        $summary += [pscustomobject]@{ Dosya = $rel; Boyut = $len; Bozuk_Sektor = 0; Yuzde = 100 }
        continue
    }
    if ((Test-Path $outPath) -and (Test-Path $statePath)) {
        $st = Get-Content $statePath -Raw | ConvertFrom-Json
        $next = [long]$st.next
        foreach ($b in $st.bad) { $bad.Add([long]$b) }
        Write-Log ("DEVAM: {0} (sektor {1}/{2}, bozuk {3})" -f $rel, $next, $count, $bad.Count)
    } else {
        Write-Log ("BASLIYOR: {0} ({1:N0} bayt, {2} sektor)" -f $rel, $len, $count)
    }

    $src = New-Object System.IO.FileStream($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite, 1)
    $dst = New-Object System.IO.FileStream($outPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
    try {
        $dst.SetLength($len)
        $bigBuf = New-Object byte[] ($unit * $BlockUnits)
        $zero = New-Object byte[] ($unit * $BlockUnits)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $iter = 0

        # 1. GECIS: hizli, buyuk bloklar. Okunamayan blok sifirla doldurulup sonra denenir.
        while ($next -lt $count) {
            $kEnd = [math]::Min($count, $next + $BlockUnits)
            [long]$a = & $unitStart $next
            [long]$b = & $unitEnd ($kEnd - 1)
            $n = [int]($b - $a)
            if (Read-Range $src $a $bigBuf $n) {
                $dst.Position = $a; $dst.Write($bigBuf, 0, $n)
            } else {
                $dst.Position = $a; $dst.Write($zero, 0, $n)
                for ($k = $next; $k -lt $kEnd; $k++) { $bad.Add($k) }
                Write-Log ("  okuma hatasi: sektor {0}-{1}" -f $next, ($kEnd - 1))
            }
            $next = $kEnd
            $iter++
            if ($iter % 100 -eq 0) {
                $dst.Flush(); Save-State $statePath $next $bad
                Write-Log ("  ilerleme {0}: %{1:N1}  bozuk aday: {2}" -f $rel, (100.0 * $next / $count), $bad.Count)
            }
        }
        $dst.Flush(); Save-State $statePath $next $bad

        # 2. GECIS: bozuk bloklari sektor sektor, tekrar tekrar dene.
        if ($bad.Count -gt 0) {
            Write-Log ("  2. gecis: {0} sektor tek tek deneniyor ({1} deneme)" -f $bad.Count, $Retries)
            $one = New-Object byte[] $unit
            $still = New-Object System.Collections.Generic.List[long]
            $i = 0
            foreach ($k in $bad) {
                [long]$a = & $unitStart $k
                $n = [int]((& $unitEnd $k) - $a)
                $ok = $false
                for ($t = 0; $t -lt $Retries -and -not $ok; $t++) {
                    if (Read-Range $src $a $one $n) { $ok = $true }
                }
                if ($ok) { $dst.Position = $a; $dst.Write($one, 0, $n) } else { $still.Add($k) }
                $i++
                if ($i % 50 -eq 0) {
                    $dst.Flush()
                    $rest = @($still) + @($bad | Select-Object -Skip $i)
                    Save-State $statePath $next $rest
                    Write-Log ("  2. gecis: {0}/{1}, kurtarilamayan: {2}" -f $i, $bad.Count, $still.Count)
                }
            }
            $bad = $still
        }
        $dst.Flush()
    } finally { $src.Close(); $dst.Close() }

    if ($bad.Count -eq 0) { Remove-Item $statePath -ErrorAction SilentlyContinue }
    else { Save-State $statePath $count $bad }
    $pct = [math]::Round(100.0 * ($count - $bad.Count) / $count, 3)
    Write-Log ("BITTI: {0}  kurtarilamayan sektor: {1}  (%{2} saglam)" -f $rel, $bad.Count, $pct)
    $summary += [pscustomobject]@{ Dosya = $rel; Boyut = $len; Bozuk_Sektor = $bad.Count; Yuzde = $pct }

    # VCD video ise oynatilabilir .mpg uret
    if ($hdr -gt 0 -and $rel -match '^(MPEGAV|SEGMENT)\\') {
        $vdir = Join-Path $Dest 'Videolar'
        New-Item -ItemType Directory -Force -Path $vdir | Out-Null
        $mpg = Join-Path $vdir ([IO.Path]::GetFileNameWithoutExtension($f.Name) + '.mpg')
        Convert-CdxaToMpg $outPath $mpg
        Write-Log "  video olusturuldu: $mpg"
    }
}

Write-Log '===== OZET ====='
$summary | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Log $_ }
Write-Log 'Kurtarilamayan sektor kaldiysa: diski temizleyip bu araci tekrar calistirin; sadece bozuk yerleri yeniden dener.'
