namespace InGameDevTools.Utils;

/// <summary>
/// Euler/matrix helpers for the model editor rotation gizmo. Vintage Story composes element
/// rotation as R = Rx(x)·Ry(y)·Rz(z) (Matrixf.Rotate applies RotateX, RotateY, RotateZ by
/// post-multiplication, gl-matrix style, right-handed, column-vector convention). Matrices here
/// are 3x3 row-major double[9] in that same convention.
/// </summary>
internal static class DevToolsRotationMath
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    public static double[] Identity => [1, 0, 0, 0, 1, 0, 0, 0, 1];

    public static double[] RotationX(double degrees)
    {
        double c = Math.Cos(degrees * Deg2Rad);
        double s = Math.Sin(degrees * Deg2Rad);
        return [1, 0, 0, 0, c, -s, 0, s, c];
    }

    public static double[] RotationY(double degrees)
    {
        double c = Math.Cos(degrees * Deg2Rad);
        double s = Math.Sin(degrees * Deg2Rad);
        return [c, 0, s, 0, 1, 0, -s, 0, c];
    }

    public static double[] RotationZ(double degrees)
    {
        double c = Math.Cos(degrees * Deg2Rad);
        double s = Math.Sin(degrees * Deg2Rad);
        return [c, -s, 0, s, c, 0, 0, 0, 1];
    }

    public static double[] Multiply(double[] a, double[] b)
    {
        double[] result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                result[row * 3 + column] =
                    a[row * 3 + 0] * b[0 * 3 + column] +
                    a[row * 3 + 1] * b[1 * 3 + column] +
                    a[row * 3 + 2] * b[2 * 3 + column];
            }
        }

        return result;
    }

    /// <summary>Builds R = Rx(x)·Ry(y)·Rz(z), matching Matrixf.Rotate(x, y, z).</summary>
    public static double[] ComposeXyz(double xDegrees, double yDegrees, double zDegrees)
    {
        return Multiply(RotationX(xDegrees), Multiply(RotationY(yDegrees), RotationZ(zDegrees)));
    }

    /// <summary>
    /// Decomposes a rotation matrix into the R = Rx·Ry·Rz Euler angles (degrees). Y lands in
    /// [-90, 90]; X and Z in (-180, 180]. At the Y = ±90 singularity Z is fixed to 0.
    /// </summary>
    public static (double X, double Y, double Z) DecomposeXyz(double[] m)
    {
        // R = Rx·Ry·Rz gives m02 = sin(y), m12 = -sin(x)cos(y), m22 = cos(x)cos(y),
        // m01 = -cos(y)sin(z), m00 = cos(y)cos(z).
        double sy = Math.Clamp(m[2], -1.0, 1.0);
        double y = Math.Asin(sy);

        double x;
        double z;
        if (Math.Abs(sy) > 0.999999)
        {
            // Gimbal lock: cos(y) ~ 0, X and Z rotate about the same axis. Put it all in X.
            x = Math.Atan2(m[7], m[4]);
            z = 0.0;
        }
        else
        {
            x = Math.Atan2(-m[5], m[8]);
            z = Math.Atan2(-m[1], m[0]);
        }

        return (x * Rad2Deg, y * Rad2Deg, z * Rad2Deg);
    }

    /// <summary>
    /// Rotates an R = Rx·Ry·Rz Euler orientation by <paramref name="deltaDegrees"/> about its own
    /// (already rotated) local axis and returns the new Euler angles. Local-axis rotation is a
    /// right-multiplication: L' = L · R_axis(delta).
    /// </summary>
    public static (double X, double Y, double Z) RotateXyzEulerAboutLocalAxis(
        double xDegrees,
        double yDegrees,
        double zDegrees,
        int axis,
        double deltaDegrees)
    {
        double[] start = ComposeXyz(xDegrees, yDegrees, zDegrees);
        double[] delta = axis switch
        {
            0 => RotationX(deltaDegrees),
            1 => RotationY(deltaDegrees),
            _ => RotationZ(deltaDegrees)
        };

        return DecomposeXyz(Multiply(start, delta));
    }

    /// <summary>Applies the rotation matrix to a column vector.</summary>
    public static (double X, double Y, double Z) Apply(double[] m, double x, double y, double z)
    {
        return (
            m[0] * x + m[1] * y + m[2] * z,
            m[3] * x + m[4] * y + m[5] * z,
            m[6] * x + m[7] * y + m[8] * z);
    }

    /// <summary>Applies the transposed (= inverse, for rotations) matrix to a column vector.</summary>
    public static (double X, double Y, double Z) ApplyTransposed(double[] m, double x, double y, double z)
    {
        return (
            m[0] * x + m[3] * y + m[6] * z,
            m[1] * x + m[4] * y + m[7] * z,
            m[2] * x + m[5] * y + m[8] * z);
    }

    /// <summary>
    /// From/To compensation for re-pivoting an element without moving its rendered box. The local
    /// placement is x(p) = origin + R·(from − origin + p); shifting From/To by the returned d and
    /// setting the origin to <paramref name="newOriginX"/>/Y/Z + d keeps every rendered point
    /// fixed, with d = (R − I)·(newOrigin − oldOrigin).
    /// </summary>
    public static (double X, double Y, double Z) PivotCompensation(
        double[] rotation,
        double oldOriginX, double oldOriginY, double oldOriginZ,
        double newOriginX, double newOriginY, double newOriginZ)
    {
        double vx = newOriginX - oldOriginX;
        double vy = newOriginY - oldOriginY;
        double vz = newOriginZ - oldOriginZ;
        (double rx, double ry, double rz) = Apply(rotation, vx, vy, vz);
        return (rx - vx, ry - vy, rz - vz);
    }

    /// <summary>Largest absolute element difference between two 3x3 matrices.</summary>
    public static double MaxDifference(double[] a, double[] b)
    {
        double max = 0.0;
        for (int index = 0; index < 9; index++)
        {
            max = Math.Max(max, Math.Abs(a[index] - b[index]));
        }

        return max;
    }
}
