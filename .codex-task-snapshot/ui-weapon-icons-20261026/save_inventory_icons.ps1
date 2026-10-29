$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = 'E:\github\RPG-2'
$taskDir = Join-Path $root '.codex-task-snapshot/ui-weapon-icons-20261026'
$targetDir = Join-Path $root 'Assets/UI素材/枪械'
$jobs = Get-Content -LiteralPath (Join-Path $taskDir 'inventory-files.json') -Raw | ConvertFrom-Json
foreach ($job in $jobs) {
    $targetPath = Join-Path $targetDir $job.filename
    $source = [System.Drawing.Bitmap]::FromFile($job.source)
    $target = [System.Drawing.Bitmap]::new(1024,512,[System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    $attrs = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $b = $job.visible_bbox
        $left = [Math]::Max(0,$b[0]-6); $top = [Math]::Max(0,$b[1]-6)
        $right = [Math]::Min($source.Width,$b[2]+6); $bottom = [Math]::Min($source.Height,$b[3]+6)
        $width = $right-$left; $height = $bottom-$top
        $scale = [Math]::Min(960.0/$width,448.0/$height)
        $drawWidth = [int][Math]::Round($width*$scale); $drawHeight = [int][Math]::Round($height*$scale)
        $rect = [System.Drawing.Rectangle]::new([int][Math]::Round((1024-$drawWidth)/2.0),[int][Math]::Round((512-$drawHeight)/2.0),$drawWidth,$drawHeight)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($source,$rect,[single]$left,[single]$top,[single]$width,[single]$height,[System.Drawing.GraphicsUnit]::Pixel,$attrs)
        $target.Save($targetPath,[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $attrs.Dispose(); $graphics.Dispose(); $target.Dispose(); $source.Dispose() }
    $metaPath = $targetPath+'.meta'
    $meta = (Get-Content -LiteralPath $metaPath -Raw).Replace('maxTextureSize: 256','maxTextureSize: 1024')
    [System.IO.File]::WriteAllText($metaPath,$meta,[System.Text.UTF8Encoding]::new($false))
}
$sheet = [System.Drawing.Bitmap]::new(1040,640,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$font = [System.Drawing.Font]::new('Segoe UI',11)
$labelFont = [System.Drawing.Font]::new('Microsoft YaHei',12)
$line = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(68,78,91),1)
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(31,38,48))
$names = @('木质步枪 · 卡通','金属步枪 · 卡通','木质步枪 · 写实','金属步枪 · 写实')
try {
    $g.Clear([System.Drawing.Color]::FromArgb(21,26,34))
    $g.DrawString('INVENTORY SPRITES | 4 x 2 CELLS | 256 px / CELL | 1024 x 512 px',$font,[System.Drawing.Brushes]::White,[single]16,[single]10)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    for ($i=0;$i -lt 4;$i++) {
        $x = 8+($i%2)*520; $y = 68+[Math]::Floor($i/2)*288
        $g.DrawString($names[$i],$labelFont,[System.Drawing.Brushes]::White,[single]($x+8),[single]($y-28))
        $g.FillRectangle($brush,[int]$x,[int]$y,512,256)
        for ($c=0;$c -le 4;$c++) { $g.DrawLine($line,[int]($x+$c*128),[int]$y,[int]($x+$c*128),[int]($y+256)) }
        for ($r=0;$r -le 2;$r++) { $g.DrawLine($line,[int]$x,[int]($y+$r*128),[int]($x+512),[int]($y+$r*128)) }
        $icon = [System.Drawing.Bitmap]::FromFile((Join-Path $targetDir $jobs[$i].filename))
        try { $g.DrawImage($icon,[int]$x,[int]$y,512,256) } finally { $icon.Dispose() }
    }
    $sheet.Save((Join-Path $taskDir 'inventory-preview.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally { $line.Dispose(); $brush.Dispose(); $font.Dispose(); $labelFont.Dispose(); $g.Dispose(); $sheet.Dispose() }
Get-ChildItem -LiteralPath $targetDir -Filter '*.png' | Select-Object Name,Length
