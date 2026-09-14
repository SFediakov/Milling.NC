#!/usr/bin/env bash
# PLACEHOLDER - implemented by T-005 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the script.
# Purpose: One-time, online: fills third_party/nuget with every package and runtime pack the solution needs.
# Steps:
#   1. create a temporary project under out/vendor referencing every PackageVersion from Directory.Packages.props
#   2. dotnet restore it with --source https://api.nuget.org/v3/index.json --packages out/vendor/cache, once without RID, once with -r win-x64, once with -r linux-x64
#   3. copy every *.nupkg found under out/vendor/cache into third_party/nuget (flat, lowercase names as NuGet stores them)
#   4. print the count; remind to run 'dotnet restore Miller.sln' with the network disabled as the acceptance check
#   5. this script is the only file in the repository that names an online package source
