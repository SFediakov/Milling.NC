using System.Text.RegularExpressions;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Core.Setup;

// The far stepdown parameter: default none, written and read with the project, carried by presets,
// and a project file from before it loads as none.
public sealed class FarStepdownParameterTests
{
    [Fact]
    public void Default_IsNone_AndRoundTripsThroughTheProjectFile()
    {
        Assert.Equal(0f, CuttingParameters.Default().FarStepdown);
        var project = MillingProject.Default();
        project.Parameters.FarStepdown = 4f;
        var text = ProjectSerializer.Serialize(project);
        Assert.Contains("\"FarStepdown\"", text);
        Assert.Equal(4f, ProjectSerializer.Deserialize(text).Parameters.FarStepdown);
    }

    [Fact]
    public void FileWithoutTheMember_LoadsNone()
    {
        var text = ProjectSerializer.Serialize(MillingProject.Default());
        var older = Regex.Replace(text, "\\s*\"FarStepdown\":\\s*[^,]*,", string.Empty);
        Assert.DoesNotContain("FarStepdown", older);
        Assert.Equal(0f, ProjectSerializer.Deserialize(older).Parameters.FarStepdown);
    }

    [Fact]
    public void Preset_CarriesTheFarStepdown()
    {
        var source = MillingProject.Default();
        source.Parameters.FarStepdown = 6f;
        var target = MillingProject.Default();
        MillingPreset.FromProject(source, "far").ApplyTo(target);
        Assert.Equal(6f, target.Parameters.FarStepdown);
    }
}
