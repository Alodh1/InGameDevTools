using InGameDevTools.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Tests;

public sealed class DevToolsRotationMathTests
{
    private const double Tolerance = 0.0001;

    private static double[] MatrixfRotation3x3(double xDegrees, double yDegrees, double zDegrees)
    {
        Matrixf matrix = new();
        matrix.Identity();
        matrix.Rotate(
            (float)(xDegrees * GameMath.DEG2RAD),
            (float)(yDegrees * GameMath.DEG2RAD),
            (float)(zDegrees * GameMath.DEG2RAD));

        // Matrixf.Values is column-major float[16]; convert the 3x3 part to row-major double[9].
        double[] result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                result[row * 3 + column] = matrix.Values[column * 4 + row];
            }
        }

        return result;
    }

    public static TheoryData<double, double, double> AngleTriples => new()
    {
        { 0, 0, 0 },
        { 30, 0, 0 },
        { 0, 45, 0 },
        { 0, 0, 60 },
        { 30, 45, 60 },
        { -130, 25, 95 },
        { 10, -80, 170 },
        { 179, -89, -179 }
    };

    [Theory]
    [MemberData(nameof(AngleTriples))]
    public void ComposeXyz_MatchesTheGamesMatrixfRotate(double x, double y, double z)
    {
        double[] expected = MatrixfRotation3x3(x, y, z);
        double[] actual = DevToolsRotationMath.ComposeXyz(x, y, z);

        // Matrixf works in float; allow float-level tolerance.
        Assert.True(DevToolsRotationMath.MaxDifference(expected, actual) < 0.001,
            $"ComposeXyz({x},{y},{z}) diverges from Matrixf.Rotate by {DevToolsRotationMath.MaxDifference(expected, actual)}");
    }

    [Theory]
    [InlineData(30, 45, 60)]
    [InlineData(-130, 25, 95)]
    [InlineData(10, -80, 170)]
    [InlineData(0, 0, 0)]
    [InlineData(179, 89.5, -179)]
    public void DecomposeXyz_RoundTripsWithinCanonicalRange(double x, double y, double z)
    {
        (double rx, double ry, double rz) = DevToolsRotationMath.DecomposeXyz(DevToolsRotationMath.ComposeXyz(x, y, z));

        // Compare matrices, not angle triples: the canonical decomposition may pick an
        // equivalent representation.
        double difference = DevToolsRotationMath.MaxDifference(
            DevToolsRotationMath.ComposeXyz(x, y, z),
            DevToolsRotationMath.ComposeXyz(rx, ry, rz));
        Assert.True(difference < Tolerance, $"round trip of ({x},{y},{z}) differs by {difference}");
        Assert.InRange(ry, -90.0 - Tolerance, 90.0 + Tolerance);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void DecomposeXyz_HandlesGimbalLock(double y)
    {
        double[] matrix = DevToolsRotationMath.ComposeXyz(35, y, -20);
        (double rx, double ry, double rz) = DevToolsRotationMath.DecomposeXyz(matrix);

        double difference = DevToolsRotationMath.MaxDifference(matrix, DevToolsRotationMath.ComposeXyz(rx, ry, rz));
        Assert.True(difference < Tolerance, $"gimbal decomposition differs by {difference}");
        Assert.Equal(0.0, rz, 3);
    }

    [Fact]
    public void RotateAboutLocalZ_EqualsAddingToTheZEuler()
    {
        // L = Rx·Ry·Rz, so local-Z rotation is exactly z + delta.
        (double x, double y, double z) = DevToolsRotationMath.RotateXyzEulerAboutLocalAxis(30, 45, 10, axis: 2, deltaDegrees: 25);

        double difference = DevToolsRotationMath.MaxDifference(
            DevToolsRotationMath.ComposeXyz(30, 45, 35),
            DevToolsRotationMath.ComposeXyz(x, y, z));
        Assert.True(difference < Tolerance, $"local-Z rotation differs from z+delta by {difference}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RotateAboutLocalAxis_MatchesRightMultiplication(int axis)
    {
        double[] start = DevToolsRotationMath.ComposeXyz(30, 45, 60);
        double[] delta = axis switch
        {
            0 => DevToolsRotationMath.RotationX(33),
            1 => DevToolsRotationMath.RotationY(33),
            _ => DevToolsRotationMath.RotationZ(33)
        };
        double[] expected = DevToolsRotationMath.Multiply(start, delta);

        (double x, double y, double z) = DevToolsRotationMath.RotateXyzEulerAboutLocalAxis(30, 45, 60, axis, 33);

        double difference = DevToolsRotationMath.MaxDifference(expected, DevToolsRotationMath.ComposeXyz(x, y, z));
        Assert.True(difference < Tolerance, $"axis {axis} local rotation differs by {difference}");
    }

    [Fact]
    public void RotateAboutLocalAxis_SingleAxisStartBehavesLikePlainAddition()
    {
        (double x, double y, double z) = DevToolsRotationMath.RotateXyzEulerAboutLocalAxis(40, 0, 0, axis: 0, deltaDegrees: 25);

        double difference = DevToolsRotationMath.MaxDifference(
            DevToolsRotationMath.ComposeXyz(65, 0, 0),
            DevToolsRotationMath.ComposeXyz(x, y, z));
        Assert.True(difference < Tolerance);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 90)]
    [InlineData(30, 45, 60)]
    [InlineData(-130, 25, 95)]
    public void PivotCompensation_KeepsRenderedBoxFixedAndCentersPivot(double rotX, double rotY, double rotZ)
    {
        double[] rotation = DevToolsRotationMath.ComposeXyz(rotX, rotY, rotZ);
        double[] from = [3, 1, -2];
        double[] size = [4, 2, 6];
        double[] oldOrigin = [0.5, -1, 2];
        double[] center = [from[0] + size[0] / 2, from[1] + size[1] / 2, from[2] + size[2] / 2];

        (double dx, double dy, double dz) = DevToolsRotationMath.PivotCompensation(
            rotation,
            oldOrigin[0], oldOrigin[1], oldOrigin[2],
            center[0], center[1], center[2]);
        double[] newFrom = [from[0] + dx, from[1] + dy, from[2] + dz];
        double[] newOrigin = [center[0] + dx, center[1] + dy, center[2] + dz];

        // The new pivot is exactly the center of the moved box fields.
        Assert.Equal(newFrom[0] + size[0] / 2, newOrigin[0], 9);
        Assert.Equal(newFrom[1] + size[1] / 2, newOrigin[1], 9);
        Assert.Equal(newFrom[2] + size[2] / 2, newOrigin[2], 9);

        // Every box corner renders at the same world position before and after.
        for (int corner = 0; corner < 8; corner++)
        {
            double px = (corner & 1) == 0 ? 0 : size[0];
            double py = (corner & 2) == 0 ? 0 : size[1];
            double pz = (corner & 4) == 0 ? 0 : size[2];

            (double ox, double oy, double oz) = DevToolsRotationMath.Apply(
                rotation,
                from[0] - oldOrigin[0] + px,
                from[1] - oldOrigin[1] + py,
                from[2] - oldOrigin[2] + pz);
            (double nx, double ny, double nz) = DevToolsRotationMath.Apply(
                rotation,
                newFrom[0] - newOrigin[0] + px,
                newFrom[1] - newOrigin[1] + py,
                newFrom[2] - newOrigin[2] + pz);

            Assert.Equal(oldOrigin[0] + ox, newOrigin[0] + nx, 9);
            Assert.Equal(oldOrigin[1] + oy, newOrigin[1] + ny, 9);
            Assert.Equal(oldOrigin[2] + oz, newOrigin[2] + nz, 9);
        }

        if (rotX == 0 && rotY == 0 && rotZ == 0)
        {
            Assert.Equal(0.0, dx, 9);
            Assert.Equal(0.0, dy, 9);
            Assert.Equal(0.0, dz, 9);
        }
    }

    [Fact]
    public void RotateAboutLocalAxis_SequentialDragsStayOnTheDrawnAxis()
    {
        // Regression for the reported bug: after a Y rotation near 90°, the X and Z rings used to
        // produce nearly identical motion. With local-axis rotation they must stay distinct.
        double[] afterY = DevToolsRotationMath.ComposeXyz(0, 88, 0);
        (double x1, double y1, double z1) = DevToolsRotationMath.DecomposeXyz(
            DevToolsRotationMath.Multiply(afterY, DevToolsRotationMath.RotationX(30)));
        (double x2, double y2, double z2) = DevToolsRotationMath.DecomposeXyz(
            DevToolsRotationMath.Multiply(afterY, DevToolsRotationMath.RotationZ(30)));

        double difference = DevToolsRotationMath.MaxDifference(
            DevToolsRotationMath.ComposeXyz(x1, y1, z1),
            DevToolsRotationMath.ComposeXyz(x2, y2, z2));
        Assert.True(difference > 0.1, $"X and Z ring results should differ, max element difference was {difference}");
    }
}
