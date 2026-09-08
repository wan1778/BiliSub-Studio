using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G24-G26 contract (user-approved Concat parity, 2026-09-08).
// Locks: speed envelope exact tiling + piecewise time mapping, atempo chain
// parity with the mux renderer, keyframe channel validation + eased
// interpolation, color grade bounds + deterministic FFmpeg mapping, schema-5
// upgrade to empty G24-G26 state, mux capability rejection for unwired
// graphs, and plan roundtrip through persistence.
public static class TimelineSpeedKeyframeGradeContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-timeline-g26-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new TimelineProjectStoreV2(root);
            var project = await store.CreateAsync("G24-G26 core", CancellationToken.None);

            var asset = new TimelineMediaAsset(
                TimelineMediaAsset.NewId(),
                "C:/video/g26.mp4",
                1920, 1080,
                DurationUs: 20_000_000,
                FrameRate: 30,
                HasAudio: true,
                Size: 123,
                LastWriteUtcTicks: 1);
            var clip = TimelineClip.Create(asset.Id, 0, 2_000_000, 12_000_000);
            var sequence = TimelineSequence.Create([clip], [asset], outputFrameRate: 30);
            Check(clip.DurationUs == 10_000_000, "clip window must be 10s for the fixtures");

            var withSequence = project with
            {
                Assets = [asset],
                Sequence = sequence,
                SourceAudio = TimelineSourceAudioState.Defaults([clip]),
            };

            // ---- G24: exact tiling, mapping, persistence ----
            // 5s@0.5x consumes 2.5s source; 5s@1.5x consumes 7.5s source;
            // together they tile 10s sequence and 10s source exactly.
            var envelope = TimelineSpeedEnvelope.Create(
                clip.Id,
                [
                    new TimelineSpeedSegment(5_000_000, 0.5),
                    new TimelineSpeedSegment(5_000_000, 1.5),
                ],
                clip.DurationUs, clip.SourceOutUs - clip.SourceInUs);
            Check(envelope.SourceCoverageUs == 10_000_000,
                "envelope must cover exactly 2.5s + 7.5s = 10s of source");
            var slowOffset = envelope.SourceToSequenceUs(2_000_000);
            Check(slowOffset == 4_000_000, "2s of source at 0.5x must land at 4s sequence time");
            var fastOffset = envelope.SequenceToSourceUs(6_000_000);
            Check(fastOffset == 4_000_000, "1s into the fast segment must map to 1.5s more source");
            Check(envelope.SourceToSequenceUs(10_000_000) == 10_000_000,
                "envelope end must map exactly to the clip sequence end");

            var envelopeProject = withSequence with { SpeedEnvelopes = [envelope] };
            await store.SaveAsync(envelopeProject, CancellationToken.None);
            var reloaded = await store.LoadAsync(envelopeProject.Id, CancellationToken.None);
            Check(reloaded.SpeedEnvelopes.Count == 1
                && reloaded.SpeedEnvelopes[0].Segments.Count == 2,
                "speed envelope must survive persistence");

            var envelopePlan = TimelineCompiler.Compile(reloaded);
            Check(envelopePlan.SpeedEnvelopes.Count == 1, "plan must carry the speed envelope");
            Check(envelopePlan.Clips[0].SequenceDurationUs == 10_000_000,
                "clip sequence window must stay fixed under an envelope");
            Check(envelopePlan.Clips[0].SourceInUs == 2_000_000
                && envelopePlan.Clips[0].SourceOutUs == 12_000_000,
                "clip source range must stay fixed under an envelope");

            // G27: spans freeze in the plan and the mux accepts envelopes.
            var spans = envelopePlan.Clips[0].SpeedSpans;
            Check(spans is { Count: 2 }, "plan clip must freeze exactly two spans");
            Check(spans![0] is { SequenceStartUs: 0, SequenceEndUs: 5_000_000, SourceOffsetUs: 0, SourceEndOffsetUs: 2_500_000 }
                && spans[1] is { SequenceStartUs: 5_000_000, SequenceEndUs: 10_000_000, SourceOffsetUs: 2_500_000, SourceEndOffsetUs: 10_000_000 },
                "span cursors must tile sequence and source exactly");
            Check(TimelineSpeedMapping.SequenceToSourceUs(spans, 1, 2_500_000) == 1_250_000,
                "piecewise mapping must resolve inside the slow span");
            Check(TimelineSpeedMapping.SequenceToSourceUs(spans, 1, 7_500_000) == 5_000_000,
                "piecewise mapping must resolve inside the fast span");
            Check(TimelineSpeedMapping.SourceToSequenceUs(spans, 1, 5_000_000) == 7_500_000,
                "inverse piecewise mapping must roundtrip the fast span");
            Check(TimelineSpeedMapping.SequenceToSourceUs(null, 2.0, 3_000_000) == 6_000_000,
                "null spans must keep the constant-rate arithmetic");
            Check(TimelineSpeedMapping.BuildSetptsExpression(spans) is { } setpts
                && setpts.Contains("if(lt(PTS-STARTPTS,2.5),", StringComparison.Ordinal)
                && setpts.Contains("5+(PTS-STARTPTS-2.5)/1.5", StringComparison.Ordinal),
                "piecewise setpts must branch at span boundaries with invariant numbers");
            Check(TimelineSpeedMapping.BuildSetptsExpression(null) is null
                && TimelineSpeedMapping.BuildSetptsExpression([spans[0]]) is null,
                "absent or single-span envelopes must keep the constant setpts");
            var spanAudio = TimelineSpeedMapping.BuildAudioChain(spans, "atrim=duration=10", 0, 1.0);
            Check(spanAudio.Contains("asplit=2", StringComparison.Ordinal)
                && spanAudio.Contains("atempo=0.5", StringComparison.Ordinal)
                && spanAudio.Contains("atempo=1.5", StringComparison.Ordinal)
                && spanAudio.Contains("concat=n=2:v=0:a=1", StringComparison.Ordinal),
                "span audio chain must split, re-time per span and concat");
            Check(TimelineSpeedMapping.BuildAudioChain(null, "atrim=duration=10", 0, 2.0)
                == "[0:a]atrim=duration=10,atempo=2",
                "null spans must keep the constant audio chain byte-identical");
            TimelineRenderCapability.EnsureMuxSupported(envelopePlan);

            // G27: envelope segment graph must splice the piecewise setpts
            // and the per-span audio chain.
            var envelopeArguments = TimelineFfmpegArguments.BuildSegment(
                MakeSegment(envelopePlan, asset),
                Path.Combine(root, "envelope.mp4")).ToList();
            var envelopeGraph = envelopeArguments[envelopeArguments.IndexOf("-filter_complex") + 1];
            Check(envelopeGraph.Contains("setpts='if(lt(PTS-STARTPTS,2.5),(PTS-STARTPTS)/0.5,",
                    StringComparison.Ordinal),
                "segment graph must carry the piecewise setpts expression");
            Check(envelopeGraph.Contains("asplit=2", StringComparison.Ordinal)
                && envelopeGraph.Contains("concat=n=2:v=0:a=1", StringComparison.Ordinal),
                "segment audio must re-time per span and concat");

            // Negative tiling cases.
            Throws<InvalidDataException>(
                () => TimelineSpeedEnvelope.Create(
                    clip.Id, [new TimelineSpeedSegment(5_000_000, 1.0)],
                    clip.DurationUs, clip.SourceOutUs - clip.SourceInUs),
                "under-covering envelope must be rejected");
            Throws<InvalidDataException>(
                () => TimelineSpeedEnvelope.Create(
                    clip.Id,
                    [new TimelineSpeedSegment(10_000_000, 0.5), new TimelineSpeedSegment(0, 1)],
                    clip.DurationUs, clip.SourceOutUs - clip.SourceInUs),
                "zero-length segment must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineSpeedSegment(1_000_000, 0.05).Validate(),
                "rate below 0.1 must be rejected");
            var duplicateEnvelope = envelope with { ClipId = envelope.ClipId };
            var duplicateProject = withSequence with { SpeedEnvelopes = [envelope, duplicateEnvelope] };
            Throws<InvalidDataException>(
                () => duplicateProject.Validate(),
                "duplicate envelope per clip must be rejected at project level");
            var orphanEnvelope = new TimelineSpeedEnvelope(
                TimelineProjectV2.NewId(),
                [new TimelineSpeedSegment(10_000_000, 1.0)]);
            Throws<InvalidDataException>(
                () => (withSequence with { SpeedEnvelopes = [orphanEnvelope] }).Validate(),
                "envelope on a missing clip must be rejected at project level");

            // Atempo chain parity with the mux renderer's historical output.
            Check(TimelineSpeedEnvelope.BuildAtempoChain(2.0) == "atempo=2",
                "atempo chain for 2x must be a single filter");
            Check(TimelineSpeedEnvelope.BuildAtempoChain(0.25) == "atempo=0.5,atempo=0.5",
                "atempo chain for 0.25x must split into two 0.5 filters");
            Check(TimelineSpeedEnvelope.BuildAtempoChain(200) == "atempo=100,atempo=2",
                "atempo chain for 200x must split at the 100 limit");
            Check(TimelineSpeedEnvelope.BuildAtempoChain(1) == "atempo=1",
                "atempo chain for 1x must stay explicit");

            // ---- G25: keyframe channels, interpolation, bounds ----
            var keyframes = TimelineKeyframes.Create(
                clip.Id,
                [
                    new TimelineKeyframeTrack(
                        TimelineKeyframeChannel.Opacity,
                        [
                            new TimelineKeyframe(0, 0, TimelineKeyframeEasing.Linear),
                            new TimelineKeyframe(4_000_000, 1, TimelineKeyframeEasing.Linear),
                        ]),
                    new TimelineKeyframeTrack(
                        TimelineKeyframeChannel.ScaleX,
                        [new TimelineKeyframe(0, 1, TimelineKeyframeEasing.Hold)]),
                ],
                clip.DurationUs);
            Check(keyframes.Evaluate(TimelineKeyframeChannel.Opacity, 2_000_000) == 0.5,
                "linear opacity must hit 0.5 at the midpoint");
            Check(keyframes.Evaluate(TimelineKeyframeChannel.ScaleX, 9_999_999) == 1,
                "single-keyframe channel must stay constant");
            Check(keyframes.Evaluate(TimelineKeyframeChannel.Opacity, 8_000_000) == 1,
                "past the last keyframe the value must hold");
            var easeIn = TimelineKeyframes.Create(
                clip.Id,
                [new TimelineKeyframeTrack(
                    TimelineKeyframeChannel.Opacity,
                    [
                        new TimelineKeyframe(0, 0, TimelineKeyframeEasing.EaseIn),
                        new TimelineKeyframe(10_000_000, 1, TimelineKeyframeEasing.EaseIn),
                    ])],
                clip.DurationUs);
            Check(Math.Abs(easeIn.Evaluate(TimelineKeyframeChannel.Opacity, 5_000_000) - 0.25) < 1e-9,
                "ease-in at midpoint must equal 0.25");
            var hold = keyframes.Evaluate(TimelineKeyframeChannel.Opacity, 2_000_000);

            var keyframeProject = withSequence with { KeyframeSets = [keyframes] };
            keyframeProject.Validate();
            var keyframePlan = TimelineCompiler.Compile(keyframeProject);
            Check(keyframePlan.KeyframeSets.Count == 1, "plan must carry keyframe sets");
            Throws<InvalidOperationException>(
                () => TimelineRenderCapability.EnsureMuxSupported(keyframePlan),
                "mux must reject keyframes until their graph is wired");
            Throws<InvalidDataException>(
                () => new TimelineKeyframe(11_000_000, 1, TimelineKeyframeEasing.Linear)
                    .Validate(clip.DurationUs),
                "keyframe beyond the clip window must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineKeyframeTrack(
                    TimelineKeyframeChannel.Opacity,
                    [
                        new TimelineKeyframe(2_000_000, 0, TimelineKeyframeEasing.Linear),
                        new TimelineKeyframe(2_000_000, 1, TimelineKeyframeEasing.Linear),
                    ]).Validate(clip.DurationUs),
                "duplicate keyframe offsets must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineKeyframeTrack(
                    TimelineKeyframeChannel.Opacity,
                    [
                        new TimelineKeyframe(4_000_000, 0, TimelineKeyframeEasing.Linear),
                        new TimelineKeyframe(2_000_000, 1, TimelineKeyframeEasing.Linear),
                    ]).Validate(clip.DurationUs),
                "unordered keyframe offsets must be rejected");
            Throws<InvalidDataException>(
                () => (withSequence with
                {
                    KeyframeSets =
                    [
                        keyframes,
                        keyframes with { ClipId = keyframes.ClipId },
                    ],
                }).Validate(),
                "duplicate keyframe set per clip must be rejected");
            _ = hold;

            // ---- G26: grade bounds + deterministic FFmpeg mapping ----
            var grade = new TimelineColorGrade(0.1, 1.2, 0.8, 0.2, -0.1);
            var filter = TimelineColorGradeGraph.BuildFilter(grade);
            Check(filter == "eq=brightness=0.1,eq=contrast=1.2,eq=saturation=0.8," +
                "colorbalance=rs=-0.1:bs=0.2:rm=0.05:bm=-0.1",
                "grade filter must map deterministically with invariant formatting");
            Check(TimelineColorGradeGraph.BuildFilter(TimelineColorGrade.Neutral) is null,
                "neutral grade must build no filter");
            Check(TimelineColorGradeGraph.BuildFilter(null) is null,
                "absent grade must build no filter");
            Throws<InvalidDataException>(
                () => new TimelineColorGrade(1.5, 1, 1, 0, 0).Validate(),
                "exposure above 1 must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineColorGrade(0, 4, 1, 0, 0).Validate(),
                "contrast above 3 must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineColorGrade(0, 1, -0.1, 0, 0).Validate(),
                "negative saturation must be rejected");
            Throws<InvalidDataException>(
                () => new TimelineColorGrade(0, 1, 1, 1.5, 0).Validate(),
                "temperature above 1 must be rejected");

            var gradeSpec = new TimelineColorGradeSpec(clip.Id, grade);
            var gradeProject = withSequence with { ColorGrades = [gradeSpec] };
            gradeProject.Validate();
            var gradePlan = TimelineCompiler.Compile(gradeProject);
            Check(gradePlan.ColorGrades.Count == 1, "plan must carry the color grade");
            var gradeArguments = TimelineFfmpegArguments.BuildSegment(
                MakeSegment(gradePlan, asset),
                Path.Combine(root, "segment.mp4"));
            var graphIndex = gradeArguments.ToList().IndexOf("-filter_complex");
            Check(graphIndex >= 0, "segment arguments must carry a filter graph");
            var graph = gradeArguments[graphIndex + 1];
            Check(graph.Contains("eq=brightness=0.1,", StringComparison.Ordinal),
                "grade must splice into the segment graph before effects and trim");
            Check(!graph.Contains("colorbalance=rs=-0.1:bs=0.2:rm=0.05:bm=-0.1,eq",
                    StringComparison.Ordinal),
                "grade must stay ahead of the normalize stage");

            var neutralPlan = TimelineCompiler.Compile(withSequence with
            {
                ColorGrades = [new TimelineColorGradeSpec(clip.Id, TimelineColorGrade.Neutral)],
            });
            var neutralArguments = TimelineFfmpegArguments.BuildSegment(
                MakeSegment(neutralPlan, asset),
                Path.Combine(root, "neutral.mp4"));
            var neutralGraph = neutralArguments[neutralArguments.ToList().IndexOf("-filter_complex") + 1];
            Check(!neutralGraph.Contains("eq=", StringComparison.Ordinal)
                && !neutralGraph.Contains("colorbalance=", StringComparison.Ordinal),
                "neutral grade must keep the segment graph free of grade filters");
            Throws<InvalidDataException>(
                () => (withSequence with
                {
                    ColorGrades =
                    [
                        gradeSpec,
                        gradeSpec with { Grade = TimelineColorGrade.Neutral },
                    ],
                }).Validate(),
                "duplicate grade per clip must be rejected");
            Throws<InvalidDataException>(
                () => (withSequence with
                {
                    ColorGrades = [new TimelineColorGradeSpec(TimelineProjectV2.NewId(), grade)],
                }).Validate(),
                "grade on a missing clip must be rejected");

            // ---- Schema 5 payload upgrade to empty G24-G26 state ----
            var path = Path.Combine(root, withSequence.Id + ".json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["schema"] = 5;
            json.Remove("speed_envelopes");
            json.Remove("keyframe_sets");
            json.Remove("color_grades");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var upgraded = await store.LoadAsync(withSequence.Id, CancellationToken.None);
            Check(upgraded.SpeedEnvelopes.Count == 0 && upgraded.KeyframeSets.Count == 0
                && upgraded.ColorGrades.Count == 0,
                "schema-5 payload must upgrade to empty G24-G26 state");

            // ---- State cleanup: envelope removed after persistence ----
            await store.SaveAsync(withSequence, CancellationToken.None);
            var cleaned = await store.LoadAsync(withSequence.Id, CancellationToken.None);
            Check(cleaned.SpeedEnvelopes.Count == 0, "saving without envelope must clear it");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static TimelineRenderSegment MakeSegment(TimelineRenderPlan plan, TimelineMediaAsset asset)
    {
        var clip = plan.Clips[0];
        return new TimelineRenderSegment(
            0, clip, asset,
            plan.Audio.SourceAudio.First(source => source.ClipId == clip.ClipId),
            plan.Audio,
            plan,
            clip.SequenceStartUs,
            clip.SequenceDurationUs);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{message} (wrong exception: {error.GetType().Name})");
        }
        throw new InvalidOperationException(message + " (no exception)");
    }
}
