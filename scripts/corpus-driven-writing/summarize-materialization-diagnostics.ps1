#Requires -Version 7.0
<#
.SYNOPSIS
    汇总材料化埋点（M0）输出的 materialization-diagnostics.jsonl，
    直接回答提速方案 §4.2 的三个 gate 问题。

.DESCRIPTION
    跑完一本真实书后执行本脚本，得到各阶段耗时占比，从而判断
    M2（去规划调用）/ M3（趟数与批大小）/ M4（SQLite WAL）各自值不值得做。

.PARAMETER Path
    诊断文件路径。默认在常见数据目录下查找 reference-anchor/materialization-diagnostics.jsonl。

.PARAMETER RunId
    只统计指定 run。默认统计全部（同一份文件里通常只有最近一次 run，
    但重跑会追加，故提供过滤）。

.PARAMETER Top
    输出耗时最高的前 N 章，便于定位异常章节。默认 5，0 表示不输出。

.EXAMPLE
    ./summarize-materialization-diagnostics.ps1

.EXAMPLE
    ./summarize-materialization-diagnostics.ps1 -RunId "mat-xxxx" -Top 10
#>
param(
    [string]$Path,
    [string]$RunId,
    [int]$Top = 5
)

$ErrorActionPreference = "Stop"

function Resolve-DiagnosticsPath {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "找不到诊断文件: $Explicit"
        }
        return $Explicit
    }

    $candidates = @()
    if ($env:APPDATA) {
        $candidates += (Join-Path $env:APPDATA "Novelist/reference-anchor/materialization-diagnostics.jsonl")
    }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Novelist/reference-anchor/materialization-diagnostics.jsonl")
    }
    $candidates += (Join-Path $HOME ".novelist/reference-anchor/materialization-diagnostics.jsonl")

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw @"
未找到诊断文件。请用 -Path 指定，例如：
  ./summarize-materialization-diagnostics.ps1 -Path "$env:APPDATA\Novelist\reference-anchor\materialization-diagnostics.jsonl"
诊断文件由材料化运行时逐章追加写入（语料库同目录）。
"@
}

function Get-Percentile {
    param([double[]]$Values, [double]$P)
    if ($Values.Count -eq 0) { return 0 }
    $sorted = $Values | Sort-Object
    $index = [math]::Min($sorted.Count - 1, [math]::Max(0, [math]::Ceiling($P * $sorted.Count) - 1))
    return $sorted[$index]
}

$resolved = Resolve-DiagnosticsPath $Path
$rows = @()
$skipped = 0
foreach ($line in [System.IO.File]::ReadLines($resolved)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $rows += ($line | ConvertFrom-Json)
    }
    catch {
        $skipped++
    }
}

if ($rows.Count -eq 0) {
    throw "诊断文件里没有可解析的行: $resolved"
}

if ($RunId) {
    $rows = @($rows | Where-Object { $_.run_id -eq $RunId })
    if ($rows.Count -eq 0) { throw "run_id=$RunId 在诊断文件里没有记录。" }
}

$sum = [double]($rows | Measure-Object -Property total_ms -Sum).Sum
$plan = [double]($rows | Measure-Object -Property plan_ms -Sum).Sum
$round = [double]($rows | Measure-Object -Property round_ms -Sum).Sum
$qualify = [double]($rows | Measure-Object -Property qualify_ms -Sum).Sum
$embed = [double]($rows | Measure-Object -Property embed_ms -Sum).Sum
$index = [double]($rows | Measure-Object -Property index_ms -Sum).Sum
$other = [double]($rows | Measure-Object -Property other_ms -Sum).Sum
$calls = [double]($rows | Measure-Object -Property model_call_count -Sum).Sum
$rounds = [double]($rows | Measure-Object -Property round_count -Sum).Sum

function Get-Share([double]$Value) {
    if ($sum -le 0) { return 0 }
    return [math]::Round(100 * $Value / $sum, 1)
}

$runIds = @($rows | ForEach-Object { $_.run_id } | Select-Object -Unique)
$perChapter = @($rows | ForEach-Object { [double]$_.total_ms } | Sort-Object)

Write-Output ""
Write-Output "=== 材料化阶段耗时汇总 ==="
Write-Output ("来源         {0}" -f $resolved)
Write-Output ("样本         {0} 章    run: {1}" -f $rows.Count, ($runIds -join ", "))
Write-Output ("总墙钟       {0}" -f ([TimeSpan]::FromMilliseconds($sum).ToString("hh\:mm\:ss")))
Write-Output ("章耗时 p50/p95/max  {0:N0} / {1:N0} / {2:N0} ms" -f (Get-Percentile $perChapter 0.50), (Get-Percentile $perChapter 0.95), ($perChapter | Measure-Object -Maximum).Maximum)
if ($skipped -gt 0) { Write-Output ("（跳过 {0} 行无法解析的记录）" -f $skipped) }

Write-Output ""
Write-Output "阶段占比（占 total_ms）:"
foreach ($entry in @(
    @{ Name = "plan    (规划调用)"; Value = $plan },
    @{ Name = "round   (提取趟次)"; Value = $round },
    @{ Name = "qualify (判定)"; Value = $qualify },
    @{ Name = "embed   (嵌入)"; Value = $embed },
    @{ Name = "index   (索引)"; Value = $index },
    @{ Name = "other   (DB 等, 差值近似)"; Value = $other }
)) {
    $share = Get-Share $entry.Value
    $bar = "#" * [math]::Min(40, [math]::Round($share / 2))
    Write-Output ("  {0,-30} {1,5:N1}%  {2}" -f $entry.Name, $share, $bar)
}

$avgRounds = if ($rows.Count -gt 0) { [math]::Round($rounds / $rows.Count, 2) } else { 0 }
$callPerRound = if ($rounds -gt 0) { [math]::Round($calls / $rounds, 2) } else { 0 }
$planShare = Get-Share $plan
$ioShare = [math]::Round((Get-Share $other) + (Get-Share $index), 1)

Write-Output ""
Write-Output "---- Gate 判定（方案 4.2）----"
$m2 = if ($planShare -ge 8) { "做" } else { "不做" }
$m4 = if ($ioShare -gt 10) { "做" } else { "不做" }
Write-Output ("[M2 去规划]    plan 占比 {0,5:N1}%   (门槛 >= 8%)          => {1}" -f $planShare, $m2)
Write-Output ("[M3 判定批]    平均 {0} 趟/章，model_call/趟 = {1}" -f $avgRounds, $callPerRound)
Write-Output ("               该比值 > 1 说明发生了掐流续跑，M3-a 提批大小要谨慎")
Write-Output ("[M4 WAL]       other+index 占比 {0,5:N1}%   (门槛 > 10%)        => {1}" -f $ioShare, $m4)

if ($Top -gt 0) {
    Write-Output ""
    Write-Output ("最慢的 {0} 章:" -f [math]::Min($Top, $rows.Count))
    $rows | Sort-Object -Property { [double]$_.total_ms } -Descending | Select-Object -First $Top | ForEach-Object {
        Write-Output ("  章 {0,-5} total={1,8:N0}ms  round={2,8:N0}ms({3} 趟)  plan={4,7:N0}ms  qualify={5,8:N0}ms" -f `
            $_.chapter_index, [double]$_.total_ms, [double]$_.round_ms, $_.round_count, [double]$_.plan_ms, [double]$_.qualify_ms)
    }
}

Write-Output ""
