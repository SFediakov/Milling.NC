using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Core.GCode;
using Xunit;

namespace Miller.Tests.Machine;

// T-141 C3: a program is prepared before it is streamed. Comments, spaces, blank lines and the %
// delimiters go; letters become upper case; every line keeps its source line number. A character
// Grbl would execute as a realtime command (? ! ~, bytes from 0x80, 0x18) must never reach the
// controller inside a line, so the program is refused with the line number instead.
public sealed class GrblProgramTests
{
    [Fact]
    public void Parse_RemovesCommentsSpacesAndDelimiters_AndKeepsSourceLines()
    {
        var program = GrblProgram.Parse("p", "%\n( header ? ! ~ )\nG21 G90 ; units !\n\r\ng1 x1.5 (move) y2\r\n%\n");
        Assert.Equal(new[] { "G21G90", "G1X1.5Y2" }, program.Lines);
        Assert.Equal(new[] { 3, 5 }, program.SourceLines);
    }

    [Fact]
    public void Parse_GoldenHeart_KeepsEveryCodeLine()
    {
        var text = File.ReadAllText(GrblPostProcessorTests.GoldenPath("heart_grbl.nc"));
        var source = text.Split('\n');
        var program = GrblProgram.Parse("heart", text);
        var codeLines = source.Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('('));
        Assert.Equal(codeLines, program.Count);
        Assert.All(program.Lines, l => Assert.InRange(l.Length, 1, GrblProgram.MaxLineLength));
        Assert.Equal("M30", program.Lines[^1]);
        Assert.Equal(source.Length - 1, program.SourceLines[^1]);
    }

    [Theory]
    [InlineData("G1 X2 ?")]
    [InlineData("G1 X2 !")]
    [InlineData("~")]
    [InlineData("G1 X2 \u0085")]
    [InlineData("G1 X2 é")]
    [InlineData("G1 X2 \u0018")]
    public void Parse_RefusesRealtimeCharacters_WithTheLineNumber(string line)
    {
        var ex = Assert.Throws<GrblProgramException>(() => GrblProgram.Parse("p", $"G1 X1\n{line}\nG1 X3\n"));
        Assert.Equal(2, ex.Line);
        Assert.Contains("realtime", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("G1 X1 (open", "not closed")]
    [InlineData("G1 X1 )", "closes no comment")]
    [InlineData("G1 X1 \u0001", "printable")]
    public void Parse_RefusesBrokenLines(string line, string reason)
    {
        var ex = Assert.Throws<GrblProgramException>(() => GrblProgram.Parse("p", line));
        Assert.Equal(1, ex.Line);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesALineLongerThanTheControllerBuffer()
    {
        var fits = "G1X" + new string('1', GrblProgram.MaxLineLength - 3);
        Assert.Single(GrblProgram.Parse("p", fits).Lines);
        var ex = Assert.Throws<GrblProgramException>(() => GrblProgram.Parse("p", fits + "1"));
        Assert.Contains($"{GrblProgram.MaxLineLength}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAProgramWithoutCode()
        => Assert.Throws<GrblProgramException>(() => GrblProgram.Parse("p", "( only a comment )\n%\n"));

    [Fact]
    public void Jobs_AddTheirOwnLines()
    {
        var program = GrblProgram.Parse("p", "G21\nG0 X1\n");
        var run = MachineJob.Program(program);
        Assert.Equal(new[] { "G21", "G0X1", MachineJob.SyncLine }, run.Lines);
        Assert.Equal(new[] { 1, 2, 0 }, run.SourceLines);
        var check = MachineJob.Check(program);
        Assert.Equal(new[] { MachineJob.CheckModeLine, "G21", "G0X1", MachineJob.CheckModeLine }, check.Lines);
        var commands = MachineJob.Commands("jog", "$J=G91 G21 X1 F500", "(comment)");
        Assert.Equal(new[] { "$J=G91G21X1F500" }, commands.Lines);
        Assert.Throws<GrblProgramException>(() => MachineJob.Commands("bad", "G0 X1 !"));
        Assert.Throws<GrblProgramException>(() => MachineJob.Commands("empty", "( nothing )"));
    }
}
