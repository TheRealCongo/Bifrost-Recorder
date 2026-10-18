// SPDX-License-Identifier: GPL-2.0-or-later
// After anything that could touch the GPU, the System log is searched for NVIDIA display driver events (nvlddmkm: engine resets,
// TDRs) logged since the run began. The first one stops the run at once (exit code 9; the job object ends anything still running)
// and says so, because on this PC a GPU encoder session started next to a running game has reset the driver and crashed the game.
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;

static class GpuSafety {
  static DateTime started = DateTime.UtcNow;

  public static void Begin() { started = DateTime.UtcNow; }

  public static List<string> Events(DateTime sinceUtc) {
    var found = new List<string>();
    try {
      string ts = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
      var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='nvlddmkm'] and TimeCreated[@SystemTime>='" + ts + "']]]");
      using (var r = new EventLogReader(q)) {
        EventRecord e;
        while ((e = r.ReadEvent()) != null) { found.Add("id " + e.Id + " at " + e.TimeCreated); e.Dispose(); }
      }
    } catch (Exception) { }   // an unreadable log is not an event
    return found;
  }

  public static void Check(string where) {
    var ev = Events(started.AddSeconds(-2));
    if (ev.Count == 0) return;
    Console.WriteLine("SAFETY STOP after " + where + ": NVIDIA driver events in the System log since this run began: " + string.Join("; ", ev.ToArray()));
    Console.WriteLine("NVLDDMKM EVENT: stopping all GPU runs.");
    Console.Out.Flush();
    Environment.Exit(9);
  }
}
