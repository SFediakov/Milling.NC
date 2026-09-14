# Placeholder for `Miller.Tests.csproj`

Implemented by T-007 (docs/DEVELOPMENT_GUIDE.md). Create `tests/Miller.Tests/Miller.Tests.csproj` and delete this file in the same commit.

Purpose: xunit v3 test project referencing all three source projects.

Required content: PackageReference xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Avalonia.Headless.XUnit, Avalonia.Themes.Fluent; IsPackable false; assembly attribute AvaloniaTestApplication(typeof(TestAppBuilder)); Golden/*.nc and the fixture path available at run time (Content or relative path).
