using InGameDevTools.Animations;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;

namespace InGameDevTools.Tests;

public sealed class AnimationGeneratorTests
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly Type ManagerType = typeof(DebugWindowManager);
    private static readonly Type WaveEnum = ManagerType.GetNestedType("VanillaGenWave", BindingFlags.NonPublic)!;
    private static readonly Type TargetEnum = ManagerType.GetNestedType("VanillaGenChannelTarget", BindingFlags.NonPublic)!;
    private static readonly Type ChannelStruct = ManagerType.GetNestedType("VanillaGenElementChannel", BindingFlags.NonPublic)!;
    private static readonly Type ParamsType = ManagerType.GetNestedType("VanillaGenParams", BindingFlags.NonPublic)!;
    private static readonly Type ModeEnum = ManagerType.GetNestedType("VanillaGenMode", BindingFlags.NonPublic)!;
    private static readonly Type ActionEnum = ManagerType.GetNestedType("VanillaGenAction", BindingFlags.NonPublic)!;
    private static readonly Type ExportServiceType = ManagerType.GetNestedType("VanillaAnimationExportService", BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("Sine", 0.0, 0.0)]
    [InlineData("Sine", 0.25, 1.0)]
    [InlineData("Cosine", 0.0, 1.0)]
    [InlineData("Triangle", 0.25, 1.0)]
    [InlineData("Triangle", 0.75, -1.0)]
    [InlineData("Square", 0.25, 1.0)]
    [InlineData("Square", 0.75, -1.0)]
    public void EvalWave_MatchesExpectedShape(string wave, double cyclePos, double expected)
    {
        Assert.Equal(expected, EvalWave(wave, cyclePos), 3);
    }

    [Fact]
    public void EvalWave_IsPeriodicAcrossIntegerCycles()
    {
        // Integer cycle counts keep the loop seamless: f(t) == f(t + n).
        Assert.Equal(EvalWave("Sine", 0.4), EvalWave("Sine", 2.4), 6);
        Assert.Equal(EvalWave("Triangle", 0.1), EvalWave("Triangle", 5.1), 6);
    }

    [Fact]
    public void BuildKeyFrames_SamplesFramesAndDrivesOnlyTheChosenChannel()
    {
        object parameters = Activator.CreateInstance(ParamsType, nonPublic: true)!;
        SetField(parameters, "Frames", 30);
        SetField(parameters, "SampleCount", 8);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("leg", "RotationZ", "Sine", 30.0, 1, 0.0, 0.0));

        Array keyFrames = InvokeBuildKeyFrames(parameters, channels);

        // 8 evenly spaced, distinct sample frames starting at 0.
        Assert.Equal(8, keyFrames.Length);
        object first = keyFrames.GetValue(0)!;
        Assert.Equal(0, Convert.ToInt32(GetMember(first, "Frame")));

        foreach (object keyFrame in keyFrames)
        {
            int frame = Convert.ToInt32(GetMember(keyFrame, "Frame"));
            Assert.InRange(frame, 0, 29);

            var elements = (IDictionary)GetMember(keyFrame, "Elements")!;
            object element = elements["leg"]!;
            Assert.NotNull(GetMember(element, "RotationZ"));   // driven
            Assert.Null(GetMember(element, "OffsetX"));        // untouched
            Assert.Null(GetMember(element, "StretchX"));       // untouched
        }

        // Sine at frame 0 with zero phase is 0 rotation.
        object firstElement = ((IDictionary)GetMember(keyFrames.GetValue(0)!, "Elements")!)["leg"]!;
        Assert.Equal(0.0, Convert.ToDouble(GetMember(firstElement, "RotationZ")), 3);
    }

    [Fact]
    public void BuildKeyFrames_StretchOscillatesAroundOne()
    {
        object parameters = Activator.CreateInstance(ParamsType, nonPublic: true)!;
        SetField(parameters, "Frames", 40);
        SetField(parameters, "SampleCount", 4);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("body", "StretchX", "Sine", 0.5, 1, 0.0, 0.0));

        Array keyFrames = InvokeBuildKeyFrames(parameters, channels);
        object firstElement = ((IDictionary)GetMember(keyFrames.GetValue(0)!, "Elements")!)["body"]!;

        // Sine is 0 at frame 0, so the stretch sits at its rest multiplier of 1.0 (not 0).
        Assert.Equal(1.0, Convert.ToDouble(GetMember(firstElement, "StretchX")), 3);
    }

    [Fact]
    public void BuildGlobs_SupportsWildcardsAndSubstring()
    {
        List<Regex> globs = BuildGlobs("leg*, tail");

        Assert.Contains(globs, g => g.IsMatch("legR1"));
        Assert.Contains(globs, g => g.IsMatch("tail3"));   // substring
        Assert.DoesNotContain(globs, g => g.IsMatch("armR1"));
    }

    [Fact]
    public void GaitPhase_BipedAlternatesLeftRight()
    {
        // One leg row = biped: left and right are half a cycle apart.
        Assert.Equal(0.0, GaitFraction("Walk", row: 0, rowCount: 1, side: 0), 3);
        Assert.Equal(0.5, GaitFraction("Walk", row: 0, rowCount: 1, side: 1), 3);
    }

    [Fact]
    public void GaitPhase_QuadrupedWalkIsFourDistinctBeats()
    {
        // The fix: a walk must not move both sides in lockstep. All four feet get distinct, evenly spaced phases.
        double fl = GaitFraction("Walk", 0, 2, 0);
        double fr = GaitFraction("Walk", 0, 2, 1);
        double bl = GaitFraction("Walk", 1, 2, 0);
        double br = GaitFraction("Walk", 1, 2, 1);

        Assert.Equal(4, new HashSet<double> { fl, fr, bl, br }.Count);
        foreach (double phase in new[] { fl, fr, bl, br })
        {
            Assert.Equal(0.0, (phase * 4.0) % 1.0, 3); // each foot lands on a quarter-cycle beat
        }
    }

    [Fact]
    public void GaitPhase_TrotMovesDiagonalPairsTogether()
    {
        double fl = GaitFraction("Trot", 0, 2, 0);
        double br = GaitFraction("Trot", 1, 2, 1);
        double fr = GaitFraction("Trot", 0, 2, 1);
        double bl = GaitFraction("Trot", 1, 2, 0);

        Assert.Equal(fl, br, 3);                    // one diagonal pair moves together
        Assert.Equal(fr, bl, 3);                    // the other diagonal pair moves together
        Assert.Equal(0.5, Math.Abs(fl - fr), 3);    // and the two diagonals are half a cycle apart
    }

    [Fact]
    public void StanceWave_PlantsForwardThenSweepsBack()
    {
        const double duty = 0.7;
        Assert.Equal(1.0, EvalWave("Stance", 0.0, duty), 3);    // foot starts forward
        Assert.Equal(-1.0, EvalWave("Stance", duty, duty), 3);  // and is at its back-most exactly when stance ends
        Assert.True(EvalWave("Stance", 0.2, duty) > EvalWave("Stance", 0.5, duty)); // monotonic back through stance
        Assert.Equal(1.0, EvalWave("Stance", 0.999, duty), 1);  // swing returns it forward to loop
    }

    [Fact]
    public void SwingBumpWave_FiresOnlyDuringSwing()
    {
        const double duty = 0.6;
        Assert.Equal(0.0, EvalWave("SwingBump", 0.0, duty), 3);   // planted: no flex
        Assert.Equal(0.0, EvalWave("SwingBump", 0.3, duty), 3);   // still stance
        Assert.Equal(0.0, EvalWave("SwingBump", 0.59, duty), 3);  // just before lift-off
        Assert.Equal(1.0, EvalWave("SwingBump", duty + 0.5 * (1.0 - duty), duty), 3); // peak mid-swing
    }

    [Fact]
    public void BuildLocomotionRig_FindsFourLegsTwoRowsTwoSides()
    {
        // The core fix: front/back rows and left/right sides are read from real geometry, so a quadruped is a
        // 2-row gait (not a 1-row biped that collapses into a left/right mirror). Each leg keeps its joint chain.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);
        HashSet<string> names = CollectNames(shape);

        MethodInfo build = ManagerType.GetMethod("BuildVanillaLocomotionRig", InstanceFlags)!;
        object rig = build.Invoke(manager, [document, names])!;

        var legs = ((IEnumerable)GetMember(rig, "Legs")!).Cast<object>().ToList();
        Assert.Equal(4, legs.Count);
        Assert.Equal(2, Convert.ToInt32(GetMember(rig, "LegRowCount")));

        foreach (object leg in legs)
        {
            Assert.Equal(2, ((IList)GetMember(leg, "Segments")!).Count); // hip + lower joint
        }

        List<int> sides = legs.Select(l => Convert.ToInt32(GetMember(l, "Side"))).ToList();
        Assert.Equal(2, sides.Count(s => s == 0)); // two left
        Assert.Equal(2, sides.Count(s => s == 1)); // two right

        List<int> rows = legs.Select(l => Convert.ToInt32(GetMember(l, "Row"))).Distinct().OrderBy(r => r).ToList();
        Assert.Equal([0, 1], rows); // a clear front row and back row
    }

    [Fact]
    public void Locomotion_WingsFlapTogetherNotAlternating()
    {
        // A bird's wings beat up and down in sync. The two wings mirror across the lateral axis, so the
        // generator must give them opposite-signed flap rotations (with the same phase) - that makes them
        // move as a mirror image (both up, both down) instead of alternating.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildWingedShape();
        object document = CreateShapeDocument(shape);

        object parameters = Activator.CreateInstance(ParamsType, nonPublic: true)!;
        Type gaitEnum = ManagerType.GetNestedType("VanillaGenGait", BindingFlags.NonPublic)!;
        SetField(parameters, "Gait", Enum.Parse(gaitEnum, "Fly"));
        SetField(parameters, "WingFlap", 40f);

        List<object> channels = InvokeBuildLocomotionChannels(manager, document, ["wingLeft", "wingRight"], parameters);

        object left = channels.Single(c => (string)GetMember(c, "Element")! == "wingLeft");
        object right = channels.Single(c => (string)GetMember(c, "Element")! == "wingRight");

        // Same flap axis and phase...
        Assert.Equal("RotationX", GetMember(left, "Field")!.ToString());
        Assert.Equal("RotationX", GetMember(right, "Field")!.ToString());
        Assert.Equal(Convert.ToDouble(GetMember(left, "PhaseDeg")), Convert.ToDouble(GetMember(right, "PhaseDeg")), 6);

        // ...but mirrored amplitude, so one flap lifts both wings the same way.
        double leftAmplitude = Convert.ToDouble(GetMember(left, "Amplitude"));
        double rightAmplitude = Convert.ToDouble(GetMember(right, "Amplitude"));
        Assert.NotEqual(0.0, leftAmplitude);
        Assert.Equal(-leftAmplitude, rightAmplitude, 6);
    }

    [Fact]
    public void Intensity_ScalesEveryChannelAmplitude()
    {
        object normal = MakeParams(40, 40);
        object boosted = MakeParams(40, 40);
        SetField(boosted, "Intensity", 2f);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("leg", "RotationZ", "Sine", 30.0, 1, 0.0, 0.0));

        double normalPeak = MaxAbsField(InvokeBuildKeyFrames(normal, channels), "leg", "RotationZ");
        double boostedPeak = MaxAbsField(InvokeBuildKeyFrames(boosted, channels), "leg", "RotationZ");

        Assert.InRange(boostedPeak / normalPeak, 1.9, 2.1);
    }

    [Fact]
    public void GlobalPhase_ShiftsTheWaveAtFrameZero()
    {
        // A +90 deg global phase turns a zero-phase sine (0 at frame 0) into its peak at frame 0.
        object parameters = MakeParams(40, 8);
        SetField(parameters, "GlobalPhase", 90f);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("leg", "RotationZ", "Sine", 30.0, 1, 0.0, 0.0));

        Assert.Equal(30.0, FieldAtFrame(InvokeBuildKeyFrames(parameters, channels), 0, "leg", "RotationZ"), 1);
    }

    [Fact]
    public void Reverse_PlaysTheLoopBackwards()
    {
        object forward = MakeParams(40, 40);
        object reversed = MakeParams(40, 40);
        SetField(reversed, "Reverse", true);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("t", "RotationY", "Sawtooth", 20.0, 1, 0.0, 0.0));

        double f = FieldAtFrame(InvokeBuildKeyFrames(forward, channels), 10, "t", "RotationY");
        double r = FieldAtFrame(InvokeBuildKeyFrames(reversed, channels), 10, "t", "RotationY");

        // The reversed loop reads the mirror point of the cycle, which for a centred sawtooth negates the value.
        Assert.Equal(-f, r, 3);
    }

    [Fact]
    public void Sharpness_AboveOnePushesMidValuesTowardTheExtremes()
    {
        object parameters = MakeParams(48, 48);
        IList soft = NewChannelList();
        soft.Add(MakeChannel("e", "RotationX", "Sine", 1.0, 1, 0.0, 0.0, sharpness: 1.0));
        IList sharp = NewChannelList();
        sharp.Add(MakeChannel("e", "RotationX", "Sine", 1.0, 1, 0.0, 0.0, sharpness: 2.0));

        // Frame 4 of 48 is the 30 deg point of the sine: exactly 0.5 at sharpness 1.
        double plain = FieldAtFrame(InvokeBuildKeyFrames(parameters, soft), 4, "e", "RotationX");
        double sharpened = FieldAtFrame(InvokeBuildKeyFrames(parameters, sharp), 4, "e", "RotationX");

        Assert.Equal(0.5, plain, 2);
        Assert.True(sharpened > plain, $"sharpened {sharpened} should exceed plain {plain}");
    }

    [Fact]
    public void Locomotion_FootLift_LiftsTheToeDuringSwing()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object parameters = MakeParams(30, 12);
        SetField(parameters, "FootLift", 3f);

        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), parameters);

        // The toe (last leg segment, named "...lower") gets a vertical swing-bump offset.
        Assert.Contains(channels, c =>
            ((string)GetMember(c, "Element")!).EndsWith("lower") && GetMember(c, "Field")!.ToString() == "OffsetY");
    }

    [Fact]
    public void Locomotion_BodyPitch_AddsATorsoRock()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object parameters = MakeParams(30, 12);
        SetField(parameters, "BodyPitch", 6f);

        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), parameters);

        Assert.Contains(channels, c =>
            (string)GetMember(c, "Element")! == "body" && GetMember(c, "Field")!.ToString() == "RotationZ");
    }

    private static object MakeParams(int frames, int sampleCount)
    {
        object parameters = Activator.CreateInstance(ParamsType, nonPublic: true)!;
        SetField(parameters, "Frames", frames);
        SetField(parameters, "SampleCount", sampleCount);
        return parameters;
    }

    private static double FieldAtFrame(Array keyFrames, int frame, string element, string field)
    {
        foreach (object keyFrame in keyFrames)
        {
            if (Convert.ToInt32(GetMember(keyFrame, "Frame")) != frame) continue;
            var elements = (IDictionary)GetMember(keyFrame, "Elements")!;
            object? value = GetMember(elements[element]!, field);
            return value == null ? 0.0 : Convert.ToDouble(value);
        }
        throw new InvalidOperationException($"Frame {frame} was not sampled.");
    }

    private static double MaxAbsField(Array keyFrames, string element, string field)
    {
        double max = 0.0;
        foreach (object keyFrame in keyFrames)
        {
            var elements = (IDictionary)GetMember(keyFrame, "Elements")!;
            if (!elements.Contains(element)) continue;
            object? value = GetMember(elements[element]!, field);
            if (value != null) max = Math.Max(max, Math.Abs(Convert.ToDouble(value)));
        }
        return max;
    }

    [Fact]
    public void Pose_Sit_DropsTheBodyAndFoldsALeg()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object parameters = MakeParams(30, 8);
        SetField(parameters, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(parameters, "Action", Enum.Parse(ActionEnum, "Sit"));

        IDictionary pose = InvokeBuildPose(manager, document, CollectNames(shape).ToList(), parameters);

        // The body drops (offsetY down) and at least one leg segment folds hard.
        Assert.True(AbsField(pose["body"]!, "OffsetY") > 0 && Convert.ToDouble(GetMember(pose["body"]!, "OffsetY")) < 0, "body should drop");
        bool foldedLeg = pose.Keys.Cast<string>().Any(k => k.StartsWith("leg") && AbsField(pose[k]!, "RotationZ") > 50.0);
        Assert.True(foldedLeg, "expected a hard-folded rear leg");
    }

    [Fact]
    public void Pose_HeldStartsAtThePose_ReturnToRestStartsAtRest()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);
        List<string> targets = CollectNames(shape).ToList();

        object held = MakeParams(40, 8);
        SetField(held, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(held, "Action", Enum.Parse(ActionEnum, "Sit"));
        IDictionary pose = InvokeBuildPose(manager, document, targets, held);
        Array heldFrames = InvokeBuildPoseKeyFrames(held, pose);

        object oneShot = MakeParams(40, 8);
        SetField(oneShot, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(oneShot, "Action", Enum.Parse(ActionEnum, "Sit"));
        SetField(oneShot, "ReturnToRest", true);
        Array oneShotFrames = InvokeBuildPoseKeyFrames(oneShot, pose);

        // Held pose holds the body drop from frame 0; the one-shot starts at rest and ramps in.
        Assert.True(Math.Abs(FieldAtFrame(heldFrames, 0, "body", "OffsetY")) > 4.0, "held should start posed");
        Assert.True(Math.Abs(FieldAtFrame(oneShotFrames, 0, "body", "OffsetY")) < 1.0, "one-shot should start at rest");
    }

    [Fact]
    public void NoiseWave_IsSeamlessAcrossTheLoop()
    {
        // Integer harmonics keep the noise periodic, so f(t) == f(t + 1) and it stays bounded.
        Assert.Equal(EvalWave("Noise", 0.3, 5.0), EvalWave("Noise", 1.3, 5.0), 6);
        Assert.InRange(EvalWave("Noise", 0.37, 2.0), -1.0, 1.0);
    }

    [Fact]
    public void GaitPhase_BoundLandsEachRowTogether()
    {
        // A bound: both feet of a row are in phase, and the two rows are half a cycle apart.
        Assert.Equal(GaitFraction("Bound", 0, 2, 0), GaitFraction("Bound", 0, 2, 1), 3);
        Assert.Equal(0.5, Math.Abs(GaitFraction("Bound", 0, 2, 0) - GaitFraction("Bound", 1, 2, 0)), 3);
    }

    // ---- New technique coverage ----

    [Fact]
    public void GaitPreset_Gallop_EnablesBodySurgeAndSagittalSpineFlex()
    {
        // Task 7/8: the gallop preset turns on a fore-aft body surge and a sagittal spine arch.
        object p = MakeParams(16, 16);
        SetField(p, "Gait", Gait("Gallop"));
        ApplyGaitPreset(p);
        Assert.True(Convert.ToSingle(GetMember(p, "BodySurge")) > 0f, "gallop should surge");
        Assert.True(Convert.ToSingle(GetMember(p, "SpineFlex")) > 0f, "gallop should arch the spine");
    }

    [Fact]
    public void GaitPreset_ClimbPitchesUp_ChargePitchesDown()
    {
        // Tasks 13/14: climb tilts the torso nose-up, charge drops the head/torso forward.
        object climb = MakeParams(34, 12);
        SetField(climb, "Gait", Gait("Climb"));
        ApplyGaitPreset(climb);
        object charge = MakeParams(14, 12);
        SetField(charge, "Gait", Gait("Charge"));
        ApplyGaitPreset(charge);
        Assert.True(Convert.ToSingle(GetMember(climb, "BodyTilt")) > 0f);
        Assert.True(Convert.ToSingle(GetMember(charge, "BodyTilt")) < 0f);
    }

    [Fact]
    public void Locomotion_Backward_ReversesHipStride()
    {
        // Task 16: stepping backward flips the sign of the hip's fore-aft stride amplitude.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);
        List<string> targets = CollectNames(shape).ToList();

        object fwd = MakeParams(30, 12);
        object back = MakeParams(30, 12);
        SetField(back, "Backward", true);

        double fwdHip = HipStanceAmplitude(InvokeBuildLocomotionChannels(manager, document, targets, fwd));
        double backHip = HipStanceAmplitude(InvokeBuildLocomotionChannels(manager, document, targets, back));
        Assert.Equal(-fwdHip, backHip, 3);
    }

    [Fact]
    public void Locomotion_LegTuck_FoldsLegsInsteadOfStriding()
    {
        // Task 20: with leg tuck the hip holds a constant fold (Sine amp 0, non-zero bias) and never strides.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(24, 8);
        SetField(p, "LegTuck", 1f);
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        Assert.DoesNotContain(channels, c => GetMember(c, "Wave")!.ToString() == "Stance");
        Assert.Contains(channels, c =>
            ((string)GetMember(c, "Element")!).StartsWith("leg") && Math.Abs(Convert.ToDouble(GetMember(c, "Bias"))) > 1.0);
    }

    [Fact]
    public void Locomotion_BodySurge_AddsForeAftOffsetOnBody()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(16, 8);
        SetField(p, "BodySurge", 4f);
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        Assert.Contains(channels, c =>
            (string)GetMember(c, "Element")! == "body" && GetMember(c, "Field")!.ToString() == "OffsetX");
    }

    [Fact]
    public void RotShortest_FlaggedWhenRotationSpansMoreThan180()
    {
        // Task 3: a large rotation arc gets the shortest-path flag so the game does not spin it the long way.
        object p = MakeParams(40, 12);
        IList channels = NewChannelList();
        channels.Add(MakeChannel("e", "RotationZ", "Sine", 120.0, 1, 0.0, 0.0)); // span 240 deg > 180

        Array keyFrames = InvokeBuildKeyFrames(p, channels);
        bool anyFlag = keyFrames.Cast<object>().Any(kf =>
        {
            var elements = (IDictionary)GetMember(kf, "Elements")!;
            return elements.Contains("e") && Convert.ToBoolean(GetMember(elements["e"]!, "RotShortestDistanceZ"));
        });
        Assert.True(anyFlag, "expected RotShortestDistanceZ to be set for a >180 deg arc");
    }

    [Fact]
    public void Optimize_PrunesConstantChannelInteriorKeyframes()
    {
        // Tasks 1/2: a constant channel (bias only) needs only its endpoints; interior values are pruned.
        object p = MakeParams(40, 10);
        SetField(p, "OptimizeKeyFrames", true);
        IList channels = NewChannelList();
        channels.Add(MakeChannel("e", "RotationZ", "Sine", 0.0, 1, 0.0, 15.0)); // amplitude 0 -> constant 15

        Array keyFrames = InvokeBuildKeyFrames(p, channels);
        int setCount = keyFrames.Cast<object>().Count(kf =>
        {
            var elements = (IDictionary)GetMember(kf, "Elements")!;
            return elements.Contains("e") && GetMember(elements["e"]!, "RotationZ") != null;
        });
        Assert.True(setCount <= 3, $"a constant channel should collapse to its endpoints, got {setCount} set keyframes");
    }

    [Fact]
    public void Export_CompletesPartialTransformGroups()
    {
        AnimationKeyFrameElement element = new()
        {
            OffsetY = 2.5,
            RotationZ = 15,
            StretchX = 1.25
        };

        JObject token = InvokeToVanillaElementToken(element);

        Assert.Contains("offsetX", token.Properties().Select(property => property.Name));
        Assert.Contains("rotationX", token.Properties().Select(property => property.Name));
        Assert.Contains("stretchY", token.Properties().Select(property => property.Name));
        Assert.Equal(0.0, token.Value<double>("offsetX"));
        Assert.Equal(2.5, token.Value<double>("offsetY"));
        Assert.Equal(0.0, token.Value<double>("offsetZ"));
        Assert.Equal(0.0, token.Value<double>("rotationX"));
        Assert.Equal(0.0, token.Value<double>("rotationY"));
        Assert.Equal(15.0, token.Value<double>("rotationZ"));
        Assert.Equal(1.25, token.Value<double>("stretchX"));
        Assert.Equal(1.0, token.Value<double>("stretchY"));
        Assert.Equal(1.0, token.Value<double>("stretchZ"));
        Assert.False(token.ContainsKey("originX"));
    }

    [Fact]
    public void EndHandling_AutoLoopsCyclesAndHoldsDeath()
    {
        object gait = MakeParams(30, 12);
        SetField(gait, "Mode", Enum.Parse(ModeEnum, "Locomotion"));
        SetField(gait, "Loop", true);
        Assert.Equal("Repeat", ResolveEndHandling(gait));

        object death = MakeParams(40, 8);
        SetField(death, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(death, "Action", Enum.Parse(ActionEnum, "Death"));
        SetField(death, "ReturnToRest", true);
        Assert.Equal("Hold", ResolveEndHandling(death));
        Assert.Equal("PlayTillEnd", ResolveStopHandling(death));
    }

    [Fact]
    public void Overlay_TurnLeft_BendsNeckOneWayAndTailTheOther()
    {
        // Task 5: a turn overlay yaws the neck/head into the turn and trails the tail the opposite way.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildNeckTailShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(1, 2);
        SetField(p, "Overlay", Enum.Parse(OverlayEnum, "TurnLeft"));
        SetField(p, "OverlayAmount", 30f);

        IDictionary pose = InvokeBuildOverlayPose(manager, document, CollectNames(shape).ToList(), p);
        double head = Convert.ToDouble(GetMember(pose["head"]!, "RotationY"));
        double tailTip = Convert.ToDouble(GetMember(pose["tail2"]!, "RotationY"));
        Assert.True(head > 0, "head should yaw into the turn");
        Assert.True(tailTip < 0, "tail should trail the opposite way");
    }

    [Fact]
    public void Pose_Howl_RaisesHeadAndOpensJaw()
    {
        // Task 22: the howl tilts the head back and opens the jaw.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildNeckTailShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(60, 10);
        SetField(p, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(p, "Action", Enum.Parse(ActionEnum, "Howl"));
        IDictionary pose = InvokeBuildPose(manager, document, CollectNames(shape).ToList(), p);

        Assert.True(Convert.ToDouble(GetMember(pose["head"]!, "RotationZ")) > 10, "head tilts back");
        Assert.True(Convert.ToDouble(GetMember(pose["jaw"]!, "RotationZ")) < -10, "jaw opens");
    }

    [Fact]
    public void Transition_StartsAtFromPoseAndEndsAtToPose()
    {
        // Task 28: the From->To transition reads the From pose at frame 0 and the To pose at the end.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);
        List<string> targets = CollectNames(shape).ToList();

        object sit = MakeParams(40, 8);
        SetField(sit, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(sit, "Action", Enum.Parse(ActionEnum, "Sit"));
        IDictionary from = InvokeBuildPose(manager, document, targets, sit);

        object rear = MakeParams(40, 8);
        SetField(rear, "Mode", Enum.Parse(ModeEnum, "Pose"));
        SetField(rear, "Action", Enum.Parse(ActionEnum, "Rear"));
        IDictionary to = InvokeBuildPose(manager, document, targets, rear);

        Array frames = InvokeBuildTransitionKeyFrames(sit, from, to);
        double bodyOffY0 = FieldAtFrame(frames, 0, "body", "OffsetY");
        double bodyRotZEnd = FieldAtFrame(frames, 39, "body", "RotationZ");
        Assert.True(bodyOffY0 < -3, "should start in the sit (body dropped)");
        Assert.True(bodyRotZEnd > 30, "should end in the rear (body pitched up)");
    }

    [Theory]
    [InlineData("Overshoot", 0.85, true)]   // overshoot passes above 1 before settling
    [InlineData("Anticipate", 0.15, false)] // anticipate dips below 0 first
    public void Envelope_ShapesTheRampPastItsEndpoints(string env, double r, bool aboveOne)
    {
        double v = EnvelopeShape(r, env);
        if (aboveOne) Assert.True(v > 1.0, $"{env} at {r} should exceed 1, got {v}");
        else Assert.True(v < 0.0, $"{env} at {r} should dip below 0, got {v}");
    }

    [Fact]
    public void GaitPhase_ManyLeggedWalkRipplesAsAMetachronalWave()
    {
        // A centipede (5 leg rows) walking: the rows fire in a travelling wave down the body, not one alternation.
        double[] side0 = Enumerable.Range(0, 5).Select(r => GaitFraction("Walk", r, 5, 0)).ToArray();
        Assert.Equal(5, side0.Distinct().Count());                          // every row at a distinct phase
        for (int r = 1; r < 5; r++) Assert.True(side0[r] > side0[r - 1]);   // a monotonic wave, front to back
        // The two sides are in antiphase (left/right alternate).
        Assert.Equal(0.5, Math.Abs(GaitFraction("Walk", 0, 5, 0) - GaitFraction("Walk", 0, 5, 1)), 3);
    }

    [Fact]
    public void GaitPhase_SixLeggedWalkKeepsTheAlternatingTripod()
    {
        // A 6-legged insect (3 rows) walks with the tripod gait: the middle leg opposes its neighbours.
        Assert.Equal(0.0, GaitFraction("Walk", 0, 3, 0), 3);
        Assert.Equal(0.5, GaitFraction("Walk", 1, 3, 0), 3);
        Assert.Equal(0.0, GaitFraction("Walk", 2, 3, 0), 3);
    }

    [Fact]
    public void GaitPhase_BipedBoundHopsBothFeetTogether()
    {
        // A bound on two legs is a hop: both feet in phase. A walk still alternates them.
        Assert.Equal(GaitFraction("Bound", 0, 1, 0), GaitFraction("Bound", 0, 1, 1), 3);
        Assert.Equal(0.5, Math.Abs(GaitFraction("Walk", 0, 1, 0) - GaitFraction("Walk", 0, 1, 1)), 3);
    }

    [Fact]
    public void HeadStabilize_CountersBodyPitchAndRoll()
    {
        // Gaze stabilization: the head gets equal-and-opposite rotations to the body's inherited pitch/roll.
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildNeckTailShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(30, 12);
        SetField(p, "BodyPitch", 6f);
        SetField(p, "BodyRoll", 4f);
        SetField(p, "HeadStabilize", 1f);
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        List<object> head = channels.Where(c => (string)GetMember(c, "Element")! == "head").ToList();
        Assert.Contains(head, c => GetMember(c, "Field")!.ToString() == "RotationZ" && Convert.ToDouble(GetMember(c, "Amplitude")) < 0);
        Assert.Contains(head, c => GetMember(c, "Field")!.ToString() == "RotationX" && Convert.ToDouble(GetMember(c, "Amplitude")) < 0);
    }

    [Fact]
    public void Locomotion_SkipsRoundingFacetsSoTheyRideTheParentBone()
    {
        // The model generator's smoothing adds facet children named "{bone}Round{n}". They must NOT get their own
        // spine channel (that would double-bend them and shift the per-segment phase sequence).
        DebugWindowManager manager = CreateManager();
        ShapeElement facet = Element("spine1Round1", [0, 0, 0], [4, 4, 4], null);
        ShapeElement spine1 = Element("spine1", [0, 0, 0], [16, 4, 4], [facet]);
        Shape shape = new() { Elements = [spine1] };
        object document = CreateShapeDocument(shape);

        object p = MakeParams(30, 12);
        SetField(p, "SpineBend", 10f);
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        Assert.Contains(channels, c => (string)GetMember(c, "Element")! == "spine1");
        Assert.DoesNotContain(channels, c => (string)GetMember(c, "Element")! == "spine1Round1");
    }

    [Fact]
    public void Locomotion_SkipsGeneratedMembranesSoTheyRideTheWingSpar()
    {
        // Generated wing membranes are visual surface panels under a real spar bone. Animating both creates a
        // second transform on the panel, which reads as a duplicate wing clipping through the animated spar.
        DebugWindowManager manager = CreateManager();
        ShapeElement membrane = Element("wingLeftMembrane1", [0, 0, 0], [8, 1, 8], null);
        ShapeElement spar = Element("wingLeftSpar1", [0, 0, 0], [8, 1, 1], [membrane]);
        ShapeElement body = Element("body", [0, 0, 0], [16, 16, 16], [spar]);
        Shape shape = new() { Elements = [body] };
        object document = CreateShapeDocument(shape);

        object p = MakeParams(30, 12);
        SetField(p, "WingFlap", 35f); // wings only flap when asked (a flight gait or a manual amplitude)
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        Assert.Contains(channels, c => (string)GetMember(c, "Element")! == "wingLeftSpar1");
        Assert.DoesNotContain(channels, c => (string)GetMember(c, "Element")! == "wingLeftMembrane1");
    }

    [Fact]
    public void JigglePhysics_LoosePartOvershootsItsInput_RigidPartDoesNot()
    {
        // A floppy, bouncy spring driven near resonance overshoots its target's range (follow-through), and the
        // physics only touches loose parts - a "body" element is left exactly as keyed.
        object p = MakeParams(48, 48);
        SetField(p, "JigglePhysics", true);
        SetField(p, "Floppiness", 1f);
        SetField(p, "JiggleBounce", 1f);

        IList tail = NewChannelList();
        tail.Add(MakeChannel("tail", "RotationZ", "Sine", 20.0, 1, 0.0, 0.0));
        double tailPeak = MaxAbsField(InvokeBuildKeyFrames(p, tail), "tail", "RotationZ");
        Assert.True(tailPeak > 24.0, $"the spring should overshoot the 20 deg input (got {tailPeak})");

        IList body = NewChannelList();
        body.Add(MakeChannel("body", "RotationZ", "Sine", 20.0, 1, 0.0, 0.0));
        double bodyPeak = MaxAbsField(InvokeBuildKeyFrames(p, body), "body", "RotationZ");
        Assert.Equal(20.0, bodyPeak, 1); // rigid: untouched
    }

    [Fact]
    public void JigglePhysics_OffIsAByteIdenticalNoOp()
    {
        object on = MakeParams(48, 48);
        object off = MakeParams(48, 48);
        SetField(off, "JigglePhysics", false);

        IList channels = NewChannelList();
        channels.Add(MakeChannel("tail", "RotationZ", "Sine", 20.0, 1, 0.0, 0.0));

        Assert.Equal(20.0, MaxAbsField(InvokeBuildKeyFrames(off, channels), "tail", "RotationZ"), 1);
    }

    [Fact]
    public void SpeedModel_FasterMeansQuickerCadenceLongerStrideLowerDuty()
    {
        object baseline = MakeParams(0, 0);
        SetField(baseline, "Gait", Gait("Walk"));
        SetField(baseline, "Speed", 1f);
        ApplyGaitPreset(baseline);

        object fast = MakeParams(0, 0);
        SetField(fast, "Gait", Gait("Walk"));
        SetField(fast, "Speed", 2f);
        ApplyGaitPreset(fast);

        Assert.True(Convert.ToInt32(GetMember(fast, "Frames")) < Convert.ToInt32(GetMember(baseline, "Frames")), "faster = fewer frames per loop");
        Assert.True(Convert.ToSingle(GetMember(fast, "LegStride")) > Convert.ToSingle(GetMember(baseline, "LegStride")), "faster = longer stride");
        Assert.True(Convert.ToSingle(GetMember(fast, "StanceRatio")) < Convert.ToSingle(GetMember(baseline, "StanceRatio")), "faster = lower duty factor");
    }

    [Fact]
    public void CoupledChainPhysics_LagAccumulatesTowardTheTip()
    {
        // Two identical sine-driven tail segments. With the hierarchy known, the child is dragged by the
        // parent's lag, so the tip peaks LATER than the root (a real whip); without it the springs are
        // independent and peak together.
        IList channels = NewChannelList();
        channels.Add(MakeChannel("tail1", "RotationZ", "Sine", 20.0, 1, 0.0, 0.0));
        channels.Add(MakeChannel("tail2", "RotationZ", "Sine", 20.0, 1, 0.0, 0.0));

        object coupled = MakeParams(60, 60);
        SetField(coupled, "JigglePhysics", true);
        SetField(coupled, "Floppiness", 1f);
        SetField(coupled, "JiggleBounce", 0.3f);
        SetField(coupled, "HierarchyParents", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tail2"] = "tail1" });
        Array cFrames = InvokeBuildKeyFrames(coupled, channels);
        Assert.True(PeakFrame(cFrames, "tail2", "RotationZ") > PeakFrame(cFrames, "tail1", "RotationZ"),
            "the coupled tip should lag behind the root");

        object indep = MakeParams(60, 60);
        SetField(indep, "JigglePhysics", true);
        SetField(indep, "Floppiness", 1f);
        SetField(indep, "JiggleBounce", 0.3f);
        Array iFrames = InvokeBuildKeyFrames(indep, channels);
        Assert.Equal(PeakFrame(iFrames, "tail1", "RotationZ"), PeakFrame(iFrames, "tail2", "RotationZ"));
    }

    [Theory]
    [InlineData(0.0, -7.0)]
    [InlineData(3.0, -6.0)]
    [InlineData(-3.5, -6.0)]
    public void FootLockIk_PlacesTheFootExactlyAtTheTarget(double fx, double fy)
    {
        // The closed-form 2-bone IK is exact: forward kinematics of the solved angles lands on the target.
        (double hip, double knee) = SolveIk(fx, fy, 4.0, 4.0, -1);
        (double x, double y) = Fk(hip, knee, 4.0, 4.0);
        Assert.Equal(fx, x, 3);
        Assert.Equal(fy, y, 3);
    }

    [Fact]
    public void FootLock_PlantsTheStanceFootWithNoSlide()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildQuadrupedShape();
        object document = CreateShapeDocument(shape);

        object p = MakeParams(40, 12);
        SetField(p, "FootLock", true);
        List<object> channels = InvokeBuildLocomotionChannels(manager, document, CollectNames(shape).ToList(), p);

        // Foot-lock replaces the analytic hip Stance wave with IK curve channels.
        Assert.DoesNotContain(channels, c => GetMember(c, "Wave")!.ToString() == "Stance");
        object hipCh = channels.First(c => (string)GetMember(c, "Element")! == "legLeftFront" && GetMember(c, "Curve") != null);
        object kneeCh = channels.First(c => (string)GetMember(c, "Element")! == "legLeftFrontlower" && GetMember(c, "Curve") != null);
        var hipFn = (System.Func<double, double>)GetMember(hipCh, "Curve")!;
        var kneeFn = (System.Func<double, double>)GetMember(kneeCh, "Curve")!;

        // Across the stance phase (duty ~0.62), the foot holds a constant height and sweeps monotonically back.
        double plantedY = double.NaN, prevX = double.NaN;
        for (double t = 0.02; t < 0.55; t += 0.05)
        {
            (double x, double y) = Fk(hipFn(t), kneeFn(t), 4.0, 4.0);
            if (double.IsNaN(plantedY)) plantedY = y;
            else Assert.Equal(plantedY, y, 2);
            if (!double.IsNaN(prevX)) Assert.True(x < prevX + 1e-6, "the stance foot must not slide forward");
            prevX = x;
        }
    }

    [Fact]
    public void GaitPreset_OnlyFlightGaitFlapsWings()
    {
        // The fix: a ground/water gait holds the wings still; only a flight gait turns flapping on.
        Assert.Equal(0f, WingFlapAfterPreset("Walk"), 3);
        Assert.Equal(0f, WingFlapAfterPreset("Swim"), 3);
        Assert.Equal(0f, WingFlapAfterPreset("Stalk"), 3);
        Assert.True(WingFlapAfterPreset("Fly") > 0f, "fly should flap");
    }

    [Fact]
    public void GaitPreset_SwitchingFromFlyToGroundStopsAllFlightMotion()
    {
        object p = MakeParams(20, 12);
        SetField(p, "Gait", Gait("Fly"));
        ApplyGaitPreset(p);
        Assert.True(Convert.ToSingle(GetMember(p, "WingFlap")) > 0f);
        Assert.True(Convert.ToSingle(GetMember(p, "LegTuck")) > 0f);

        SetField(p, "Gait", Gait("Walk"));
        ApplyGaitPreset(p);
        // No flight-only motion leaks onto the walk.
        Assert.Equal(0f, Convert.ToSingle(GetMember(p, "WingFlap")), 3);
        Assert.Equal(0f, Convert.ToSingle(GetMember(p, "LegTuck")), 3);
        Assert.Equal(0f, Convert.ToSingle(GetMember(p, "FlightBob")), 3);
        Assert.Equal(0f, Convert.ToSingle(GetMember(p, "WingChainLag")), 3);
    }

    [Fact]
    public void Locomotion_WalkEmitsNoWingFlapChannel_FlyDoes()
    {
        DebugWindowManager manager = CreateManager();
        Shape shape = BuildWingedShape();
        object document = CreateShapeDocument(shape);
        List<string> targets = CollectNames(shape).ToList();

        object walk = MakeParams(30, 12);
        SetField(walk, "Gait", Gait("Walk"));
        ApplyGaitPreset(walk);
        List<object> walkChannels = InvokeBuildLocomotionChannels(manager, document, targets, walk);
        Assert.DoesNotContain(walkChannels, c => ((string)GetMember(c, "Element")!).StartsWith("wing"));

        object fly = MakeParams(30, 12);
        SetField(fly, "Gait", Gait("Fly"));
        ApplyGaitPreset(fly);
        List<object> flyChannels = InvokeBuildLocomotionChannels(manager, document, targets, fly);
        Assert.Contains(flyChannels, c => ((string)GetMember(c, "Element")!).StartsWith("wing"));
    }

    private static float WingFlapAfterPreset(string gait)
    {
        object p = MakeParams(20, 12);
        SetField(p, "Gait", Gait(gait));
        ApplyGaitPreset(p);
        return Convert.ToSingle(GetMember(p, "WingFlap"));
    }

    private static int PeakFrame(Array keyFrames, string element, string field)
    {
        int peak = 0;
        double max = double.MinValue;
        foreach (object kf in keyFrames)
        {
            var elements = (IDictionary)GetMember(kf, "Elements")!;
            if (!elements.Contains(element)) continue;
            object? v = GetMember(elements[element]!, field);
            if (v == null) continue;
            double value = Convert.ToDouble(v);
            if (value > max) { max = value; peak = Convert.ToInt32(GetMember(kf, "Frame")); }
        }
        return peak;
    }

    private static (double hip, double knee) SolveIk(double fx, double fy, double l1, double l2, int kneeSign)
    {
        object r = ManagerType.GetMethod("SolveVanillaLegIk", StaticFlags)!.Invoke(null, [fx, fy, l1, l2, kneeSign])!;
        return ((double, double))r;
    }

    private static (double x, double y) Fk(double hip, double knee, double l1, double l2)
    {
        object r = ManagerType.GetMethod("ForwardKinematicsLeg", StaticFlags)!.Invoke(null, [hip, knee, l1, l2])!;
        return ((double, double))r;
    }

    private static double HipStanceAmplitude(List<object> channels)
    {
        object hip = channels.First(c => GetMember(c, "Wave")!.ToString() == "Stance");
        return Convert.ToDouble(GetMember(hip, "Amplitude"));
    }

    private static object Gait(string name) => Enum.Parse(ManagerType.GetNestedType("VanillaGenGait", BindingFlags.NonPublic)!, name);
    private static readonly Type OverlayEnum = ManagerType.GetNestedType("VanillaGenOverlay", BindingFlags.NonPublic)!;

    private static void ApplyGaitPreset(object parameters)
    {
        ManagerType.GetMethod("ApplyVanillaGaitPreset", StaticFlags)!.Invoke(null, [parameters]);
    }

    private static string ResolveEndHandling(object parameters) =>
        ManagerType.GetMethod("ResolveVanillaEndHandling", StaticFlags)!.Invoke(null, [parameters])!.ToString()!;

    private static string ResolveStopHandling(object parameters) =>
        ManagerType.GetMethod("ResolveVanillaStopHandling", StaticFlags)!.Invoke(null, [parameters])!.ToString()!;

    private static double EnvelopeShape(double r, string env)
    {
        Type envEnum = ManagerType.GetNestedType("VanillaGenPoseEnvelope", BindingFlags.NonPublic)!;
        MethodInfo method = ManagerType.GetMethod("VanillaEnvelopeShape", StaticFlags)!;
        return (double)method.Invoke(null, [r, Enum.Parse(envEnum, env), true])!;
    }

    private static IDictionary InvokeBuildOverlayPose(DebugWindowManager manager, object document, List<string> targets, object parameters)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaOverlayPose", InstanceFlags)!;
        return (IDictionary)method.Invoke(manager, [document, targets, parameters])!;
    }

    private static Array InvokeBuildTransitionKeyFrames(object parameters, IDictionary from, IDictionary to)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaTransitionKeyFrames", StaticFlags)!;
        return (Array)method.Invoke(null, [parameters, from, to])!;
    }

    // A body with a multi-segment neck + head + jaw and a two-segment tail (for overlay / vocalization tests).
    private static Shape BuildNeckTailShape()
    {
        ShapeElement jaw = Element("jaw", [0, 14, -8], [2, 16, -6], null);
        ShapeElement head = Element("head", [-2, 14, -8], [2, 18, -4], [jaw]);
        ShapeElement neck2 = Element("neck2", [-1, 12, -6], [1, 16, -2], [head]);
        ShapeElement neck1 = Element("neck1", [-1, 10, -4], [1, 14, 0], [neck2]);
        ShapeElement tail2 = Element("tail2", [-1, 6, 14], [1, 8, 18], null);
        ShapeElement tail1 = Element("tail1", [-1, 6, 8], [1, 8, 14], [tail2]);
        ShapeElement body = Element("body", [0, 0, 0], [16, 16, 16], [neck1, tail1]);
        return new Shape { Elements = [body] };
    }

    private static IDictionary InvokeBuildPose(DebugWindowManager manager, object document, List<string> targets, object parameters)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaPose", InstanceFlags)!;
        return (IDictionary)method.Invoke(manager, [document, targets, parameters])!;
    }

    private static Array InvokeBuildPoseKeyFrames(object parameters, IDictionary pose)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaPoseKeyFrames", StaticFlags)!;
        return (Array)method.Invoke(null, [parameters, pose])!;
    }

    private static double AbsField(object keyElement, string field)
    {
        object? value = GetMember(keyElement, field);
        return value == null ? 0.0 : Math.Abs(Convert.ToDouble(value));
    }

    private static List<object> InvokeBuildLocomotionChannels(DebugWindowManager manager, object document, List<string> targets, object parameters)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaLocomotionChannels", InstanceFlags)!;
        object result = method.Invoke(manager, [document, targets, parameters])!;
        return ((IEnumerable)result).Cast<object>().ToList();
    }

    // A body with a symmetric wing pair on opposite lateral (Z) sides.
    private static Shape BuildWingedShape()
    {
        ShapeElement wingLeft = Element("wingLeft", [0, 8, -10], [2, 10, -2], null);
        ShapeElement wingRight = Element("wingRight", [0, 8, 2], [2, 10, 10], null);
        ShapeElement body = Element("body", [0, 0, 0], [16, 16, 16], [wingLeft, wingRight]);
        return new Shape { Elements = [body] };
    }

    private static double GaitFraction(string gait, int row, int rowCount, int side)
    {
        Type gaitEnum = ManagerType.GetNestedType("VanillaGenGait", BindingFlags.NonPublic)!;
        MethodInfo method = ManagerType.GetMethod("GaitPhaseFraction", StaticFlags)!;
        return (double)method.Invoke(null, [Enum.Parse(gaitEnum, gait), row, rowCount, side])!;
    }

    private static double EvalWave(string wave, double cyclePos)
    {
        return EvalWave(wave, cyclePos, 0.0);
    }

    private static double EvalWave(string wave, double cyclePos, double shape)
    {
        MethodInfo method = ManagerType.GetMethod("EvalVanillaGenWave", StaticFlags)!;
        return (double)method.Invoke(null, [Enum.Parse(WaveEnum, wave), cyclePos, shape])!;
    }

    private static List<Regex> BuildGlobs(string filter)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaGenGlobs", StaticFlags)!;
        return (List<Regex>)method.Invoke(null, [filter])!;
    }

    private static Array InvokeBuildKeyFrames(object parameters, IList channels)
    {
        MethodInfo method = ManagerType.GetMethod("BuildVanillaGenKeyFrames", StaticFlags)!;
        return (Array)method.Invoke(null, [parameters, channels])!;
    }

    private static JObject InvokeToVanillaElementToken(AnimationKeyFrameElement element)
    {
        MethodInfo method = ExportServiceType.GetMethod("ToVanillaElementToken", StaticFlags)!;
        return (JObject)method.Invoke(null, [element, null])!;
    }

    private static IList NewChannelList()
    {
        Type listType = typeof(List<>).MakeGenericType(ChannelStruct);
        return (IList)Activator.CreateInstance(listType)!;
    }

    private static object MakeChannel(string element, string target, string wave, double amplitude, int frequency, double phaseDeg, double bias, double sharpness = 1.0)
    {
        return Activator.CreateInstance(ChannelStruct,
            element, Enum.Parse(TargetEnum, target), Enum.Parse(WaveEnum, wave), amplitude, frequency, phaseDeg, bias, 0.0, sharpness)!;
    }

    private static DebugWindowManager CreateManager()
    {
#pragma warning disable SYSLIB0050
        var manager = (DebugWindowManager)FormatterServices.GetUninitializedObject(typeof(DebugWindowManager));
#pragma warning restore SYSLIB0050
        // Symmetry pair resolution reads this dictionary; field initializers don't run on an uninitialized object.
        SetField(manager, "_vanillaSymmetryPairOverrides", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return manager;
    }

    private static object CreateShapeDocument(Shape shape)
    {
        Type docType = ManagerType.GetNestedType("VanillaAnimationDocument", BindingFlags.NonPublic)!;
        object document = Activator.CreateInstance(docType, nonPublic: true)!;
        docType.GetProperty("Shape", InstanceFlags)!.SetValue(document, shape);
        return document;
    }

    // A minimal quadruped: a body with four two-segment legs at distinct front/back (X) and left/right (Z) pivots.
    private static Shape BuildQuadrupedShape()
    {
        ShapeElement[] legs =
        [
            Leg("legLeftFront", 12, 12),
            Leg("legRightFront", 12, 4),
            Leg("legLeftBack", 4, 12),
            Leg("legRightBack", 4, 4)
        ];
        ShapeElement body = Element("body", [0, 0, 0], [16, 16, 16], legs);
        return new Shape { Elements = [body] };
    }

    private static ShapeElement Leg(string name, double originX, double originZ)
    {
        ShapeElement lower = Element(name + "lower", [originX - 1, 2, originZ - 1], [originX + 1, 6, originZ + 1], null);
        SetOrigin(lower, originX, 4, originZ);
        ShapeElement hip = Element(name, [originX - 1, 6, originZ - 1], [originX + 1, 10, originZ + 1], [lower]);
        SetOrigin(hip, originX, 8, originZ);
        return hip;
    }

    private static ShapeElement Element(string name, double[] from, double[] to, ShapeElement[]? children)
    {
        ShapeElement element = new() { Name = name, From = from, To = to, ScaleX = 1, ScaleY = 1, ScaleZ = 1 };
        if (children != null) element.Children = children;
        return element;
    }

    private static void SetOrigin(ShapeElement element, double x, double y, double z)
    {
        element.RotationOrigin = [x, y, z];
    }

    private static HashSet<string> CollectNames(Shape shape)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void Visit(ShapeElement element)
        {
            if (!string.IsNullOrWhiteSpace(element.Name)) names.Add(element.Name);
            foreach (ShapeElement child in element.Children ?? [])
            {
                Visit(child);
            }
        }
        foreach (ShapeElement root in shape.Elements ?? [])
        {
            Visit(root);
        }
        return names;
    }

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFlags)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static object? GetMember(object target, string name)
    {
        Type type = target.GetType();
        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null) return field.GetValue(target);
        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null) return property.GetValue(target);
        throw new MissingMemberException(type.FullName, name);
    }
}
