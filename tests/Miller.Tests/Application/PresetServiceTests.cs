using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Application;

// One presets.json: round trip, replace by name (case does not matter), delete, sorted names, a
// missing file means none, a corrupt or foreign file throws, and Load/Apply never share instances
// with the project.
public sealed class PresetServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-presets-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static MillingProject Sample()
    {
        var project = MillingProject.Default();
        project.Tool.CutterDiameter = 3.175f;
        project.Tool.TipType = TipType.Ball;
        project.Axes.FlipZ = true;
        project.Axes.OriginMode = OriginMode.Custom;
        project.Axes.CustomOffset = new Vector3(1, 2, 3);
        project.Parameters.FeedRate = 1200f;
        project.Parameters.Stepdown = 0.8f;
        project.RoutingStrategyId = "three-axis-freedom";
        project.CutScope = CutScope.Separation;
        project.MinIslandVolume = 40f;
        project.ReachPercent = 66f;
        project.Stock.SizeX = 77f;
        project.Models.Add(new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(4, 0, 0) });
        return project;
    }

    [Fact]
    public void MissingFile_MeansNoPresets_AndSaveCreatesOneDocument()
    {
        var service = new PresetService(_root);
        service.Load();
        Assert.Empty(service.Presets);
        Assert.False(File.Exists(service.FilePath));

        service.Save(MillingPreset.FromProject(Sample(), "Brass 3 mm"));
        service.Save(MillingPreset.FromProject(MillingProject.Default(), "aluminium"));
        Assert.True(File.Exists(service.FilePath));
        Assert.Equal(Path.Combine(_root, PresetService.FileName), service.FilePath);
        Assert.Equal(new[] { "aluminium", "Brass 3 mm" }, service.Presets.Select(p => p.Name));
        Assert.Single(Directory.GetFiles(_root));

        var reloaded = new PresetService(_root);
        reloaded.Load();
        Assert.Equal(new[] { "aluminium", "Brass 3 mm" }, reloaded.Presets.Select(p => p.Name));
        var brass = reloaded.Find("brass 3 MM")!;
        Assert.Equal(3.175f, brass.Tool.CutterDiameter);
        Assert.Equal(TipType.Ball, brass.Tool.TipType);
        Assert.Equal(new Vector3(1, 2, 3), brass.Axes.CustomOffset);
        Assert.Equal(0.8f, brass.Parameters.Stepdown);
        Assert.Equal("three-axis-freedom", brass.RoutingStrategyId);
        Assert.Equal(CutScope.Separation, brass.CutScope);
        Assert.Equal(40f, brass.MinIslandVolume);
        Assert.Equal(66f, brass.ReachPercent);
    }

    [Fact]
    public void SaveWithAnExistingName_Replaces_AndDeleteRemoves()
    {
        var service = new PresetService(_root);
        service.Save(MillingPreset.FromProject(Sample(), "A"));
        var changed = Sample();
        changed.Tool.CutterDiameter = 9f;
        service.Save(MillingPreset.FromProject(changed, "a"));
        Assert.Single(service.Presets);
        Assert.Equal(9f, service.Presets[0].Tool.CutterDiameter);

        Assert.False(service.Delete("missing"));
        Assert.True(service.Delete("A"));
        Assert.Empty(service.Presets);
        var reloaded = new PresetService(_root);
        reloaded.Load();
        Assert.Empty(reloaded.Presets);
    }

    [Fact]
    public void ApplyTo_CopiesTheFourGroups_AndLeavesStockAndModelsAlone()
    {
        var preset = MillingPreset.FromProject(Sample(), "P");
        var target = MillingProject.Default();
        target.Stock.SizeX = 55f;
        target.Models.Add(new ModelPlacement { StlPath = "b.stl" });
        preset.ApplyTo(target);

        Assert.Equal(3.175f, target.Tool.CutterDiameter);
        Assert.True(target.Axes.FlipZ);
        Assert.Equal(1200f, target.Parameters.FeedRate);
        Assert.Equal("three-axis-freedom", target.RoutingStrategyId);
        Assert.Equal(CutScope.Separation, target.CutScope);
        Assert.Equal(40f, target.MinIslandVolume);
        Assert.Equal(66f, target.ReachPercent);
        Assert.Equal(55f, target.Stock.SizeX);
        Assert.Equal("b.stl", Assert.Single(target.Models).StlPath);

        // No aliasing in either direction.
        target.Tool.CutterDiameter = 1f;
        Assert.Equal(3.175f, preset.Tool.CutterDiameter);
        var source = Sample();
        var fromSource = MillingPreset.FromProject(source, "Q");
        source.Tool.CutterDiameter = 2f;
        Assert.Equal(3.175f, fromSource.Tool.CutterDiameter);
    }

    [Fact]
    public void CorruptOrForeignFile_Throws()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, PresetService.FileName);
        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new PresetService(_root).Load());

        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"Presets\": [] }");
        Assert.Throws<InvalidDataException>(() => new PresetService(_root).Load());

        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Presets\": [ { \"Name\": \"x\" }, { \"Name\": \"X\" } ] }");
        Assert.Throws<InvalidDataException>(() => new PresetService(_root).Load());

        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Presets\": [ { \"Name\": \"x\", \"Typo\": 1 } ] }");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => new PresetService(_root).Load());

        Assert.Throws<ArgumentException>(() => MillingPreset.FromProject(MillingProject.Default(), " "));
    }
}
