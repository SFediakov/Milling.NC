# Placeholder for `Miller.App.csproj`

Implemented by T-006 (docs/DEVELOPMENT_GUIDE.md). Create `src/Miller.App/Miller.App.csproj` and delete this file in the same commit.

Purpose: Avalonia desktop application project.

Required content: Sdk Microsoft.NET.Sdk; OutputType WinExe; ProjectReference ../Miller.Application; PackageReference Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, CommunityToolkit.Mvvm; Avalonia.Diagnostics with Condition Debug; ApplicationIcon Assets/Icons/miller.ico (T-100); AvaloniaResource Assets/**.
