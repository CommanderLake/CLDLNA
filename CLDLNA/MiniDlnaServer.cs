using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
namespace CLDLNA{
	internal sealed class MiniDlnaServer : IDisposable{
		private readonly string _friendlyName;
		private readonly object _gate = new object();
		private readonly HttpListener _http = new HttpListener();
		private readonly int _port;
		private readonly List<string> _roots = new List<string>();
		private readonly string _udn = "uuid:" + Guid.NewGuid();
		private Thread _httpThread;
		private volatile bool _running;
		private Thread _ssdpThread;
		private UdpClient _ssdpUdp;
		internal MiniDlnaServer(string friendlyName, int port){
			_friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "CLDLNA" : friendlyName.Trim();
			_port = port <= 0 ? 8200 : port;
		}
		internal bool IsRunning => _running;
		internal string BaseUrl => "http://" + GetLocalIp() + ":" + _port + "/";
		public void Dispose(){
			Stop();
			try{ _http.Close(); } catch{}
		}
		internal void SetFolders(IEnumerable<string> folders){
			lock(_gate){
				_roots.Clear();
				if(folders == null) return;
				foreach(var full in from f in folders where !string.IsNullOrWhiteSpace(f) select Path.GetFullPath(f) into full where Directory.Exists(full) && !_roots.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)) select full) _roots.Add(full);
			}
		}
		internal void Start(){
			if(_running) return;
			_http.Prefixes.Clear();
			_http.Prefixes.Add("http://+:" + _port + "/");
			_http.Start();
			_running = true;
			_httpThread = new Thread(HttpLoop){ IsBackground = true, Name = "DLNA-HTTP" };
			_httpThread.Start();
			_ssdpThread = new Thread(SsdpLoop){ IsBackground = true, Name = "DLNA-SSDP" };
			_ssdpThread.Start();
		}
		private void Stop(){
			if(!_running) return;
			_running = false;
			try{ _ssdpUdp?.Close(); } catch{}
			try{ _http.Stop(); } catch{}
			try{
				if(_httpThread != null && _httpThread.IsAlive) _httpThread.Join(500);
			} catch{}
			try{
				if(_ssdpThread != null && _ssdpThread.IsAlive) _ssdpThread.Join(500);
			} catch{}
		}
		private void HttpLoop(){
			while(_running){
				HttpListenerContext ctx;
				try{ ctx = _http.GetContext(); } catch{
					if(!_running) break;
					continue;
				}
				try{ HandleHttp(ctx); } catch{
					try{
						ctx.Response.StatusCode = 500;
						ctx.Response.Close();
					} catch{}
				}
			}
		}
		private void HandleHttp(HttpListenerContext ctx){
			var path = ctx.Request.Url.AbsolutePath;
			if(path.Equals("/description.xml", StringComparison.OrdinalIgnoreCase)){
				WriteDescription(ctx);
				return;
			}
			if(path.Equals("/ContentDirectory/control", StringComparison.OrdinalIgnoreCase)){
				HandleContentDirectoryControl(ctx);
				return;
			}
			if(path.StartsWith("/file/", StringComparison.OrdinalIgnoreCase)){
				ServeFile(ctx, path.Substring(6));
				return;
			}
			ctx.Response.StatusCode = 404;
			ctx.Response.Close();
		}
		private void WriteDescription(HttpListenerContext ctx){
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			var x = "<?xml version=\"1.0\"?>" + "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">" + "<specVersion><major>1</major><minor>0</minor></specVersion>" + "<URLBase>" + XmlEscape(BaseUrl) +
					"</URLBase>" + "<device>" + "<deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>" + "<friendlyName>" + XmlEscape(_friendlyName) + "</friendlyName>" +
					"<manufacturer>CLDLNA</manufacturer><modelName>MinimalDLNA</modelName>" + "<UDN>" + _udn + "</UDN>" + "<serviceList><service>" +
					"<serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType>" + "<serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId>" +
					"<controlURL>/ContentDirectory/control</controlURL>" + "<eventSubURL>/noop</eventSubURL><SCPDURL>/noop</SCPDURL>" + "</service></serviceList></device></root>";
			WriteUtf8(ctx.Response, x);
		}
		private void HandleContentDirectoryControl(HttpListenerContext ctx){
			string body;
			using(var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8)){ body = sr.ReadToEnd(); }
			var objectId = ExtractBetween(body, "<ObjectID>", "</ObjectID>") ?? "0";
			var start = ParseUint(ExtractBetween(body, "<StartingIndex>", "</StartingIndex>"));
			var req = ParseUint(ExtractBetween(body, "<RequestedCount>", "</RequestedCount>"));
			if(req == 0) req = uint.MaxValue;
			var didl = BuildDidl(objectId, start, req, out var total, out var count);
			var resp = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
						"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
						"<u:BrowseResponse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\">" + "<Result>" + XmlEscape(didl) + "</Result><NumberReturned>" + count +
						"</NumberReturned><TotalMatches>" + total + "</TotalMatches><UpdateID>1</UpdateID>" + "</u:BrowseResponse></s:Body></s:Envelope>";
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			ctx.Response.AddHeader("EXT", "");
			ctx.Response.AddHeader("SERVER", "Windows/10 UPnP/1.0 CLDLNA/1.0");
			WriteUtf8(ctx.Response, resp);
		}
		private string BuildDidl(string objectId, uint start, uint req, out int total, out int returned){
			var all = new List<Item>();
			if(objectId == "0")
				lock(_gate){
					all.AddRange(_roots.Select((t, i) => new Item{
						Id = "r" + i, ParentId = "0", Title = Path.GetFileName(t), IsContainer = true,
						Path = t
					}));
				}
			else ResolveChildren(objectId, all);
			total = all.Count;
			var s = (int)Math.Min(start, (uint)all.Count);
			var e = (int)Math.Min((uint)all.Count, start + req);
			var sb = new StringBuilder();
			sb.Append("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">");
			for(var i = s; i < e; i++){
				var it = all[i];
				if(it.IsContainer)
					sb.Append("<container id=\"").Append(it.Id).Append("\" parentID=\"").Append(it.ParentId).Append("\" restricted=\"1\"><dc:title>").Append(XmlEscape(it.Title))
						.Append("</dc:title><upnp:class>object.container.storageFolder</upnp:class></container>");
				else
					sb.Append("<item id=\"").Append(it.Id).Append("\" parentID=\"").Append(it.ParentId).Append("\" restricted=\"1\"><dc:title>").Append(XmlEscape(it.Title))
						.Append("</dc:title><res protocolInfo=\"http-get:*:*:*\">").Append(XmlEscape(BaseUrl + "file/" + Uri.EscapeDataString(it.Path)))
						.Append("</res><upnp:class>object.item</upnp:class></item>");
			}
			sb.Append("</DIDL-Lite>");
			returned = Math.Max(0, e - s);
			return sb.ToString();
		}
		private void ResolveChildren(string objectId, List<Item> dst){
			if(string.IsNullOrEmpty(objectId)) return;
			if(!objectId.StartsWith("r")) return;
			var parts = objectId.Substring(1).Split(new[]{ '_' }, StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length == 0 || !int.TryParse(parts[0], out var rootIndex)) return;
			string current;
			lock(_gate){
				if(rootIndex < 0 || rootIndex >= _roots.Count) return;
				current = _roots[rootIndex];
			}
			for(var i = 1; i < parts.Length; i++) current = Path.Combine(current, Uri.UnescapeDataString(parts[i]));
			dst.AddRange(from d in SafeDirs(current) let name = Path.GetFileName(d) select new Item{
				Id = objectId + "_" + Uri.EscapeDataString(name), ParentId = objectId, Title = name, IsContainer = true,
				Path = d
			});
			dst.AddRange(from f in SafeFiles(current) let name = Path.GetFileName(f) select new Item{
				Id = "f_" + Uri.EscapeDataString(f), ParentId = objectId, Title = name, IsContainer = false,
				Path = f
			});
		}
		private void ServeFile(HttpListenerContext ctx, string encodedPath){
			var full = Uri.UnescapeDataString(encodedPath ?? "");
			if(string.IsNullOrWhiteSpace(full) || !File.Exists(full) || !IsUnderAllowedRoot(full)){
				ctx.Response.StatusCode = 404;
				ctx.Response.Close();
				return;
			}
			ctx.Response.ContentType = "application/octet-stream";
			ctx.Response.AddHeader("Accept-Ranges", "bytes");
			using(var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)){
				ctx.Response.ContentLength64 = fs.Length;
				fs.CopyTo(ctx.Response.OutputStream);
			}
			ctx.Response.OutputStream.Close();
		}
		private bool IsUnderAllowedRoot(string file){
			var full = Path.GetFullPath(file);
			lock(_gate){
				if(_roots.Any(t => full.StartsWith(t.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(full, t, StringComparison.OrdinalIgnoreCase))) return true;
			}
			return false;
		}
		private void SsdpLoop(){
			try{
				_ssdpUdp = new UdpClient();
				_ssdpUdp.ExclusiveAddressUse = false;
				_ssdpUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				_ssdpUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
				_ssdpUdp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
				var ep = new IPEndPoint(IPAddress.Any, 0);
				while(_running){
					if(_ssdpUdp.Available == 0){
						Thread.Sleep(50);
						continue;
					}
					var data = _ssdpUdp.Receive(ref ep);
					var req = Encoding.UTF8.GetString(data);
					if(req.IndexOf("M-SEARCH", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if(req.IndexOf("ssdp:discover", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if(req.IndexOf("urn:schemas-upnp-org:device:MediaServer:1", StringComparison.OrdinalIgnoreCase) < 0 && req.IndexOf("ssdp:all", StringComparison.OrdinalIgnoreCase) < 0) continue;
					var resp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nEXT:\r\nST: urn:schemas-upnp-org:device:MediaServer:1\r\nUSN: " + _udn +
								"::urn:schemas-upnp-org:device:MediaServer:1\r\nSERVER: Windows/10 UPnP/1.0 CLDLNA/1.0\r\nLOCATION: " + BaseUrl + "description.xml\r\n\r\n";
					var bytes = Encoding.UTF8.GetBytes(resp);
					_ssdpUdp.Send(bytes, bytes.Length, ep);
				}
			} catch{}
		}
		private static void WriteUtf8(HttpListenerResponse resp, string text){
			var bytes = Encoding.UTF8.GetBytes(text ?? "");
			resp.ContentLength64 = bytes.Length;
			resp.OutputStream.Write(bytes, 0, bytes.Length);
			resp.OutputStream.Close();
		}
		private static string ExtractBetween(string s, string a, string b){
			if(string.IsNullOrEmpty(s)) return null;
			var i = s.IndexOf(a, StringComparison.OrdinalIgnoreCase);
			if(i < 0) return null;
			i += a.Length;
			var j = s.IndexOf(b, i, StringComparison.OrdinalIgnoreCase);
			if(j < 0) return null;
			return s.Substring(i, j - i);
		}
		private static uint ParseUint(string s){
			return uint.TryParse((s ?? "").Trim(), out var v) ? v : 0;
		}
		private static string XmlEscape(string s){return SecurityElement.Escape(s ?? "") ?? "";}
		private static string GetLocalIp(){
			try{
				foreach(var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))) return ip.ToString();
			} catch{}
			return "127.0.0.1";
		}
		private static IEnumerable<string> SafeDirs(string p){
			try{ return Directory.GetDirectories(p); } catch{ return new string[0]; }
		}
		private static IEnumerable<string> SafeFiles(string p){
			try{ return Directory.GetFiles(p); } catch{ return new string[0]; }
		}
		private sealed class Item{
			internal string Id;
			internal bool IsContainer;
			internal string ParentId;
			internal string Path;
			internal string Title;
		}
	}
}