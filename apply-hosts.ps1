$ErrorActionPreference = "Continue"
Stop-Process -Name "GitHub自动刷新" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

$out = "$env:TEMP\gh-apply-result.txt"
$hostsPath = "C:\Windows\System32\drivers\etc\hosts"
$candidates = @(
  "20.201.28.151","20.201.28.150","140.82.112.4","140.82.113.4",
  "140.82.114.3","140.82.114.4","140.82.121.3","140.82.121.4",
  "20.87.245.0","20.200.245.247","20.207.73.82","20.205.243.166",
  "20.27.177.113","20.233.83.145","4.208.26.197"
)
$apiIp = "140.82.112.6"
$rawIp = "185.199.108.133"

function Test-Ip([string]$ip) {
  $gitB = (& curl.exe -k -sS --http1.1 --connect-timeout 2 --max-time 4 -A "git/2.22.0" `
    --resolve "github.com:443:$ip" "https://github.com/git/git.git/info/refs?service=git-upload-pack" 2>$null | Out-String)
  if ($gitB -match "service=git-upload-pack") { return $true }
  $htmlLen = (& curl.exe -k -sS --http1.1 --connect-timeout 2 --max-time 4 -A "Mozilla/5.0" `
    --resolve "github.com:443:$ip" "https://github.com/" 2>$null | Out-String).Length
  return ($htmlLen -gt 5000)
}

function Set-Hosts([string]$ip) {
  $text = [System.IO.File]::ReadAllText($hostsPath)
  if ($text -match "(?s)# BEGIN GITHUB FIX.*?# END GITHUB FIX\r?\n?") {
    $text = [regex]::Replace($text, "(?s)# BEGIN GITHUB FIX.*?# END GITHUB FIX\r?\n?", "")
  }
  $lines = @(
    "# BEGIN GITHUB FIX",
    "$ip github.com",
    "$ip www.github.com",
    "$apiIp api.github.com",
    "$ip codeload.github.com",
    "$rawIp raw.githubusercontent.com",
    "185.199.109.215 github.githubassets.com",
    "$rawIp objects.githubusercontent.com",
    "$rawIp avatars.githubusercontent.com",
    "$rawIp avatars0.githubusercontent.com",
    "$rawIp avatars1.githubusercontent.com",
    "$rawIp avatars2.githubusercontent.com",
    "$rawIp avatars3.githubusercontent.com",
    "$rawIp avatars4.githubusercontent.com",
    "$rawIp avatars5.githubusercontent.com",
    "$rawIp gist.githubusercontent.com",
    "$rawIp user-images.githubusercontent.com",
    "$rawIp media.githubusercontent.com",
    "$rawIp camo.githubusercontent.com",
    "$rawIp cloud.githubusercontent.com",
    "$rawIp private-user-images.githubusercontent.com",
    "# END GITHUB FIX"
  )
  $new = $text.TrimEnd() + "`r`n`r`n" + ($lines -join "`r`n") + "`r`n"
  [System.IO.File]::WriteAllText($hostsPath, $new, (New-Object System.Text.UTF8Encoding $false))
  ipconfig /flushdns | Out-Null
}

$chosen = $null
foreach ($round in 1..3) {
  foreach ($ip in $candidates) {
    if (Test-Ip $ip) { $chosen = $ip; break }
  }
  if ($chosen) { break }
  Start-Sleep -Seconds 2
}

if (-not $chosen) {
  Set-Content $out "NO_IP_FOUND" -Encoding UTF8
  return
}

Set-Hosts $chosen
Start-Sleep -Milliseconds 300

$w = & curl.exe -sS -o NUL -w "%{http_code}" --http1.1 --connect-timeout 5 --max-time 10 https://github.com/ 2>$null
$a = & curl.exe -sS -o NUL -w "%{http_code}" --http1.1 --connect-timeout 5 --max-time 10 https://api.github.com/ 2>$null
$g = (& curl.exe -sS --http1.1 --connect-timeout 5 --max-time 10 -A "git/2.22.0" "https://github.com/git/git.git/info/refs?service=git-upload-pack" 2>$null | Out-String)
$dns = (Resolve-DnsName github.com -Type A -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
$msg = "ip=$chosen web=$w api=$a git=$([bool]($g -match 'service=git-upload-pack')) dns=$dns"
Set-Content $out $msg -Encoding UTF8
