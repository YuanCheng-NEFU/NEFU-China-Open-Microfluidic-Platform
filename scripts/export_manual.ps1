param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$source = Join-Path $Root 'Manual\Build_and_Operation_Manual.docx'
$target = Join-Path $Root 'Manual\Build_and_Operation_Manual.pdf'
$word = $null
$document = $null
try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $document = $word.Documents.Open($source, $false, $true)
    $document.Repaginate()
    $document.ExportAsFixedFormat($target, 17)
    Write-Output $target
}
finally {
    if ($null -ne $document) {
        $document.Close(0)
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($document)
    }
    if ($null -ne $word) {
        $word.Quit()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($word)
    }
}
