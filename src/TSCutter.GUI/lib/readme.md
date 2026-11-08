# Windows static libraries

Windows x64 Native AOT builds link these libraries into the executable:

| Component | Native version | Required files |
| --- | --- | --- |
| SkiaSharp | 2.88.9 | `skia.lib`, `SkiaSharp.lib` |
| HarfBuzz | 8.3.1 | `libHarfBuzzSharp.lib` |
| ANGLE | Chromium 7151 | `libANGLE_static.lib`, `libGLESv2_static.lib` |

Place the files directly in this directory. The release workflow downloads the pinned builds automatically.

These versions target the SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1 managed dependencies used by Avalonia 11.
The HarfBuzz archive is labelled `3.119.0` upstream because that is its SkiaSharp source branch; only its HarfBuzz library is used.
The SkiaSharp 3 native libraries require Avalonia 12 and must not replace the SkiaSharp 2 libraries listed above.

Sources:

- <https://github.com/2ndlab/ANGLE.Static/releases>
- <https://github.com/2ndlab/SkiaSharp.Static/releases>