namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private const float OverlayFontSizePx = 62f;

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndPurge = 0x0040;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(nint pszSound, nint hmod, uint fdwSound);

    private sealed class WavClip
    {
        public required string FileName { get; init; }
        public required byte[] Bytes { get; init; }
        public required int DataOffset { get; init; }
        public required int DataLength { get; init; }
        public required ushort Channels { get; init; }
        public required int SampleRate { get; init; }
        public required ushort BitsPerSample { get; init; }
    }

    private GCHandle _activeAudioBufferHandle;
    private bool _activeAudioBufferPinned;

    private void initialize_audio_resources()
    {
        _parryAudioClips.Clear();
        if (string.IsNullOrWhiteSpace(_audioResourcesDir) || !Directory.Exists(_audioResourcesDir))
        {
            _logger.Warning($"[Parry] Audio resource directory not found: {_audioResourcesDir}");
            return;
        }

        for (int i = 1; i <= 7; i++)
        {
            string path = Path.Combine(_audioResourcesDir, $"Parry_{i:D2}.wav");
            if (!File.Exists(path)) continue;

            if (try_load_wav_clip(path, out WavClip clip))
            {
                _parryAudioClips.Add(clip);
            }
        }

        _logger.Info($"[Parry] Loaded {_parryAudioClips.Count} parry audio clip(s).");
    }

    private bool try_load_wav_clip(string path, out WavClip clip)
    {
        clip = null!;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Failed to read audio clip '{path}': {ex.Message}");
            return false;
        }

        if (!try_parse_wav_metadata(bytes, out int dataOffset, out int dataLength, out ushort channels, out int sampleRate, out ushort bitsPerSample))
        {
            _logger.Warning($"[Parry] Unsupported WAV format for '{path}'. Expected PCM 16-bit WAV.");
            return false;
        }

        clip = new WavClip
        {
            FileName = Path.GetFileName(path),
            Bytes = bytes,
            DataOffset = dataOffset,
            DataLength = dataLength,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample
        };

        return true;
    }

    private static bool try_parse_wav_metadata(
        byte[] bytes,
        out int dataOffset,
        out int dataLength,
        out ushort channels,
        out int sampleRate,
        out ushort bitsPerSample)
    {
        dataOffset = 0;
        dataLength = 0;
        channels = 0;
        sampleRate = 0;
        bitsPerSample = 0;

        if (bytes.Length < 44) return false;
        if (!is_ascii(bytes, 0, "RIFF") || !is_ascii(bytes, 8, "WAVE")) return false;

        int offset = 12;
        bool foundFmt = false;
        bool foundData = false;
        ushort fmtCode = 0;

        while (offset + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > bytes.Length) return false;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) return false;
                fmtCode = BitConverter.ToUInt16(bytes, offset + 0);
                channels = BitConverter.ToUInt16(bytes, offset + 2);
                sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                bitsPerSample = BitConverter.ToUInt16(bytes, offset + 14);
                foundFmt = true;
            }
            else if (chunkId == "data")
            {
                dataOffset = offset;
                dataLength = chunkSize;
                foundData = true;
            }

            int advance = chunkSize + (chunkSize & 1);
            offset += advance;
        }

        if (!foundFmt || !foundData) return false;
        if (fmtCode != 1) return false; // PCM
        if (bitsPerSample != 16) return false;
        if (channels is < 1 or > 2) return false;
        if (sampleRate <= 0) return false;

        return true;
    }

    private static bool is_ascii(byte[] bytes, int offset, string text)
    {
        if (offset < 0 || offset + text.Length > bytes.Length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (bytes[offset + i] != text[i]) return false;
        }

        return true;
    }

    private static byte[] scale_wav_pcm_16(WavClip clip, float volume)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);
        if (clamped >= 0.999f)
        {
            return clip.Bytes;
        }

        byte[] scaled = (byte[])clip.Bytes.Clone();
        int end = Math.Min(scaled.Length, clip.DataOffset + clip.DataLength);

        for (int i = clip.DataOffset; i + 1 < end; i += 2)
        {
            short sample = BitConverter.ToInt16(scaled, i);
            int scaledSample = (int)MathF.Round(sample * clamped);
            scaledSample = Math.Clamp(scaledSample, short.MinValue, short.MaxValue);
            short s = (short)scaledSample;
            scaled[i] = (byte)(s & 0xFF);
            scaled[i + 1] = (byte)((s >> 8) & 0xFF);
        }

        return scaled;
    }

    private void play_feedback_sound()
    {
        if (!_optionSound) return;
        if (_parryAudioClips.Count == 0) return;
        if (_optionAudioVolume <= 0f) return;

        int idx = _rng.Next(_parryAudioClips.Count);
        WavClip clip = _parryAudioClips[idx];
        byte[] bytes = scale_wav_pcm_16(clip, _optionAudioVolume);
        if (!play_wave_from_memory(bytes))
        {
            log_debug($"Parry SFX playback failed for {clip.FileName}.");
        }
    }

    private bool play_wave_from_memory(byte[] wavBytes)
    {
        stop_audio_playback();
        try
        {
            _activeAudioBufferHandle = GCHandle.Alloc(wavBytes, GCHandleType.Pinned);
            _activeAudioBufferPinned = true;
            nint ptr = _activeAudioBufferHandle.AddrOfPinnedObject();
            bool ok = PlaySoundW(ptr, 0, SndAsync | SndMemory | SndNoDefault);
            if (!ok)
            {
                stop_audio_playback();
            }
            return ok;
        }
        catch
        {
            stop_audio_playback();
            return false;
        }
    }

    private void stop_audio_playback()
    {
        // SND_PURGE synchronously stops the current sound and waits for the
        // audio thread to finish, ensuring the old buffer is no longer accessed
        // before we free the GCHandle. PlaySoundW(0, 0, 0) does not carry this
        // guarantee and could leave the audio thread reading freed memory.
        PlaySoundW(0, 0, SndPurge);
        if (_activeAudioBufferPinned)
        {
            _activeAudioBufferHandle.Free();
            _activeAudioBufferPinned = false;
        }
    }

    private void initialize_overlay_fonts()
    {
        _overlayFont = default;
        _overlayFontPath = null;
        _overlayFontsInitialized = false;
        _overlayFontWarningIssued = false;

        if (string.IsNullOrWhiteSpace(_fontResourcesDir) || !Directory.Exists(_fontResourcesDir))
        {
            _logger.Warning($"[Parry] Font resource directory not found: {_fontResourcesDir}");
            return;
        }

        string regularPath = Path.Combine(_fontResourcesDir, "Cinzel-Regular.ttf");
        if (File.Exists(regularPath))
        {
            _overlayFontPath = regularPath;
        }
        else
        {
            _logger.Warning($"[Parry] Required overlay font not found: {regularPath}");
        }
    }

    private void ensure_overlay_fonts_loaded()
    {
        if (_overlayFontsInitialized) return;
        _overlayFontsInitialized = true;

        if (string.IsNullOrWhiteSpace(_overlayFontPath))
        {
            return;
        }

        try
        {
            ImGuiIOPtr io = ImGui.GetIO();
            _overlayFont = io.Fonts.AddFontFromFileTTF(_overlayFontPath, OverlayFontSizePx);
            if (_overlayFont.Equals(default(ImFontPtr)))
            {
                _logger.Warning("[Parry] Overlay font loading failed. Falling back to default ImGui font.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"[Parry] Overlay font loading failed: {ex.Message}");
        }
    }

    private bool try_get_selected_overlay_font(out ImFontPtr font)
    {
        ensure_overlay_fonts_loaded();

        font = _overlayFont;
        if (!font.Equals(default(ImFontPtr))) return true;

        if (!_overlayFontWarningIssued)
        {
            _overlayFontWarningIssued = true;
            _logger.Warning("[Parry] Overlay font unavailable; defaulting to ImGui font.");
        }
        return false;
    }
}
