using System;
using System.Numerics;

namespace U9Editor.Core
{
    /// <summary>
    /// A camera turning round a target point, from the UW editor. World coordinates are x east, y up, z north,
    /// in point spacings. That is a left-handed set, so the view matrix mirrors z before the usual right-handed
    /// look-at; nothing else needs to know. Distances are scaled up for U9's 1024-point maps.
    /// </summary>
    public sealed class OrbitCamera
    {
        public Vector3 Target = new Vector3(512, 20, 512);
        /// <summary>Which way the camera looks, clockwise from north, in degrees.</summary>
        public float Yaw;
        /// <summary>How far it looks down, in degrees (0 level, 89 straight down).</summary>
        public float Pitch = 50;
        public float Distance = 200;
        public float FieldOfView = 60;
        public const float MinDistance = 1f, MaxDistance = 3000f;
        public float MinPitch = -10;
        /// <summary>Straight down without perspective; Distance sets the scale.</summary>
        public bool Orthographic;
        public const float OrthoEye = 1000f;

        public Vector3 Forward
        {
            get
            {
                double y = Yaw * Math.PI / 180, p = Pitch * Math.PI / 180;
                return new Vector3((float)(Math.Sin(y) * Math.Cos(p)), (float)-Math.Sin(p), (float)(Math.Cos(y) * Math.Cos(p)));
            }
        }

        /// <summary>The horizontal direction to the camera's right.</summary>
        public Vector3 Right
        {
            get
            {
                double y = Yaw * Math.PI / 180;
                return new Vector3((float)Math.Cos(y), 0, (float)-Math.Sin(y));
            }
        }

        public Vector3 Up => Vector3.Normalize(Vector3.Cross(Forward, Right));

        public Vector3 Eye => Target - Forward * (Orthographic ? OrthoEye : Distance);

        public float OrthoHalfHeight => Distance * (float)Math.Tan(FieldOfView * Math.PI / 360);

        static Vector3 Mirror(Vector3 v) => new Vector3(v.X, v.Y, -v.Z);

        public Matrix4x4 View
        {
            get
            {
                var look = Matrix4x4.CreateLookAt(Mirror(Eye), Mirror(Target), Vector3.UnitY);
                return Matrix4x4.CreateScale(1, 1, -1) * look;
            }
        }

        float Near => Math.Max(0.1f, Distance / 500f);
        float Far => Math.Max(4000f, Distance * 8f);

        /// <summary>An OpenGL projection (depth -1..1).</summary>
        public Matrix4x4 Projection(float aspect)
        {
            if (Orthographic)
            {
                float h = OrthoHalfHeight, w = h * Math.Max(0.01f, aspect), n = 1f, fa = OrthoEye * 2;
                var o = Matrix4x4.Identity;
                o.M11 = 1 / w; o.M22 = 1 / h;
                o.M33 = -2 / (fa - n);
                o.M43 = -(fa + n) / (fa - n);
                return o;
            }
            float near = Near, far = Far;
            float f = 1f / (float)Math.Tan(FieldOfView * Math.PI / 360);
            var m = new Matrix4x4();
            m.M11 = f / Math.Max(0.01f, aspect);
            m.M22 = f;
            m.M33 = (far + near) / (near - far);
            m.M34 = -1;
            m.M43 = 2 * far * near / (near - far);
            return m;
        }

        public void Orbit(float dYaw, float dPitch)
        {
            if (Fly)
            {
                // looking round: the eye stays where it is
                var eye = Eye;
                Yaw = (Yaw + dYaw) % 360;
                if (Yaw < 0) Yaw += 360;
                Pitch = Math.Clamp(Pitch + dPitch, -89, 89);
                Target = eye + Forward * Distance;
                return;
            }
            Yaw = (Yaw + dYaw) % 360;
            if (Yaw < 0) Yaw += 360;
            Pitch = Math.Clamp(Pitch + dPitch, MinPitch, 89);
        }

        /// <summary>The fly camera: the eye moves and the view turns round it (the target sits just in front of it).</summary>
        public bool Fly { get; private set; }
        public const float FlyLead = 0.05f;
        float orbitDistance = 30;

        /// <summary>Starts or stops flying, keeping the eye and the view where they are.</summary>
        public void SetFly(bool on)
        {
            if (on == Fly) return;
            var eye = Eye;
            if (on)
            {
                orbitDistance = Distance;
                Fly = true;
                Distance = FlyLead;
                Target = eye + Forward * Distance;
            }
            else
            {
                Fly = false;
                Distance = Math.Clamp(orbitDistance, MinDistance, MaxDistance);
                Pitch = Math.Clamp(Pitch, MinPitch, 89);
                Target = eye + Forward * Distance;
            }
        }

        /// <summary>Flying: moves the eye along where it looks, to its right and straight up (point spacings).</summary>
        public void FlyMove(float forward, float right, float up)
        {
            Target += Forward * forward + Right * right + Vector3.UnitY * up;
        }

        public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);

        /// <summary>Moves the target in the view's plane by pixels (the view is height pixels high).</summary>
        public void Pan(float dxPixels, float dyPixels, float viewHeight)
        {
            float unitsPerPixel = 2f * Distance * (float)Math.Tan(FieldOfView * Math.PI / 360) / Math.Max(1, viewHeight);
            if (Orthographic || Pitch > 60)
            {
                // looking down: drag the ground, not the view plane, so the target stays on the map
                var f = Vector3.Normalize(new Vector3((float)Math.Sin(Yaw * Math.PI / 180), 0, (float)Math.Cos(Yaw * Math.PI / 180)));
                Target += (-Right * dxPixels + f * dyPixels) * unitsPerPixel;
                return;
            }
            var up = Vector3.Cross(Forward, Right);
            Target += (-Right * dxPixels + up * dyPixels) * unitsPerPixel;
        }

        /// <summary>Moves the target along the ground (forward / right in world units).</summary>
        public void Move(float forward, float right)
        {
            var f = Vector3.Normalize(new Vector3((float)Math.Sin(Yaw * Math.PI / 180), 0, (float)Math.Cos(Yaw * Math.PI / 180)));
            Target += f * forward + Right * right;
        }

        /// <summary>The ray under a pixel (x right, y down, in a view of width x height).</summary>
        public void Ray(float px, float py, float width, float height, out Vector3 origin, out Vector3 dir)
        {
            float nx = 2 * px / Math.Max(1, width) - 1, ny = 1 - 2 * py / Math.Max(1, height);
            var vp = View * Projection(width / Math.Max(1, height));
            Matrix4x4.Invert(vp, out var inv);
            var a = Vector4.Transform(new Vector4(nx, ny, -1, 1), inv);
            var b = Vector4.Transform(new Vector4(nx, ny, 1, 1), inv);
            var pa = new Vector3(a.X, a.Y, a.Z) / a.W;
            var pb = new Vector3(b.X, b.Y, b.Z) / b.W;
            origin = pa;
            dir = Vector3.Normalize(pb - pa);
        }
    }
}
