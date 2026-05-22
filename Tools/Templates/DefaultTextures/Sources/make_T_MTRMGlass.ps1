# Generates T_MTRMGlass.png - 4x4 solid-color PNG used as the source for the
# cooked T_MTRMGlass.uasset/uexp/ubulk default texture shipped under
# Tools/Templates/DefaultTextures/.
#
# MTRM channel semantics (Studio convention, see Docs/HowTo-AuthorBuildingItem.md):
#   R = Metallic
#   G = Tint / Specular / often unused (engine-default 0.5 = 128)
#   B = Roughness  (LOW = glossy/mirror, HIGH = matte) - BUT see note below
#   A = AO / Mask (255 = no occlusion)
#
# IMPORTANT: Some Windrose master materials read the B channel as Smoothness
# (1 - Roughness) instead of Roughness. If you see "extra matte" after
# importing the texture with low B, try inverting B (use 235 instead of 20)
# - that's the current setting in this script, kept after the first round
# of in-game testing showed the surface stayed matte at B=20.
#
# Generated PNG settings:
#   Size:        4x4 px (smallest reasonable for a constant-value lookup)
#   Format:      32bpp ARGB
#   Color space: Linear (sRGB OFF on import in UE!)
#
# Usage:
#   1. Edit $R/$G/$B/$A below if you want different glass behaviour
#   2. Run: powershell -ExecutionPolicy Bypass -File make_T_MTRMGlass.ps1
#   3. Drag the produced T_MTRMGlass.png into UE under /Content/Quartermaster/
#   4. In Texture Editor: sRGB OFF, Compression = Masks (no sRGB)
#   5. Cook (File -> Cook Content -> selected) and copy the resulting
#      .uasset/.uexp/.ubulk triplet to ../

# ----- Tune here ------------------------------------------------------------
$R = 0    # Metallic = 0 (glass is dielectric)
$G = 128  # Tint/Specular neutral
$B = 235  # Smoothness=0.92 (= Roughness=0.08 if master treats B as Smoothness)
$A = 255  # AO = no occlusion
# ----------------------------------------------------------------------------

Add-Type -AssemblyName System.Drawing

$outDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$outPath = Join-Path $outDir 'T_MTRMGlass.png'

$bmp = New-Object System.Drawing.Bitmap 4, 4, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$color = [System.Drawing.Color]::FromArgb($A, $R, $G, $B)
for ($y = 0; $y -lt 4; $y++) {
    for ($x = 0; $x -lt 4; $x++) {
        $bmp.SetPixel($x, $y, $color)
    }
}
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# Verify
$verify = New-Object System.Drawing.Bitmap $outPath
$p = $verify.GetPixel(0, 0)
Write-Host ("Wrote {0} ({1} bytes), pixel(0,0) = R={2} G={3} B={4} A={5}" -f `
    $outPath, (Get-Item $outPath).Length, $p.R, $p.G, $p.B, $p.A)
$verify.Dispose()
