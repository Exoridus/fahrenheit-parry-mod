// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private const uint SndAsync = 0x0001;
    private const uint SndMemory = 0x0004;
    private const uint SndNoDefault = 0x0002;
    private const uint SndPurge = 0x0040;
    private const float DefaultParryAudioVolumeRatio = 0.3f;
    private const float OverlayFontSizePx = 62f;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(nint pszSound, nint hmod, uint fdwSound);

    private readonly List<WavClip> _parryAudioClips = new(8);
    private WavClip? _silenceAudioClip;
    private bool _audioWarmupAttempted;

    private GCHandle _activeAudioBufferHandle;
    private bool _activeAudioBufferPinned;

    private void initialize_audio_resources()
    {
        _parryAudioClips.Clear();
        _silenceAudioClip = null;
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

        string silencePath = Path.Combine(_audioResourcesDir, "silence.wav");
        if (File.Exists(silencePath) && try_load_wav_clip(silencePath, out WavClip silence))
        {
            _silenceAudioClip = silence;
        }
        else
        {
            _logger.Warning($"[Parry] Audio warmup clip missing or invalid: {silencePath}");
        }

        _logger.Info($"[Parry] Loaded {_parryAudioClips.Count} parry audio clip(s).");
    }

    private static bool try_load_wav_clip(string path, out WavClip clip)
    {
        clip = default;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44) return false;
            if (!match_ascii(bytes, 0, "RIFF") || !match_ascii(bytes, 8, "WAVE")) return false;

            int offset = 12;
            bool foundFmt = false;
            bool foundData = false;
            ushort fmtCode = 0;
            ushort channels = 0;
            int sampleRate = 0;
            ushort bitsPerSample = 0;
            int dataOffset = 0;
            int dataSize = 0;

            while (offset + 8 <= bytes.Length)
            {
                int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
                if (chunkSize < 0 || offset + 8 + chunkSize > bytes.Length) return false;

                if (match_ascii(bytes, offset, "fmt "))
                {
                    if (chunkSize < 16) return false;
                    fmtCode       = BitConverter.ToUInt16(bytes, offset + 8);
                    channels      = BitConverter.ToUInt16(bytes, offset + 10);
                    sampleRate    = BitConverter.ToInt32(bytes,  offset + 12);
                    bitsPerSample = BitConverter.ToUInt16(bytes, offset + 22);
                    foundFmt = true;
                }
                else if (match_ascii(bytes, offset, "data"))
                {
                    dataOffset = offset + 8;
                    dataSize   = chunkSize;
                    foundData  = true;
                }

                int advance = chunkSize + (chunkSize & 1);
                offset += 8 + advance;
            }

            if (!foundFmt || !foundData) return false;
            if (fmtCode != 1) return false;
            if (bitsPerSample != 16) return false;
            if (channels is < 1 or > 2) return false;
            if (sampleRate <= 0) return false;

            clip = new WavClip(Path.GetFileName(path), bytes, dataOffset, dataSize);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool match_ascii(byte[] bytes, int offset, string text)
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
        int end = Math.Min(scaled.Length, clip.DataOffset + clip.DataSize);

        for (int i = clip.DataOffset; i + 1 < end; i += 2)
        {
            short sample = BitConverter.ToInt16(scaled, i);
            int scaledSample = (int)MathF.Round(sample * clamped);
            scaledSample = Math.Clamp(scaledSample, short.MinValue, short.MaxValue);
            short s = (short)scaledSample;
            scaled[i]     = (byte)(s & 0xFF);
            scaled[i + 1] = (byte)((s >> 8) & 0xFF);
        }

        return scaled;
    }

    private void play_feedback_sound()
    {
        if (!_optionSound)
        {
            write_session_hook_entry("[ParrySFX] skipped: _optionSound=false");
            return;
        }
        if (_parryAudioClips.Count == 0)
        {
            write_session_hook_entry("[ParrySFX] skipped: no clips loaded");
            return;
        }

        if (!try_get_game_audio_volume_ratio(out float gameVolumeRatio))
        {
            gameVolumeRatio = DefaultParryAudioVolumeRatio;
        }

        float finalVolume = Math.Clamp(gameVolumeRatio, 0f, 1f);
        if (finalVolume <= 0f)
        {
            write_session_hook_entry("[ParrySFX] skipped: game volume is 0");
            return;
        }

        int idx = _rng.Next(_parryAudioClips.Count);
        WavClip clip = _parryAudioClips[idx];
        byte[] bytes = scale_wav_pcm_16(clip, finalVolume);
        bool ok = play_wave_from_memory(bytes);
        write_session_hook_entry($"[ParrySFX] play clip={clip.FileName} gameVolume={finalVolume:F2} ok={ok}");
        if (!ok)
        {
            log_debug($"Parry SFX playback failed for {clip.FileName}.");
        }
    }

    private bool play_wave_from_memory(byte[] wavBytes, bool asyncPlayback = true)
    {
        stop_audio_playback();
        try
        {
            _activeAudioBufferHandle = GCHandle.Alloc(wavBytes, GCHandleType.Pinned);
            _activeAudioBufferPinned = true;
            nint ptr = _activeAudioBufferHandle.AddrOfPinnedObject();
            uint flags = (asyncPlayback ? SndAsync : 0u) | SndMemory | SndNoDefault;
            bool ok = PlaySoundW(ptr, 0, flags);
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

    private void warmup_audio_playback_once()
    {
        if (_audioWarmupAttempted) return;
        _audioWarmupAttempted = true;

        if (_silenceAudioClip == null) return;

        // One synchronous silent playback eagerly initializes WinMM audio state so
        // first real parry feedback is less likely to be delayed or dropped.
        bool ok = play_wave_from_memory(_silenceAudioClip.Value.Bytes, asyncPlayback: false);
        stop_audio_playback();
        if (!ok)
        {
            _logger.Warning("[Parry] Audio warmup playback failed; continuing with normal audio path.");
        }
    }

    private bool try_get_game_audio_volume_ratio(out float ratio)
    {
        ratio = 0f;
        try
        {
            uint* pointerCell = FhUtil.ptr_at<uint>(ExternalMemoryOffsetMap.OptionsStruct.PointerAddress);
            if (pointerCell == null) return false;

            uint settingsBaseAddr = *pointerCell;
            if (settingsBaseAddr == 0) return false;

            int* settings  = (int*)settingsBaseAddr;
            int  masterRaw = settings[ExternalMemoryOffsetMap.OptionsStruct.MasterVolumeIndex];
            int  seRaw     = settings[ExternalMemoryOffsetMap.OptionsStruct.SeVolumeIndex];

            int maxScale = ExternalMemoryOffsetMap.OptionsStruct.VolumeScaleMax;
            if ((uint)masterRaw > (uint)maxScale) return false;
            if ((uint)seRaw     > (uint)maxScale) return false;

            ratio = (masterRaw / (float)maxScale) * (seRaw / (float)maxScale);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void stop_audio_playback()
    {
        // SND_PURGE synchronously stops the current sound and waits for the
        // audio thread to finish, ensuring the old buffer is no longer accessed.
        PlaySoundW(0, 0, SndPurge);
        if (_activeAudioBufferPinned)
        {
            _activeAudioBufferHandle.Free();
            _activeAudioBufferPinned = false;
        }
    }

    private readonly struct WavClip
    {
        public readonly string FileName;
        public readonly byte[] Bytes;
        public readonly int DataOffset;
        public readonly int DataSize;

        public WavClip(string fileName, byte[] bytes, int dataOffset, int dataSize)
        {
            FileName = fileName;
            Bytes = bytes;
            DataOffset = dataOffset;
            DataSize = dataSize;
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
