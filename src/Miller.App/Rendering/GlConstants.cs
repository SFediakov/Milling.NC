namespace Miller.App.Rendering;

// Values that Avalonia.OpenGL.GlConsts does not define; everything else is taken from GlConsts.
public static class GlConstants
{
    public const int GL_LINES = 0x0001;
    public const int GL_LINE_STRIP = 0x0003;
    public const int GL_LEQUAL = 0x0203;
    public const int GL_UNSIGNED_INT = 0x1405;
    public const int GL_SRC_ALPHA = 0x0302;
    public const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int GL_BLEND = 0x0BE2;
    public const int GL_POLYGON_OFFSET_FILL = 0x8037;
    public const int GL_DYNAMIC_DRAW = 0x88E8;
}
