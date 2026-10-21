using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DeskflowCompanion
{
    internal sealed class TransferFileInfo
    {
        public string Id;
        public string Name;
        public string Path;
        public long Size;
        public DateTime CreatedAt;
        public bool Incoming;
    }

    internal sealed class LanTransferServer : IDisposable
    {
        private const long MaxUploadBytes = 20L * 1024L * 1024L * 1024L;
        private const int MaxClipboardBytes = 2 * 1024 * 1024;
        private readonly ConcurrentDictionary<string, TransferFileInfo> outgoing =
            new ConcurrentDictionary<string, TransferFileInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TransferFileInfo> incomingFiles =
            new ConcurrentDictionary<string, TransferFileInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> pathIndex =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly string computerName;
        private readonly string incomingDirectory;
        private readonly Func<string> clipboardProvider;
        private readonly Action<string> clipboardSetter;
        private readonly object lifecycleLock = new object();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;
        private int requestedPort;

        public event Action<string> FileReceived;
        public event Action<string> ClipboardReceived;
        public event Action<string> TransferMessage;

        public LanTransferServer(int port, string token, string computerName, Func<string> clipboardProvider, Action<string> clipboardSetter)
        {
            if (String.IsNullOrWhiteSpace(token)) throw new ArgumentException("Transfer token is required.", "token");
            requestedPort = port > 0 && port <= 65535 ? port : 45873;
            Token = token;
            this.computerName = String.IsNullOrWhiteSpace(computerName) ? Environment.MachineName : computerName;
            this.clipboardProvider = clipboardProvider;
            this.clipboardSetter = clipboardSetter;
            incomingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Deskflow Companion");
            Directory.CreateDirectory(incomingDirectory);
        }

        public string Token { get; private set; }
        public int Port { get; private set; }
        public bool IsRunning { get { return running; } }
        public string LastError { get; private set; }
        public string IncomingDirectory { get { return incomingDirectory; } }
        public int OutgoingCount { get { return outgoing.Count; } }
        public int IncomingCount { get { return incomingFiles.Count; } }

        public void Start()
        {
            lock (lifecycleLock)
            {
                if (running) return;
                LastError = "";
                SocketException lastSocketError = null;
                for (int offset = 0; offset < 12; offset++)
                {
                    int candidate = requestedPort + offset;
                    if (candidate > 65535) break;
                    try
                    {
                        var candidateListener = new TcpListener(IPAddress.Any, candidate);
                        candidateListener.Start();
                        listener = candidateListener;
                        Port = candidate;
                        running = true;
                        acceptThread = new Thread(AcceptLoop);
                        acceptThread.IsBackground = true;
                        acceptThread.Name = "Deskflow transfer listener";
                        acceptThread.Start();
                        RaiseMessage("局域网传输服务已启动，端口 " + Port + "。");
                        return;
                    }
                    catch (SocketException ex) { lastSocketError = ex; }
                    catch (Exception ex) { LastError = ex.Message; break; }
                }
                if (lastSocketError != null) LastError = "端口 " + requestedPort + " 附近不可用：" + lastSocketError.Message;
                if (String.IsNullOrEmpty(LastError)) LastError = "无法启动局域网传输服务。";
                RaiseMessage(LastError);
            }
        }

        public void Stop()
        {
            lock (lifecycleLock)
            {
                running = false;
                try { if (listener != null) listener.Stop(); } catch { }
                listener = null;
            }
        }

        public void Dispose() { Stop(); }

        public string BuildPrimaryUrl()
        {
            string address = GetPrimaryLanAddress();
            if (String.IsNullOrEmpty(address)) address = "127.0.0.1";
            return "http://" + address + ":" + Port + "/" + Token + "/";
        }

        public static string GetPrimaryLanAddress()
        {
            List<string> addresses = GetLanAddresses();
            return addresses.Count > 0 ? addresses[0] : "";
        }

        public static List<string> GetLanAddresses()
        {
            var result = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string name = (nic.Name + " " + nic.Description).ToLowerInvariant();
                    if (name.Contains("virtual") || name.Contains("vmware") || name.Contains("vbox") || name.Contains("hyper-v")) continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(info.Address)) continue;
                        string address = info.Address.ToString();
                        if (address.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (!result.Contains(address)) result.Add(address);
                    }
                }
            }
            catch { }
            return result;
        }
        public int SharePaths(IEnumerable<string> paths)
        {
            int added = 0;
            if (paths == null) return added;
            foreach (string raw in paths)
            {
                if (String.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    string path = Path.GetFullPath(raw);
                    if (File.Exists(path))
                    {
                        if (ShareFile(path, Path.GetFileName(path)) != null) added++;
                    }
                    else if (Directory.Exists(path))
                    {
                        string rootName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (String.IsNullOrEmpty(rootName)) rootName = "folder";
                        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            if (!File.Exists(file)) continue;
                            string relative;
                            try { relative = file.Substring(path.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
                            catch { relative = Path.GetFileName(file); }
                            string downloadName = rootName + "_" + relative.Replace('\\', '_').Replace('/', '_');
                            if (ShareFile(file, downloadName) != null) added++;
                        }
                    }
                }
                catch (Exception ex) { RaiseMessage("添加共享文件失败：" + ex.Message); }
            }
            if (added > 0) RaiseMessage("已添加 " + added + " 个文件，Mac 可以从传输页下载。");
            return added;
        }

        public TransferFileInfo[] GetOutgoingSnapshot()
        {
            return outgoing.Values.OrderByDescending(x => x.CreatedAt).ToArray();
        }

        public TransferFileInfo[] GetIncomingSnapshot()
        {
            return incomingFiles.Values.OrderByDescending(x => x.CreatedAt).ToArray();
        }

        public void ClearOutgoing()
        {
            outgoing.Clear();
            pathIndex.Clear();
            RaiseMessage("已清空 Windows 共享列表。");
        }

        private TransferFileInfo ShareFile(string path, string displayName)
        {
            string fullPath = Path.GetFullPath(path);
            string existingId;
            if (pathIndex.TryGetValue(fullPath, out existingId)) return null;
            var info = new TransferFileInfo();
            info.Id = Guid.NewGuid().ToString("N");
            info.Name = String.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(fullPath) : displayName;
            info.Path = fullPath;
            try { info.Size = new FileInfo(fullPath).Length; }
            catch { info.Size = 0; }
            info.CreatedAt = DateTime.Now;
            info.Incoming = false;
            outgoing[info.Id] = info;
            pathIndex[fullPath] = info.Id;
            return info;
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state) { HandleClient((TcpClient)state); }, client);
                }
                catch (SocketException) { if (!running) break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    RaiseMessage("传输服务连接错误：" + ex.Message);
                    try { if (client != null) client.Close(); } catch { }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            if (client == null) return;
            try
            {
                client.ReceiveTimeout = 120000;
                client.SendTimeout = 120000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    string target;
                    string method;
                    Dictionary<string, string> headers = ReadHeaders(stream, out method, out target);
                    if (headers == null)
                    {
                        WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad Request");
                        return;
                    }
                    if (String.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteResponse(stream, 204, "No Content", "text/plain; charset=utf-8", new byte[0], null, false);
                        return;
                    }

                    var uri = new Uri("http://localhost" + target);
                    string[] parts = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !String.Equals(Uri.UnescapeDataString(parts[0]), Token, StringComparison.Ordinal))
                    {
                        WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found");
                        return;
                    }
                    string route = parts.Length <= 1 ? "" : String.Join("/", parts.Skip(1).ToArray());
                    if (String.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || String.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                        HandleGet(stream, route, uri, String.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase));
                    else if (String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                        HandlePost(stream, route, uri, headers);
                    else
                        WriteText(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Method not allowed");
                }
            }
            catch (Exception ex)
            {
                RaiseMessage("处理传输请求失败：" + ex.Message);
                try { client.Close(); } catch { }
            }
        }

        private void HandleGet(NetworkStream stream, string route, Uri uri, bool headOnly)
        {
            if (route.Length == 0 || String.Equals(route, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                byte[] page = Encoding.UTF8.GetBytes(GetEmbeddedPage());
                WriteResponse(stream, 200, "OK", "text/html; charset=utf-8", page, null, headOnly);
                return;
            }
            if (String.Equals(route, "api/state", StringComparison.OrdinalIgnoreCase))
            {
                string json = BuildStateJson();
                WriteResponse(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), null, headOnly);
                return;
            }
            if (String.Equals(route, "api/clipboard", StringComparison.OrdinalIgnoreCase))
            {
                string text = "";
                try { if (clipboardProvider != null) text = clipboardProvider() ?? ""; }
                catch { }
                WriteResponse(stream, 200, "OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), null, headOnly);
                return;
            }
            if (String.Equals(route, "favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                WriteResponse(stream, 204, "No Content", "image/x-icon", new byte[0], null, true);
                return;
            }
            if (route.StartsWith("download/", StringComparison.OrdinalIgnoreCase))
            {
                string id = Uri.UnescapeDataString(route.Substring("download/".Length));
                TransferFileInfo info;
                if (!outgoing.TryGetValue(id, out info) || !File.Exists(info.Path))
                {
                    WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "File not found");
                    return;
                }
                var extra = new Dictionary<string, string>();
                extra["Content-Disposition"] = BuildContentDisposition(info.Name);
                long length;
                try { length = new FileInfo(info.Path).Length; }
                catch { length = info.Size; }
                WriteResponseHeaders(stream, 200, "OK", GetContentType(info.Name), length, extra);
                if (!headOnly)
                {
                    using (var file = new FileStream(info.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        CopyStream(file, stream, length);
                }
                return;
            }
            WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found");
        }
        private void HandlePost(NetworkStream stream, string route, Uri uri, Dictionary<string, string> headers)
        {
            long length = GetContentLength(headers);
            if (String.Equals(route, "api/upload", StringComparison.OrdinalIgnoreCase))
            {
                string requestedName = GetQueryValue(uri.Query, "name");
                if (String.IsNullOrWhiteSpace(requestedName)) requestedName = "file";
                string target = BuildUniqueIncomingPath(requestedName);
                string partial = target + ".part";
                try
                {
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                        CopyRequestBytes(stream, output, length);
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(partial, target);
                    var info = new TransferFileInfo();
                    info.Id = Guid.NewGuid().ToString("N");
                    info.Name = Path.GetFileName(target);
                    info.Path = target;
                    info.Size = new FileInfo(target).Length;
                    info.CreatedAt = DateTime.Now;
                    info.Incoming = true;
                    incomingFiles[info.Id] = info;
                    if (FileReceived != null) { try { FileReceived(target); } catch { } }
                    WriteResponse(stream, 200, "OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("{\"ok\":true,\"name\":\"" + JsonEscape(info.Name) + "\"}"), null, false);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    WriteText(stream, 500, "Internal Server Error", "application/json; charset=utf-8",
                        "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}");
                }
                return;
            }
            if (String.Equals(route, "api/clipboard", StringComparison.OrdinalIgnoreCase))
            {
                if (length < 0 || length > MaxClipboardBytes)
                {
                    WriteText(stream, 413, "Payload Too Large", "text/plain; charset=utf-8", "Clipboard is too large");
                    return;
                }
                string text = ReadRequestBody(stream, length);
                if (clipboardSetter != null) clipboardSetter(text);
                if (ClipboardReceived != null) { try { ClipboardReceived(text); } catch { } }
                WriteResponse(stream, 200, "OK", "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes("{\"ok\":true}"), null, false);
                return;
            }
            WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found");
        }

        private string BuildStateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"computer\":\"").Append(JsonEscape(computerName)).Append("\"");
            sb.Append(",\"port\":").Append(Port);
            sb.Append(",\"outgoing\":[");
            TransferFileInfo[] shared = GetOutgoingSnapshot();
            for (int i = 0; i < shared.Length; i++)
            {
                if (i > 0) sb.Append(',');
                TransferFileInfo item = shared[i];
                sb.Append("{\"id\":\"").Append(JsonEscape(item.Id)).Append("\",");
                sb.Append("\"name\":\"").Append(JsonEscape(item.Name)).Append("\",");
                sb.Append("\"size\":").Append(item.Size).Append("}");
            }
            sb.Append("],\"incoming\":[");
            TransferFileInfo[] received = GetIncomingSnapshot().Take(20).ToArray();
            for (int i = 0; i < received.Length; i++)
            {
                if (i > 0) sb.Append(',');
                TransferFileInfo item = received[i];
                sb.Append("{\"name\":\"").Append(JsonEscape(item.Name)).Append("\",");
                sb.Append("\"size\":").Append(item.Size).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static Dictionary<string, string> ReadHeaders(NetworkStream stream, out string method, out string target)
        {
            method = "";
            target = "";
            var headerBytes = new MemoryStream();
            int matched = 0;
            int value;
            while ((value = stream.ReadByte()) >= 0)
            {
                headerBytes.WriteByte((byte)value);
                if (headerBytes.Length > 65536) return null;
                if (value == '\r') matched = matched == 0 ? 1 : (matched == 2 ? 3 : matched);
                else if (value == '\n' && (matched == 1 || matched == 3)) matched++;
                else matched = 0;
                if (matched == 4) break;
            }
            if (headerBytes.Length == 0) return null;
            string text = Encoding.ASCII.GetString(headerBytes.ToArray());
            string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;
            method = requestLine[0];
            target = requestLine[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return headers;
        }

        private static long GetContentLength(Dictionary<string, string> headers)
        {
            string raw;
            if (!headers.TryGetValue("Content-Length", out raw)) return 0;
            long length;
            return Int64.TryParse(raw, out length) ? length : -1;
        }

        private static string ReadRequestBody(NetworkStream stream, long length)
        {
            if (length <= 0) return "";
            if (length > MaxClipboardBytes) throw new InvalidDataException("Body is too large.");
            var bytes = new byte[(int)length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static void CopyRequestBytes(NetworkStream stream, Stream output, long length)
        {
            if (length < 0) throw new InvalidDataException("Content-Length is required.");
            if (length > MaxUploadBytes) throw new InvalidDataException("File is too large.");
            byte[] buffer = new byte[128 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, wanted);
                if (read <= 0) throw new EndOfStreamException();
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void CopyStream(Stream input, Stream output, long length)
        {
            byte[] buffer = new byte[128 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, wanted);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        private static void WriteText(NetworkStream stream, int code, string reason, string contentType, string text)
        {
            WriteResponse(stream, code, reason, contentType, Encoding.UTF8.GetBytes(text ?? ""), null, false);
        }

        private static void WriteResponse(NetworkStream stream, int code, string reason, string contentType, byte[] body, Dictionary<string, string> extra, bool headOnly)
        {
            WriteResponseHeaders(stream, code, reason, contentType, body == null ? 0 : body.Length, extra);
            if (!headOnly && body != null && body.Length > 0) stream.Write(body, 0, body.Length);
        }

        private static void WriteResponseHeaders(NetworkStream stream, int code, string reason, string contentType, long contentLength, Dictionary<string, string> extra)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            sb.Append("Content-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data: blob:\r\n");
            if (extra != null)
            {
                foreach (var pair in extra) sb.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
            }
            sb.Append("\r\n");
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
        }

        private static string BuildContentDisposition(string name)
        {
            string safe = String.IsNullOrEmpty(name) ? "file" : name;
            char[] invalid = Path.GetInvalidFileNameChars();
            var clean = new StringBuilder();
            foreach (char ch in safe)
            {
                if (Array.IndexOf(invalid, ch) >= 0 || ch == '"' || ch == '\r' || ch == '\n') clean.Append('_');
                else clean.Append(ch);
            }
            string fallback = clean.ToString();
            if (fallback.Length == 0) fallback = "file";
            return "attachment; filename=\"" + fallback.Replace("\\", "_") + "\"; filename*=UTF-8''" + Uri.EscapeDataString(fallback);
        }

        private static string GetQueryValue(string query, string key)
        {
            if (String.IsNullOrEmpty(query)) return "";
            string raw = query.TrimStart('?');
            foreach (string pair in raw.Split('&'))
            {
                int split = pair.IndexOf('=');
                string name = split >= 0 ? pair.Substring(0, split) : pair;
                if (!String.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase)) continue;
                return split >= 0 ? Uri.UnescapeDataString(pair.Substring(split + 1)) : "";
            }
            return "";
        }

        private string BuildUniqueIncomingPath(string requestedName)
        {
            string name = Path.GetFileName(requestedName ?? "");
            if (String.IsNullOrWhiteSpace(name)) name = "file";
            char[] invalid = Path.GetInvalidFileNameChars();
            var clean = new StringBuilder();
            foreach (char ch in name)
            {
                if (Array.IndexOf(invalid, ch) >= 0 || ch == '\r' || ch == '\n') clean.Append('_');
                else clean.Append(ch);
            }
            name = clean.ToString().Trim();
            if (String.IsNullOrEmpty(name)) name = "file";
            string target = Path.Combine(incomingDirectory, name);
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            int index = 1;
            while (true)
            {
                target = Path.Combine(incomingDirectory, stem + " (" + index + ")" + extension);
                if (!File.Exists(target) && !Directory.Exists(target)) return target;
                index++;
            }
        }

        private static string GetContentType(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".txt": return "text/plain; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                case ".zip": return "application/zip";
                case ".mp4": return "video/mp4";
                case ".mp3": return "audio/mpeg";
                default: return "application/octet-stream";
            }
        }

        private static string GetEmbeddedPage()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("DeskflowCompanion.mac-transfer.html"))
                {
                    if (stream == null) return "<!doctype html><meta charset='utf-8'><h1>传输页面资源缺失</h1>";
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                return "<!doctype html><meta charset='utf-8'><h1>传输页面读取失败</h1><pre>" + WebUtility.HtmlEncode(ex.Message) + "</pre>";
            }
        }

        private static string JsonEscape(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder();
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private void RaiseMessage(string message)
        {
            if (TransferMessage != null)
            {
                try { TransferMessage(message); } catch { }
            }
        }
    }
}
