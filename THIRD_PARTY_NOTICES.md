# Third-party notices

StarSim Core depends on open-source components distributed under their own licenses. The authoritative terms and copyright notices supplied by each project continue to apply.

Principal components include:

| Component | License | Project |
|---|---|---|
| .NET runtime | MIT and included third-party notices | https://github.com/dotnet/runtime |
| Avalonia UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| .NET Community Toolkit | MIT | https://github.com/CommunityToolkit/dotnet |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp / HarfBuzz | MIT | https://github.com/harfbuzz/harfbuzz |
| libpng | libpng license | https://github.com/pnggroup/libpng |
| libtiff | libtiff license | https://gitlab.com/libtiff/libtiff |
| zlib | zlib license | https://zlib.net/ |
| libjpeg-turbo | BSD-style, IJG and zlib licenses by component | https://github.com/libjpeg-turbo/libjpeg-turbo |
| XZ Utils / liblzma | 0BSD and component-specific notices | https://tukaani.org/xz/ |
| libspng | BSD-2-Clause | https://libspng.org/ |

Release packaging copies the locally supplied .NET and vcpkg license/notices into the `licenses` directory beside the application. NuGet and native package versions are pinned by the repository manifests; consult their metadata and copied notices for the exact version shipped in a particular release.

StarSim Core does not claim ownership of third-party components, names, or trademarks.

