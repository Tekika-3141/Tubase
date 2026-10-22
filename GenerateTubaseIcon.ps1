Add-Type -AssemblyName System.Drawing
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $margin = [single]($size * 0.05)
    $rect = [System.Drawing.RectangleF]::new($margin, $margin, [single]($size - 2 * $margin), [single]($size - 2 * $margin))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [single]($size * 0.25)
    $path.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, [System.Drawing.Color]::FromArgb(255, 36, 47, 60), [System.Drawing.Color]::FromArgb(255, 13, 23, 29), 135)
    $g.FillPath($bg, $path)
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 61, 89, 96), [single]([Math]::Max(1, $size * 0.018)))
    $g.DrawPath($outline, $path)
    $white = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 233, 241, 241), [single]([Math]::Max(2, $size * 0.115)))
    $white.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $white.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLine($white, [single]($size * .34), [single]($size * .31), [single]($size * .66), [single]($size * .31))
    $g.DrawLine($white, [single]($size * .50), [single]($size * .31), [single]($size * .50), [single]($size * .65))
    $mint = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 103, 223, 190), [single]([Math]::Max(1.4, $size * .045)))
    $mint.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $mint.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $mint.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]($size*.20), [single]($size*.72)),
        [System.Drawing.PointF]::new([single]($size*.32), [single]($size*.72)),
        [System.Drawing.PointF]::new([single]($size*.39), [single]($size*.63)),
        [System.Drawing.PointF]::new([single]($size*.47), [single]($size*.81)),
        [System.Drawing.PointF]::new([single]($size*.55), [single]($size*.64)),
        [System.Drawing.PointF]::new([single]($size*.63), [single]($size*.77)),
        [System.Drawing.PointF]::new([single]($size*.70), [single]($size*.71)),
        [System.Drawing.PointF]::new([single]($size*.80), [single]($size*.71))
    )
    $g.DrawLines($mint, $points)
    $png = [System.IO.MemoryStream]::new()
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($png.ToArray())
    $png.Dispose(); $g.Dispose(); $bmp.Dispose(); $bg.Dispose(); $white.Dispose(); $mint.Dispose(); $outline.Dispose(); $path.Dispose()
}

$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { [byte]0 } else { [byte]$sizes[$i] }
    $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32); $writer.Write([UInt32]$frames[$i].Length)
    $writer.Write([UInt32]$offset); $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'Assets/Tubase.ico'), $output.ToArray())
$writer.Dispose(); $output.Dispose()
Write-Output 'Generated Assets/Tubase.ico'
