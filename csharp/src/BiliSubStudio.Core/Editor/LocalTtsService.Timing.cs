using System.Text;

namespace BiliSubStudio.Core.Editor;

internal sealed partial class LocalTtsService
{
    private const int MaxSentenceGroupCues = 512;
    // Group admission tolerates small positive gaps (rapid dialogue separated by
    // a breath beat) so a passage shares time. Must mirror the TTS worker's
    // MAX_GROUP_GAP_FRAMES exactly (frames @22050 Hz); 5512 == 0.25s, the same
    // magnitude as the overlap bound below.
    private const long MaxSentenceGroupGapFrames = 5512;
    private const long MaxSentenceGroupOverlapFrames = 5512;
    private const long MaxSentenceGroupDurationFrames = 6615000;
    // Luật pace (01/10 + micro-band 02/10 2.0x, user 07/10 nghe thử mở
    // 2.5x, chỉ VieNeu): tăng tốc không vượt 1.5x mọi đường, NGOẠI TRỪ
    // cue micro (khung <1.1s) được 2.5x; cue rộng stretch tối đa 1.4x
    // thời lượng (pace không dưới 1/1.4). Trần record nới 0.01
    // (1.51 / micro 2.51) để trừ FIT_HEADROOM + margin retry của worker.
    // Mirror y hệt worker theo window, comment lý do.
    private const double VieneuMaxTempoFactor = 1.51;
    private const double VieneuMicroMaxTempoFactor = 2.51;
    private const long VieneuMicroWindowFrames = 24255; // 1.1s @22050 Hz
    // Overflow tran khung (user duyet cho cue don qua dai: doc nguyen cau toi
    // tran window-cap, de len 1-2 cue sau, khong doi timeline). Mirror worker
    // OVERFLOW_MAX_FRAMES; validation chi mo khi worker khai OverflowFrames.
    private const long OverflowBudgetFrames = 66150; // 3.0s @22050 Hz
    private const double VieneuMinTempoFactor = 1.0 / 1.4;

    private static double VieneuMaxFactorFor(long targetFrames) =>
        targetFrames is > 0 and < VieneuMicroWindowFrames ? VieneuMicroMaxTempoFactor : VieneuMaxTempoFactor;

    private static bool ValidateNativeSynthesis(TtsWorkerCue cue, long targetFrames, bool naturalSample, int sampleRate, bool isVieneu, int index)
    {
        // Silence quarantine: no speech could be synthesized for this cue (garbage text or an
        // unfillable window). The timeline stays intact because every sample is real silence of
        // exactly the window length; the cue always needs review.
        if (cue.FitMethod == "silence-fallback")
        {
            if (cue.SourceFrames != 0 || cue.GeneratedFrames != 0 || cue.TrimmedSilenceFrames != 0
                || cue.PaddingFrames != targetFrames || cue.Frames != targetFrames
                || cue.TempoFactor != 0 || cue.TempoInputFrames != 0 || cue.TempoAttempts != 0
                || cue.SynthesisAttempts < 1 || cue.SynthesisAttempts > 12
                || cue.NativeReferenceFrames != 0 || cue.GroupNativeFrames != 0 || cue.ActualSpeedFactor != 0
                || naturalSample || cue.Status != "review" || !cue.VoiceReview)
                throw new InvalidDataException("Voice silence-fallback không hợp lệ; không nhận master này.");
            return true;
        }
        var precisionPaddingBudget = Math.Max(1L, Math.Min((long)Math.Round(.04 * sampleRate), targetFrames / 50));
        var relativeScale = cue.LengthScale / cue.BaseLengthScale;
        var sentenceGroup = cue.TimingSource == "sentence-group";
        var atempoMethod = isVieneu ? "vieneu-atempo" : "piper-atempo";
        var tempoFallback = cue.FitMethod == atempoMethod;
        const double minimumScale = .30;
        if (cue.FitMethod is not ("piper-length-scale" or "piper-atempo" or "vieneu-natural" or "vieneu-atempo")
            || !double.IsFinite(cue.BaseLengthScale) || cue.BaseLengthScale <= 0
            || !double.IsFinite(cue.LengthScale) || !double.IsFinite(relativeScale) || relativeScale < minimumScale || relativeScale > 1.20
            || cue.SourceFrames <= 0 || cue.GeneratedFrames <= 0 || cue.GeneratedFrames > cue.Frames
            || cue.TrimmedSilenceFrames < 0 || cue.TrimmedSilenceFrames >= cue.SourceFrames
            || cue.PaddingFrames < 0 || cue.PaddingFrames >= targetFrames || cue.GeneratedFrames != cue.Frames - cue.PaddingFrames
            || cue.SynthesisAttempts < 1 || cue.SynthesisAttempts > 12
            // 10/10-11/10 vụ rớt master oan (cue 3996 calls 17/5, cue 1274
            // calls 11/1, cue 5841 calls 13/1): synthesis_calls KHÔNG phải số
            // infer như tên gọi — nó đếm mọi vòng lặp on_attempt, kể cả vòng
            // tái dùng batch không tổng hợp mới (Phase-2 condense: batch một
            // lần rồi loop vẫn đếm từng variant). Mọi bound đặt trên nó (tuyệt
            // đối +10, tỉ lệ 6x, cả hai dạng) đều là mò mẫm theo từng ca rớt:
            // ca sau luôn phá bound của ca trước (13/1 lọt mọi bound mà cue
            // lỗi cũ 13-35/1 cũng lọt). Bỏ chặn trên hẳn, chỉ giữ chặn dưới
            // (tổng vòng lặp không thể ít hơn attempts phase cuối) + attempts
            // trong 1..12 + cache-hit thì calls==0. Chống chạy vô hạn thực sự
            // nằm ở worker (mọi vòng retry đều có trần, assertion
            // phase_calls==attempts raise RuntimeError khi đếm lệch), còn đúng
            // sai AUDIO đã có frames/SHA/pace/tempo/speed/review bounds độc lập
            // bao phủ — chưa từng có ca audio hỏng nào mà chỉ mỗi bound calls
            // này bắt được.
            || (cue.CacheHit ? cue.SynthesisCalls != 0 : cue.SynthesisCalls < cue.SynthesisAttempts)
            || (naturalSample && cue.TimingSource != "sample"))
            throw new InvalidDataException($"Voice không có metadata nhịp đọc Piper hợp lệ; không nhận master này (cue {index} {cue.Id}).");
        if (cue.NativeReferenceFrames <= 0 || !double.IsFinite(cue.ActualSpeedFactor)
            || (isVieneu
                ? cue.ActualSpeedFactor < VieneuMinTempoFactor - 1e-9 || cue.ActualSpeedFactor > VieneuMaxFactorFor(targetFrames)
                : cue.ActualSpeedFactor < 1 || cue.ActualSpeedFactor > 100)
            || (sentenceGroup
                ? cue.GroupNativeFrames != cue.NativeReferenceFrames
                : cue.GroupNativeFrames != 0
                    || Math.Abs(cue.NativeReferenceFrames / (double)cue.GeneratedFrames - cue.ActualSpeedFactor) > 1e-9))
            throw new InvalidDataException($"Voice không có giới hạn tốc độ đọc thực đo hợp lệ (cue {index} {cue.Id}).");
        if (cue.SynthesisAttempts == 1
            && (cue.LengthScale != cue.BaseLengthScale
                || Math.Abs(cue.RawDuration - cue.SourceFrames / (double)sampleRate) > 1e-9
                // 30/09 (vụ cue5 VieNeu im oan): atempo ngay lượt 1 chỉ xảy ra
                // khi không có vòng scale (VieNeu) — native != generated ở record
                // tempo là trung thực. Miễn CHỈ cho vieneu-atempo; Piper giữ
                // nghiêm byte-for-byte như cũ (với Piper là unreachable).
                || (!sentenceGroup && !(isVieneu && cue.FitMethod == atempoMethod) && cue.NativeReferenceFrames != cue.GeneratedFrames)))
            throw new InvalidDataException($"Voice lượt tự nhiên không có metadata Piper gốc hợp lệ (cue {index} {cue.Id}).");
        if (tempoFallback
            ? cue.TempoInputFrames != cue.SourceFrames - cue.TrimmedSilenceFrames
                || (isVieneu
                    ? cue.TempoInputFrames <= 0
                    : cue.TempoInputFrames <= cue.GeneratedFrames)
                || !double.IsFinite(cue.TempoFactor)
                || (isVieneu
                    ? cue.TempoFactor < VieneuMinTempoFactor - 1e-9 || cue.TempoFactor > VieneuMaxFactorFor(targetFrames)
                    : cue.TempoFactor <= 1)
                || Math.Abs(cue.TempoInputFrames / (double)cue.GeneratedFrames - cue.TempoFactor) > 1e-9
                || cue.TempoAttempts is < 1 or > 6
            : cue.TempoFactor != 0 || cue.TempoInputFrames != 0 || cue.TempoAttempts != 0
                || cue.SourceFrames - cue.TrimmedSilenceFrames != cue.GeneratedFrames)
            throw new InvalidDataException($"Voice không bảo toàn đầy đủ chuỗi thoại hoặc metadata thời lượng (cue {index} {cue.Id}).");
        return tempoFallback || sentenceGroup || relativeScale is < .70 or > 1
            || cue.TrimmedSilenceFrames > 0 || cue.PaddingFrames > precisionPaddingBudget;
    }

    private static void ValidateSentenceGroupWindows(
        IReadOnlyList<TtsWorkerCue> actual,
        IReadOnlyList<TtsCueManifest> expected,
        int sampleRate,
        bool isVieneu)
    {
        for (var first = 0; first < actual.Count;)
        {
            var last = first;
            var groupStart = checked((long)Math.Round(expected[first].CueStart * sampleRate));
            while (last + 1 < expected.Count && last - first + 1 < MaxSentenceGroupCues)
            {
                var gap = checked((long)Math.Round(expected[last + 1].CueStart * sampleRate))
                    - checked((long)Math.Round(expected[last].CueEnd * sampleRate));
                var candidateEnd = checked((long)Math.Round(expected[last + 1].CueEnd * sampleRate));
                if (expected[last].TimingSource == "sample" || expected[last + 1].TimingSource == "sample"
                    || gap < -MaxSentenceGroupOverlapFrames || gap > MaxSentenceGroupGapFrames
                    || candidateEnd - groupStart > MaxSentenceGroupDurationFrames)
                    break;
                last++;
            }
            var grouped = 0;
            for (var item = first; item <= last; item++)
                if (actual[item].TimingSource == "sentence-group") grouped++;
            if (grouped == 0)
            {
                first = last + 1;
                continue;
            }
            if (last == first || grouped != last - first + 1)
                throw new InvalidDataException("Nhóm câu TTS không hợp lệ.");
            var groupEnd = checked((long)Math.Round(expected[last].CueEnd * sampleRate));
            if (groupEnd - groupStart > MaxSentenceGroupDurationFrames
                || actual[first].ClipStartSample != groupStart
                || checked(actual[last].ClipStartSample + actual[last].TargetFrames) != groupEnd)
                throw new InvalidDataException("Nhóm câu TTS vượt biên timecode nguồn.");
            var generatedFrames = 0L;
            for (var item = first; item <= last; item++)
            {
                if (actual[item].TargetFrames <= 0
                    || (item > first && actual[item].ClipStartSample
                        != actual[item - 1].ClipStartSample + actual[item - 1].TargetFrames)
                    || actual[item].GroupNativeFrames != actual[first].GroupNativeFrames
                    || actual[item].ActualSpeedFactor != actual[first].ActualSpeedFactor)
                    throw new InvalidDataException("Nhóm câu TTS không phủ liên tục timecode nguồn.");
                generatedFrames = checked(generatedFrames + actual[item].GeneratedFrames);
            }
            var measuredSpeed = actual[first].GroupNativeFrames / (double)generatedFrames;
            var speedLow = isVieneu ? VieneuMinTempoFactor - 1e-9 : 1.0;
            var speedHigh = isVieneu ? VieneuMaxFactorFor(groupEnd - groupStart) : 100.0;
            if (Math.Abs(measuredSpeed - actual[first].ActualSpeedFactor) > 1e-9
                || measuredSpeed < speedLow || measuredSpeed > speedHigh)
                throw new InvalidDataException("Nhóm câu TTS vượt giới hạn tốc độ đọc rõ.");
            first = last + 1;
        }
    }

    // Verify PCM frame count from the actual WAV, not only the worker's duration.
    // FFmpeg may include LIST/JUNK chunks; never assume a fixed 44-byte header.
    private static long ReadClipFrames(string path, int sampleRate)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF"
            || (long)reader.ReadUInt32() + 8 != stream.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("WAV voice thiếu hoặc sai RIFF header.");
        var format = false;
        long frames = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end + (size & 1) > stream.Length) throw new InvalidDataException("WAV voice bị thiếu dữ liệu.");
            if (name == "fmt ")
            {
                if (format || size < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1
                    || reader.ReadUInt32() != sampleRate || reader.ReadUInt32() != sampleRate * 2
                    || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16)
                    throw new InvalidDataException("WAV voice không phải mono PCM16 đúng sample rate.");
                format = true;
            }
            else if (name == "data")
            {
                if (frames >= 0 || size == 0 || size % 2 != 0) throw new InvalidDataException("WAV voice sai số mẫu PCM.");
                frames = size / 2;
            }
            stream.Position = end + (size & 1);
        }
        if (!format || frames <= 0 || stream.Position != stream.Length) throw new InvalidDataException("WAV voice không hoàn chỉnh.");
        return frames;
    }
}
