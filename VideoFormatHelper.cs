using System;
using System.Linq;
using System.Text.RegularExpressions;
using TIS.Imaging;

namespace QueenPix
{
    /// <summary>
    /// Video formats by name, e.g. "Y800 (640x480)". Besides the camera's preset list
    /// (ICImagingControl.VideoFormats), a camera with a region-of-interest format description
    /// (ICImagingControl.VideoFormatDescs) accepts any width x height between that description's
    /// MinSize and MaxSize in steps of StepSize, e.g. "Y800 (480x800)".
    /// </summary>
    internal static class VideoFormatHelper
    {
        static readonly Regex FormatName = new Regex(@"^\s*(\S+)\s*\(\s*(\d+)\s*x\s*(\d+)\s*\)\s*$");

        /// <summary>The camera's own format object for this name: a preset, or one created from
        /// the format description. Null (with the reason in message) if the camera can't do it.</summary>
        public static VideoFormat? Find(VideoFormat[]? presets, VideoFormatDesc[]? descs, string name, out string message)
        {
            message = "";
            var preset = presets?.FirstOrDefault(f => f.ToString() == name);
            if (preset != null) return preset;

            var m = FormatName.Match(name ?? "");
            if (!m.Success) { message = $"'{name}' is not a format name like 'Y800 (480x800)'."; return null; }
            string type = m.Groups[1].Value;
            int w = int.Parse(m.Groups[2].Value), h = int.Parse(m.Groups[3].Value);

            foreach (var d in Plain(descs, type))
            {
                if (d.IsValidSize(w, h)) return d.CreateVideoFormat(w, h);
                message = $"{w}x{h} is not possible for {type}. " + Range(d);
            }
            if (message == "")
                message = $"This camera has no {type} format that allows a free size" +
                          (descs == null || descs.Length == 0 ? "." : $" (formats: {string.Join(", ", descs.Select(x => x.ToString()))}).");
            return null;
        }

        /// <summary>Allowed sizes of the camera's free-size formats, for help texts.</summary>
        public static string Help(VideoFormatDesc[]? descs)
        {
            var lines = (descs ?? Array.Empty<VideoFormatDesc>())
                .Where(d => SafeROI(d) && Unbinned(d))
                .Select(d => $"{d}: {Range(d)}").ToArray();
            return lines.Length == 0 ? "This camera offers only the listed sizes."
                : "Type any size, e.g. Y800 (480x800).\n" + string.Join("\n", lines);
        }

        static string Range(VideoFormatDesc d)
        {
            var min = d.MinSize; var max = d.MaxSize; var step = d.StepSize;
            return $"Width {min.Width}-{max.Width} in steps of {step.Width}, height {min.Height}-{max.Height} in steps of {step.Height}.";
        }

        // Free-size descriptions of this colour type without binning or skipping (full resolution)
        static VideoFormatDesc[] Plain(VideoFormatDesc[]? descs, string type) =>
            (descs ?? Array.Empty<VideoFormatDesc>())
                .Where(d => d.ToString().StartsWith(type, StringComparison.OrdinalIgnoreCase) && SafeROI(d) && Unbinned(d))
                .ToArray();

        static bool SafeROI(VideoFormatDesc d) { try { return d.IsROIFormat; } catch { return false; } }

        static bool Unbinned(VideoFormatDesc d)
        {
            try
            {
                return d.BinningFactorHorizontal <= 1 && d.BinningFactorVertical <= 1 &&
                       d.SkippingFactorHorizontal <= 1 && d.SkippingFactorVertical <= 1;
            }
            catch { return true; }
        }
    }
}
