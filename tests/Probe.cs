// SPDX-License-Identifier: GPL-2.0-or-later
// What the tests ask about a recorded file, with ffmpeg and ffprobe from Bifrost Studio (build\testbin, test only: the Recorder itself
// doesn't ship them). C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

static class Tools {
  public static readonly string Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testbin");
  public static string Ffmpeg { get { return Path.Combine(Dir, "ffmpeg.exe"); } }
  public static string Ffprobe { get { return Path.Combine(Dir, "ffprobe.exe"); } }
  public static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

  // One still picture from a video at the given second, as a PNG. True when it was made.
  public static bool Still(string video, double at, string png) {
    string so, se;
    int code = Proc.Run(Ffmpeg, "-hide_banner -loglevel error -nostdin -y -ss " + at.ToString("0.###", CultureInfo.InvariantCulture) + " -i " + Quote(video) + " -frames:v 1 " + Quote(png), 30000, out so, out se);
    return code == 0 && File.Exists(png);
  }

  // The last lines of a text, joined (for short error reports).
  public static string Last(string text, int lines) {
    var all = new List<string>();
    foreach (var l in (text ?? "").Split('\n')) if (l.Trim().Length > 0) all.Add(l.Trim());
    int from = Math.Max(0, all.Count - lines);
    return string.Join(" | ", all.GetRange(from, all.Count - from).ToArray());
  }
}

class AudioStream {
  public string Codec, Title, Handler;
  public int Channels, SampleRate;
  public double Duration;
}

class ProbeResult {
  public bool Ok;
  public string Codec, Profile, PixFmt, FrameRate, AvgFrameRate, Range, Space, Transfer, Primaries, Tag, Error;
  public int Width, Height;
  public long Frames = -1;       // nb_frames from the file, or the count when asked to count
  public double Duration, VideoDuration;
  public long Size;
  public readonly List<AudioStream> Audio = new List<AudioStream>();

  static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
  static long L(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; }
  static double D(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }

  // Asks ffprobe about a file's first video stream and every audio stream. countFrames decodes the video to count its frames (slow on long files).
  public static ProbeResult Run(string file, bool countFrames) {
    var r = new ProbeResult();
    string so, se;
    string args = "-v error " + (countFrames ? "-count_frames " : "") +
                  "-show_entries stream=codec_type,codec_name,profile,width,height,r_frame_rate,avg_frame_rate,nb_frames,nb_read_frames,pix_fmt,color_range,color_space,color_transfer,color_primaries,codec_tag_string,channels,sample_rate,duration:stream_tags=title,name,handler_name:format=duration,size -of json " + Tools.Quote(file);
    int code = Proc.Run(Tools.Ffprobe, args, countFrames ? 300000 : 30000, out so, out se);
    if (code != 0) { r.Error = "ffprobe exit " + code + ": " + Tools.Last(se, 2); return r; }
    try {
      var root = new JavaScriptSerializer().DeserializeObject(so) as Dictionary<string, object>;
      var streams = root != null && root.ContainsKey("streams") ? root["streams"] as object[] : null;
      bool video = false;
      foreach (var o in streams ?? new object[0]) {
        var s = (Dictionary<string, object>)o;
        var tags = s.ContainsKey("tags") ? s["tags"] as Dictionary<string, object> : null;
        if (S(s, "codec_type") == "video" && !video) {
          video = true;
          r.Codec = S(s, "codec_name"); r.Profile = S(s, "profile"); r.PixFmt = S(s, "pix_fmt");
          r.Width = (int)L(S(s, "width") ?? "0"); r.Height = (int)L(S(s, "height") ?? "0");
          r.FrameRate = S(s, "r_frame_rate"); r.AvgFrameRate = S(s, "avg_frame_rate");
          r.Range = S(s, "color_range"); r.Space = S(s, "color_space"); r.Transfer = S(s, "color_transfer"); r.Primaries = S(s, "color_primaries");
          r.Tag = S(s, "codec_tag_string"); r.VideoDuration = D(S(s, "duration"));
          r.Frames = countFrames ? L(S(s, "nb_read_frames")) : L(S(s, "nb_frames") ?? "-1");
        } else if (S(s, "codec_type") == "audio") {
          r.Audio.Add(new AudioStream { Codec = S(s, "codec_name"), Channels = (int)L(S(s, "channels") ?? "0"), SampleRate = (int)L(S(s, "sample_rate") ?? "0"),
                                        Duration = D(S(s, "duration")), Title = S(tags, "title") ?? S(tags, "name"), Handler = S(tags, "handler_name") });   // OBS's MP4 names a track in its udta "name" box, which ffprobe shows as the tag "name"
        }
      }
      if (!video) { r.Error = "no video stream"; return r; }
      var fmt = root.ContainsKey("format") ? root["format"] as Dictionary<string, object> : null;
      if (fmt != null) { r.Duration = D(S(fmt, "duration")); r.Size = L(S(fmt, "size") ?? "0"); }
      r.Ok = r.Codec != null && r.Width > 0 && r.Height > 0;
      if (!r.Ok) r.Error = "ffprobe found no usable picture";
    } catch (Exception ex) { r.Error = "ffprobe output unreadable: " + ex.Message; }
    return r;
  }

  public string AudioSummary() {
    var parts = new List<string>();
    foreach (var a in Audio) parts.Add((a.Title ?? a.Handler ?? "?") + " " + a.Codec + " " + a.Channels + "ch " + a.SampleRate + "Hz " + a.Duration.ToString("0.00", CultureInfo.InvariantCulture) + "s");
    return Audio.Count + " audio: " + string.Join("; ", parts.ToArray());
  }
}
