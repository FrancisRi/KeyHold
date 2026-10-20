using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyHoldMacro
{
    internal enum FeedbackCue { None, Enabled, Paused, Emergency, Error }

    // Called only after worker operations succeed; this class never plays audio.
    internal sealed class FeedbackTransitions
    {
        private bool running;
        internal FeedbackCue Observe(string kind, bool confirmStop)
        {
            if (kind == "running")
            {
                bool changed = !running; running = true;
                return changed ? FeedbackCue.Enabled : FeedbackCue.None;
            }
            if (kind == "error") { running = false; return FeedbackCue.Error; }
            if (kind != "stopped") return FeedbackCue.None;
            bool wasRunning = running; running = false;
            return confirmStop ? FeedbackCue.Emergency : wasRunning ? FeedbackCue.Paused : FeedbackCue.None;
        }
    }

    // One UI tick can contain a whole start/stop cycle. Announce the last state,
    // and suppress an enable acknowledgement invalidated by a later stop ticket.
    internal sealed class FeedbackBatch
    {
        private FeedbackCue pending;
        private int version;
        internal void Add(FeedbackCue cue, int startVersion)
        { if (cue != FeedbackCue.None) { pending = cue; version = startVersion; } }
        internal void Clear() { pending = FeedbackCue.None; }
        internal FeedbackCue Take(int currentVersion)
        {
            FeedbackCue cue = pending; pending = FeedbackCue.None;
            return cue == FeedbackCue.Enabled && version != currentVersion ? FeedbackCue.None : cue;
        }
    }

    internal static class FeedbackPreference
    {
        internal static string PathName
        { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyHoldMacro", "sound-enabled.txt"); } }
        internal static bool Read(string path)
        {
            try { return !File.Exists(path) || File.ReadAllText(path, Encoding.UTF8).Trim() != "off"; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        internal static void Write(string path, bool enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            // As for language.txt, use ordinary writes in EFS-managed folders.
            File.WriteAllText(path, enabled ? "on" : "off", new UTF8Encoding(false));
        }
    }

    internal sealed class FeedbackAudio : IDisposable
    {
        private readonly Dictionary<FeedbackCue, GCHandle> sounds = new Dictionary<FeedbackCue, GCHandle>();
        private bool disposed;
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

        internal bool Play(FeedbackCue cue)
        {
            if (disposed || cue == FeedbackCue.None) return false;
            try
            {
                GCHandle sound;
                if (!sounds.TryGetValue(cue, out sound))
                {
                    sound = GCHandle.Alloc(CreateWave(cue), GCHandleType.Pinned);
                    sounds.Add(cue, sound);
                }
                // ASYNC | NODEFAULT | MEMORY. Keep every buffer pinned until
                // playback is stopped on Dispose; a newer cue replaces the old.
                return PlaySound(sound.AddrOfPinnedObject(), IntPtr.Zero, 0x0001 | 0x0002 | 0x0004);
            }
            catch (ExternalException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        internal void Stop()
        { if (!disposed && sounds.Count > 0) PlaySound(IntPtr.Zero, IntPtr.Zero, 0); }
        public void Dispose()
        {
            if (disposed) return;
            Stop(); disposed = true;
            foreach (GCHandle sound in sounds.Values) if (sound.IsAllocated) sound.Free();
            sounds.Clear();
        }
        internal static byte[] CreateWave(FeedbackCue cue)
        {
            int[] frequencies;
            switch (cue)
            {
                case FeedbackCue.Enabled: frequencies = new int[] { 660, 990 }; break;
                case FeedbackCue.Paused: frequencies = new int[] { 990, 660 }; break;
                case FeedbackCue.Emergency: frequencies = new int[] { 440, 330, 220 }; break;
                case FeedbackCue.Error: frequencies = new int[] { 220, 440, 220 }; break;
                default: throw new ArgumentException("A playable feedback cue is required.");
            }
            const int rate = 22050, tone = 1654, gap = 441, fade = 154;
            int samples = frequencies.Length * tone + (frequencies.Length - 1) * gap;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int note = 0; note < frequencies.Length; note++)
                {
                    for (int i = 0; i < tone; i++)
                    {
                        double envelope = Math.Min(1.0, Math.Min(i, tone - 1 - i) / (double)fade);
                        writer.Write((short)(7200 * envelope * Math.Sin(2 * Math.PI * frequencies[note] * i / rate)));
                    }
                    if (note + 1 < frequencies.Length) for (int i = 0; i < gap; i++) writer.Write((short)0);
                }
                return stream.ToArray();
            }
        }
    }
}
