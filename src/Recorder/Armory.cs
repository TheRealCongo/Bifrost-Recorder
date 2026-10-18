// SPDX-License-Identifier: GPL-2.0-or-later
// Sending a recording to the TFH Armory's video library, by the website's contract (25vid-Website docs/ARMORY.md, "Videos"):
//   GET  /api/armory/videos/me, /options
//   POST /api/armory/videos/uploads {folder_id, category_id, filename, size, title?, duration?, accept_terms:true} -> {upload_id, part_size, part_count, part_urls_max}
//   POST /api/armory/videos/uploads/{id}/parts {numbers:[..]} -> {urls:{"1":"https://..."}}   (signed URLs, up to 50)
//   PUT  <signed url>  the part's raw bytes, straight to the bucket (up to 4 at a time, each retried with a fresh URL)
//   POST /api/armory/videos/uploads/{id}/complete -> the receipt;   DELETE /api/armory/videos/uploads/{id} to cancel
// The member's key goes in the X-Armory-Key header of the calls to the team server and NOWHERE else: not to the bucket, not into a
// log, a message or a file. The team server must be https (plain http only for this PC, like Bifrost Suite); the bucket's signed URLs
// must be https too; no redirect is ever followed (the key, or the file, would go wherever it points). C# 5, no NuGet.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

class ArmoryException : Exception {
  public int Status;   // the HTTP status, or 0
  public ArmoryException(string message, int status) : base(message) { Status = status; }
}

class NamedItem { public string Id, Name, Description; public override string ToString() { return Name; } }

class VideoMe {
  public bool Connected;
  public long MaxBytes;
  public string[] Formats = new string[0];
  public int RetentionDays;
  public string Name;
}

class UploadStarted { public string Id; public long PartSize; public int PartCount, PartUrlsMax; }

class ArmoryClient {
  // Tests set this so a stand-in server on this PC (http://127.0.0.1) can be used for the bucket as well; a real run never does.
  public static bool TestLoopbackHttp;

  readonly string server, key;
  public int Timeout = 15000;

  // The key goes in a header, so the server must be https, or on this PC. Returns null when the server is usable, else why not.
  public static string ServerProblem(string server) {
    Uri u;
    if (string.IsNullOrEmpty(server) || !Uri.TryCreate(server, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return "The team server isn't a web address.";
    if (u.Scheme == Uri.UriSchemeHttp && !u.IsLoopback) return "The team server isn't https, so the key would travel unprotected.";
    if (!string.IsNullOrEmpty(u.UserInfo)) return "The team server address must not hold a user name or password.";
    return null;
  }

  // Signed URLs for the bucket: https (or this PC), never anything with a user name in it.
  public static string StorageProblem(string url) {
    Uri u;
    if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return "The storage address isn't a web address.";
    if (u.Scheme == Uri.UriSchemeHttps) return string.IsNullOrEmpty(u.UserInfo) ? null : "The storage address holds a user name.";
    if (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback && TestLoopbackHttp) return null;
    return "The storage address isn't https.";
  }

  public ArmoryClient(string server, string key) {
    string why = ServerProblem((server ?? "").TrimEnd('/'));
    if (why != null) throw new ArmoryException(why, 0);
    if (string.IsNullOrEmpty(key)) throw new ArmoryException("There is no Armory key.", 0);
    this.server = server.TrimEnd('/'); this.key = key;
    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }
  }

  // True when the settings carry a usable server and a key (so uploads can be offered); false means "open the website instead".
  public static bool Usable(AppSettings s) { return s != null && !string.IsNullOrEmpty(s.TeamKey) && ServerProblem(s.TeamServer) == null; }

  static string Version { get { try { return System.Windows.Forms.Application.ProductVersion; } catch (Exception) { return "0"; } } }

  HttpWebRequest Request(string method, string path) {
    var r = (HttpWebRequest)WebRequest.Create(server + "/api/armory/videos" + path);
    r.Method = method; r.UserAgent = "BifrostRecorder/" + Version; r.Accept = "application/json";
    r.Headers["X-Armory-Key"] = key;
    r.Timeout = Timeout; r.ReadWriteTimeout = 60000; r.AllowAutoRedirect = false;
    return r;
  }

  static string DetailOf(string body) {
    try {
      var d = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
      object v;
      if (d != null && d.TryGetValue("detail", out v)) {
        if (v is string) return (string)v;
        var arr = v as object[];
        if (arr != null && arr.Length > 0) { var first = arr[0] as Dictionary<string, object>; object m; if (first != null && first.TryGetValue("msg", out m)) return Convert.ToString(m, CultureInfo.InvariantCulture); }
      }
    } catch (Exception) { }
    return null;
  }

  // A call to the team server: its JSON answer, or an ArmoryException with a message fit to show (never the key, never a URL).
  object Call(string method, string path, object body) {
    try {
      var r = Request(method, path);
      if (body != null) {
        byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
        r.ContentType = "application/json"; r.ContentLength = bytes.Length;
        using (var s = r.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
      } else if (method != "GET" && method != "DELETE") r.ContentLength = 0;
      using (var res = (HttpWebResponse)r.GetResponse()) {
        int code = (int)res.StatusCode;
        if (code >= 300 && code < 400) throw new ArmoryException("The team server answered " + code + " (a redirect), so the team server in the settings is wrong.", code);
        using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) {
          string text = sr.ReadToEnd();
          return text.Length == 0 ? null : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        }
      }
    } catch (WebException ex) {
      var res = ex.Response as HttpWebResponse;
      if (res == null) throw new ArmoryException("The Armory can't be reached (" + ex.Status + ").", 0);
      int code = (int)res.StatusCode;
      string text = "";
      try { using (var sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch (Exception) { }
      res.Close();
      if (code >= 300 && code < 400) throw new ArmoryException("The team server answered " + code + " (a redirect), so the team server in the settings is wrong.", code);
      if (code == 401) throw new ArmoryException("The Armory didn't accept the key in the settings.", code);
      throw new ArmoryException(DetailOf(text) ?? ("The Armory answered " + code + "."), code);
    }
  }

  static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
  static long L(Dictionary<string, object> d, string k) { object v; long n; return d != null && d.TryGetValue(k, out v) && v != null && long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0; }

  public VideoMe GetMe() {
    var d = Call("GET", "/me", null) as Dictionary<string, object>;
    var me = new VideoMe { Connected = d != null && d.ContainsKey("connected") && d["connected"] is bool && (bool)d["connected"], MaxBytes = L(d, "max_bytes"), RetentionDays = (int)L(d, "retention_days"), Name = S(d, "name") };
    var f = d != null && d.ContainsKey("formats") ? d["formats"] as object[] : null;
    if (f != null) { var list = new List<string>(); foreach (object o in f) list.Add(Convert.ToString(o, CultureInfo.InvariantCulture)); me.Formats = list.ToArray(); }
    return me;
  }

  public void GetOptions(out List<NamedItem> folders, out List<NamedItem> categories) {
    var d = Call("GET", "/options", null) as Dictionary<string, object>;
    folders = Items(d, "folders"); categories = Items(d, "categories");
  }

  static List<NamedItem> Items(Dictionary<string, object> d, string key) {
    var list = new List<NamedItem>();
    var arr = d != null && d.ContainsKey(key) ? d[key] as object[] : null;
    if (arr != null) foreach (object o in arr) { var x = o as Dictionary<string, object>; if (x != null) list.Add(new NamedItem { Id = S(x, "id"), Name = S(x, "name"), Description = S(x, "description") }); }
    return list;
  }

  public UploadStarted StartUpload(string folderId, string categoryId, string filename, long size, string title, int? durationSeconds) {
    var body = new Dictionary<string, object> { { "folder_id", folderId }, { "category_id", categoryId }, { "filename", filename }, { "size", size }, { "title", title ?? "" }, { "accept_terms", true } };
    if (durationSeconds.HasValue) body["duration"] = durationSeconds.Value;
    var d = Call("POST", "/uploads", body) as Dictionary<string, object>;
    var u = new UploadStarted { Id = S(d, "upload_id"), PartSize = L(d, "part_size"), PartCount = (int)L(d, "part_count"), PartUrlsMax = (int)L(d, "part_urls_max") };
    if (u.Id.Length == 0 || u.PartSize <= 0 || u.PartCount <= 0) throw new ArmoryException("The Armory's answer to starting the upload was incomplete.", 0);
    return u;
  }

  public Dictionary<int, string> PartUrls(string uploadId, IList<int> numbers) {
    var nums = new List<object>(); foreach (int n in numbers) nums.Add(n);
    var d = Call("POST", "/uploads/" + Uri.EscapeDataString(uploadId) + "/parts", new Dictionary<string, object> { { "numbers", nums } }) as Dictionary<string, object>;
    var urls = d != null && d.ContainsKey("urls") ? d["urls"] as Dictionary<string, object> : null;
    var result = new Dictionary<int, string>();
    if (urls != null) foreach (var kv in urls) { int n; if (int.TryParse(kv.Key, out n)) result[n] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture); }
    foreach (int n in numbers) if (!result.ContainsKey(n)) throw new ArmoryException("The Armory gave no address for part " + n + ".", 0);
    return result;
  }

  public Dictionary<string, object> Complete(string uploadId) {
    var d = Call("POST", "/uploads/" + Uri.EscapeDataString(uploadId) + "/complete", new Dictionary<string, object>()) as Dictionary<string, object>;
    return d ?? new Dictionary<string, object>();
  }

  public void Abort(string uploadId) { Call("DELETE", "/uploads/" + Uri.EscapeDataString(uploadId), null); }

  // Puts one part's bytes (length bytes of the file from offset) on a signed URL. No key is sent. onBytes gets the bytes sent so far in this
  // attempt; cancelled is polled between 64 KB writes and ends the request at once. Any non-2xx answer, redirects included, is an error.
  public static void PutPart(string url, string file, long offset, long length, Action<long> onBytes, Func<bool> cancelled) {
    string why = StorageProblem(url);
    if (why != null) throw new ArmoryException(why, 0);
    var r = (HttpWebRequest)WebRequest.Create(url);
    r.Method = "PUT"; r.AllowAutoRedirect = false; r.AllowWriteStreamBuffering = false; r.ContentLength = length;
    r.Timeout = 600000; r.ReadWriteTimeout = 60000; r.KeepAlive = true; r.UserAgent = "BifrostRecorder/" + Version;
    r.ServicePoint.Expect100Continue = false;
    // a cancel ends the request even while it waits for the answer
    using (var watcher = new Timer(delegate { try { if (cancelled != null && cancelled()) r.Abort(); } catch (Exception) { } }, null, 100, 100))
    try {
      using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
      using (var rs = r.GetRequestStream()) {
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[65536];
        long left = length, sent = 0;
        while (left > 0) {
          if (cancelled != null && cancelled()) { r.Abort(); throw new OperationCanceledException(); }
          int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
          if (n <= 0) throw new ArmoryException("The recording file ended early.", 0);
          rs.Write(buf, 0, n);
          left -= n; sent += n;
          if (onBytes != null) onBytes(sent);
        }
      }
      using (var res = (HttpWebResponse)r.GetResponse()) {
        int code = (int)res.StatusCode;
        if (code >= 300 && code < 400) throw new ArmoryException("The storage server answered a redirect (" + code + "), which is never followed.", code);
        if (code < 200 || code >= 300) throw new ArmoryException("The storage server answered " + code + ".", code);
      }
    } catch (WebException ex) {
      if (cancelled != null && cancelled()) throw new OperationCanceledException();
      var res = ex.Response as HttpWebResponse;
      if (res != null) { int code = (int)res.StatusCode; res.Close(); throw new ArmoryException(code >= 300 && code < 400 ? "The storage server answered a redirect." : "The storage server answered " + code + ".", code); }
      throw new ArmoryException("The storage server can't be reached (" + ex.Status + ").", 0);
    }
  }
}

// One whole upload: start, parts (four at a time, each retried with a fresh URL), complete; cancel or failure aborts it at the server.
class UploadJob {
  public ArmoryClient Client;
  public string File, FolderId, CategoryId, Title;
  public int? DurationSeconds;
  public int Parallel = 4, Attempts = 4, RetryDelayMs = 1000, UrlBatch = 16;
  public event Action<long, long> Progress;       // bytes sent, total bytes (from any worker thread)
  public event Action<string> Note;               // short facts for a log: never a key, never a URL
  volatile bool cancel;
  public void Cancel() { cancel = true; }
  public bool Cancelled { get { return cancel; } }

  void Say(string s) { var h = Note; if (h != null) h(s); }

  // Returns the receipt (the video record). Throws OperationCanceledException after a cancel and ArmoryException otherwise.
  public Dictionary<string, object> Run() {
    var fi = new FileInfo(File);
    long size = fi.Length;
    var up = Client.StartUpload(FolderId, CategoryId, fi.Name, size, Title, DurationSeconds);
    Say("upload started: " + up.PartCount + " parts of " + up.PartSize + " bytes");
    var sent = new long[up.PartCount + 1];
    var urls = new Dictionary<int, string>();
    var gate = new object();
    int next = 0;
    Exception failure = null;
    int batch = Math.Max(1, Math.Min(UrlBatch, up.PartUrlsMax > 0 ? up.PartUrlsMax : UrlBatch));

    Action report = delegate {
      long total = 0; lock (sent) foreach (long v in sent) total += v;
      var h = Progress; if (h != null) h(total, size);
    };

    Func<int, bool, string> urlFor = delegate(int n, bool fresh) {
      if (fresh) return Client.PartUrls(up.Id, new[] { n })[n];
      lock (urls) {
        string u;
        if (urls.TryGetValue(n, out u)) return u;
        var want = new List<int>();
        int start = ((n - 1) / batch) * batch + 1;
        for (int p = start; p < start + batch && p <= up.PartCount; p++) want.Add(p);
        foreach (var kv in Client.PartUrls(up.Id, want)) urls[kv.Key] = kv.Value;
        return urls[n];
      }
    };

    ThreadStart worker = delegate {
      while (true) {
        int n;
        lock (gate) { if (cancel || failure != null || next >= up.PartCount) return; next++; n = next; }
        long off = (n - 1) * up.PartSize, len = Math.Min(size - off, up.PartSize);
        try {
          for (int attempt = 1; ; attempt++) {
            try {
              string url = urlFor(n, attempt > 1);
              ArmoryClient.PutPart(url, File, off, len, delegate(long b) { lock (sent) sent[n] = b; report(); }, delegate { return cancel; });
              lock (sent) sent[n] = len;
              report();
              break;
            } catch (OperationCanceledException) { throw; }
            catch (Exception ex) {
              lock (sent) sent[n] = 0; report();
              if (cancel) throw new OperationCanceledException();
              if (attempt >= Attempts) throw new ArmoryException("Part " + n + " of " + up.PartCount + " failed after " + Attempts + " tries: " + ex.Message, 0);
              Say("part " + n + " try " + attempt + " failed (" + ex.Message + "); trying again with a fresh address");
              int waited = 0, delay = RetryDelayMs << (attempt - 1);
              while (waited < delay && !cancel) { Thread.Sleep(20); waited += 20; }
            }
          }
        } catch (Exception ex) {
          lock (gate) { if (failure == null && !(ex is OperationCanceledException)) failure = ex; }
          return;
        }
      }
    };

    try {
      var threads = new List<Thread>();
      for (int i = 0; i < Math.Min(Parallel, up.PartCount); i++) { var t = new Thread(worker) { IsBackground = true, Name = "upload part" }; threads.Add(t); t.Start(); }
      foreach (var t in threads) t.Join();
      if (cancel) throw new OperationCanceledException();
      if (failure != null) throw failure;
      var receipt = Client.Complete(up.Id);
      Say("upload complete");
      return receipt;
    } catch (Exception ex) {
      try { Client.Abort(up.Id); Say("upload aborted at the server"); } catch (Exception) { }
      if (ex is OperationCanceledException || ex is ArmoryException) throw;
      throw new ArmoryException(ex.Message, 0);
    }
  }
}
