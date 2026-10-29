$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = 'E:\github\RPG-2'
$taskDir = Join-Path $root '.codex-task-snapshot/ui-weapon-icons-20261026'
$targetDir = Join-Path $root 'Assets/UI素材/枪械'
$jobs = Get-Content -LiteralPath (Join-Path $taskDir 'generated-files.json') -Raw | ConvertFrom-Json
foreach ($job in $jobs) {
    $targetPath = Join-Path $targetDir $job.filename
    if (Test-Path -LiteralPath $targetPath) { throw "Target already exists: $targetPath" }
    $source = [System.Drawing.Bitmap]::FromFile($job.source)
    $target = [System.Drawing.Bitmap]::new(256,256,[System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    $attrs = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $bounds = $job.visible_bbox
        $left = [Math]::Max(0,$bounds[0]-6)
        $top = [Math]::Max(0,$bounds[1]-6)
        $right = [Math]::Min($source.Width,$bounds[2]+6)
        $bottom = [Math]::Min($source.Height,$bounds[3]+6)
        $width = $right-$left
        $height = $bottom-$top
        $scale = 224.0 / [Math]::Max($width,$height)
        $drawWidth = [int][Math]::Round($width*$scale)
        $drawHeight = [int][Math]::Round($height*$scale)
        $rect = [System.Drawing.Rectangle]::new([int][Math]::Round((256-$drawWidth)/2.0),[int][Math]::Round((256-$drawHeight)/2.0),$drawWidth,$drawHeight)
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attrs.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage($source,$rect,[single]$left,[single]$top,[single]$width,[single]$height,[System.Drawing.GraphicsUnit]::Pixel,$attrs)
        $target.Save($targetPath,[System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $attrs.Dispose()
        $graphics.Dispose()
        $target.Dispose()
        $source.Dispose()
    }
    $templatePath = Join-Path $root 'Assets/CombatGirlsCharacterPack/UI_&_environmental/UI_Resource/UI_Tex/UI_category_W_01.png.meta'
    $meta = Get-Content -LiteralPath $templatePath -Raw
    $meta = [regex]::Replace($meta,'(?m)^guid: .+$',('guid: '+[guid]::NewGuid().ToString('N')))
    $meta = [regex]::Replace($meta,'(?m)^    spriteID: .+$',('    spriteID: '+[guid]::NewGuid().ToString('N')))
    $meta = $meta.Replace('maxTextureSize: 2048','maxTextureSize: 256').Replace('spritePixelsToUnits: 100','spritePixelsToUnits: 256').Replace('spriteGenerateFallbackPhysicsShape: 1','spriteGenerateFallbackPhysicsShape: 0').Replace('textureCompression: 1','textureCompression: 0').Replace('wrapW: 0','wrapW: 1')
    [System.IO.File]::WriteAllText(($targetPath+'.meta'),$meta,[System.Text.UTF8Encoding]::new($false))
}
foreach ($folder in @('Assets/UI素材','Assets/UI素材/枪械')) {
    $metaPath = Join-Path $root ($folder+'.meta')
    if (-not (Test-Path -LiteralPath $metaPath)) {
        $folderMeta = "fileFormatVersion: 2`nguid: $([guid]::NewGuid().ToString('N'))`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        [System.IO.File]::WriteAllText($metaPath,$folderMeta,[System.Text.UTF8Encoding]::new($false))
    }
}
foreach ($doc in @('README.md','生成提示词.json')) {
    $importer = if ($doc.EndsWith('.json')) { 'TextScriptImporter' } else { 'DefaultImporter' }
    $docMeta = "fileFormatVersion: 2`nguid: $([guid]::NewGuid().ToString('N'))`n${importer}:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    [System.IO.File]::WriteAllText((Join-Path $targetDir ($doc+'.meta')),$docMeta,[System.Text.UTF8Encoding]::new($false))
}
$sheet = [System.Drawing.Bitmap]::new(1024,576,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$font = [System.Drawing.Font]::new('Segoe UI',11,[System.Drawing.FontStyle]::Regular)
$labels = @('STYLIZED WOOD','STYLIZED METAL','REALISTIC WOOD','REALISTIC METAL')
try {
    $g.Clear([System.Drawing.Color]::FromArgb(24,29,36))
    for ($i=0;$i -lt 4;$i++) {
        $g.DrawString($labels[$i],$font,[System.Drawing.Brushes]::White,([single]($i*256+16)),[single]8)
        for ($y=0;$y -lt 256;$y+=16) {
            for ($x=0;$x -lt 256;$x+=16) {
                $dark = if ((($x/16+$y/16)%2) -eq 0) { 36 } else { 43 }
                $light = if ((($x/16+$y/16)%2) -eq 0) { 222 } else { 236 }
                $b1 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($dark,$dark+5,$dark+12))
                $b2 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($light,$light,$light))
                try { $g.FillRectangle($b1,$i*256+$x,32+$y,16,16); $g.FillRectangle($b2,$i*256+$x,304+$y,16,16) } finally { $b1.Dispose();$b2.Dispose() }
            }
        }
        $icon = [System.Drawing.Bitmap]::FromFile((Join-Path $targetDir $jobs[$i].filename))
        try { $g.DrawImageUnscaled($icon,$i*256,32);$g.DrawImageUnscaled($icon,$i*256,304) } finally { $icon.Dispose() }
    }
    $sheet.Save((Join-Path $taskDir 'weapon-icons-preview.png'),[System.Drawing.Imaging.ImageFormat]::Png)
} finally { $font.Dispose();$g.Dispose();$sheet.Dispose() }
Get-ChildItem -LiteralPath $targetDir -Filter '*.png' | Select-Object Name,Length
