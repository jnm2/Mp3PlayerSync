using System.Globalization;

namespace Mp3PlayerSync.Core.Download;

public sealed record AudioFormatSelection(YouTubeFormat? Format, string? FailureReason)
{
    public bool Succeeded => Format is not null;
}

/// <summary>
/// Picks the audio format to download. Only AAC is accepted: it plays on the target players without conversion, so
/// no ffmpeg is needed. Opus is never chosen.
/// </summary>
public static class AudioFormatSelector
{
    /// <summary>
    /// Formats below this bitrate (e.g. YouTube's 48 kbps HE-AAC format 139) are too poor to keep.
    /// </summary>
    public const double DefaultMinimumBitrateKbps = 96;

    public static AudioFormatSelection SelectBestAac(IEnumerable<YouTubeFormat> formats, double minimumBitrateKbps = DefaultMinimumBitrateKbps)
    {
        var candidates = formats
            .Where(f => f.IsAudioOnly && f.IsAac && !f.IsOpus && !f.HasDrm)
            .Where(f => f.Extension is "m4a" or "mp4")
            // Direct downloads only. HLS (m3u8) would need ffmpeg or produce MPEG-TS.
            .Where(f => f.Protocol is null or "https" or "http")
            .OrderBy(f => f.IsDrc)
            .ThenByDescending(f => f.LanguagePreference ?? 0)
            .ThenByDescending(f => f.AudioBitrate ?? 0)
            .ThenByDescending(f => f.SampleRate ?? 0)
            .ToList();

        if (candidates.Count == 0)
            return new(null, "YouTube offers no AAC audio for this track (Opus is not accepted).");

        var best = candidates[0];
        if (best.AudioBitrate is { } bitrate && bitrate < minimumBitrateKbps)
        {
            return new(null, string.Create(CultureInfo.InvariantCulture,
                $"YouTube offers only low-quality AAC audio for this track ({bitrate:0} kbps; at least {minimumBitrateKbps:0} kbps is required)."));
        }

        return new(best, null);
    }
}
