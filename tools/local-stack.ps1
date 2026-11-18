<#
.SYNOPSIS
  Drive the local dev stack (docker-compose.yml + docker-compose.local.yml, settings in .env.local).
.EXAMPLE
  tools/local-stack.ps1 up        # build from this working tree and start
  tools/local-stack.ps1 logs api  # follow one service
  tools/local-stack.ps1 reset     # stop and DELETE the local database
#>
param(
  [Parameter(Mandatory, Position = 0)]
  [ValidateSet('up', 'down', 'restart', 'logs', 'ps', 'psql', 'reset')]
  [string]$Action,
  [Parameter(Position = 1, ValueFromRemainingArguments)]
  [string[]]$Rest
)
# Compose writes progress to stderr; judge success by exit code, not by stderr output.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not (Test-Path .env.local)) {
  Copy-Item .env.local.example .env.local
  Write-Host 'Created .env.local from the example. Add the dev Discord credentials to it for the bot and sign-in.'
}
$dc = @('compose', '--env-file', '.env.local', '-f', 'docker-compose.yml', '-f', 'docker-compose.local.yml')
switch ($Action) {
  'up'      { docker @dc up -d --build --wait @Rest; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; docker @dc ps }
  'down'    { docker @dc down @Rest }
  'restart' { docker @dc restart @Rest }
  'logs'    { docker @dc logs -f --tail 100 @Rest }
  'ps'      { docker @dc ps }
  'psql'    { docker @dc exec postgres psql -U calcrony -d calcrony }
  'reset'   {
    $answer = Read-Host 'This deletes the local dev database and Data Protection keys. Type yes to continue'
    if ($answer -eq 'yes') { docker @dc down -v }
  }
}
