using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.App.Rendering;

// Orbit camera around a target with Z up. Angles are degrees. Matrices follow System.Numerics
// conventions (row vectors); the projection maps depth to the OpenGL clip range [-1, 1].
public sealed class Camera
{
    public const float MinPitch = -89f;
    public const float MaxPitch = 89f;
    public const float MinDistance = 0.5f;
    public const float MaxDistance = 100000f;
    public const float FieldOfViewDegrees = 45f;
    public const float FitMargin = 1.15f;
    public const float NearFactor = 0.01f;
    public const float FarFactor = 100f;
    public const float MinNear = 0.05f;
    public const float DefaultYaw = 225f;
    public const float DefaultPitch = 30f;
    public const float DefaultDistance = 200f;

    private const float DegToRad = MathF.PI / 180f;

    private float _pitch = DefaultPitch;
    private float _distance = DefaultDistance;

    public Vector3 Target { get; set; }

    public float Yaw { get; set; } = DefaultYaw;

    public float Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, MinPitch, MaxPitch);
    }

    public float Distance
    {
        get => _distance;
        set => _distance = Math.Clamp(value, MinDistance, MaxDistance);
    }

    public float Aspect { get; set; } = 1f;

    // Unit vector from the target towards the camera.
    public Vector3 Direction
    {
        get
        {
            var yaw = Yaw * DegToRad;
            var pitch = Pitch * DegToRad;
            return new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        }
    }

    public Vector3 Position => Target + Direction * Distance;

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(-Direction, Vector3.UnitZ));

    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, -Direction));

    public float Near => MathF.Max(MinNear, Distance * NearFactor);

    public float Far => Distance * FarFactor;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitZ);

    public Matrix4x4 Projection => CreatePerspectiveGl(FieldOfViewDegrees * DegToRad, Aspect, Near, Far);

    public Matrix4x4 ViewProjection => View * Projection;

    public void Orbit(float deltaYawDegrees, float deltaPitchDegrees)
    {
        Yaw = (Yaw + deltaYawDegrees) % 360f;
        Pitch += deltaPitchDegrees;
    }

    // World units along the screen axes.
    public void Pan(float dx, float dy) => Target += Right * dx + Up * dy;

    public void Zoom(float factor)
    {
        if (!(factor > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Zoom factor must be positive.");
        }

        Distance *= factor;
    }

    public void FitToBounds(BoundingBox bounds)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("Cannot fit the camera to empty bounds.", nameof(bounds));
        }

        Target = bounds.Center;
        var radius = bounds.Size.Length() / 2;
        var verticalHalfAngle = FieldOfViewDegrees * DegToRad / 2;
        var horizontalHalfAngle = MathF.Atan(MathF.Tan(verticalHalfAngle) * Aspect);
        Distance = radius / MathF.Sin(MathF.Min(verticalHalfAngle, horizontalHalfAngle)) * FitMargin;
    }

    // Homogeneous clip coordinates of a world point; divide by W for normalized device coordinates.
    public Vector4 ToClip(Vector3 world) => Vector4.Transform(new Vector4(world, 1f), ViewProjection);

    // Right-handed OpenGL projection in row-vector form: near maps to -1, far to +1.
    public static Matrix4x4 CreatePerspectiveGl(float fieldOfView, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fieldOfView / 2);
        var m = default(Matrix4x4);
        m.M11 = f / aspect;
        m.M22 = f;
        m.M33 = (far + near) / (near - far);
        m.M34 = -1f;
        m.M43 = 2f * far * near / (near - far);
        return m;
    }
}
