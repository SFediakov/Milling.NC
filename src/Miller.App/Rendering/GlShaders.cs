using Avalonia.OpenGL;

namespace Miller.App.Rendering;

// GLSL sources shared by both platforms. Only the version line differs: OpenGL ES 3.0 (ANGLE on
// Windows) or desktop OpenGL 3.3 core (GLX on Linux). Attribute locations are fixed so the
// renderers never look them up.
public static class GlShaders
{
    public const int PositionLocation = 0;
    public const int NormalLocation = 1;
    public const int ColorLocation = 2;

    public const string EsPreamble = "#version 300 es\nprecision highp float;\n";
    public const string CorePreamble = "#version 330 core\n";

    public const string LitVertex = @"
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aColor;
uniform mat4 uModelViewProjection;
uniform vec3 uLightDirection;
uniform float uAmbient;
out vec4 vColor;
void main()
{
    float diffuse = max(dot(normalize(aNormal), normalize(uLightDirection)), 0.0);
    float shade = uAmbient + (1.0 - uAmbient) * diffuse;
    vColor = vec4(aColor.rgb * shade, aColor.a);
    gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
}
";

    public const string LineVertex = @"
layout(location = 0) in vec3 aPosition;
layout(location = 2) in vec4 aColor;
uniform mat4 uModelViewProjection;
out vec4 vColor;
void main()
{
    vColor = aColor;
    gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
}
";

    public const string ColorFragment = @"
in vec4 vColor;
out vec4 fragColor;
void main()
{
    fragColor = vColor;
}
";

    public static string Preamble(GlVersion version)
        => version.Type == GlProfileType.OpenGLES ? EsPreamble : CorePreamble;
}
