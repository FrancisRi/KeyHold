using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KeyHoldMacro;

internal static class FeedbackTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); checks++; }
    internal static int Run()
    {
        FeedbackTransitions state = new FeedbackTransitions();
        Check(state.Observe("stopped", false) == FeedbackCue.None, "paused startup and ordinary repeated stops are silent");
        Check(state.Observe("start", false) == FeedbackCue.None, "an F8 request does not announce success");
        Check(state.Observe("running", false) == FeedbackCue.Enabled, "confirmed start announces enabled");
        Check(state.Observe("running", false) == FeedbackCue.None, "duplicate running events do not repeat audio");
        Check(state.Observe("active", false) == FeedbackCue.None, "macro activity does not generate state sounds");
        Check(state.Observe("stopped", false) == FeedbackCue.Paused, "actual running-to-paused transition announces pause");
        Check(state.Observe("stopped", false) == FeedbackCue.None, "F6 or settings while already paused stay silent");
        Check(state.Observe("stopped", true) == FeedbackCue.Emergency, "explicit emergency confirms even when already paused");
        Check(state.Observe("error", false) == FeedbackCue.Error, "failed start has its own sound");
        Check(state.Observe("running", false) == FeedbackCue.Enabled, "successful retry announces enabled again");
        Check(state.Observe("error", false) == FeedbackCue.Error && state.Observe("stopped", false) == FeedbackCue.None,
            "input failure is not followed by a misleading ordinary pause sound");

        FeedbackBatch batch = new FeedbackBatch();
        batch.Add(FeedbackCue.Enabled, 1); batch.Add(FeedbackCue.Paused, 2);
        Check(batch.Take(2) == FeedbackCue.Paused, "quick start-stop batch announces the final paused state");
        Check(batch.Take(2) == FeedbackCue.None, "a consumed batch cannot replay a sound");
        batch.Add(FeedbackCue.Enabled, 3);
        Check(batch.Take(4) == FeedbackCue.None, "a stopped start ticket cannot produce a stale enabled sound");
        batch.Add(FeedbackCue.Enabled, 4); batch.Add(FeedbackCue.Error, 5);
        Check(batch.Take(5) == FeedbackCue.Error, "failure overrides a queued enabled acknowledgement");
        batch.Add(FeedbackCue.Emergency, 6); batch.Add(FeedbackCue.None, 6);
        Check(batch.Take(6) == FeedbackCue.Emergency, "coordinate and activity events do not erase emergency acknowledgement");
        batch.Add(FeedbackCue.Error, 7); batch.Clear();
        Check(batch.Take(7) == FeedbackCue.None, "an error already played before a modal dialog cannot replay afterward");

        string directory = Path.Combine(Path.GetTempPath(), "KeyHold-sound-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "sound-enabled.txt");
        try
        {
            Check(FeedbackPreference.Read(path), "sound is enabled by default for existing installations");
            FeedbackPreference.Write(path, false);
            Check(!FeedbackPreference.Read(path), "mute persists independently of profiles and language");
            FeedbackPreference.Write(path, true);
            Check(FeedbackPreference.Read(path), "the existing preference can be rewritten without file replacement");
            File.WriteAllText(path, "corrupt");
            Check(FeedbackPreference.Read(path), "invalid preference uses the documented enabled default");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            bool denied = false;
            try { FeedbackPreference.Write(path, false); } catch (UnauthorizedAccessException) { denied = true; }
            Check(denied && File.ReadAllText(path) == "corrupt", "failed sound preference writes are reported without losing file content");
        }
        finally
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }

        List<byte[]> waves = new List<byte[]>();
        foreach (FeedbackCue cue in new FeedbackCue[] { FeedbackCue.Enabled, FeedbackCue.Paused, FeedbackCue.Emergency, FeedbackCue.Error })
        {
            byte[] wave = FeedbackAudio.CreateWave(cue); waves.Add(wave);
            Check(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt ", "valid RIFF/WAVE container for " + cue);
            Check(BitConverter.ToInt32(wave, 4) == wave.Length - 8 && BitConverter.ToInt32(wave, 40) == wave.Length - 44, "complete PCM buffer for " + cue);
            Check(BitConverter.ToInt16(wave, 20) == 1 && BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt32(wave, 24) == 22050 && BitConverter.ToInt16(wave, 34) == 16, "supported mono PCM format for " + cue);
            double duration = (wave.Length - 44) / 44100.0;
            Check(duration > 0.1 && duration < 0.35, "state cue stays short for " + cue);
            int peak = 0;
            for (int i = 44; i < wave.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wave, i)));
            Check(peak > 1000 && peak <= 7200 && BitConverter.ToInt16(wave, 44) == 0 && BitConverter.ToInt16(wave, wave.Length - 2) == 0,
                "cue has bounded nonzero volume and faded endpoints: " + cue);
        }
        Check(Crossings(waves[0], false) < Crossings(waves[0], true), "enabled cue ascends in pitch");
        Check(Crossings(waves[1], false) > Crossings(waves[1], true), "paused cue descends in pitch");
        Check(Convert.ToBase64String(waves[2]) != Convert.ToBase64String(waves[3]), "emergency and error cues are distinguishable");
        return checks;
    }
    private static int Crossings(byte[] wave, bool last)
    {
        int samples = (wave.Length - 44) / 2;
        int start = last ? samples - 1000 : 0, crossings = 0;
        for (int i = start + 1; i < start + 1000; i++)
            if (BitConverter.ToInt16(wave, 44 + (i - 1) * 2) < 0 && BitConverter.ToInt16(wave, 44 + i * 2) >= 0) crossings++;
        return crossings;
    }
}
