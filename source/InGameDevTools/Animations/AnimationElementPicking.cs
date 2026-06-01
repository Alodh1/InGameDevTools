using System.Numerics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace InGameDevTools.Animations;

internal readonly record struct AnimationElementPickHit(
    string ElementName,
    ElementPose Pose,
    double Distance,
    int HierarchyDepth);

internal static class AnimationElementPicking
{
    public static bool TryPickWorldRay(
        IEnumerable<ElementPose>? rootPoses,
        Matrixf modelMatrix,
        Vec3d worldOffset,
        Vec3d rayOrigin,
        Vec3d rayDirection,
        System.Func<ElementPose, bool>? poseFilter,
        out AnimationElementPickHit hit)
    {
        hit = default;
        if (rootPoses == null) return false;
        if (rayDirection.LengthSq() < 0.000001) return false;

        Vec3d direction = new(rayDirection.X, rayDirection.Y, rayDirection.Z);
        direction.Normalize();

        bool found = false;
        foreach (ElementPose root in rootPoses)
        {
            CollectWorldRayHits(root, modelMatrix, worldOffset, rayOrigin, direction, poseFilter, depth: 0, ref found, ref hit);
        }

        return found;
    }

    public static bool TryIntersectScreenLocalBox(
        Matrixf projectionView,
        Matrixf localToWorld,
        ShapeElement element,
        Vector2 viewportMin,
        float viewportWidth,
        float viewportHeight,
        Vector2 mouse,
        out double distance)
    {
        distance = 0;
        Matrixf clipFromLocal = new();
        clipFromLocal.Set(localToWorld.Values);
        clipFromLocal.ReverseMul(projectionView.Values);

        double[] inverseClipFromLocal = Mat4d.Create();
        if (Mat4d.Invert(inverseClipFromLocal, ToDoubleMatrix(clipFromLocal.Values)) == null) return false;
        if (!UnprojectViewportPoint(inverseClipFromLocal, viewportMin, viewportWidth, viewportHeight, mouse, -1.0, out Vec3d near)) return false;
        if (!UnprojectViewportPoint(inverseClipFromLocal, viewportMin, viewportWidth, viewportHeight, mouse, 1.0, out Vec3d far)) return false;

        Vec3d direction = new(far.X - near.X, far.Y - near.Y, far.Z - near.Z);
        if (direction.LengthSq() < 0.000001) return false;
        direction.Normalize();

        return TryIntersectLocalAabb(near, direction, GetElementLocalBoxCorners(element), out distance);
    }

    public static Matrixf BuildPoseModelMatrix(Matrixf modelMatrix, ElementPose pose)
    {
        Matrixf result = new();
        result.Set(modelMatrix.Values);
        result.Mul(pose.AnimModelMatrix);
        return result;
    }

    public static bool TryGetPoseWorldBox(Matrixf modelMatrix, Vec3d worldOffset, ElementPose pose, out Vec3d[] corners)
    {
        corners = [];
        if (pose.ForElement == null) return false;

        Matrixf localToWorld = BuildPoseModelMatrix(modelMatrix, pose);
        Vec3f[] localCorners = GetElementLocalBoxCorners(pose.ForElement);
        corners = new Vec3d[localCorners.Length];

        for (int index = 0; index < localCorners.Length; index++)
        {
            Vec3f local = localCorners[index];
            Vec4f relative = localToWorld.TransformVector(new Vec4f(local.X, local.Y, local.Z, 1f));
            corners[index] = new Vec3d(worldOffset.X + relative.X, worldOffset.Y + relative.Y, worldOffset.Z + relative.Z);
        }

        return corners.Length > 0;
    }

    public static Vec3f[] GetElementLocalBoxCorners(ShapeElement element)
    {
        Vec3f center = GetElementLocalCenter(element);
        float halfX = 0.12f;
        float halfY = 0.12f;
        float halfZ = 0.12f;

        if (element.From != null && element.To != null && element.From.Length >= 3 && element.To.Length >= 3)
        {
            halfX = Math.Max(0.08f, (float)Math.Abs(element.To[0] - element.From[0]) / 32f);
            halfY = Math.Max(0.08f, (float)Math.Abs(element.To[1] - element.From[1]) / 32f);
            halfZ = Math.Max(0.08f, (float)Math.Abs(element.To[2] - element.From[2]) / 32f);
        }

        const float padding = 0.035f;
        halfX += padding;
        halfY += padding;
        halfZ += padding;

        float minX = center.X - halfX;
        float minY = center.Y - halfY;
        float minZ = center.Z - halfZ;
        float maxX = center.X + halfX;
        float maxY = center.Y + halfY;
        float maxZ = center.Z + halfZ;

        return
        [
            new Vec3f(minX, minY, minZ),
            new Vec3f(maxX, minY, minZ),
            new Vec3f(maxX, maxY, minZ),
            new Vec3f(minX, maxY, minZ),
            new Vec3f(minX, minY, maxZ),
            new Vec3f(maxX, minY, maxZ),
            new Vec3f(maxX, maxY, maxZ),
            new Vec3f(minX, maxY, maxZ)
        ];
    }

    private static void CollectWorldRayHits(
        ElementPose pose,
        Matrixf modelMatrix,
        Vec3d worldOffset,
        Vec3d rayOrigin,
        Vec3d rayDirection,
        System.Func<ElementPose, bool>? poseFilter,
        int depth,
        ref bool found,
        ref AnimationElementPickHit best)
    {
        if ((poseFilter == null || poseFilter(pose)) &&
            TryBuildWorldRayHit(pose, modelMatrix, worldOffset, rayOrigin, rayDirection, depth, out AnimationElementPickHit candidate) &&
            (!found || IsBetterHit(candidate, best)))
        {
            best = candidate;
            found = true;
        }

        if (pose.ChildElementPoses == null) return;
        foreach (ElementPose child in pose.ChildElementPoses)
        {
            CollectWorldRayHits(child, modelMatrix, worldOffset, rayOrigin, rayDirection, poseFilter, depth + 1, ref found, ref best);
        }
    }

    private static bool TryBuildWorldRayHit(
        ElementPose pose,
        Matrixf modelMatrix,
        Vec3d worldOffset,
        Vec3d rayOrigin,
        Vec3d rayDirection,
        int depth,
        out AnimationElementPickHit hit)
    {
        hit = default;
        if (pose.ForElement == null || string.IsNullOrWhiteSpace(pose.ForElement.Name)) return false;
        if (!TryGetPoseWorldBox(modelMatrix, worldOffset, pose, out Vec3d[] corners)) return false;
        if (!TryIntersectRayBox(rayOrigin, rayDirection, corners, out double distance)) return false;

        hit = new(pose.ForElement.Name, pose, distance, depth);
        return true;
    }

    private static bool IsBetterHit(AnimationElementPickHit candidate, AnimationElementPickHit current)
    {
        if (candidate.Distance < current.Distance - 0.01) return true;
        if (candidate.Distance > current.Distance + 0.01) return false;
        return candidate.HierarchyDepth > current.HierarchyDepth;
    }

    private static Vec3f GetElementLocalCenter(ShapeElement element)
    {
        if (element.From == null || element.To == null || element.From.Length < 3 || element.To.Length < 3) return new Vec3f();
        return new Vec3f(
            (float)((element.To[0] - element.From[0]) / 32.0),
            (float)((element.To[1] - element.From[1]) / 32.0),
            (float)((element.To[2] - element.From[2]) / 32.0));
    }

    private static bool TryIntersectRayBox(Vec3d origin, Vec3d direction, Vec3d[] corners, out double distance)
    {
        distance = double.PositiveInfinity;
        if (corners.Length < 8) return false;

        ReadOnlySpan<(int A, int B, int C)> triangles =
        [
            (0, 1, 2), (0, 2, 3),
            (4, 6, 5), (4, 7, 6),
            (0, 4, 5), (0, 5, 1),
            (1, 5, 6), (1, 6, 2),
            (2, 6, 7), (2, 7, 3),
            (3, 7, 4), (3, 4, 0)
        ];

        bool hit = false;
        foreach ((int a, int b, int c) in triangles)
        {
            if (!TryIntersectRayTriangle(origin, direction, corners[a], corners[b], corners[c], out double triangleDistance)) continue;
            if (triangleDistance >= distance) continue;

            distance = triangleDistance;
            hit = true;
        }

        return hit;
    }

    private static bool TryIntersectRayTriangle(Vec3d origin, Vec3d direction, Vec3d a, Vec3d b, Vec3d c, out double distance)
    {
        distance = 0;
        const double epsilon = 0.0000001;
        Vec3d edge1 = new(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        Vec3d edge2 = new(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        Vec3d h = direction.Cross(edge2);
        double det = Dot(edge1, h);
        if (det > -epsilon && det < epsilon) return false;

        double invDet = 1.0 / det;
        Vec3d s = new(origin.X - a.X, origin.Y - a.Y, origin.Z - a.Z);
        double u = invDet * Dot(s, h);
        if (u < 0 || u > 1) return false;

        Vec3d q = s.Cross(edge1);
        double v = invDet * Dot(direction, q);
        if (v < 0 || u + v > 1) return false;

        distance = invDet * Dot(edge2, q);
        return distance >= 0;
    }

    private static bool TryIntersectLocalAabb(Vec3d origin, Vec3d direction, Vec3f[] corners, out double distance)
    {
        distance = 0;
        if (corners.Length == 0) return false;

        double minX = corners.Min(corner => corner.X);
        double minY = corners.Min(corner => corner.Y);
        double minZ = corners.Min(corner => corner.Z);
        double maxX = corners.Max(corner => corner.X);
        double maxY = corners.Max(corner => corner.Y);
        double maxZ = corners.Max(corner => corner.Z);

        double tMin = 0;
        double tMax = double.MaxValue;
        if (!UpdateRaySlab(origin.X, direction.X, minX, maxX, ref tMin, ref tMax)) return false;
        if (!UpdateRaySlab(origin.Y, direction.Y, minY, maxY, ref tMin, ref tMax)) return false;
        if (!UpdateRaySlab(origin.Z, direction.Z, minZ, maxZ, ref tMin, ref tMax)) return false;

        distance = tMin >= 0 ? tMin : tMax;
        return distance >= 0 && distance < double.MaxValue;
    }

    private static bool UpdateRaySlab(double origin, double direction, double min, double max, ref double tMin, ref double tMax)
    {
        const double epsilon = 0.000001;
        if (Math.Abs(direction) < epsilon)
        {
            return origin >= min && origin <= max;
        }

        double t1 = (min - origin) / direction;
        double t2 = (max - origin) / direction;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
        return tMin <= tMax;
    }

    private static bool UnprojectViewportPoint(double[] inverseClipFromLocal, Vector2 viewportMin, float width, float height, Vector2 mouse, double clipZ, out Vec3d local)
    {
        local = new Vec3d();
        double ndcX = 2.0 * (mouse.X - viewportMin.X) / Math.Max(1f, width) - 1.0;
        double ndcY = 1.0 - 2.0 * (mouse.Y - viewportMin.Y) / Math.Max(1f, height);
        double[] result = Mat4d.MulWithVec4(inverseClipFromLocal, [ndcX, ndcY, clipZ, 1.0]);
        if (Math.Abs(result[3]) < 0.000001) return false;

        local.X = result[0] / result[3];
        local.Y = result[1] / result[3];
        local.Z = result[2] / result[3];
        return IsFinite((float)local.X) && IsFinite((float)local.Y) && IsFinite((float)local.Z);
    }

    private static double[] ToDoubleMatrix(float[] values)
    {
        double[] result = new double[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static double Dot(Vec3d left, Vec3d right)
    {
        return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }
}
