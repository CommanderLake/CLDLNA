using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
namespace CLDLNA{
	internal sealed class DlnaServer : IDisposable{
		private const string DeviceType = "urn:schemas-upnp-org:device:MediaServer:1";
		private const string ContentDirType = "urn:schemas-upnp-org:service:ContentDirectory:1";
		private const string ConnectionMgrType = "urn:schemas-upnp-org:service:ConnectionManager:1";
		private readonly string _friendlyName;
		private readonly object _gate = new object();
		private readonly HttpListener _http = new HttpListener();
		private readonly Semaphore _httpSlots = new Semaphore(64, 64);
		private readonly List<IPAddress> _interfaceIps = new List<IPAddress>();
		private readonly int _port;
		private readonly List<string> _roots = new List<string>();
		private readonly string _udn = "uuid:" + Guid.NewGuid();
		private Thread _httpThread;
		private volatile bool _running;
		private Thread _ssdpNotifyThread;
		private Thread _ssdpThread;
		private UdpClient _ssdpUdp;
		internal DlnaServer(string friendlyName, int port){
			_friendlyName = string.IsNullOrWhiteSpace(friendlyName) ? "CLDLNA" : friendlyName.Trim();
			_port = port <= 0 ? 8200 : port;
			_interfaceIps.AddRange(GetAdvertiseIps());
			if(_interfaceIps.Count == 0) _interfaceIps.Add(IPAddress.Loopback);
		}
		internal string BaseUrl => "http://" + GetBestBaseIp() + ":" + _port + "/";
		public void Dispose(){
			Stop();
			try{ _http.Close(); } catch{}
		}
		internal void SetFolders(IEnumerable<string> folders){
			lock(_gate){
				_roots.Clear();
				if(folders == null) return;
				foreach(var full in from f in folders where !string.IsNullOrWhiteSpace(f) select Path.GetFullPath(f) into full
						where Directory.Exists(full) && !_roots.Exists(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)) select full) _roots.Add(full);
			}
		}
		internal void Start(){
			if(_running) return;
			_http.Prefixes.Clear();
			_http.Prefixes.Add("http://+:" + _port + "/");
			_http.Prefixes.Add("http://127.0.0.1:" + _port + "/");
			_http.Start();
			_running = true;
			_httpThread = new Thread(HttpLoop){ IsBackground = true, Name = "DLNA-HTTP" };
			_httpThread.Start();
			_ssdpThread = new Thread(SsdpLoop){ IsBackground = true, Name = "DLNA-SSDP" };
			_ssdpThread.Start();
			_ssdpNotifyThread = new Thread(SsdpNotifyLoop){ IsBackground = true, Name = "DLNA-SSDP-NOTIFY" };
			_ssdpNotifyThread.Start();
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
			try{
				if(_ssdpNotifyThread != null && _ssdpNotifyThread.IsAlive) _ssdpNotifyThread.Join(500);
			} catch{}
		}
		private void HttpLoop(){
			while(_running){
				HttpListenerContext ctx;
				try{ ctx = _http.GetContext(); } catch{
					if(!_running) break;
					continue;
				}
				if(!_httpSlots.WaitOne(2000)){
					try{
						ctx.Response.StatusCode = 503;
						ctx.Response.Close();
					} catch{}
					continue;
				}
				ThreadPool.QueueUserWorkItem(o => {
					var ctxx = (HttpListenerContext)o;
					try{ HandleHttp(ctxx); } catch{
						try{
							ctxx.Response.StatusCode = 500;
							ctxx.Response.Close();
						} catch(Exception e){ MessageBox.Show(e.ToString()); }
					} finally{
						try{ _httpSlots.Release(); } catch{}
					}
				}, ctx);
			}
		}
		private void HandleHttp(HttpListenerContext ctx){
			var path = ctx.Request.Url.AbsolutePath;
			if(path.Equals("/description.xml", StringComparison.OrdinalIgnoreCase)){
				WriteDescription(ctx);
				return;
			}
			if(path.Equals("/ContentDirectory/scpd.xml", StringComparison.OrdinalIgnoreCase)){
				WriteContentDirectoryScpd(ctx);
				return;
			}
			if(path.Equals("/ContentDirectory/control", StringComparison.OrdinalIgnoreCase)){
				HandleContentDirectoryControl(ctx);
				return;
			}
			if(path.Equals("/ConnectionManager/scpd.xml", StringComparison.OrdinalIgnoreCase)){
				WriteConnectionManagerScpd(ctx);
				return;
			}
			if(path.Equals("/ConnectionManager/control", StringComparison.OrdinalIgnoreCase)){
				HandleConnectionManagerControl(ctx);
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
			var localIp = GetRequestLocalIp(ctx);
			var baseUrl = GetBaseUrl(localIp ?? GetBestBaseIp());
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			var x = "<?xml version=\"1.0\"?>" + "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">" + "<specVersion><major>1</major><minor>0</minor></specVersion>" + "<URLBase>" + XmlEscape(baseUrl) +
					"</URLBase>" + "<device>" + "<deviceType>" + DeviceType + "</deviceType>" + "<friendlyName>" + XmlEscape(_friendlyName) + "</friendlyName>" +
					"<manufacturer>CLDLNA</manufacturer><manufacturerURL>https://local</manufacturerURL><modelDescription>Minimal DLNA Server</modelDescription><modelName>MinimalDLNA</modelName><modelNumber>1</modelNumber><serialNumber>1</serialNumber><presentationURL>/</presentationURL>" +
					"<UDN>" + _udn + "</UDN>" + "<serviceList>" + "<service><serviceType>" + ContentDirType +
					"</serviceType><serviceId>urn:upnp-org:serviceId:ContentDirectory</serviceId><controlURL>/ContentDirectory/control</controlURL><eventSubURL>/ContentDirectory/event</eventSubURL><SCPDURL>/ContentDirectory/scpd.xml</SCPDURL></service>" +
					"<service><serviceType>" + ConnectionMgrType +
					"</serviceType><serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId><controlURL>/ConnectionManager/control</controlURL><eventSubURL>/ConnectionManager/event</eventSubURL><SCPDURL>/ConnectionManager/scpd.xml</SCPDURL></service>" +
					"</serviceList></device></root>";
			WriteUtf8(ctx.Response, x);
		}
		private void WriteConnectionManagerScpd(HttpListenerContext ctx){
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			var x = "<?xml version=\"1.0\"?>" + "<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\"><specVersion><major>1</major><minor>0</minor></specVersion><actionList>" +
					"<action><name>GetProtocolInfo</name><argumentList><argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument><argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument></argumentList></action>" +
					"<action><name>GetCurrentConnectionIDs</name><argumentList><argument><name>ConnectionIDs</name><direction>out</direction><relatedStateVariable>CurrentConnectionIDs</relatedStateVariable></argument></argumentList></action>" +
					"<action><name>GetCurrentConnectionInfo</name><argumentList><argument><name>ConnectionID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>RcsID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_RcsID</relatedStateVariable></argument><argument><name>AVTransportID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_AVTransportID</relatedStateVariable></argument><argument><name>ProtocolInfo</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ProtocolInfo</relatedStateVariable></argument><argument><name>PeerConnectionManager</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionManager</relatedStateVariable></argument><argument><name>PeerConnectionID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument><argument><name>Direction</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Direction</relatedStateVariable></argument><argument><name>Status</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionStatus</relatedStateVariable></argument></argumentList></action>" +
					"</actionList><serviceStateTable>" +
					"<stateVariable sendEvents=\"no\"><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_ConnectionID</name><dataType>i4</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_RcsID</name><dataType>i4</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_AVTransportID</name><dataType>i4</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_ProtocolInfo</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_ConnectionManager</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_Direction</name><dataType>string</dataType></stateVariable><stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_ConnectionStatus</name><dataType>string</dataType></stateVariable>" +
					"</serviceStateTable></scpd>";
			WriteUtf8(ctx.Response, x);
		}
		private void HandleConnectionManagerControl(HttpListenerContext ctx){
			string body;
			using(var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8)){ body = sr.ReadToEnd(); }
			var soapAction = (ctx.Request.Headers["SOAPACTION"] ?? "").Trim().Trim('"');
			var action = "";
			var hash = soapAction.LastIndexOf('#');
			if(hash >= 0 && hash + 1 < soapAction.Length) action = soapAction.Substring(hash + 1);
			if(string.IsNullOrEmpty(action)){
				if(body.IndexOf(":GetProtocolInfo", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetProtocolInfo";
				else if(body.IndexOf(":GetCurrentConnectionIDs", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetCurrentConnectionIDs";
				else if(body.IndexOf(":GetCurrentConnectionInfo", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetCurrentConnectionInfo";
			}
			string respBody;
			switch(action){
				case "GetProtocolInfo":
					respBody = "<u:GetProtocolInfoResponse xmlns:u=\"" + ConnectionMgrType +
								"\"><Source>http-get:*:audio/mpeg:*,http-get:*:audio/flac:*,http-get:*:audio/mp4:*,http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:image/jpeg:*,http-get:*:image/png:*</Source><Sink></Sink></u:GetProtocolInfoResponse>";
					break;
				case "GetCurrentConnectionIDs":
					respBody = "<u:GetCurrentConnectionIDsResponse xmlns:u=\"" + ConnectionMgrType + "\"><ConnectionIDs>0</ConnectionIDs></u:GetCurrentConnectionIDsResponse>";
					break;
				case "GetCurrentConnectionInfo":
					respBody = "<u:GetCurrentConnectionInfoResponse xmlns:u=\"" + ConnectionMgrType +
								"\"><RcsID>-1</RcsID><AVTransportID>-1</AVTransportID><ProtocolInfo></ProtocolInfo><PeerConnectionManager></PeerConnectionManager><PeerConnectionID>-1</PeerConnectionID><Direction>Output</Direction><Status>OK</Status></u:GetCurrentConnectionInfoResponse>";
					break;
				default:
					ctx.Response.StatusCode = 500;
					ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
					WriteUtf8(ctx.Response,
						"<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>401</errorCode><errorDescription>Invalid Action</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>");
					return;
			}
			var resp =
				"<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
				respBody + "</s:Body></s:Envelope>";
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			ctx.Response.AddHeader("ContentFeatures.DLNA.ORG", "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000");
			ctx.Response.AddHeader("EXT", "");
			ctx.Response.AddHeader("SERVER", "Windows/10 UPnP/1.0 CLDLNA/1.0");
			WriteUtf8(ctx.Response, resp);
		}
		private void WriteContentDirectoryScpd(HttpListenerContext ctx){
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			var x = "<?xml version=\"1.0\"?>" + "<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">" + "<specVersion><major>1</major><minor>0</minor></specVersion>" + "<actionList>" +
					"<action><name>Browse</name><argumentList>" +
					"<argument><name>ObjectID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ObjectID</relatedStateVariable></argument>" +
					"<argument><name>BrowseFlag</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_BrowseFlag</relatedStateVariable></argument>" +
					"<argument><name>Filter</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Filter</relatedStateVariable></argument>" +
					"<argument><name>StartingIndex</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Index</relatedStateVariable></argument>" +
					"<argument><name>RequestedCount</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>" +
					"<argument><name>SortCriteria</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_SortCriteria</relatedStateVariable></argument>" +
					"<argument><name>Result</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Result</relatedStateVariable></argument>" +
					"<argument><name>NumberReturned</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>" +
					"<argument><name>TotalMatches</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Count</relatedStateVariable></argument>" +
					"<argument><name>UpdateID</name><direction>out</direction><relatedStateVariable>SystemUpdateID</relatedStateVariable></argument>" + "</argumentList></action>" +
					"<action><name>GetSearchCapabilities</name><argumentList>" +
					"<argument><name>SearchCaps</name><direction>out</direction><relatedStateVariable>SearchCapabilities</relatedStateVariable></argument>" + "</argumentList></action>" +
					"<action><name>GetSortCapabilities</name><argumentList>" +
					"<argument><name>SortCaps</name><direction>out</direction><relatedStateVariable>SortCapabilities</relatedStateVariable></argument>" + "</argumentList></action>" +
					"<action><name>GetSystemUpdateID</name><argumentList>" +
					"<argument><name>Id</name><direction>out</direction><relatedStateVariable>SystemUpdateID</relatedStateVariable></argument>" + "</argumentList></action>" + "</actionList>" +
					"<serviceStateTable>" + "<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_ObjectID</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_BrowseFlag</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_Filter</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_Index</name><dataType>ui4</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_Count</name><dataType>ui4</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_SortCriteria</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>A_ARG_TYPE_Result</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>SearchCapabilities</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>SortCapabilities</name><dataType>string</dataType></stateVariable>" +
					"<stateVariable sendEvents=\"no\"><name>SystemUpdateID</name><dataType>ui4</dataType></stateVariable>" + "</serviceStateTable>" + "</scpd>";
			WriteUtf8(ctx.Response, x);
		}
		private void HandleContentDirectoryControl(HttpListenerContext ctx){
			string body;
			using(var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8)){ body = sr.ReadToEnd(); }
			var soapAction = (ctx.Request.Headers["SOAPACTION"] ?? "").Trim().Trim('"');
			var action = "";
			var hash = soapAction.LastIndexOf('#');
			if(hash >= 0 && hash + 1 < soapAction.Length) action = soapAction.Substring(hash + 1);
			if(string.IsNullOrEmpty(action)){
				if(body.IndexOf(":Browse", StringComparison.OrdinalIgnoreCase) >= 0) action = "Browse";
				else if(body.IndexOf(":GetSearchCapabilities", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetSearchCapabilities";
				else if(body.IndexOf(":GetSortCapabilities", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetSortCapabilities";
				else if(body.IndexOf(":GetSystemUpdateID", StringComparison.OrdinalIgnoreCase) >= 0) action = "GetSystemUpdateID";
			}
			string respBody;
			switch(action){
				case "Browse":{
					var objectId = ExtractSoapValue(body, "ObjectID") ?? "0";
					var browseFlag = ExtractSoapValue(body, "BrowseFlag") ?? "BrowseDirectChildren";
					var start = ParseUint(ExtractSoapValue(body, "StartingIndex"));
					var req = ParseUint(ExtractSoapValue(body, "RequestedCount"));
					if(req == 0) req = uint.MaxValue;
					var localIp = GetRequestLocalIp(ctx) ?? GetBestBaseIp();
					var didl = BuildDidl(objectId, browseFlag, start, req, GetBaseUrl(localIp), out var total, out var count);
					respBody = "<u:BrowseResponse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\">" + "<Result>" + XmlEscape(didl) + "</Result>" + "<NumberReturned>" + count +
								"</NumberReturned>" + "<TotalMatches>" + total + "</TotalMatches>" + "<UpdateID>1</UpdateID>" + "</u:BrowseResponse>";
					break;
				}
				case "GetSearchCapabilities":
					respBody = "<u:GetSearchCapabilitiesResponse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\"><SearchCaps></SearchCaps></u:GetSearchCapabilitiesResponse>";
					break;
				case "GetSortCapabilities":
					respBody = "<u:GetSortCapabilitiesResponse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\"><SortCaps></SortCaps></u:GetSortCapabilitiesResponse>";
					break;
				case "GetSystemUpdateID":
					respBody = "<u:GetSystemUpdateIDResponse xmlns:u=\"urn:schemas-upnp-org:service:ContentDirectory:1\"><Id>1</Id></u:GetSystemUpdateIDResponse>";
					break;
				default:
					ctx.Response.StatusCode = 500;
					ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
					WriteUtf8(ctx.Response,
						"<?xml version=\"1.0\" encoding=\"utf-8\"?>" + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
						"<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring>" +
						"<detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>401</errorCode><errorDescription>Invalid Action</errorDescription></UPnPError></detail>" +
						"</s:Fault></s:Body></s:Envelope>");
					return;
			}
			var resp = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
						"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" + "<s:Body>" + respBody +
						"</s:Body></s:Envelope>";
			ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
			ctx.Response.AddHeader("EXT", "");
			ctx.Response.AddHeader("SERVER", "Windows/10 UPnP/1.0 CLDLNA/1.0");
			WriteUtf8(ctx.Response, resp);
		}
		private string BuildDidl(string objectId, string browseFlag, uint start, uint req, string baseUrl, out int total, out int returned){
			var all = new List<Item>();
			var metadata = string.Equals(browseFlag, "BrowseMetadata", StringComparison.OrdinalIgnoreCase);
			if(metadata){
				if(objectId == "0") all.Add(new Item{ Id = "0", ParentId = "-1", Title = "Root", IsContainer = true, Path = null });
				else ResolveMetadata(objectId, all);
			}
			else{
				if(objectId == "0")
					lock(_gate){
						all.AddRange(_roots.Select((t, i) => new Item{
							Id = "r" + i, ParentId = "0", Title = GetRootTitle(t), IsContainer = true,
							Path = t
						}));
					}
				else ResolveChildren(objectId, all);
			}
			all.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
			total = all.Count;
			var s = (int)Math.Min(start, (uint)all.Count);
			var endReq = req > uint.MaxValue - start ? uint.MaxValue : start + req;
			var e = (int)Math.Min((uint)all.Count, endReq);
			var sb = new StringBuilder();
			sb.Append("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">");
			for(var i = s; i < e; i++){
				var it = all[i];
				if(it.IsContainer){
					var childCount = it.Path == null ? _roots.Count : CountChildren(it.Path);
					if(childCount < 0) childCount = 0;
					sb.Append("<container id=\"").Append(it.Id).Append("\" parentID=\"").Append(it.ParentId).Append("\" restricted=\"1\" searchable=\"1\" childCount=\"").Append(childCount)
						.Append("\"><dc:title>").Append(XmlEscape(it.Title)).Append("</dc:title><upnp:class>object.container.storageFolder</upnp:class></container>");
				}
				else{
					var cls = GetUpnpClass(it.Path);
					long size = 0;
					try{ size = new FileInfo(it.Path).Length; } catch{}
					var pi = GetProtocolInfo(it.Path);
					var cf = GetDlnaContentFeatures(it.Path);
					sb.Append("<item id=\"").Append(it.Id).Append("\" parentID=\"").Append(it.ParentId).Append("\" restricted=\"1\"><dc:title>").Append(XmlEscape(it.Title))
						.Append("</dc:title><upnp:class>").Append(cls).Append("</upnp:class><res protocolInfo=\"").Append(XmlEscape(pi)).Append("\" size=\"").Append(size)
						.Append("\" xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\" dlna:profileID=\"").Append(XmlEscape(GetDlnaProfileId(it.Path))).Append("\" dlna:contentFeatures=\"").Append(XmlEscape(cf)).Append("\">")
						.Append(XmlEscape(baseUrl + "file/" + Uri.EscapeDataString(it.Path))).Append("</res></item>");
				}
			}
			sb.Append("</DIDL-Lite>");
			returned = Math.Max(0, e - s);
			return sb.ToString();
		}
		private void ResolveChildren(string objectId, List<Item> dst){
			if(string.IsNullOrEmpty(objectId)) return;
			if(!objectId.StartsWith("r")) return;
			var parts = objectId.Substring(1).Split(new[]{ '/' }, StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length == 0 || !int.TryParse(parts[0], out var rootIndex)) return;
			string current;
			lock(_gate){
				if(rootIndex < 0 || rootIndex >= _roots.Count) return;
				current = _roots[rootIndex];
			}
			for(var i = 1; i < parts.Length; i++){
				if(string.Equals(parts[i], "d", StringComparison.OrdinalIgnoreCase)) continue;
				current = Path.Combine(current, Uri.UnescapeDataString(parts[i]));
			}
			dst.AddRange(from d in SafeDirs(current).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase) let name = Path.GetFileName(d) select new Item{
				Id = objectId + "/d/" + Uri.EscapeDataString(name), ParentId = objectId, Title = name, IsContainer = true,
				Path = d
			});
			dst.AddRange(from f in SafeFiles(current).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase) let name = Path.GetFileName(f) select new Item{
				Id = "f_" + Uri.EscapeDataString(f), ParentId = objectId, Title = name, IsContainer = false,
				Path = f
			});
		}
		private void ResolveMetadata(string objectId, List<Item> dst){
			if(string.IsNullOrEmpty(objectId)) return;
			if(objectId == "0") return;
			if(objectId.StartsWith("f_")){
				var p = Uri.UnescapeDataString(objectId.Substring(2));
				if(!File.Exists(p) || !IsUnderAllowedRoot(p)) return;
				dst.Add(new Item{ Id = objectId, ParentId = GetParentIdForPath(p), Title = Path.GetFileName(p), IsContainer = false, Path = p });
				return;
			}
			if(!objectId.StartsWith("r")) return;
			var parts = objectId.Substring(1).Split(new[]{ '/' }, StringSplitOptions.RemoveEmptyEntries);
			if(parts.Length == 0 || !int.TryParse(parts[0], out var rootIndex)) return;
			string current;
			lock(_gate){
				if(rootIndex < 0 || rootIndex >= _roots.Count) return;
				current = _roots[rootIndex];
			}
			for(var i = 1; i < parts.Length; i++){
				if(parts[i] == "d") continue;
				current = Path.Combine(current, Uri.UnescapeDataString(parts[i]));
			}
			if(!Directory.Exists(current) || !IsUnderAllowedRoot(current)) return;
			var pid = objectId == ("r" + rootIndex) ? "0" : objectId.Substring(0, objectId.LastIndexOf("/d/", StringComparison.Ordinal));
			dst.Add(new Item{ Id = objectId, ParentId = pid, Title = Path.GetFileName(current), IsContainer = true, Path = current });
		}
		private string GetParentIdForPath(string p){
			lock(_gate){
				for(var i = 0; i < _roots.Count; i++){
					var r = _roots[i];
					if(!p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
					var rel = p.Substring(r.TrimEnd('\\').Length + 1).Split('\\');
					if(rel.Length <= 1) return "r" + i;
					var id = "r" + i;
					for(var j = 0; j < rel.Length - 1; j++) id += "/d/" + Uri.EscapeDataString(rel[j]);
					return id;
				}
			}
			return "0";
		}
		private void ServeFile(HttpListenerContext ctx, string encodedPath){
			var full = Uri.UnescapeDataString((encodedPath ?? "").Replace("+", "%20"));
			if(string.IsNullOrWhiteSpace(full) || !File.Exists(full) || !IsUnderAllowedRoot(full)){
				ctx.Response.StatusCode = 404;
				ctx.Response.Close();
				return;
			}
			long len;
			try{ len = new FileInfo(full).Length; } catch{
				ctx.Response.StatusCode = 500;
				ctx.Response.Close();
				return;
			}
			ctx.Response.ContentType = GetMimeType(full);
			ctx.Response.AddHeader("Accept-Ranges", "bytes");
			ctx.Response.AddHeader("transferMode.dlna.org", "Streaming");
			ctx.Response.AddHeader("contentFeatures.dlna.org", GetDlnaContentFeatures(full));
			var isHead = string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
			var range = ctx.Request.Headers["Range"];
			long start = 0, end = len - 1;
			var hasRange = !string.IsNullOrEmpty(range);
			var rangeOk = !hasRange || TryParseRange(range, len, out start, out end);
			if(hasRange && !rangeOk) hasRange = false;
			if(hasRange){
				ctx.Response.StatusCode = 206;
				ctx.Response.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + len);
				ctx.Response.ContentLength64 = end - start + 1;
			}
			else{
				ctx.Response.StatusCode = 200;
				ctx.Response.ContentLength64 = len;
			}
			if(isHead){
				ctx.Response.Close();
				return;
			}
			try{
				using(var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 524288, FileOptions.SequentialScan)){
					if(hasRange) fs.Position = start;
					CopyBytes(fs, ctx.Response.OutputStream, ctx.Response.ContentLength64);
				}
				try{ ctx.Response.OutputStream.Close(); } catch{}
			} catch{
				try{ ctx.Response.Abort(); } catch{}
			}
		}
		private static bool TryParseRange(string header, long length, out long start, out long end){
			start = 0;
			end = length - 1;
			if(length <= 0) return false;
			var m = Regex.Match(header ?? "", "^\\s*bytes\\s*=\\s*(\\d*)\\s*-\\s*(\\d*)\\s*$", RegexOptions.IgnoreCase);
			if(!m.Success) return false;
			var a = m.Groups[1].Value;
			var b = m.Groups[2].Value;
			if(a.Length == 0 && b.Length == 0) return false;
			if(a.Length == 0){
				if(!long.TryParse(b, out var suffix) || suffix <= 0) return false;
				if(suffix >= length){
					start = 0;
					end = length - 1;
					return true;
				}
				start = length - suffix;
				end = length - 1;
				return true;
			}
			if(!long.TryParse(a, out start) || start < 0 || start >= length) return false;
			if(b.Length == 0){
				end = length - 1;
				return true;
			}
			if(!long.TryParse(b, out end) || end < start) return false;
			if(end >= length) end = length - 1;
			return true;
		}
		private static void CopyBytes(Stream src, Stream dst, long count){
			var buf = new byte[524288];
			if(dst is NetworkStream ns)
				try{ ns.WriteTimeout = 15000; } catch{}
			while(count > 0){
				var want = count > buf.Length ? buf.Length : (int)count;
				var n = src.Read(buf, 0, want);
				if(n <= 0) break;
				try{ dst.Write(buf, 0, n); } catch(IOException){ break; } catch(ObjectDisposedException){ break; }
				count -= n;
			}
		}
		private bool IsUnderAllowedRoot(string file){
			var full = Path.GetFullPath(file);
			lock(_gate){
				if(_roots.Any(t => full.StartsWith(NormalizeRoot(t), StringComparison.OrdinalIgnoreCase) || string.Equals(full, t, StringComparison.OrdinalIgnoreCase))) return true;
			}
			return false;
		}
		private static string NormalizeRoot(string root){
			if(string.IsNullOrEmpty(root)) return "";
			var full = Path.GetFullPath(root).TrimEnd('\\', '/');
			if(full.Length == 2 && full[1] == ':') full += "\\";
			if(!full.EndsWith("\\", StringComparison.Ordinal)) full += "\\";
			return full;
		}
		private void SsdpLoop(){
			try{
				_ssdpUdp = new UdpClient();
				_ssdpUdp.ExclusiveAddressUse = false;
				_ssdpUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
				_ssdpUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 1900));
				_ssdpUdp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
				_ssdpUdp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
				var ep = new IPEndPoint(IPAddress.Any, 0);
				var errDelay = 50;
				while(_running){
					byte[] data;
					try{
						data = _ssdpUdp.Receive(ref ep);
						errDelay = 50;
					} catch(SocketException ex){
						if(!_running) break;
						if(!IsExpectedSsdpSocketError(ex)) throw;
						Thread.Sleep(errDelay);
						if(errDelay < 200) errDelay += 25;
						continue;
					} catch{
						if(!_running) break;
						Thread.Sleep(errDelay);
						if(errDelay < 200) errDelay += 25;
						continue;
					}
					var req = Encoding.ASCII.GetString(data);
					if(req.IndexOf("M-SEARCH", StringComparison.OrdinalIgnoreCase) < 0) continue;
					if(req.IndexOf("ssdp:discover", StringComparison.OrdinalIgnoreCase) < 0) continue;
					var st = GetSearchTarget(req);
					if(st == null) continue;
					SendSearchResponse(ep, st);
				}
			} catch{}
		}
		private void SsdpNotifyLoop(){
			while(_running){
				try{ SendNotifyAlive(); } catch{}
				Thread.Sleep(3000);
			}
			try{ SendNotifyByebye(); } catch{}
		}
		private string GetSearchTarget(string req){
			if(req.IndexOf("ssdp:all", StringComparison.OrdinalIgnoreCase) >= 0) return "ssdp:all";
			if(req.IndexOf(DeviceType, StringComparison.OrdinalIgnoreCase) >= 0) return DeviceType;
			if(req.IndexOf(ContentDirType, StringComparison.OrdinalIgnoreCase) >= 0) return ContentDirType;
			if(req.IndexOf(ConnectionMgrType, StringComparison.OrdinalIgnoreCase) >= 0) return ConnectionMgrType;
			if(req.IndexOf("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) >= 0) return "upnp:rootdevice";
			if(req.IndexOf(_udn, StringComparison.OrdinalIgnoreCase) >= 0) return _udn;
			return null;
		}
		private void SendSearchResponse(IPEndPoint ep, string st){
			var outSt = st == "ssdp:all" ? "upnp:rootdevice" : st;
			foreach(var ip in _interfaceIps){
				var usn = outSt == "upnp:rootdevice" ? _udn + "::upnp:rootdevice" :
					outSt == DeviceType ? _udn + "::" + DeviceType :
					outSt == ContentDirType ? _udn + "::" + ContentDirType :
					outSt == ConnectionMgrType ? _udn + "::" + ConnectionMgrType : _udn;
				var resp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nDATE: " + DateTime.UtcNow.ToString("R") + "\r\nEXT:\r\nLOCATION: " + GetBaseUrl(ip) +
							"description.xml\r\nSERVER: Windows/10 UPnP/1.0 CLDLNA/1.0\r\nST: " + outSt + "\r\nUSN: " + usn + "\r\n\r\n";
				var bytes = Encoding.ASCII.GetBytes(resp);
				try{
					using(var udp = new UdpClient(new IPEndPoint(ip, 0))){
						udp.Client.SendTimeout = 500;
						udp.Send(bytes, bytes.Length, ep);
					}
				} catch(SocketException ex){
					if(!IsExpectedSsdpSocketError(ex)) throw;
				}
			}
		}
		private void SendNotifyAlive(){
			foreach(var ip in _interfaceIps)
				using(var udp = CreateNotifyUdp(ip)){
					var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
					SendNotifyPacket(udp, ep, ip, "upnp:rootdevice", _udn + "::upnp:rootdevice", "ssdp:alive");
					SendNotifyPacket(udp, ep, ip, _udn, _udn, "ssdp:alive");
					SendNotifyPacket(udp, ep, ip, DeviceType, _udn + "::" + DeviceType, "ssdp:alive");
					SendNotifyPacket(udp, ep, ip, ContentDirType, _udn + "::" + ContentDirType, "ssdp:alive");
					SendNotifyPacket(udp, ep, ip, ConnectionMgrType, _udn + "::" + ConnectionMgrType, "ssdp:alive");
				}
		}
		private void SendNotifyByebye(){
			foreach(var ip in _interfaceIps)
				using(var udp = CreateNotifyUdp(ip)){
					var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
					SendNotifyPacket(udp, ep, ip, "upnp:rootdevice", _udn + "::upnp:rootdevice", "ssdp:byebye");
					SendNotifyPacket(udp, ep, ip, _udn, _udn, "ssdp:byebye");
					SendNotifyPacket(udp, ep, ip, DeviceType, _udn + "::" + DeviceType, "ssdp:byebye");
					SendNotifyPacket(udp, ep, ip, ContentDirType, _udn + "::" + ContentDirType, "ssdp:byebye");
					SendNotifyPacket(udp, ep, ip, ConnectionMgrType, _udn + "::" + ConnectionMgrType, "ssdp:byebye");
				}
		}
		private void SendNotifyPacket(UdpClient udp, IPEndPoint ep, IPAddress ip, string nt, string usn, string nts){
			var msg = "NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: " + GetBaseUrl(ip) + "description.xml\r\nNT: " + nt + "\r\nNTS: " + nts +
					"\r\nSERVER: Windows/10 UPnP/1.0 CLDLNA/1.0\r\nUSN: " + usn + "\r\n\r\n";
			var bytes = Encoding.ASCII.GetBytes(msg);
			try{ udp.Send(bytes, bytes.Length, ep); } catch(SocketException ex){
				if(!IsExpectedSsdpSocketError(ex)) throw;
			}
		}
		private static bool IsExpectedSsdpSocketError(SocketException ex){
			if(ex == null) return false;
			switch(ex.SocketErrorCode){
				case SocketError.TimedOut:
				case SocketError.Interrupted:
				case SocketError.WouldBlock:
				case SocketError.ConnectionReset:
				case SocketError.NotSocket:
				case SocketError.OperationAborted:
				case SocketError.NetworkDown:
				case SocketError.NetworkUnreachable:
				case SocketError.HostUnreachable:
				case SocketError.AddressNotAvailable: return true;
				default: return false;
			}
		}
		private static void WriteUtf8(HttpListenerResponse resp, string text){
			var bytes = Encoding.UTF8.GetBytes(text ?? "");
			resp.ContentLength64 = bytes.Length;
			resp.OutputStream.Write(bytes, 0, bytes.Length);
			resp.OutputStream.Close();
		}
		private static string ExtractSoapValue(string xml, string localName){
			if(string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(localName)) return null;
			var pat = "<\\s*(?:[A-Za-z_][\\w.-]*:)?" + Regex.Escape(localName) + "\\b[^>]*>(.*?)</\\s*(?:[A-Za-z_][\\w.-]*:)?" + Regex.Escape(localName) + "\\s*>";
			var m = Regex.Match(xml, pat, RegexOptions.IgnoreCase | RegexOptions.Singleline);
			if(!m.Success) return null;
			return m.Groups[1].Value.Trim();
		}
		private static uint ParseUint(string s){return uint.TryParse((s ?? "").Trim(), out var v) ? v : 0;}
		private static string XmlEscape(string s){return SecurityElement.Escape(s ?? "") ?? "";}
		private IPAddress GetBestBaseIp(){
			foreach(var ip in _interfaceIps)
				if(!IPAddress.IsLoopback(ip))
					return ip;
			return _interfaceIps.Count > 0 ? _interfaceIps[0] : IPAddress.Loopback;
		}
		private static IPAddress GetRequestLocalIp(HttpListenerContext ctx){
			try{ return ctx?.Request?.LocalEndPoint?.Address; } catch{}
			return null;
		}
		private string GetBaseUrl(IPAddress ip){return "http://" + ip + ":" + _port + "/";}
		private static UdpClient CreateNotifyUdp(IPAddress ip){
			var udp = new UdpClient(new IPEndPoint(ip, 0));
			udp.MulticastLoopback = true;
			return udp;
		}
		private static IEnumerable<IPAddress> GetAdvertiseIps(){
			try{ return Dns.GetHostAddresses(Dns.GetHostName()).Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).Distinct().ToArray(); } catch{}
			return new IPAddress[0];
		}
		private static IEnumerable<string> SafeDirs(string p){
			try{ return Directory.GetDirectories(p); } catch{ return new string[0]; }
		}
		private static IEnumerable<string> SafeFiles(string p){
			try{ return Directory.GetFiles(p); } catch{ return new string[0]; }
		}
		private static int CountChildren(string p){
			try{ return Directory.GetDirectories(p).Length + Directory.GetFiles(p).Length; } catch{ return 0; }
		}
		private static string GetRootTitle(string path){
			if(string.IsNullOrWhiteSpace(path)) return "Root";
			try{
				var full = Path.GetFullPath(path).TrimEnd('\\', '/');
				var name = Path.GetFileName(full);
				if(!string.IsNullOrEmpty(name)) return name;
				if(full.Length >= 2 && full[1] == ':') return full.Substring(0, 2);
				return full;
			} catch{ return path; }
		}
		private static string GetMimeType(string path){
			var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
			switch(ext){
				case ".mp3": return "audio/mpeg";
				case ".flac": return "audio/flac";
				case ".wav": return "audio/wav";
				case ".m4a": return "audio/mp4";
				case ".aac": return "audio/aac";
				case ".ogg": return "audio/ogg";
				case ".wma": return "audio/x-ms-wma";
				case ".mp4": return "video/mp4";
				case ".m4v": return "video/mp4";
				case ".mkv": return "video/x-matroska";
				case ".avi": return "video/x-msvideo";
				case ".mov": return "video/quicktime";
				case ".wmv": return "video/x-ms-wmv";
				case ".ts":
				case ".m2ts": return "video/mp2t";
				case ".jpg":
				case ".jpeg": return "image/jpeg";
				case ".png": return "image/png";
				case ".gif": return "image/gif";
				case ".bmp": return "image/bmp";
				default: return "application/octet-stream";
			}
		}
		private static string GetUpnpClass(string path){
			var m = GetMimeType(path);
			if(m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "object.item.audioItem.musicTrack";
			if(m.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "object.item.videoItem";
			if(m.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "object.item.imageItem.photo";
			return "object.item";
		}
		private static string GetProtocolInfo(string path){
			var mime = GetMimeType(path);
			return "http-get:*:" + mime + ":" + GetDlnaContentFeatures(path);
		}
		private static string GetDlnaContentFeatures(string path){
			var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
			string pn;
			switch(ext){
				case ".mp3":
					pn = "MP3";
					break;
				case ".jpg":
				case ".jpeg":
					pn = "JPEG_LRG";
					break;
				case ".png":
					pn = "PNG_LRG";
					break;
				case ".mp4":
				case ".m4v":
					pn = "AVC_MP4_BL_CIF15_AAC_520";
					break;
				default:
					pn = "";
					break;
			}
			var flags = "01700000000000000000000000000000";
			if(!string.IsNullOrEmpty(pn)) return "DLNA.ORG_PN=" + pn + ";DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=" + flags;
			return "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=" + flags;
		}
		private static string GetDlnaProfileId(string path){
			var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
			switch(ext){
				case ".mp3": return "MP3";
				case ".jpg":
				case ".jpeg": return "JPEG_LRG";
				case ".png": return "PNG_LRG";
				case ".mp4":
				case ".m4v": return "AVC_MP4_BL_CIF15_AAC_520";
				default: return "";
			}
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