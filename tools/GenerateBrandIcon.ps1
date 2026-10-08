# Renders the user-supplied vector brand into native Windows executable and
# desktop shortcut icon sizes. Runs before dotnet build (Windows WPF required).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
[xml]$svg = Get-Content -LiteralPath "assets/signal-brand.svg" -Raw -Encoding UTF8
$paths = @($svg.svg.path)
if ($paths.Count -ne 2) { throw "Expected two vector brand paths" }
$black = [System.Windows.Media.Geometry]::Parse($paths[0].d)
$white = [System.Windows.Media.Geometry]::Parse($paths[1].d)
$files = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
  $visual = [System.Windows.Media.DrawingVisual]::new()
  $ctx = $visual.RenderOpen()
  $scale = [double]$size / 122.88
  $ctx.PushTransform([System.Windows.Media.ScaleTransform]::new($scale,$scale))
  $ctx.DrawGeometry([System.Windows.Media.Brushes]::Black,$null,$black)
  $ctx.DrawGeometry([System.Windows.Media.Brushes]::White,$null,$white)
  $ctx.Pop()
  $ctx.Close()
  $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
    $size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
  $target.Render($visual)
  $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
  $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))
  $ms = [System.IO.MemoryStream]::new()
  $encoder.Save($ms)
  $files += ,@{ Size=$size; Bytes=$ms.ToArray() }
  if ($size -eq 256) {
    [System.IO.File]::WriteAllBytes(
      (Join-Path $pwd "assets/signal-brand.png"), $ms.ToArray())
  }
  $ms.Dispose()
}
$stream = [System.IO.File]::Create((Join-Path $pwd "assets/signal-brand.ico"))
try {
  $writer = [System.IO.BinaryWriter]::new($stream)
  $writer.Write([ushort]0)
  $writer.Write([ushort]1)
  $writer.Write([ushort]$files.Count)
  $offset = 6 + 16 * $files.Count
  foreach ($icon in $files) {
    $writer.Write([byte]($icon.Size % 256))
    $writer.Write([byte]($icon.Size % 256))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([ushort]1)
    $writer.Write([ushort]32)
    $writer.Write([uint32]$icon.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $icon.Bytes.Length
  }
  foreach ($icon in $files) { $writer.Write([byte[]]$icon.Bytes) }
  $writer.Flush()
} finally { $stream.Dispose() }
if ((Get-Item "assets/signal-brand.ico").Length -lt 1000) {
  throw "Generated icon is unexpectedly small"
}
Write-Output "Generated native EXE / shortcut ICO + WPF window PNG from Signal SVG."