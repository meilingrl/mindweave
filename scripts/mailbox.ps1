<#
Mindweave mailbox, Windows side. Mac Claude and Windows Claude exchange messages through E:\mindweave-mailbox so the
user doesn't have to carry text between the machines. Mac Claude reaches the same folder over SSH (scripts/mailbox.sh).
Protocol: README.md in the mailbox folder. Windows PowerShell 5.1 compatible.

  mailbox.ps1 list                         messages waiting for Windows (to-windows\), oldest first
  mailbox.ps1 read <id>                    print one message (any folder)
  mailbox.ps1 send -Kind report -Slug full-suite-4ca799c [-Re <id>] [-Sha <sha>] (-BodyFile <path> | -Body <text>)
  mailbox.ps1 done <id>                    move a handled message out of to-windows\ into done\
  mailbox.ps1 status                       counts in each folder and the newest message each way
#>
param(
    [Parameter(Position = 0)][ValidateSet('list', 'read', 'send', 'done', 'status')][string]$Command = 'list',
    [Parameter(Position = 1)][string]$Id,
    [ValidateSet('task', 'report', 'question', 'answer', 'note')][string]$Kind = 'report',
    [string]$Slug = 'message',
    [string]$Re = '',
    [string]$Sha = '',
    [string]$Body,
    [string]$BodyFile
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Root = if ($env:MINDWEAVE_MAILBOX) { $env:MINDWEAVE_MAILBOX } else { 'E:\mindweave-mailbox' }
$Utf8 = New-Object Text.UTF8Encoding($false)
$Inbox = Join-Path $Root 'to-windows'
$Outbox = Join-Path $Root 'to-mac'
$Done = Join-Path $Root 'done'

function Header([string]$Path) {
    $h = [ordered]@{}
    foreach ($line in [IO.File]::ReadAllLines($Path, $Utf8)) {
        if ($line -eq '---') { if ($h.Count) { break } else { continue } }
        if ($line -match '^([a-z]+):\s*(.*)$') { $h[$Matches[1]] = $Matches[2] }
    }
    $h
}

# Finished messages only: a writer creates ".<id>.tmp" and renames it, so a half-written file is never listed.
function Messages([string]$Dir) { Get-ChildItem -Path $Dir -Filter '*.md' -File | Where-Object { $_.Name -notlike '.*' } | Sort-Object Name }

function Find([string]$MessageId) {
    foreach ($dir in $Inbox, $Outbox, $Done) {
        $p = Join-Path $dir "$MessageId.md"
        if (Test-Path $p) { return $p }
    }
    throw "no message $MessageId"
}

switch ($Command) {
    'list' {
        $items = @(Messages $Inbox)
        if (-not $items.Count) { 'to-windows is empty'; break }
        foreach ($f in $items) { $h = Header $f.FullName; '{0}  [{1}]  re={2}  sha={3}' -f $f.BaseName, $h.kind, $h.re, $h.sha }
    }
    'read' { [IO.File]::ReadAllText((Find $Id), $Utf8) }
    'send' {
        if ($BodyFile) { $text = [IO.File]::ReadAllText((Resolve-Path $BodyFile), $Utf8) } elseif ($Body) { $text = $Body } else { throw 'give -Body or -BodyFile' }
        $clean = ($Slug.ToLowerInvariant() -replace '[^a-z0-9-]+', '-').Trim('-')
        if (-not $clean) { $clean = 'message' }
        if ($clean.Length -gt 40) { $clean = $clean.Substring(0, 40).Trim('-') }
        $newId = '{0}-w-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $clean
        $content = "---`nid: $newId`nfrom: windows-claude`nto: mac-claude`nkind: $Kind`nre: $Re`nsha: $Sha`ncreated: $((Get-Date).ToString('o'))`n---`n`n$($text.TrimEnd())`n"
        $tmp = Join-Path $Outbox ".$newId.tmp"
        [IO.File]::WriteAllText($tmp, ($content -replace "`r`n", "`n"), $Utf8)
        Move-Item -LiteralPath $tmp -Destination (Join-Path $Outbox "$newId.md")
        "sent $newId"
    }
    'done' {
        $p = Join-Path $Inbox "$Id.md"
        if (-not (Test-Path $p)) { throw "$Id is not in to-windows" }
        Move-Item -LiteralPath $p -Destination (Join-Path $Done "$Id.md")
        "moved $Id to done"
    }
    'status' {
        foreach ($dir in $Inbox, $Outbox, $Done) {
            $items = @(Messages $dir)
            $last = if ($items.Count) { $items[-1].BaseName } else { '-' }
            '{0,-11} {1,3}  newest: {2}' -f (Split-Path $dir -Leaf), $items.Count, $last
        }
    }
}
