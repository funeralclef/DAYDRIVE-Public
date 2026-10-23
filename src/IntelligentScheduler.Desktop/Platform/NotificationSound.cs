using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IntelligentScheduler.Desktop.Platform;

/// <summary>
/// The short sound a reminder makes. It is the one the user already chose for notifications in
/// their own system settings, not a sample shipped with the application: a machine set to be
/// silent stays silent, and a machine with a chosen sound uses it.
///
/// It also has a volume, which the alias the shell used to play does not. A reminder that
/// arrives while the widget is already open and being read is not the same announcement as one
/// arriving on an empty screen — the user is looking straight at the thing that is about to
/// change — so that one is played quietly. Windows has no way to ask for an alias at a given
/// volume, so the chosen sound is read off disk once and its samples are scaled in memory,
/// which is a few lines of arithmetic and costs nothing per play after the first.
///
/// Everything here falls back to the plain alias. A machine whose sound is an MP3, a format
/// this does not scale, or a registry that will not answer, still gets its reminder sound —
/// just at whatever volume the system plays it.
/// </summary>
internal static class NotificationSound
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndAlias = 0x00010000;
    private const uint SndNoWait = 0x00002000;

    /// <summary>How loud a reminder is when the user is already looking at the widget.</summary>
    public const double SoftGain = 0.42;

    private static readonly object Gate = new();
    /// <summary>The chosen sound as it is on disk, read once. Empty means "there is none".</summary>
    private static byte[]? _source;
    private static bool _sourceRead;
    /// <summary>The last gain that was asked for and the buffer scaled to it.</summary>
    private static double _scaledGain = -1;
    private static byte[]? _scaled;
    /// <summary>
    /// The buffer currently handed to the audio device. SND_ASYNC returns immediately and the
    /// device reads from this memory afterwards, so it has to outlive the call — it is freed
    /// only when the next sound replaces it, by which time this one has long finished.
    /// </summary>
    private static IntPtr _playing;

    public static void Play(double gain = 1.0)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (gain < 0.995 && PlayScaled(gain)) return;
                PlayAlias();
                return;
            }

            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("canberra-gtk-play")
                {
                    ArgumentList = { "-i", "message" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
        }
        catch
        {
            // A machine with no sound device, or none configured, is not a failure worth
            // reporting: the reminder itself is on screen either way.
        }
    }

    /// <summary>
    /// The alias the shell registers for notifications, falling back to the plain system alert
    /// on a machine that has no entry for it.
    /// </summary>
    private static void PlayAlias()
    {
        if (!PlaySoundAlias("Notification.Default", IntPtr.Zero, SndAlias | SndAsync | SndNoWait | SndNoDefault))
            PlaySoundAlias("SystemAsterisk", IntPtr.Zero, SndAlias | SndAsync | SndNoWait | SndNoDefault);
    }

    /// <summary>
    /// Plays the chosen sound at a share of its own volume. False when there is nothing to
    /// scale — no file, or one this does not know how to open — and the caller falls back.
    /// </summary>
    private static bool PlayScaled(double gain)
    {
        byte[] buffer;
        lock (Gate)
        {
            var source = LoadSource();
            if (source is null || source.Length == 0) return false;
            if (_scaled is null || Math.Abs(_scaledGain - gain) > 0.001)
            {
                var scaled = Scale(source, gain);
                if (scaled is null) return false;
                _scaled = scaled;
                _scaledGain = gain;
            }
            buffer = _scaled;
        }

        // Handed to the device rather than played from managed memory: pinning a managed array
        // for the length of an asynchronous play is the same allocation with a longer reach into
        // the collector.
        var memory = Marshal.AllocHGlobal(buffer.Length);
        Marshal.Copy(buffer, 0, memory, buffer.Length);
        var played = PlaySoundMemory(memory, IntPtr.Zero, SndMemory | SndAsync | SndNoWait | SndNoDefault);
        if (!played)
        {
            Marshal.FreeHGlobal(memory);
            return false;
        }

        var previous = Interlocked.Exchange(ref _playing, memory);
        if (previous != IntPtr.Zero) Marshal.FreeHGlobal(previous);
        return true;
    }

    /// <summary>The file behind the notification alias, read once and kept.</summary>
    private static byte[]? LoadSource()
    {
        if (_sourceRead) return _source;
        _sourceRead = true;
        try
        {
            var path = ResolveSoundPath();
            if (path is not null && File.Exists(path)) _source = File.ReadAllBytes(path);
        }
        catch
        {
            // Unreadable is the same as absent here: the alias plays instead.
        }
        return _source;
    }

    /// <summary>
    /// Where Windows keeps the file for the notification sound the user picked. The value is
    /// the scheme's current setting, which may be empty (the user chose "None"), a bare file
    /// name relative to the media folder, or a path with environment variables still in it.
    /// </summary>
    private static string? ResolveSoundPath()
    {
        var value = ReadCurrentUserString(@"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current")
                    ?? ReadCurrentUserString(@"AppEvents\Schemes\Apps\.Default\.Default\.Current");
        if (string.IsNullOrWhiteSpace(value)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (Path.IsPathRooted(expanded)) return expanded;
        var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        return Path.Combine(media, expanded);
    }

    /// <summary>
    /// One string out of the current user's registry. The project targets plain .NET rather
    /// than the Windows flavour of it, so the registry classes are not in the framework it is
    /// built against and the one value needed is read through the API directly.
    /// </summary>
    private static string? ReadCurrentUserString(string subKey)
    {
        const uint restrictString = 0x00000002;      // RRF_RT_REG_SZ
        const uint restrictExpandString = 0x00000004; // RRF_RT_REG_EXPAND_SZ
        const uint doNotExpand = 0x10000000;          // RRF_NOEXPAND, expanded above instead
        var hkcu = new IntPtr(unchecked((int)0x80000001));
        uint size = 0;
        if (RegGetValue(hkcu, subKey, null, restrictString | restrictExpandString | doNotExpand,
                IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (RegGetValue(hkcu, subKey, null, restrictString | restrictExpandString | doNotExpand,
                    IntPtr.Zero, buffer, ref size) != 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// The same WAV with every sample multiplied by <paramref name="gain"/>. Null when the file
    /// is not something this can take apart — anything but linear 16-bit PCM, which is what
    /// every sound Windows ships is.
    /// </summary>
    private static byte[]? Scale(byte[] wave, double gain)
    {
        // "RIFF" .... "WAVE", then a run of chunks, each an id, a length and that many bytes.
        if (wave.Length < 44 ||
            Encoding.ASCII.GetString(wave, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wave, 8, 4) != "WAVE") return null;

        var format = 0;
        var bits = 0;
        var dataAt = -1;
        var dataLength = 0;
        var at = 12;
        while (at + 8 <= wave.Length)
        {
            var id = Encoding.ASCII.GetString(wave, at, 4);
            var length = BitConverter.ToInt32(wave, at + 4);
            if (length < 0 || at + 8 + length > wave.Length) break;
            if (id == "fmt " && length >= 16)
            {
                format = BitConverter.ToInt16(wave, at + 8);
                bits = BitConverter.ToInt16(wave, at + 22);
            }
            else if (id == "data")
            {
                dataAt = at + 8;
                dataLength = length;
                break;
            }
            // Chunks are padded to an even length; the pad byte is not counted in the length.
            at += 8 + length + (length & 1);
        }

        // 1 is WAVE_FORMAT_PCM; 0xFFFE is extensible, whose 16-bit form is the same samples.
        if (dataAt < 0 || dataLength < 2 || bits != 16 || (format != 1 && format != 0xFFFE)) return null;

        var scaled = (byte[])wave.Clone();
        var factor = Math.Clamp(gain, 0, 1);
        for (var index = dataAt; index + 1 < dataAt + dataLength; index += 2)
        {
            var sample = (short)(BitConverter.ToInt16(scaled, index) * factor);
            scaled[index] = (byte)(sample & 0xFF);
            scaled[index + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return scaled;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundAlias(string? sound, IntPtr module, uint flags);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundMemory(IntPtr sound, IntPtr module, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValue(IntPtr key, string subKey, string? value, uint flags,
        IntPtr type, IntPtr data, ref uint dataSize);
}
