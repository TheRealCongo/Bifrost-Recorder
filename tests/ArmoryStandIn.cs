// SPDX-License-Identifier: GPL-2.0-or-later
// A stand-in for the TFH Armory's video routes and its bucket, on this PC (HttpListener on 127.0.0.1), for the tests of the upload
// client. The tests never contact the real site. The key it expects is a made-up one the test generates; it never prints it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class ArmoryStandIn : IDisposable {
  public readonly string Url, Key;
  public long PartSize = 1024 * 1024;
  public int PutDelayMs;                          // each part's PUT takes at least this long (so uploads can overlap or be cancelled)
  public int FailFirstPutOfPart;                  // 500 for the first PUT of this part
  public int RedirectPartTo;                      // 302 for PUTs of this part, pointing at /evil
  public string CompleteError;                    // complete answers 400 with this detail
  public bool NotConnected;
  readonly HttpListener listener = new HttpListener();
  readonly Thread loop;
  readonly object gate = new object();
  public readonly List<string> Requests = new List<string>();          // "METHOD path"
  public readonly List<string> PartNumberRequests = new List<string>(); // the "numbers" of each /parts call
  public bool KeyOnBucket;                         // true if X-Armory-Key was ever sent to the bucket
  public bool KeyMissingOnApi;                     // true if an API call came without the key
  public int EvilHits, Deletes, Completes, Starts;
  public int MaxConcurrentPuts, concurrent;
  public string LastStartBody;
  readonly Dictionary<int, long> received = new Dictionary<int, long>();
  readonly HashSet<int> failedOnce = new HashSet<int>();
  string uploadId = "up1";
  long size; int partCount;

  public ArmoryStandIn(string key) {
    Key = key;
    int port;
    var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
    Url = "http://127.0.0.1:" + port;
    listener.Prefixes.Add(Url + "/");
    listener.Start();
    loop = new Thread(Loop) { IsBackground = true, Name = "armory stand-in" };
    loop.Start();
  }

  public void Dispose() { try { listener.Stop(); listener.Close(); } catch (Exception) { } }

  void Loop() {
    while (listener.IsListening) {
      HttpListenerContext c;
      try { c = listener.GetContext(); } catch (Exception) { return; }
      ThreadPool.QueueUserWorkItem(delegate { try { Handle(c); } catch (Exception) { try { c.Response.Abort(); } catch (Exception) { } } });
    }
  }

  static void Json(HttpListenerContext c, int status, object o) {
    byte[] b = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(o));
    c.Response.StatusCode = status; c.Response.ContentType = "application/json"; c.Response.ContentLength64 = b.Length;
    c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
  }

  static string Body(HttpListenerContext c) { using (var sr = new StreamReader(c.Request.InputStream, Encoding.UTF8)) return sr.ReadToEnd(); }

  void Handle(HttpListenerContext c) {
    string path = c.Request.Url.AbsolutePath, method = c.Request.HttpMethod;
    lock (gate) Requests.Add(method + " " + path);
    if (path.StartsWith("/evil")) { lock (gate) EvilHits++; Json(c, 200, new Dictionary<string, object>()); return; }
    if (path.StartsWith("/bucket/")) { Bucket(c, path); return; }
    const string root = "/api/armory/videos";
    if (!path.StartsWith(root)) { Json(c, 404, new Dictionary<string, object> { { "detail", "no such route" } }); return; }
    if (c.Request.Headers["X-Armory-Key"] != Key) { lock (gate) KeyMissingOnApi = true; Json(c, 401, new Dictionary<string, object> { { "detail", "A valid Armory access key is required (S-4 > Armory > Download)." } }); return; }
    string rest = path.Substring(root.Length);
    var ser = new JavaScriptSerializer();
    if (method == "GET" && rest == "/me") {
      Json(c, 200, new Dictionary<string, object> { { "connected", !NotConnected }, { "can_manage", false }, { "can_edit_lists", false }, { "name", "Test Member" }, { "max_bytes", 20L * 1024 * 1024 * 1024 },
        { "formats", new object[] { ".mp4", ".mkv" } }, { "retention_days", 60 }, { "warn_days", new object[] { 15, 10, 1 } } });
    } else if (method == "GET" && rest == "/options") {
      Json(c, 200, new Dictionary<string, object> {
        { "folders", new object[] { new Dictionary<string, object> { { "id", "f1" }, { "name", "Operations" }, { "description", "Clips from operations" } }, new Dictionary<string, object> { { "id", "f2" }, { "name", "Training" }, { "description", "" } } } },
        { "categories", new object[] { new Dictionary<string, object> { { "id", "c1" }, { "name", "Highlight" } }, new Dictionary<string, object> { { "id", "c2" }, { "name", "Bug" } } } } });
    } else if (method == "POST" && rest == "/uploads") {
      string body = Body(c);
      var d = ser.DeserializeObject(body) as Dictionary<string, object>;
      lock (gate) { LastStartBody = body; Starts++; received.Clear(); failedOnce.Clear(); }
      if (d == null || !(d.ContainsKey("accept_terms") && d["accept_terms"] is bool && (bool)d["accept_terms"])) { Json(c, 400, new Dictionary<string, object> { { "detail", "You must accept the terms." } }); return; }
      size = Convert.ToInt64(d["size"], CultureInfo.InvariantCulture);
      partCount = (int)((size + PartSize - 1) / PartSize);
      Json(c, 200, new Dictionary<string, object> { { "upload_id", uploadId }, { "part_size", PartSize }, { "part_count", partCount }, { "part_urls_max", 50 } });
    } else if (method == "POST" && rest == "/uploads/" + uploadId + "/parts") {
      var d = ser.DeserializeObject(Body(c)) as Dictionary<string, object>;
      var nums = d["numbers"] as object[];
      var urls = new Dictionary<string, object>();
      var sb = new StringBuilder();
      foreach (object o in nums) { int n = Convert.ToInt32(o, CultureInfo.InvariantCulture); urls[n.ToString()] = Url + "/bucket/" + uploadId + "/" + n + "?sig=" + Guid.NewGuid().ToString("N"); sb.Append(n).Append(' '); }
      lock (gate) PartNumberRequests.Add(sb.ToString().Trim());
      Json(c, 200, new Dictionary<string, object> { { "urls", urls } });
    } else if (method == "POST" && rest == "/uploads/" + uploadId + "/complete") {
      lock (gate) Completes++;
      if (CompleteError != null) { Json(c, 400, new Dictionary<string, object> { { "detail", CompleteError } }); return; }
      string bad = null;
      lock (gate) {
        for (int n = 1; n <= partCount; n++) {
          long want = n < partCount ? PartSize : size - PartSize * (partCount - 1);
          if (!received.ContainsKey(n)) { bad = "part " + n + " is missing"; break; }
          if (received[n] != want) { bad = "part " + n + " has " + received[n] + " bytes, not " + want; break; }
        }
      }
      if (bad != null) { Json(c, 400, new Dictionary<string, object> { { "detail", bad } }); return; }
      Json(c, 200, new Dictionary<string, object> { { "id", "v1" }, { "title", "Test" }, { "format", "mp4" }, { "size", size } });
    } else if (method == "DELETE" && rest == "/uploads/" + uploadId) {
      lock (gate) Deletes++;
      Json(c, 200, new Dictionary<string, object> { { "aborted", uploadId } });
    } else Json(c, 404, new Dictionary<string, object> { { "detail", "no such route" } });
  }

  void Bucket(HttpListenerContext c, string path) {
    if (c.Request.Headers["X-Armory-Key"] != null) lock (gate) KeyOnBucket = true;
    var parts = path.Split('/');
    int n = int.Parse(parts[parts.Length - 1], CultureInfo.InvariantCulture);
    if (c.Request.HttpMethod != "PUT") { Json(c, 405, new Dictionary<string, object>()); return; }
    bool fail = false;
    lock (gate) { if (FailFirstPutOfPart == n && failedOnce.Add(n)) fail = true; concurrent++; if (concurrent > MaxConcurrentPuts) MaxConcurrentPuts = concurrent; }
    int status = 200; long total = 0;
    try {
      var buf = new byte[65536]; int r;
      while ((r = c.Request.InputStream.Read(buf, 0, buf.Length)) > 0) total += r;
      if (PutDelayMs > 0) Thread.Sleep(PutDelayMs);
      if (RedirectPartTo == n) status = 302; else if (fail) status = 500; else lock (gate) received[n] = total;
    } finally { lock (gate) concurrent--; }   // counted out before the answer goes, so the client can't start another before this one is over
    c.Response.StatusCode = status;
    if (status == 302) c.Response.RedirectLocation = Url + "/evil/" + n;
    c.Response.Close();
  }

  public long ReceivedBytes() { lock (gate) { long t = 0; foreach (var v in received.Values) t += v; return t; } }
  public int ReceivedParts() { lock (gate) return received.Count; }
  public int CountRequests(string prefix) { lock (gate) { int n = 0; foreach (string r in Requests) if (r.StartsWith(prefix)) n++; return n; } }
}
