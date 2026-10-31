# Postet einen Text-Post auf Reddit via OAuth (Script-App).
# Zugangsdaten bleiben lokal: Client-ID/Secret per Parameter oder Umgebungsvariable,
# das Passwort wird interaktiv abgefragt und nie gespeichert.
#
# Einmalige Vorbereitung:
#   https://www.reddit.com/prefs/apps  ->  "create another app..."
#   type: script   |   redirect uri: http://localhost:8080
#   -> client id steht direkt unter dem App-Namen, client secret daneben
#
# Aufruf:
#   .\post_reddit.ps1 -Subreddit csharp -Username DEIN_NAME -ClientId "..." -ClientSecret "..."

param(
    [Parameter(Mandatory = $true)][string]$Subreddit,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$ClientId,
    [Parameter(Mandatory = $true)][string]$ClientSecret,
    [string]$Title = "Show r/csharp: I built a WPF/.NET 8 gaming optimizer with a custom motion system - what I learned",
    [string]$BodyFile = "$PSScriptRoot\..\docs\reddit-post.md",
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $BodyFile)) { throw "Body-Datei fehlt: $BodyFile" }
$body = Get-Content $BodyFile -Raw

$secure = Read-Host -Prompt "Reddit-Passwort für $Username (wird nicht gespeichert)" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
$password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

$ua = "windows:nexusoptimizer-poster:1.0 (by /u/$Username)"

Write-Host "1/3 Token anfordern ..."
$auth = Invoke-RestMethod -Method Post -Uri "https://www.reddit.com/api/v1/access_token" `
    -UserAgent $ua `
    -Authentication Basic `
    -Credential ([pscredential]::new($ClientId, (ConvertTo-SecureString $ClientSecret -AsPlainText -Force))) `
    -Body @{ grant_type = 'password'; username = $Username; password = $password }

if (-not $auth.access_token) { throw "Kein Token erhalten: $($auth | ConvertTo-Json -Compress)" }
$password = $null

$headers = @{ Authorization = "bearer $($auth.access_token)"; 'User-Agent' = $ua }

Write-Host "2/3 Konto prüfen ..."
$me = Invoke-RestMethod -Uri "https://oauth.reddit.com/api/v1/me" -Headers $headers
Write-Host "    eingeloggt als: $($me.name)"

if ($DryRun) {
    Write-Host "3/3 DryRun - nichts gepostet."
    Write-Host "Titel: $Title"
    Write-Host "Body-Zeichen: $($body.Length)"
    return
}

Write-Host "3/3 Post an r/$Subreddit senden ..."
$result = Invoke-RestMethod -Method Post -Uri "https://oauth.reddit.com/api/submit" -Headers $headers -Body @{
    sr       = $Subreddit
    kind     = 'self'
    title    = $Title
    text     = $body
    api_type = 'json'
}

if ($result.json.errors -and $result.json.errors.Count -gt 0) {
    Write-Host "Reddit meldet Fehler:" -ForegroundColor Red
    $result.json.errors | ForEach-Object { Write-Host ("  " + ($_ -join ': ')) }
    exit 1
}

$url = $result.json.data.url
Write-Host "Fertig: https://www.reddit.com$url" -ForegroundColor Green
