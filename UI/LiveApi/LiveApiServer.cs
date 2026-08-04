using Mesen.Interop;
using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.LiveApi
{
	public static class LiveApiServer
	{
		private const int DefaultPort = 8901;

		private static HttpListener? _listener;
		private static Task? _acceptTask;
		private static CancellationTokenSource _cts = new CancellationTokenSource();
		private static bool _running;

		public static int Port { get; private set; } = DefaultPort;
		public static bool Running { get { return _running; } }

		public static void Start(int? port = null)
		{
			if(_running) {
				return;
			}

			if(port.HasValue && port.Value > 0 && port.Value < 65536) {
				Port = port.Value;
			} else {
				string? envPort = Environment.GetEnvironmentVariable("MESEN_LIVE_API_PORT");
				if(int.TryParse(envPort, out int envValue) && envValue > 0 && envValue < 65536) {
					Port = envValue;
				} else {
					Port = DefaultPort;
				}
			}

			try {
				_listener = new HttpListener();
				_listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
				_listener.Start();
			} catch(Exception ex) {
				Console.WriteLine($"[LiveApi] Failed to start on port {Port}: {ex.Message}");
				_listener = null;
				return;
			}

			_running = true;
			_cts = new CancellationTokenSource();
			LiveDataService.Start();
			_acceptTask = Task.Run(AcceptLoop);
			Console.WriteLine($"[LiveApi] Listening on http://127.0.0.1:{Port}/");
		}

		public static void Stop()
		{
			if(!_running) {
				return;
			}
			_running = false;
			_cts.Cancel();
			try {
				_listener?.Stop();
			} catch {
			}
			_listener = null;
			LiveDataService.Stop();
		}

		private static async Task AcceptLoop()
		{
			while(!_cts.IsCancellationRequested && _listener != null) {
				try {
					HttpListenerContext context = await _listener.GetContextAsync();
					_ = Task.Run(() => HandleContext(context));
				} catch {
					if(_cts.IsCancellationRequested) {
						break;
					}
				}
			}
		}

		private static async Task HandleContext(HttpListenerContext context)
		{
			try {
				string? path = context.Request.Url?.AbsolutePath;
				if(path != null && path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase)) {
					await HandleWebSocket(context);
				} else {
					await HandleHttp(context);
				}
			} catch {
				try {
					context.Response.Abort();
				} catch {
				}
			}
		}

		private static async Task HandleHttp(HttpListenerContext context)
		{
			string? path = context.Request.Url?.AbsolutePath ?? "/";
			string method = context.Request.HttpMethod.ToUpperInvariant();

			JsonNode? result = null;
			int status = 200;

			try {
				switch(path) {
					case "/":
					case "/ui":
					case "/index.html":
						await ServeUi(context);
						return;
					case "/api/i18n":
						await ServeI18n(context, Query(context, "lang", "en"));
						return;
					case "/api":
						result = GetIndex();
						break;
					case "/api/status":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetStatus(), LiveApiSerializerContext.Default.LiveApiStatus);
						break;
					case "/api/rom":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetRomInfoSafe(), LiveApiSerializerContext.Default.LiveApiRomInfo);
						break;
					case "/api/memSize":
						result = SerializeJson(new JsonObject() { ["size"] = LiveDataService.GetMemorySize(Query(context, "type", "SnesWorkRam")) });
						break;
					case "/api/memory":
						if(method == "POST") {
							LiveApiMemoryWriteRequest? request = await ReadJsonBody<LiveApiMemoryWriteRequest>(context, LiveApiSerializerContext.Default.LiveApiMemoryWriteRequest);
							if(request == null) {
								result = SerializeJson(new JsonObject() { ["ok"] = false });
							} else {
								result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.WriteMemory(request.Type, request.Start, request.Data, request.Values) });
							}
						} else {
							string type = Query(context, "type", "SnesWorkRam");
							UInt32 start = ParseUInt(Query(context, "start", "0"));
							UInt32 length = ParseUInt(Query(context, "length", "16"));
							result = JsonSerializer.SerializeToNode(LiveDataService.ReadMemory(type, start, length), LiveApiSerializerContext.Default.LiveApiMemoryRead);
						}
						break;
					case "/api/cpu":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetCpuState(Query(context, "cpu", "Snes")), LiveApiSerializerContext.Default.LiveApiCpuState);
						break;
					case "/api/ppu":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetPpuState(Query(context, "cpu", "Snes")), LiveApiSerializerContext.Default.LiveApiPpuState);
						break;
					case "/api/trace":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetTrace(Query(context, "cpu", "Snes"), ParseUInt(Query(context, "count", "100"))), LiveApiSerializerContext.Default.LiveApiTraceRowArray);
						break;
					case "/api/trace/enable":
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.SetTraceEnabled(Query(context, "cpu", "Snes"), Query(context, "enable", "1") == "1") });
						break;
					case "/api/dma":
						result = LiveDataService.GetDmaState(Query(context, "cpu", "Snes"));
						break;
					case "/api/dma/log":
						DebugApi.SnesSetDmaLogEnabled(true);
						result = LiveDataService.GetDmaLog(Query(context, "cpu", "Snes"), (Int32)ParseUInt(Query(context, "count", "100")), ParseUInt(Query(context, "since", "0")));
						break;
					case "/api/events/history":
						result = LiveDataService.GetEventHistory(Query(context, "cpu", "Snes"), (Int32)ParseUInt(Query(context, "count", "100")), ParseUInt(Query(context, "since", "0")), Query(context, "type", ""));
						break;
					case "/api/vram/writes":
						result = LiveDataService.GetVramWrites(Query(context, "cpu", "Snes"), (Int32)ParseUInt(Query(context, "count", "100")), ParseUInt(Query(context, "since", "0")));
						break;
					case "/api/wram/writes":
						result = LiveDataService.GetWramWrites(Query(context, "cpu", "Snes"), (Int32)ParseUInt(Query(context, "count", "100")), ParseUInt(Query(context, "since", "0")), Query(context, "start", ""), Query(context, "end", ""), ParseUInt(Query(context, "minLen", "1")), Query(context, "memType", "SnesWorkRam"));
						break;
					case "/api/dma/log/count":
						result = SerializeJson(new JsonObject() { ["count"] = DebugApi.SnesGetDmaLogCount() });
						break;
					case "/api/dma/log/enable":
						result = SerializeJson(new JsonObject() { ["ok"] = true, ["enabled"] = Query(context, "enable", "1") == "1" });
						DebugApi.SnesSetDmaLogEnabled(Query(context, "enable", "1") == "1");
						break;
					case "/api/snapshot":
						result = LiveDataService.GetSnapshot(Query(context, "cpu", "Snes"));
						break;
					case "/api/savestate":
						if(method == "POST") {
							JsonObject? ssBody = await ReadJsonBody<JsonObject>(context, LiveApiSerializerContext.Default.JsonObject);
							string? ssAction = ssBody?["action"]?.GetValue<string>();
							UInt32 ssSlot = ssBody?["slot"]?.GetValue<UInt32>() ?? 1;
							bool ssOk = ssAction == "save" ? LiveDataService.SaveState(ssSlot) : ssAction == "load" ? LiveDataService.LoadState(ssSlot) : false;
							result = SerializeJson(new JsonObject() { ["ok"] = ssOk });
						} else {
							status = 405;
						}
						break;
					case "/api/input":
						if(method == "POST") {
							JsonObject? inputBody = await ReadJsonBody<JsonObject>(context, LiveApiSerializerContext.Default.JsonObject);
							string? key = inputBody?["key"]?.GetValue<string>();
							bool pressed = inputBody?["pressed"]?.GetValue<bool>() ?? false;
							int holdMs = inputBody?["holdMs"]?.GetValue<int>() ?? 0;
							result = key != null ? LiveDataService.SetInput(key, pressed, holdMs) : SerializeJson(new JsonObject() { ["ok"] = false });
						} else {
							status = 405;
						}
						break;
					case "/api/disasm":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetDisassembly(Query(context, "cpu", "Snes"), ParseUInt(Query(context, "address", "0")), ParseUInt(Query(context, "count", "20"))), LiveApiSerializerContext.Default.LiveApiDisasmLineArray);
						break;
					case "/api/events":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetEvents(Query(context, "cpu", "Snes")), LiveApiSerializerContext.Default.LiveApiEventInfoArray);
						break;
					case "/api/callstack":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetCallstack(Query(context, "cpu", "Snes")), LiveApiSerializerContext.Default.LiveApiStackFrameArray);
						break;
					case "/api/expression":
						result = JsonSerializer.SerializeToNode(LiveDataService.EvaluateExpression(Query(context, "expr", ""), Query(context, "cpu", "Snes")), LiveApiSerializerContext.Default.LiveApiExpressionResult);
						break;
					case "/api/breakpoints":
						if(method == "DELETE") {
							result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.ClearBreakpoints() });
						} else if(method == "POST") {
							LiveApiBreakpointSetRequest? request = await ReadJsonBody<LiveApiBreakpointSetRequest>(context, LiveApiSerializerContext.Default.LiveApiBreakpointSetRequest);
							if(request == null) {
								result = SerializeJson(new JsonObject() { ["ok"] = false });
							} else {
								result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.SetBreakpoint(request) });
							}
						} else {
							result = JsonSerializer.SerializeToNode(LiveDataService.GetBreakpoints(), LiveApiSerializerContext.Default.LiveApiBreakpointArray);
						}
						break;
					case "/api/control":
						if(method == "POST") {
							LiveApiControlRequest? request = await ReadJsonBody<LiveApiControlRequest>(context, LiveApiSerializerContext.Default.LiveApiControlRequest);
							if(request == null) {
								result = SerializeJson(new JsonObject() { ["ok"] = false });
							} else {
								result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.Control(request.Action, request.Cpu, request.StepCount) });
							}
						} else {
							status = 405;
						}
						break;
					case "/api/load":
						if(method == "POST") {
							LiveApiLoadRequest? request = await ReadJsonBody<LiveApiLoadRequest>(context, LiveApiSerializerContext.Default.LiveApiLoadRequest);
							if(request == null) {
								result = SerializeJson(new JsonObject() { ["ok"] = false });
							} else {
								result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.LoadRom(request.Path) });
							}
						} else {
							status = 405;
						}
						break;
					case "/api/tracker":
						if(method == "POST") {
							JsonObject? trk = await ReadJsonBody<JsonObject>(context, LiveApiSerializerContext.Default.JsonObject);
							result = LiveDataService.TrackerStart(
								trk?["memType"]?.GetValue<string>(),
								trk?["start"]?.GetValue<string>(),
								trk?["end"]?.GetValue<string>(),
								trk?["onRead"]?.GetValue<bool>() ?? false,
								trk?["onWrite"]?.GetValue<bool>() ?? true,
								trk?["value"]?.GetValue<string>(),
								trk?["valueSet"]?.GetValue<bool>() ?? false,
								trk?["logExec"]?.GetValue<bool>() ?? true,
								(UInt64)(trk?["maxMb"]?.GetValue<UInt32>() ?? 100) * 1024 * 1024,
								trk?["mode"]?.GetValue<string>() ?? "disk",
								(UInt64)(trk?["bufferMb"]?.GetValue<UInt32>() ?? 256));
						} else if(method == "GET") {
							result = LiveDataService.TrackerStatus();
						} else {
							status = 405;
						}
						break;
					case "/api/tracker/stop":
						if(method == "POST") {
							result = LiveDataService.TrackerStop();
						} else {
							status = 405;
						}
						break;
					case "/api/tracker/status":
						result = LiveDataService.TrackerStatus();
						break;
					case "/api/tracker/log":
						result = LiveDataService.GetTrackerLog((Int32)ParseUInt(Query(context, "count", "500")), ParseUInt(Query(context, "since", "0")));
						break;
					case "/api/gfx/state":
						result = GfxService.GetGfxState(Query(context, "cpu", "Snes"));
						break;
					case "/api/gfx/sprites.json":
						result = GfxService.GetSpritesJson(Query(context, "cpu", "Snes"));
						break;
					case "/api/gfx/sprites_decoded":
						result = GfxService.GetSpritesDecoded(Query(context, "cpu", "Snes"));
						break;
					case "/api/gfx/tilemap":
						await WritePng(context, GfxService.GetTilemapPng(Query(context, "cpu", "Snes"), Query(context, "layer", "0"), Query(context, "bg", "Black")));
						return;
					case "/api/gfx/screen":
						await WritePng(context, GfxService.GetScreenPng(Query(context, "cpu", "Snes"), Query(context, "layers", "all"), Query(context, "sprites", "1") == "1", Query(context, "bg", "Black")));
						return;
					case "/api/gfx/live":
						await WritePng(context, GfxService.GetLivePng(Query(context, "cpu", "Snes")));
						return;
					case "/api/gfx/sprites":
						await WritePng(context, GfxService.GetSpritesPng(Query(context, "cpu", "Snes")));
						return;
					case "/api/gfx/tiles":
						await WritePng(context, GfxService.GetTilesPng(Query(context, "cpu", "Snes"), Query(context, "format", "Bpp4"), Query(context, "mem", "SnesVideoRam"), (Int32)ParseUInt(Query(context, "cols", "16")), (Int32)ParseUInt(Query(context, "rows", "16")), (Int32)ParseUInt(Query(context, "palette", "0")), Query(context, "start", "0"), Query(context, "bg", "Black")));
						return;
					case "/api/spc":
						await WriteSpc(context, SpcService.ExportSpc(Query(context, "song", ""), Query(context, "game", ""), Query(context, "artist", "")));
						return;
					case "/api/spc/wav":
						await WriteWav(context, SpcService.RecordWav((Int32)ParseUInt(Query(context, "seconds", "30"))));
						return;
					case "/api/spc/record":
						if(method == "POST") {
							result = SpcService.StartRecording();
						} else {
							status = 405;
						}
						break;
					case "/api/spc/record/status":
						result = SpcService.GetRecordingStatus();
						break;
					case "/api/spc/record/stop":
						if(method == "POST") {
							result = SpcService.StopRecording();
						} else {
							status = 405;
						}
						break;
					case "/api/spc/record/file":
						await WriteWav(context, SpcService.GetRecordingFile());
						return;
					case "/api/spc/state":
						result = SpcService.GetSpcState();
						break;
					case "/api/plugins":
						if(method == "POST") {
							JsonObject? pluginBody = await ReadJsonBody<JsonObject>(context, LiveApiSerializerContext.Default.JsonObject);
							string? pName = pluginBody?["name"]?.GetValue<string>();
							string? pContent = pluginBody?["content"]?.GetValue<string>();
							result = SerializeJson(new JsonObject() { ["ok"] = PluginService.SavePlugin(pName ?? "", pContent ?? "") });
						} else {
							result = PluginService.ListPlugins();
						}
						break;
					case "/api/export":
						if(method == "POST") {
							JsonObject? exportBody = await ReadJsonBody<JsonObject>(context, LiveApiSerializerContext.Default.JsonObject);
							string? eName = exportBody?["name"]?.GetValue<string>();
							string? eData = exportBody?["data"]?.GetValue<string>();
							string? eMode = exportBody?["mode"]?.GetValue<string>();
							result = SerializeJson(new JsonObject() { ["ok"] = PluginService.Export(eName ?? "", eData ?? "", eMode ?? "text") });
						} else {
							status = 405;
						}
						break;
					default:
						if(path != null && path.StartsWith("/api/plugins/", StringComparison.OrdinalIgnoreCase)) {
							string rest = path.Substring("/api/plugins/".Length);
							string fileName = rest.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? rest.Substring(0, rest.Length - 3) : rest;
							if(method == "GET") {
								byte[]? pluginBytes = PluginService.GetPlugin(fileName);
								if(pluginBytes == null) {
									status = 404;
									result = SerializeJson(new JsonObject() { ["error"] = "Plugin nicht gefunden" });
								} else {
									await WriteBytes(context, 200, "text/javascript; charset=utf-8", pluginBytes);
									return;
								}
							} else if(method == "DELETE") {
								result = SerializeJson(new JsonObject() { ["ok"] = PluginService.DeletePlugin(fileName) });
							} else {
								status = 405;
							}
						} else {
							status = 404;
							result = SerializeJson(new JsonObject() { ["error"] = $"Unknown endpoint: {path}" });
						}
						break;
				}
			} catch(Exception ex) {
				status = 500;
				result = SerializeJson(new JsonObject() { ["error"] = ex.Message });
			}

			await WriteResponse(context, status, result);
		}

		private static async Task HandleWebSocket(HttpListenerContext context)
		{
			if(!context.Request.IsWebSocketRequest) {
				context.Response.StatusCode = 400;
				context.Response.Close();
				return;
			}

			HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
			WebSocket socket = wsContext.WebSocket;

			LiveApiSubscriber subscriber = new LiveApiSubscriber();
			object sendLock = new object();
			subscriber.Send = msg => {
				JsonObject payload = JsonSerializer.SerializeToNode(msg, LiveApiSerializerContext.Default.LiveApiEventMessage) as JsonObject ?? new JsonObject();
				SendJson(socket, sendLock, payload);
			};
			LiveDataService.RegisterSubscriber(subscriber);

			try {
				byte[] buffer = new byte[65536];
				while(!_cts.IsCancellationRequested && socket.State == WebSocketState.Open) {
					WebSocketReceiveResult receiveResult;
					using(MemoryStream ms = new MemoryStream()) {
						ArraySegment<byte> segment = new ArraySegment<byte>(buffer);
						do {
							receiveResult = await socket.ReceiveAsync(segment, CancellationToken.None);
							if(receiveResult.MessageType == WebSocketMessageType.Close) {
								await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
								return;
							}
							if(receiveResult.MessageType == WebSocketMessageType.Text) {
								ms.Write(buffer, 0, receiveResult.Count);
							}
						} while(!receiveResult.EndOfMessage);

						if(receiveResult.MessageType == WebSocketMessageType.Text) {
							string text = Encoding.UTF8.GetString(ms.ToArray());
							HandleRpc(text, subscriber, socket, sendLock);
						}
					}
				}
			} catch {
			} finally {
				LiveDataService.UnregisterSubscriber(subscriber);
				try {
					socket.Dispose();
				} catch {
				}
			}
		}

		private static void HandleRpc(string text, LiveApiSubscriber subscriber, WebSocket socket, object sendLock)
		{
			LiveApiRpcResponse? response = null;
			try {
				LiveApiRpcRequest? rpc = JsonSerializer.Deserialize(text, LiveApiSerializerContext.Default.LiveApiRpcRequest);
				if(rpc == null) {
					response = RpcError(null, -32700, "Parse error");
				} else {
					response = ProcessMethod(rpc, subscriber);
				}
			} catch {
				response = RpcError(null, -32700, "Parse error");
			}

			if(response != null && response.Id != null) {
				JsonObject payload = JsonSerializer.SerializeToNode(response, LiveApiSerializerContext.Default.LiveApiRpcResponse) as JsonObject ?? new JsonObject();
				SendJson(socket, sendLock, payload);
			}
		}

		private static LiveApiRpcResponse ProcessMethod(LiveApiRpcRequest rpc, LiveApiSubscriber subscriber)
		{
			UInt64? id = rpc.Id;
			JsonElement parameters = rpc.Params;

			JsonNode? result = null;
			try {
				switch(rpc.Method) {
					case "status":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetStatus(), LiveApiSerializerContext.Default.LiveApiStatus);
						break;
					case "rom":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetRomInfoSafe(), LiveApiSerializerContext.Default.LiveApiRomInfo);
						break;
					case "memory.size": {
						LiveApiRange? req = DeserializeParams<LiveApiRange>(parameters, LiveApiSerializerContext.Default.LiveApiRange);
						result = SerializeJson(new JsonObject() { ["size"] = LiveDataService.GetMemorySize(req?.Type ?? "SnesWorkRam") });
						break;
					}
					case "memory.read": {
						LiveApiRange? req = DeserializeParams<LiveApiRange>(parameters, LiveApiSerializerContext.Default.LiveApiRange);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						result = JsonSerializer.SerializeToNode(LiveDataService.ReadMemory(req.Type, req.Start, req.Length), LiveApiSerializerContext.Default.LiveApiMemoryRead);
						break;
					}
					case "memory.write": {
						LiveApiMemoryWriteRequest? req = DeserializeParams<LiveApiMemoryWriteRequest>(parameters, LiveApiSerializerContext.Default.LiveApiMemoryWriteRequest);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.WriteMemory(req.Type, req.Start, req.Data, req.Values) });
						break;
					}
					case "cpu": {
						string cpu = GetString(parameters, "cpu", "Snes");
						result = JsonSerializer.SerializeToNode(LiveDataService.GetCpuState(cpu), LiveApiSerializerContext.Default.LiveApiCpuState);
						break;
					}
					case "ppu": {
						string cpu = GetString(parameters, "cpu", "Snes");
						result = JsonSerializer.SerializeToNode(LiveDataService.GetPpuState(cpu), LiveApiSerializerContext.Default.LiveApiPpuState);
						break;
					}
					case "trace": {
						string cpu = GetString(parameters, "cpu", "Snes");
						UInt32 count = GetUInt(parameters, "count", 100);
						result = JsonSerializer.SerializeToNode(LiveDataService.GetTrace(cpu, count), LiveApiSerializerContext.Default.LiveApiTraceRowArray);
						break;
					}
					case "disasm": {
						string cpu = GetString(parameters, "cpu", "Snes");
						UInt32 address = GetUInt(parameters, "address", 0);
						UInt32 count = GetUInt(parameters, "count", 20);
						result = JsonSerializer.SerializeToNode(LiveDataService.GetDisassembly(cpu, address, count), LiveApiSerializerContext.Default.LiveApiDisasmLineArray);
						break;
					}
					case "events": {
						string cpu = GetString(parameters, "cpu", "Snes");
						result = JsonSerializer.SerializeToNode(LiveDataService.GetEvents(cpu), LiveApiSerializerContext.Default.LiveApiEventInfoArray);
						break;
					}
					case "callstack": {
						string cpu = GetString(parameters, "cpu", "Snes");
						result = JsonSerializer.SerializeToNode(LiveDataService.GetCallstack(cpu), LiveApiSerializerContext.Default.LiveApiStackFrameArray);
						break;
					}
					case "expression": {
						string expr = GetString(parameters, "expr", "");
						string cpu = GetString(parameters, "cpu", "Snes");
						result = JsonSerializer.SerializeToNode(LiveDataService.EvaluateExpression(expr, cpu), LiveApiSerializerContext.Default.LiveApiExpressionResult);
						break;
					}
					case "breakpoints.get":
						result = JsonSerializer.SerializeToNode(LiveDataService.GetBreakpoints(), LiveApiSerializerContext.Default.LiveApiBreakpointArray);
						break;
					case "breakpoints.set": {
						LiveApiBreakpointSetRequest? req = DeserializeParams<LiveApiBreakpointSetRequest>(parameters, LiveApiSerializerContext.Default.LiveApiBreakpointSetRequest);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.SetBreakpoint(req) });
						break;
					}
					case "breakpoints.clear":
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.ClearBreakpoints() });
						break;
					case "control": {
						LiveApiControlRequest? req = DeserializeParams<LiveApiControlRequest>(parameters, LiveApiSerializerContext.Default.LiveApiControlRequest);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.Control(req.Action, req.Cpu, req.StepCount) });
						break;
					}
					case "load": {
						LiveApiLoadRequest? req = DeserializeParams<LiveApiLoadRequest>(parameters, LiveApiSerializerContext.Default.LiveApiLoadRequest);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						result = SerializeJson(new JsonObject() { ["ok"] = LiveDataService.LoadRom(req.Path) });
						break;
					}
					case "subscribe": {
						LiveApiSubscribeRequest? req = DeserializeParams<LiveApiSubscribeRequest>(parameters, LiveApiSerializerContext.Default.LiveApiSubscribeRequest);
						if(req == null) {
							return RpcError(id, -32602, "Invalid params");
						}
						subscriber.Events.Clear();
						subscriber.Snapshots.Clear();
						if(req.Events != null) {
							subscriber.Events.AddRange(req.Events);
						}
						subscriber.Ranges.Clear();
						if(req.Ranges != null) {
							subscriber.Ranges.AddRange(req.Ranges);
						}
						result = SerializeJson(new JsonObject() { ["ok"] = true });
						break;
					}
					case "unsubscribe":
						subscriber.Events.Clear();
						subscriber.Ranges.Clear();
						subscriber.Snapshots.Clear();
						result = SerializeJson(new JsonObject() { ["ok"] = true });
						break;
					default:
						return RpcError(id, -32601, "Method not found: " + rpc.Method);
				}
			} catch(Exception ex) {
				return RpcError(id, -32603, ex.Message);
			}

			return new LiveApiRpcResponse() {
				Id = id,
				Result = result,
				Error = null
			};
		}

		private static LiveApiRpcResponse RpcError(UInt64? id, int code, string message)
		{
			return new LiveApiRpcResponse() {
				Id = id,
				Result = null,
				Error = new LiveApiError() { Code = code, Message = message }
			};
		}

		private static void SendJson(WebSocket socket, object sendLock, JsonNode payload)
		{
			byte[] bytes;
			try {
				bytes = JsonSerializer.SerializeToUtf8Bytes(payload, LiveApiSerializerContext.Default.JsonNode);
			} catch {
				return;
			}
			try {
				lock(sendLock) {
					if(socket.State == WebSocketState.Open) {
						socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
					}
				}
			} catch {
			}
		}

		private static JsonNode SerializeJson(JsonObject obj)
		{
			return JsonSerializer.SerializeToNode(obj, LiveApiSerializerContext.Default.JsonObject) ?? new JsonObject();
		}

		private static async Task WriteResponse(HttpListenerContext context, int status, JsonNode? body)
		{
			byte[] bytes = body == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body, LiveApiSerializerContext.Default.JsonNode);

			context.Response.StatusCode = status;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			context.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
			context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
			context.Response.AddHeader("Cache-Control", "no-store");
			if(context.Request.HttpMethod == "OPTIONS") {
				context.Response.StatusCode = 204;
				context.Response.ContentLength64 = 0;
				context.Response.Close();
				return;
			}
			if(bytes.Length > 0) {
				await context.Response.OutputStream.WriteAsync(bytes);
			}
			context.Response.Close();
		}

		private static async Task WriteBytes(HttpListenerContext context, int status, string contentType, byte[] bytes)
		{
			context.Response.StatusCode = status;
			context.Response.ContentType = contentType;
			context.Response.ContentLength64 = bytes.Length;
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			context.Response.AddHeader("Cache-Control", "no-store");
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}

		private static async Task WritePng(HttpListenerContext context, byte[]? png)
		{
			if(png == null) {
				context.Response.StatusCode = 404;
				byte[] msg = Encoding.UTF8.GetBytes("No image available (game not loaded or unsupported layer)");
				context.Response.ContentType = "text/plain; charset=utf-8";
				context.Response.ContentLength64 = msg.Length;
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				await context.Response.OutputStream.WriteAsync(msg);
				context.Response.Close();
				return;
			}
			context.Response.StatusCode = 200;
			context.Response.ContentType = "image/png";
			context.Response.ContentLength64 = png.Length;
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			context.Response.AddHeader("Cache-Control", "no-store");
			await context.Response.OutputStream.WriteAsync(png);
			context.Response.Close();
		}

		private static async Task WriteSpc(HttpListenerContext context, byte[]? spc)
		{
			if(spc == null) {
				context.Response.StatusCode = 404;
				byte[] msg = Encoding.UTF8.GetBytes("No SPC snapshot available (game not loaded)");
				context.Response.ContentType = "text/plain; charset=utf-8";
				context.Response.ContentLength64 = msg.Length;
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				await context.Response.OutputStream.WriteAsync(msg);
				context.Response.Close();
				return;
			}
			context.Response.StatusCode = 200;
			context.Response.ContentType = "application/octet-stream";
			context.Response.ContentLength64 = spc.Length;
			context.Response.AddHeader("Content-Disposition", "attachment; filename=\"mesen_dump.spc\"");
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			context.Response.AddHeader("Cache-Control", "no-store");
			await context.Response.OutputStream.WriteAsync(spc);
			context.Response.Close();
		}

		private static async Task WriteWav(HttpListenerContext context, byte[]? wav)
		{
			if(wav == null) {
				context.Response.StatusCode = 404;
				byte[] msg = Encoding.UTF8.GetBytes("Keine Aufnahme möglich (Emulator läuft nicht)");
				context.Response.ContentType = "text/plain; charset=utf-8";
				context.Response.ContentLength64 = msg.Length;
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				await context.Response.OutputStream.WriteAsync(msg);
				context.Response.Close();
				return;
			}
			context.Response.StatusCode = 200;
			context.Response.ContentType = "audio/wav";
			context.Response.ContentLength64 = wav.Length;
			context.Response.AddHeader("Content-Disposition", "attachment; filename=\"terranigma_live.wav\"");
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			context.Response.AddHeader("Cache-Control", "no-store");
			await context.Response.OutputStream.WriteAsync(wav);
			context.Response.Close();
		}

		private static byte[]? _uiBytes;

		private static async Task ServeUi(HttpListenerContext context)
		{
			if(_uiBytes == null) {
				_uiBytes = LoadUi();
			}
			if(_uiBytes == null) {
				context.Response.StatusCode = 404;
				context.Response.ContentType = "text/plain";
				byte[] msg = Encoding.UTF8.GetBytes("LiveApiUi.html nicht gefunden (EmbeddedResource fehlt)");
				context.Response.ContentLength64 = msg.Length;
				await context.Response.OutputStream.WriteAsync(msg);
				context.Response.Close();
				return;
			}
			context.Response.StatusCode = 200;
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.ContentLength64 = _uiBytes.Length;
			context.Response.AddHeader("Access-Control-Allow-Origin", "*");
			await context.Response.OutputStream.WriteAsync(_uiBytes);
			context.Response.Close();
		}

		private static async Task ServeI18n(HttpListenerContext context, string lang)
		{
			//Sprach-XML für die WebUI ausliefern (gleiches Format wie Mesens resources.<lang>.xml;
			//die WebUI liest nur den Form-Block "LiveApiUi")
			string safeLang = lang switch {
				"de" => "de",
				"fr" => "fr",
				"zh" => "zh",
				"ja" => "ja",
				_ => "en"
			};
			string resourceName = $"Mesen.Localization.resources.{safeLang}.xml";
			byte[]? xml = null;
			using(Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)) {
				if(stream != null) {
					using MemoryStream ms = new MemoryStream();
					stream.CopyTo(ms);
					xml = ms.ToArray();
				}
			}

			if(xml == null) {
				context.Response.StatusCode = 404;
				context.Response.ContentType = "text/plain";
				byte[] msg = Encoding.UTF8.GetBytes($"i18n '{lang}' nicht gefunden");
				context.Response.ContentLength64 = msg.Length;
				await context.Response.OutputStream.WriteAsync(msg);
			} else {
				context.Response.StatusCode = 200;
				context.Response.ContentType = "text/xml; charset=utf-8";
				context.Response.ContentLength64 = xml.Length;
				context.Response.AddHeader("Access-Control-Allow-Origin", "*");
				context.Response.AddHeader("Cache-Control", "no-store");
				await context.Response.OutputStream.WriteAsync(xml);
			}
			context.Response.Close();
		}

		private static byte[]? LoadUi()
		{
			try {
				using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mesen.LiveApi.LiveApiUi.html");
				if(stream == null) {
					return null;
				}
				using MemoryStream ms = new MemoryStream();
				stream.CopyTo(ms);
				return ms.ToArray();
			} catch {
				return null;
			}
		}

		private static JsonObject GetIndex()
		{
			string[] endpointList = new[] {
				"GET  /api/status",
				"GET  /api/rom",
				"GET  /api/memory?type=SnesWorkRam&start=0&length=64",
				"POST /api/memory",
				"GET  /api/cpu?cpu=Snes",
				"GET  /api/ppu?cpu=Snes",
				"GET  /api/trace?cpu=Snes&count=100",
				"GET  /api/trace/enable?cpu=Snes&enable=1",
				"GET  /api/dma?cpu=Snes  (DMA-Kanal-Log)",
				"POST /api/input {key,pressed}  (z. B. Pad1 Up)",
				"GET  /api/disasm?cpu=Snes&address=0x8000&count=20",
				"GET  /api/events?cpu=Snes",
				"GET  /api/events/history?cpu=Snes&count=100&type=Nmi  (Ring, since=ID)",
				"GET  /api/vram/writes?cpu=Snes&count=100  (PC + Zieladresse)",
				"GET  /api/wram/writes?cpu=Snes&start=0&end=0xFFFF&minLen=1&memType=SnesWorkRam  (PC + Run)",
				"GET  /api/dma/log?cpu=Snes&count=100  /api/dma/log/count  /api/dma/log/enable?enable=1",
				"GET  /api/snapshot?cpu=Snes  (atomar: frame/scroll/nametable/dma)",
				"POST /api/savestate {action:save|load,slot}",
				"POST /api/tracker {memType,start,end,onRead,onWrite,value,logExec,mode:ram|disk,bufferMb}  | GET /api/tracker/status | POST /api/tracker/stop | GET /api/tracker/log",
				"GET  /api/callstack?cpu=Snes",
				"GET  /api/expression?expr=A:B&cpu=Snes",
				"GET/POST/DELETE /api/breakpoints",
				"POST /api/control {action}",
				"POST /api/load {path}",
				"GET  /api/gfx/state?cpu=Snes",
				"GET  /api/gfx/tilemap?cpu=Snes&layer=0&bg=Black",
				"GET  /api/gfx/screen?cpu=Snes&layers=all&sprites=1&bg=Black",
				"GET  /api/gfx/sprites?cpu=Snes",
				"GET  /api/gfx/sprites.json?cpu=Snes",
				"GET  /api/gfx/sprites_decoded?cpu=Snes  (dekodierte OAM-Liste)",
				"GET  /api/gfx/tiles?cpu=Snes&format=Bpp4&mem=SnesVideoRam&cols=16&rows=16&palette=0&start=0&bg=Black",
				"GET  /api/spc?song=&game=&artist=  (.spc Snapshot, octet-stream)",
				"GET  /api/spc/wav?seconds=30  (WAV-Aufnahme des laufenden Audio)",
				"POST /api/spc/record  | GET /api/spc/record/status | POST /api/spc/record/stop | GET /api/spc/record/file",
				"GET  /api/spc/state  (APU/DSP JSON)",
				"GET  /api/plugins  (Liste), GET/DELETE /api/plugins/{name}, POST /api/plugins {name,content}",
				"POST /api/export  {name,data,mode}  (Plugin-Export: text/append/png)",
				"WS   /ws"
			};
			JsonArray endpoints = (JsonArray)(JsonSerializer.SerializeToNode(endpointList, LiveApiSerializerContext.Default.StringArray) ?? new JsonArray());
			return new JsonObject() {
				["name"] = "MesenCE Live API",
				["port"] = Port,
				["running"] = LiveDataService.Started,
				["endpoints"] = endpoints
			};
		}

		private static string Query(HttpListenerContext context, string key, string defaultValue)
		{
			string? value = context.Request.QueryString[key];
			return string.IsNullOrEmpty(value) ? defaultValue : value;
		}

		private static UInt32 ParseUInt(string text)
		{
			text = text.Trim();
			try {
				if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					return Convert.ToUInt32(text.Substring(2), 16);
				}
				return Convert.ToUInt32(text, 10);
			} catch {
				return 0;
			}
		}

		private static async Task<T?> ReadJsonBody<T>(HttpListenerContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
		{
			try {
				using(StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) {
					string text = await reader.ReadToEndAsync();
					if(string.IsNullOrWhiteSpace(text)) {
						return null;
					}
					return JsonSerializer.Deserialize(text, typeInfo);
				}
			} catch {
				return null;
			}
		}

		private static T? DeserializeParams<T>(JsonElement parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
		{
			if(parameters.ValueKind != JsonValueKind.Object) {
				return null;
			}
			try {
				return JsonSerializer.Deserialize(parameters.GetRawText(), typeInfo);
			} catch {
				return null;
			}
		}

		private static string GetString(JsonElement parameters, string name, string defaultValue)
		{
			if(parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) {
				return value.GetString() ?? defaultValue;
			}
			return defaultValue;
		}

		private static UInt32 GetUInt(JsonElement parameters, string name, UInt32 defaultValue)
		{
			if(parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value)) {
				if(value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out UInt32 number)) {
					return number;
				}
				if(value.ValueKind == JsonValueKind.String && value.GetString() is string text) {
					return ParseUInt(text);
				}
			}
			return defaultValue;
		}
	}
}
