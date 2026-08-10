using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.LiveApi
{
	public sealed class LiveApiSubscriber
	{
		public List<string> Events { get; } = new List<string>();
		public List<LiveApiRange> Ranges { get; } = new List<LiveApiRange>();
		public Dictionary<string, byte[]> Snapshots { get; } = new Dictionary<string, byte[]>();
		public Action<LiveApiEventMessage>? Send { get; set; }
	}

	public static class LiveDataService
	{
		private static SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private static ConcurrentQueue<ConsoleNotificationType> _notifications = new ConcurrentQueue<ConsoleNotificationType>();
		private static NotificationListener? _listener;
		private static Thread? _worker;
		private static CancellationTokenSource _cts = new CancellationTokenSource();
		private static readonly object SubscriberLock = new object();
		private static readonly List<LiveApiSubscriber> Subscribers = new List<LiveApiSubscriber>();
		private static long _frameCount;
		private static bool _started;

		public static bool Started { get { return _started; } }

		public static void Start()
		{
			if(_started) {
				return;
			}
			_started = true;
			_cts = new CancellationTokenSource();

			try {
				_listener = new NotificationListener();
				_listener.OnNotification += OnNotification;
			} catch {
				_listener = null;
			}

			if(_listener == null) {
				return;
			}

			_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "LiveApiWorker" };
			_worker.Start();
		}

		public static void Stop()
		{
			if(!_started) {
				return;
			}
			_started = false;
			_cts.Cancel();
			_listener?.Dispose();
			_listener = null;
		}

		private static void OnNotification(NotificationEventArgs e)
		{
			_notifications.Enqueue(e.NotificationType);
		}

		private static void WorkerLoop()
		{
			while(!_cts.IsCancellationRequested) {
				if(_notifications.TryDequeue(out ConsoleNotificationType type)) {
					try {
						ProcessNotification(type);
					} catch {
					}
				} else {
					Thread.Sleep(1);
				}
			}
		}

		private static void ProcessNotification(ConsoleNotificationType type)
		{
			switch(type) {
				case ConsoleNotificationType.PpuFrameDone:
					Interlocked.Increment(ref _frameCount);
					BroadcastFrame();
					break;
				case ConsoleNotificationType.GameLoaded:
					ClearSnapshots();
					BroadcastRomLoaded();
					break;
				case ConsoleNotificationType.BeforeGameUnload:
				case ConsoleNotificationType.EmulationStopped:
					BroadcastSimple("stopped");
					break;
				case ConsoleNotificationType.GamePaused:
					BroadcastSimple("paused");
					break;
				case ConsoleNotificationType.GameResumed:
					BroadcastSimple("resumed");
					break;
				case ConsoleNotificationType.CodeBreak:
					BroadcastSimple("break");
					break;
			}
		}

		private static void BroadcastSimple(string eventName)
		{
			LiveApiEventMessage msg = new LiveApiEventMessage() {
				Event = eventName,
				Frame = (UInt64)Interlocked.Read(ref _frameCount),
				Paused = GetIsPaused()
			};
			Broadcast(msg);
		}

		private static void BroadcastRomLoaded()
		{
			LiveApiEventMessage msg = new LiveApiEventMessage() {
				Event = "romLoaded",
				Frame = (UInt64)Interlocked.Read(ref _frameCount),
				Paused = GetIsPaused(),
				Rom = GetRomInfoSafe()
			};
			Broadcast(msg);
		}

		private static void BroadcastFrame()
		{
			lock(SubscriberLock) {
				foreach(LiveApiSubscriber subscriber in Subscribers) {
					LiveApiEventMessage? msg = null;
					if(subscriber.Ranges.Count > 0) {
						List<LiveApiChange> changes = ComputeChanges(subscriber);
						if(changes.Count > 0) {
							msg = new LiveApiEventMessage() {
								Event = "frame",
								Frame = (UInt64)Interlocked.Read(ref _frameCount),
								Paused = GetIsPaused(),
								Changes = changes.ToArray()
							};
						} else if(subscriber.Events.Contains("frame")) {
							msg = new LiveApiEventMessage() {
								Event = "frame",
								Frame = (UInt64)Interlocked.Read(ref _frameCount),
								Paused = GetIsPaused()
							};
						}
					} else if(subscriber.Events.Contains("frame")) {
						msg = new LiveApiEventMessage() {
							Event = "frame",
							Frame = (UInt64)Interlocked.Read(ref _frameCount),
							Paused = GetIsPaused()
						};
					}
					if(msg != null) {
						subscriber.Send?.Invoke(msg);
					}
				}
			}
		}

		private static List<LiveApiChange> ComputeChanges(LiveApiSubscriber subscriber)
		{
			List<LiveApiChange> changes = new List<LiveApiChange>();
			foreach(LiveApiRange range in subscriber.Ranges) {
				MemoryType? memType = ParseMemoryType(range.Type);
				if(memType == null) {
					continue;
				}
				byte[]? data = ReadMemoryRaw(memType.Value, range.Start, range.Length);
				if(data == null || data.Length == 0) {
					continue;
				}
				string key = range.Type + ":" + range.Start + ":" + range.Length;
				if(!subscriber.Snapshots.TryGetValue(key, out byte[]? snapshot)) {
					snapshot = new byte[data.Length];
					Array.Copy(data, snapshot, data.Length);
					subscriber.Snapshots[key] = snapshot;
				}
				if(snapshot.Length != data.Length) {
					Array.Resize(ref snapshot, data.Length);
					Array.Copy(data, snapshot, data.Length);
				}

				List<(UInt32 offset, byte[] bytes)> runs = new List<(UInt32, byte[])>();
				UInt32 runStart = UInt32.MaxValue;
				List<byte> runBytes = new List<byte>();
				for(int i = 0; i < data.Length; i++) {
					if(data[i] != snapshot[i]) {
						if(runStart == UInt32.MaxValue) {
							runStart = (UInt32)i;
							runBytes = new List<byte>();
						}
						runBytes.Add(data[i]);
						snapshot[i] = data[i];
					} else if(runStart != UInt32.MaxValue) {
						runs.Add((runStart, runBytes.ToArray()));
						runStart = UInt32.MaxValue;
						runBytes = new List<byte>();
					}
				}
				if(runStart != UInt32.MaxValue) {
					runs.Add((runStart, runBytes.ToArray()));
				}

				foreach((UInt32 offset, byte[] bytes) run in runs) {
					changes.Add(new LiveApiChange() {
						Type = range.Type,
						Start = range.Start + run.offset,
						Data = ToHex(run.bytes)
					});
				}
			}
			return changes;
		}

		private static void ClearSnapshots()
		{
			lock(SubscriberLock) {
				foreach(LiveApiSubscriber subscriber in Subscribers) {
					subscriber.Snapshots.Clear();
				}
			}
		}

		private static void Broadcast(LiveApiEventMessage msg)
		{
			lock(SubscriberLock) {
				foreach(LiveApiSubscriber subscriber in Subscribers) {
					if(subscriber.Events.Contains(msg.Event)) {
						subscriber.Send?.Invoke(msg);
					}
				}
			}
		}

		public static void RegisterSubscriber(LiveApiSubscriber subscriber)
		{
			lock(SubscriberLock) {
				Subscribers.Add(subscriber);
			}
		}

		public static void UnregisterSubscriber(LiveApiSubscriber subscriber)
		{
			lock(SubscriberLock) {
				Subscribers.Remove(subscriber);
			}
		}

		private static T RunExclusive<T>(Func<T> action)
		{
			_gate.Wait();
			try {
				return action();
			} finally {
				_gate.Release();
			}
		}

		private static void RunExclusive(Action action)
		{
			_gate.Wait();
			try {
				action();
			} finally {
				_gate.Release();
			}
		}

		private static bool GetIsPaused()
		{
			return RunExclusive(() => {
				try {
					return EmuApi.IsRunning() && EmuApi.IsPaused();
				} catch {
					return false;
				}
			});
		}

		public static LiveApiStatus GetStatus()
		{
			return RunExclusive(() => {
				LiveApiStatus status = new LiveApiStatus();
				try {
					status.Running = EmuApi.IsRunning();
					status.Paused = status.Running && EmuApi.IsPaused();
					status.Frame = (UInt64)Interlocked.Read(ref _frameCount);
					if(status.Running) {
						RomInfo info = EmuApi.GetRomInfo();
						status.RomLoaded = info.Format != RomFormat.Unknown;
						status.RomName = info.GetRomName();
						status.RomPath = info.RomPath;
						status.ConsoleType = info.ConsoleType.ToString();
						status.Language = ConfigManager.Config.Preferences.Language.ToString();
						try {
							status.RomHash = EmuApi.GetRomHash(HashType.Sha1);
						} catch {
							status.RomHash = "";
						}
					}
				} catch {
				}
				return status;
			});
		}

		public static LiveApiRomInfo? GetRomInfoSafe()
		{
			return RunExclusive(() => {
				try {
					if(!EmuApi.IsRunning()) {
						return null;
					}
					RomInfo info = EmuApi.GetRomInfo();
					LiveApiRomInfo result = new LiveApiRomInfo() {
						RomPath = info.RomPath,
						RomName = info.GetRomName(),
						Format = info.Format.ToString(),
						ConsoleType = info.ConsoleType.ToString()
					};
					foreach(CpuType cpu in info.CpuTypes) {
						result.CpuTypes = new string[] { cpu.ToString() };
					}
					try {
						result.Sha1 = EmuApi.GetRomHash(HashType.Sha1);
					} catch {
					}
					try {
						result.GameCode = EmuApi.GetRomGameCode();
					} catch {
					}
					return result;
				} catch {
					return null;
				}
			});
		}

		public static LiveApiMemoryRead? ReadMemory(string type, UInt32 start, UInt32 length)
		{
			MemoryType? memType = ParseMemoryType(type);
			if(memType == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					byte[] data = DebugApi.GetMemoryValues(memType.Value, start, start + Math.Max(length, 1) - 1);
					return new LiveApiMemoryRead() {
						Type = memType.Value.ToString(),
						Start = start,
						Length = (UInt32)data.Length,
						Data = ToHex(data)
					};
				} catch {
					return null;
				}
			});
		}

		private static byte[]? ReadMemoryRaw(MemoryType type, UInt32 start, UInt32 length)
		{
			return RunExclusive(() => {
				try {
					return DebugApi.GetMemoryValues(type, start, start + Math.Max(length, 1) - 1);
				} catch {
					return null;
				}
			});
		}

		// NICHT-lockende Variante: wird nur innerhalb eines bestehenden RunExclusive aufgerufen
		// (z.B. vom nativen Script-Modul) - sonst Deadlock (Semaphor ist nicht rekursiv).
		private static byte[]? ReadMemoryRawNoLock(MemoryType type, UInt32 start, UInt32 length)
		{
			try {
				return DebugApi.GetMemoryValues(type, start, start + Math.Max(length, 1) - 1);
			} catch {
				return null;
			}
		}

		public static bool WriteMemory(string type, UInt32 start, string? hexData, byte[]? values)
		{
			MemoryType? memType = ParseMemoryType(type);
			if(memType == null) {
				return false;
			}
			return RunExclusive(() => {
				try {
					byte[]? data = null;
					if(values != null && values.Length > 0) {
						data = values;
					} else if(!string.IsNullOrEmpty(hexData)) {
						data = FromHex(hexData);
					}
					if(data == null || data.Length == 0) {
						return false;
					}
					DebugApi.SetMemoryValues(memType.Value, start, data, data.Length);
					return true;
				} catch {
					return false;
				}
			});
		}

		public static LiveApiCpuState? GetCpuState(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(cpu.Value);
					return new LiveApiCpuState() {
						Cpu = cpu.Value.ToString(),
						A = state.A,
						X = state.X,
						Y = state.Y,
						SP = state.SP,
						D = state.D,
						PC = state.PC,
						K = state.K,
						DBR = state.DBR,
						PS = (byte)state.PS,
						EmulationMode = state.EmulationMode,
						StopState = state.StopState.ToString(),
						CycleCount = state.CycleCount
					};
				} catch {
					return null;
				}
			});
		}

		public static LiveApiPpuState? GetPpuState(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesPpuState state = DebugApi.GetPpuState<SnesPpuState>(cpu.Value);
					return new LiveApiPpuState() {
						Cpu = cpu.Value.ToString(),
						Scanline = state.Scanline,
						Cycle = state.Cycle,
						HClock = state.HClock,
						FrameCount = state.FrameCount,
						ForcedBlank = state.ForcedBlank,
						ScreenBrightness = state.ScreenBrightness,
						BgMode = state.BgMode,
						MainScreenLayers = state.MainScreenLayers,
						SubScreenLayers = state.SubScreenLayers,
						VramAddress = state.VramAddress,
						OamRamAddress = state.OamRamAddress,
						CgramAddress = state.CgramAddress,
						Layers = BuildLayerList(state),
						Mode7 = new LiveApiMode7State() {
							HScroll = state.Mode7.HScroll,
							VScroll = state.Mode7.VScroll,
							CenterX = state.Mode7.CenterX,
							CenterY = state.Mode7.CenterY,
							LargeMap = state.Mode7.LargeMap,
							Matrix = new Int16[4] { state.Mode7.Matrix[0], state.Mode7.Matrix[1], state.Mode7.Matrix[2], state.Mode7.Matrix[3] }
						}
					};
				} catch {
					return null;
				}
			});
		}

		private static List<LiveApiLayerState> BuildLayerList(SnesPpuState state)
		{
			List<LiveApiLayerState> list = new();
			for(int i = 0; i < 4; i++) {
				list.Add(new LiveApiLayerState() {
					TilemapAddress = state.Layers[i].TilemapAddress,
					ChrAddress = state.Layers[i].ChrAddress,
					LargeTiles = state.Layers[i].LargeTiles
				});
			}
			return list;
		}

		public static LiveApiTraceRow[] GetTrace(string cpuType, UInt32 count)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return Array.Empty<LiveApiTraceRow>();
			}
			return RunExclusive(() => {
				try {
					UInt32 size = DebugApi.GetExecutionTraceSize();
					UInt32 start = size > count ? size - count : 0;
					TraceRow[] rows = DebugApi.GetExecutionTrace(start, count);
					List<LiveApiTraceRow> result = new List<LiveApiTraceRow>();
					foreach(TraceRow row in rows) {
						result.Add(new LiveApiTraceRow() {
							PC = row.ProgramCounter,
							Cpu = row.Type.ToString(),
							ByteCode = row.GetByteCodeStr(),
							Output = row.GetOutput()
						});
					}
					return result.ToArray();
				} catch {
					return Array.Empty<LiveApiTraceRow>();
				}
			});
		}

		/// <summary>
		/// Enables/disables the trace logger for a CPU (so /api/trace returns data).
		/// </summary>
		public static bool SetTraceEnabled(string cpuType, bool enabled)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return false;
			}
			return RunExclusive(() => {
				try {
					DebugApi.SetTraceOptions(cpu.Value, new InteropTraceLoggerOptions() {
						Enabled = enabled,
						IndentCode = false,
						UseLabels = false,
						Condition = new byte[1000],
						Format = new byte[1000]
					});
					if(!enabled) {
						DebugApi.ClearExecutionTrace();
					}
					return true;
				} catch {
					return false;
				}
			});
		}

		/// <summary>
		/// DMA channel state (registers $4300-$437F + $420B/$420C): source, target, length, mode per channel.
		/// </summary>
		public static JsonNode? GetDmaState(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => GetDmaStateInternal(cpu.Value));
		}

		private static JsonNode? GetDmaStateInternal(CpuType cpu)
		{
			try {
				byte[] regs = DebugApi.GetMemoryValues(MemoryType.SnesRegister, 0x4300, 0x437F);
				byte mdmaen = DebugApi.GetMemoryValues(MemoryType.SnesRegister, 0x420B, 0x420B)[0];
				byte hdmaen = DebugApi.GetMemoryValues(MemoryType.SnesRegister, 0x420C, 0x420C)[0];

				JsonArray channels = new JsonArray();
				for(int ch = 0; ch < 8; ch++) {
					int off = ch * 16;
					byte dmap = regs[off + 0];
					byte bbad = regs[off + 1];
					UInt16 sourceAddr = (UInt16)(regs[off + 2] | (regs[off + 3] << 8));
					byte sourceBank = regs[off + 4];
					UInt16 size = (UInt16)(regs[off + 5] | (regs[off + 6] << 8));
					byte hdmaBank = regs[off + 7];
					byte hdmaAddr = regs[off + 8];
					byte hdmaAddrHi = regs[off + 9];

					bool isHdma = (dmap & 0x80) != 0;
					bool toCpu = (dmap & 0x01) != 0; //0 = CPU->Peripherie, 1 = Peripherie->CPU
					int mode = (dmap >> 1) & 0x07;
					bool enabled = ((isHdma ? hdmaen : mdmaen) & (1 << ch)) != 0;

					channels.Add((JsonNode)(new JsonObject() {
						["channel"] = ch,
						["enabled"] = enabled,
						["hdma"] = isHdma,
						["toCpu"] = toCpu,
						["direction"] = toCpu ? "Peripherie->CPU" : "CPU->Peripherie",
						["mode"] = mode,
						["modeText"] = GetDmaModeText(mode),
						["destAddr"] = bbad,
						["destName"] = GetDmaDestName(bbad),
						["source"] = $"${sourceBank:X2}:{sourceAddr:X4}",
						["sourceBank"] = sourceBank,
						["sourceAddr"] = sourceAddr,
						["length"] = size,
						["hdmaBank"] = hdmaBank,
						["hdmaAddr"] = (UInt16)(hdmaAddr | (hdmaAddrHi << 8))
					}));
				}

				return new JsonObject() {
					["mdmaen"] = mdmaen,
					["hdmaen"] = hdmaen,
					["channels"] = channels
				};
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Atomic snapshot of all map-scanning data in a single gate (consistent frame):
		/// Scroll, layers, nametable (BG2 0x3800), CGRAM, VRAM tiles, DMA state.
		/// </summary>
		public static JsonNode? GetSnapshot(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesPpuState ppu = DebugApi.GetPpuState<SnesPpuState>(cpu.Value);
					byte[] vram = DebugApi.GetMemoryState(MemoryType.SnesVideoRam);
					byte[] cgram = DebugApi.GetMemoryState(MemoryType.SnesCgRam);
					bool paused = EmuApi.IsRunning() && EmuApi.IsPaused();

					JsonArray layers = new JsonArray();
					for(int i = 0; i < 4; i++) {
						LayerConfig layer = ppu.Layers[i];
						layers.Add((JsonNode)(new JsonObject() {
							["index"] = i,
							["name"] = "BG" + (i + 1),
							["tilemapAddress"] = layer.TilemapAddress,
							["chrAddress"] = layer.ChrAddress,
							["hScroll"] = layer.HScroll,
							["vScroll"] = layer.VScroll,
							["doubleWidth"] = layer.DoubleWidth,
							["doubleHeight"] = layer.DoubleHeight
						}));
					}

					return new JsonObject() {
						["frame"] = Interlocked.Read(ref _frameCount),
						["paused"] = paused,
						["bgMode"] = ppu.BgMode,
						["mainScreenLayers"] = ppu.MainScreenLayers,
						["subScreenLayers"] = ppu.SubScreenLayers,
						["scroll"] = new JsonObject() {
							["bg1H"] = ppu.Layers[0].HScroll,
							["bg1V"] = ppu.Layers[0].VScroll,
							["bg2H"] = ppu.Layers[1].HScroll,
							["bg2V"] = ppu.Layers[1].VScroll,
							["bg3H"] = ppu.Layers[2].HScroll,
							["bg3V"] = ppu.Layers[2].VScroll,
							["bg4H"] = ppu.Layers[3].HScroll,
							["bg4V"] = ppu.Layers[3].VScroll
						},
						["layers"] = layers,
						["nametable"] = new JsonObject() {
							["type"] = "SnesVideoRam",
							["start"] = 0x3800,
							["length"] = 0x800,
							["data"] = ToHex(vram, 0x3800, 0x800)
						},
						["cgram"] = new JsonObject() {
							["start"] = 0,
							["length"] = cgram.Length,
							["data"] = ToHex(cgram, 0, cgram.Length)
						},
						["vramTiles"] = new JsonObject() {
							["start"] = 0,
							["length"] = Math.Min(vram.Length, 0x4000),
							["data"] = ToHex(vram, 0, Math.Min(vram.Length, 0x4000))
						},
						["dma"] = GetDmaStateInternal(cpu.Value)
					};
				} catch {
					return null;
				}
			});
		}

		public static string ToHex(byte[] data, int offset, int length)
		{
			length = Math.Min(length, data.Length - offset);
			StringBuilder sb = new StringBuilder(length * 2);
			for(int i = 0; i < length; i++) {
				sb.Append(data[offset + i].ToString("X2"));
			}
			return sb.ToString();
		}

		/// <summary>
		/// Cumulative DMA log (requirement P1.1): ring buffer holding every DMA/HDMA block.
		/// count = max. entries, since = incremental fetch (the index from which entries are returned).
		/// </summary>
		public static JsonNode? GetDmaLog(string cpuType, int count, UInt32 since)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => {
				try {
					UInt32 total = DebugApi.SnesGetDmaLogCount();
					if(total == 0 || since >= total || count <= 0) {
						return new JsonObject() { ["count"] = total, ["entries"] = new JsonArray() };
					}

					UInt32 start = since;
					UInt32 n = Math.Min((UInt32)count, total - start);
					DebugApi.InteropDmaLogEntry[] entries = new DebugApi.InteropDmaLogEntry[n];
					UInt32 got = DebugApi.SnesGetDmaLog(entries, start, n);

					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropDmaLogEntry e = entries[i];
						arr.Add((JsonNode)(new JsonObject() {
							["frame"] = e.frame,
							["cycle"] = e.cycle,
							["channel"] = e.channel,
							["isHdma"] = e.isHdma != 0,
							["toCpu"] = e.toCpu != 0,
							["mode"] = e.mode,
							["source"] = $"${e.sourceBank:X2}:{e.sourceAddr:X4}",
							["sourceBank"] = e.sourceBank,
							["sourceAddr"] = e.sourceAddr,
							["destAddr"] = e.destAddr,
							["destName"] = GetDmaDestName((byte)e.destAddr),
							["vramAddr"] = e.vramAddr,
							["length"] = e.length
						}));
					}

					return new JsonObject() { ["count"] = total, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R2.1: Cumulative event history (ring buffer). Automatically enabled on first call.
		/// </summary>
		public static JsonNode? GetEventHistory(string cpuType, int count, UInt64 since, string? typeFilter)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => {
				try {
					DebugApi.InitializeDebugger();
					DebugApi.SnesSetEventLogEnabled(true);
					UInt32 total = DebugApi.SnesGetEventLogCount();
					if(total == 0 || count <= 0) {
						return new JsonObject() { ["count"] = total, ["entries"] = new JsonArray() };
					}

					UInt32 n = Math.Min((UInt32)count, total);
					DebugApi.InteropEventLogEntry[] entries = new DebugApi.InteropEventLogEntry[n];
					UInt32 got = DebugApi.SnesGetEventLogSince(entries, since, n);

					DebugEventType? filter = null;
					if(!String.IsNullOrEmpty(typeFilter)) {
						filter = Enum.TryParse<DebugEventType>(typeFilter, true, out DebugEventType t) ? t : null;
					}

					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropEventLogEntry e = entries[i];
						DebugEventType type = (DebugEventType)e.type;
						if(filter != null && type != filter.Value) {
							continue;
						}
						arr.Add((JsonNode)(new JsonObject() {
							["id"] = e.id,
							["frame"] = e.frame,
							["cycle"] = e.cycle,
							["scanline"] = e.scanline,
							["type"] = type.ToString(),
							["pc"] = e.pc,
							["breakpointId"] = e.breakpointId,
							["dmaChannel"] = e.dmaChannel,
							["operation"] = new JsonObject() {
								["address"] = e.opAddress,
								["value"] = e.opValue,
								["type"] = ((MemoryOperationType)e.opType).ToString(),
								["memType"] = ((MemoryType)e.opMemType).ToString()
							}
						}));
					}

					return new JsonObject() { ["count"] = total, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R2.2: VRAM write history (CPU writes to 0x2118/0x2119) with PC and target address.
		/// </summary>
		public static JsonNode? GetVramWrites(string cpuType, int count, UInt32 since)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => {
				try {
					DebugApi.InitializeDebugger();
					DebugApi.SnesSetVramLogEnabled(true);
					UInt32 total = DebugApi.SnesGetVramLogCount();
					if(total == 0 || since >= total || count <= 0) {
						return new JsonObject() { ["count"] = total, ["entries"] = new JsonArray() };
					}

					UInt32 start = since;
					UInt32 n = Math.Min((UInt32)count, total - start);
					DebugApi.InteropVramLogEntry[] entries = new DebugApi.InteropVramLogEntry[n];
					UInt32 got = DebugApi.SnesGetVramLog(entries, start, n);

					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropVramLogEntry e = entries[i];
						arr.Add((JsonNode)(new JsonObject() {
							["type"] = "VramWrite",
							["frame"] = e.frame,
							["cycle"] = e.cycle,
							["pc"] = e.pc,
							["scanline"] = e.scanline,
							["isDma"] = e.isDma != 0,
							["value"] = e.value,
							["vramAddr"] = e.vramAddr
						}));
					}

					return new JsonObject() { ["count"] = total, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Map-load source trace. Arms the capture of every VRAM/CGRAM write
		/// (CPU or DMA) together with the ROM/WRAM address the data was read from.
		/// After a map change, GetMapLoadReport() groups the sources into
		/// tilemap / tiles / palette with their exact ROM addresses.
		/// </summary>
		public static JsonNode? MapLoadArm()
		{
			return RunExclusive(() => {
				try {
					DebugApi.InitializeDebugger();
					DebugApi.SnesMapLoadLogSetEnabled(true);
					return new JsonObject() { ["ok"] = true };
				} catch {
					return null;
				}
			});
		}

		public static JsonNode? MapLoadStatus()
		{
			return RunExclusive(() => {
				try {
					return new JsonObject() { ["enabled"] = DebugApi.SnesMapLoadLogIsEnabled(), ["autoStopped"] = DebugApi.SnesMapLoadLogIsAutoStopped(), ["count"] = DebugApi.SnesMapLoadLogGetCount() };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Auto-capture - the map-load log always runs in the background and
		/// automatically detects a VRAM load burst (a map change). The report then shows
		/// exactly that burst, without needing to manually arm before the map change.
		/// </summary>
		public static JsonNode? MapLoadAutoCapture()
		{
			return RunExclusive(() => {
				try {
					DebugApi.InitializeDebugger();
					DebugApi.SnesMapLoadLogSetEnabled(true);
					DebugApi.SnesMapLoadLogSetAutoCapture(true);
					DebugApi.SnesMapLoadLogClearAutoBurst();
					return new JsonObject() { ["ok"] = true, ["autoCapture"] = DebugApi.SnesMapLoadLogIsAutoCapture(), ["count"] = DebugApi.SnesMapLoadLogGetCount() };
				} catch {
					return null;
				}
			});
		}

		public static JsonNode? MapLoadAutoCaptureStatus()
		{
			return RunExclusive(() => {
				try {
					return new JsonObject() {
						["enabled"] = DebugApi.SnesMapLoadLogIsEnabled(),
						["autoCapture"] = DebugApi.SnesMapLoadLogIsAutoCapture(),
						["autoStopped"] = DebugApi.SnesMapLoadLogIsAutoStopped(),
						["hasBurst"] = DebugApi.SnesMapLoadLogHasAutoBurst(),
						["burstFrame"] = DebugApi.SnesMapLoadLogGetAutoBurstFrame(),
						["burstEnd"] = DebugApi.SnesMapLoadLogGetAutoBurstEndFrame(),
						["count"] = DebugApi.SnesMapLoadLogGetCount()
					};
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Debug - show the RAW DMA hardware source values (SrcBank:SrcAddress) the
		/// game programmed for each DMA to VRAM/CGRAM/WRAM, before linear mapping. This shows
		/// what the game really writes into the DMA registers (e.g. $44:A957 vs $73:EED4).
		/// </summary>
		public static JsonNode? MapLoadDmaSrc()
		{
			return RunExclusive(() => {
				try {
					UInt32 total = DebugApi.SnesMapLoadDmaSrcCount();
					UInt32 n = Math.Min(total, 512u);
					DebugApi.InteropDmaSrcEntry[] entries = new DebugApi.InteropDmaSrcEntry[n];
					UInt32 got = DebugApi.SnesMapLoadDmaSrc(entries, 0, n);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropDmaSrcEntry e = entries[i];
						string dest = e.destAddr == 0x18 || e.destAddr == 0x19 ? "VRAM" : (e.destAddr == 0x22 ? "CGRAM" : (e.destAddr == 0x80 ? "WRAM" : "0x" + e.destAddr.ToString("X2")));
						arr.Add((JsonNode)new JsonObject() {
							["frame"] = e.frame,
							["srcBus"] = "0x" + e.srcBus.ToString("X6"),
							["dest"] = dest,
							["ch"] = e.channel
						});
					}
					return new JsonObject() { ["count"] = total, ["first"] = DebugApi.SnesMapLoadDmaSrcFirst(), ["shown"] = got, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		private static UInt32 ParseUIntHex(string text)
		{
			return ParseUInt(text, out UInt32 v) ? v : 0;
		}

		/// <summary>
		/// R3.2: REVERSE LOOKUP - for a target range (CGRAM palette / VRAM tilemap / VRAM
		/// tiles), return the ROM file offsets that filled it. This is the user's
		/// "rückwärtssuche": we know where the palette sits in CGRAM, so we look up directly
		/// which ROM offset each CGRAM/VRAM word came from - no forward LastRomRead tracing.
		/// </summary>
		public static JsonNode? MapLoadTargetRom(string targetName, string addrHex, string wordsHex)
		{
			return RunExclusive(() => {
				try {
					byte targetType = targetName.Trim().ToLowerInvariant() == "vram" ? (byte)0 : (byte)1;
					UInt32 addr = ParseUIntHex(addrHex);
					UInt32 words = ParseUIntHex(wordsHex);
					if(words == 0 || words > 0x8000) {
						words = 0x100;
					}
					const int max = 64;
					UInt32[] starts = new UInt32[max];
					UInt32[] wordCounts = new UInt32[max];
					UInt32 got = DebugApi.SnesMapLoadTargetRomSources(targetType, addr, words, starts, wordCounts, max);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						arr.Add((JsonNode)new JsonObject() {
							["rom"] = "0x" + starts[i].ToString("X6"),
							["words"] = wordCounts[i],
							["bytes"] = wordCounts[i] * 2,
							["isPalette"] = IsPaletteData(starts[i])
						});
					}
					return new JsonObject() { ["target"] = targetName, ["addr"] = "0x" + addr.ToString("X4"), ["words"] = words, ["shown"] = arr.Count, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: THE definitive reverse lookup for the palette - compare the ACTUAL CGRAM
		/// content against the ROM bytes at every candidate source address (from the reverse
		/// lookup table AND the ROM-read bitmap). The ROM address whose bytes best match the
		/// CGRAM content IS the palette source - no WRAM/LastRomRead tracing needed.
		/// </summary>
		public static JsonNode? MapLoadPaletteMatch()
		{
			return RunExclusive(() => {
				try {
					byte[] cgram = DebugApi.GetMemoryState(MemoryType.SnesCgRam);
					if(cgram == null || cgram.Length < 32) {
						return new JsonObject() { ["error"] = "kein CGRAM-Inhalt" };
					}
					//Candidates: reverse-lookup CGRAM sources + a sample of the read bitmap.
					HashSet<UInt32> cands = new HashSet<UInt32>();
					{
						const int max = 64;
						UInt32[] starts = new UInt32[max];
						UInt32[] words = new UInt32[max];
						UInt32 got = DebugApi.SnesMapLoadTargetRomSources(1, 0, 0x100, starts, words, max);
						for(int i = 0; i < (int)got; i++) {
							cands.Add(starts[i]);
						}
					}
					//R3.2: THE key candidates - scan the whole ROM-read bitmap for reads that
					//happen to be palette-shaped (0 hi-bits), then match against CGRAM content.
					//This is the content-based reverse search the user described: we know the
					//palette sits in CGRAM, so we find the ROM offset whose bytes equal it.
					JsonArray arr = new JsonArray();
					//brute-force scan of all ROM-read addresses (bitmap) for palette matches
					{
						UInt32 bestAddr = 0;
						int bestMatch = 0;
						for(UInt32 off = 0; off < 0x400000; off++) {
							if(!DebugApi.SnesRomWasRead(off)) {
								continue;
							}
							if(off + 32 > 0x400000) {
								continue;
							}
							byte[] rom = DebugApi.GetMemoryValues(MemoryType.SnesPrgRom, off, off + 31);
							if(rom == null || rom.Length < 32) {
								continue;
							}
							//palette-shape check: no 16-bit word with bit 15 set in first 8 words
							bool shape = true;
							for(int i = 0; i < 8; i++) {
								UInt16 w = (UInt16)(rom[i * 2] | (rom[i * 2 + 1] << 8));
								if((w & 0x8000) != 0) {
									shape = false;
									break;
								}
							}
							if(!shape) {
								continue;
							}
							//test several byte offsets (the first 1-2 colors can be animated
							//in CGRAM, so start the comparison a few bytes in)
							int best = 0;
							for(int startOff = 0; startOff < 6; startOff++) {
								int match = 0;
								for(int i = 0; i < 32 - startOff; i++) {
									if(rom[i + startOff] == cgram[i]) {
										match++;
									}
								}
								if(match > best) {
									best = match;
								}
							}
							if(best > bestMatch) {
								bestMatch = best;
								bestAddr = off;
							}
						}
						if(bestAddr != 0) {
							arr.Add((JsonNode)new JsonObject() {
								["cand"] = "0x" + bestAddr.ToString("X6"),
								["matchBytes"] = bestMatch,
								["matchRatio"] = Math.Round(bestMatch / 32.0, 2),
								["best"] = true
							});
						}
					}
					foreach(UInt32 cand in cands) {
						if(cand == 0 || cand == 0xFFFFFFFF || cand + 32 > 0x400000) {
							continue;
						}
						byte[] rom = DebugApi.GetMemoryValues(MemoryType.SnesPrgRom, cand, cand + 31);
						if(rom == null || rom.Length < 32) {
							continue;
						}
						int match = 0;
						for(int i = 0; i < 32; i++) {
							if(rom[i] == cgram[i]) {
								match++;
							}
						}
						double ratio = match / 32.0;
						arr.Add((JsonNode)new JsonObject() {
							["cand"] = "0x" + cand.ToString("X6"),
							["matchBytes"] = match,
							["matchRatio"] = Math.Round(ratio, 2)
						});
					}
					//Sort by match ratio, best first (manual, JsonArray is not LINQ-friendly here)
					JsonArray sorted = new JsonArray();
					List<(double ratio, string cand, int match, double ratioVal)> list = new();
					foreach(JsonNode? n in arr) {
						string cand = n?["cand"]?.ToString() ?? "";
						int match = 0;
						if(n != null && n["matchBytes"] is JsonValue jv) {
							jv.TryGetValue<int>(out match);
						}
						double r = 0;
						if(n != null && n["matchRatio"] is JsonValue jv2) {
							jv2.TryGetValue<double>(out r);
						}
						if(n != null && n["best"] is JsonValue bv && bv.TryGetValue<bool>(out bool isBest) && isBest) {
							r += 100;
						}
						list.Add((r, cand, match, r >= 100 ? r - 100 : r));
					}
					list.Sort((a, b) => b.ratio.CompareTo(a.ratio));
					foreach((double ratio, string cand, int match, double ratioVal) in list) {
						sorted.Add((JsonNode)new JsonObject() {
							["cand"] = cand,
							["matchBytes"] = match,
							["matchRatio"] = ratioVal
						});
					}
					return new JsonObject() { ["cgramBytes"] = BitConverter.ToString(cgram, 0, 32), ["candidates"] = sorted };
				} catch(Exception ex) {
					return new JsonObject() { ["error"] = ex.Message, ["stack"] = ex.StackTrace?.Split('\n')[0] };
				}
			});
		}

		/// <summary>
		/// R3.2: Reverse-search for decompressed data - show the contiguous ROM reads that
		/// happened in a frame window. A decompression routine reads a ROM block and writes
		/// decompressed data to WRAM; the ROM block read around the DMA frame is the source.
		/// </summary>
		public static JsonNode? MapLoadWramSource(string frameHex)
		{
			return RunExclusive(() => {
				try {
					UInt64 frame = ParseUInt(frameHex, out UInt32 f) ? f : 0;
					const int max = 128;
					UInt32[] starts = new UInt32[max];
					UInt32[] lens = new UInt32[max];
					//window: 20 frames before the given frame (or whole capture if frame==0)
					UInt64 to = frame == 0 ? ulong.MaxValue : frame;
					UInt64 from = frame == 0 ? 0 : (frame > 30 ? frame - 30 : 0);
					UInt32 got = DebugApi.SnesMapLoadRomReadsInFrames(from, to, starts, lens, max);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						arr.Add((JsonNode)new JsonObject() {
							["rom"] = "0x" + starts[i].ToString("X6"),
							["bytes"] = lens[i],
							["end"] = "0x" + (starts[i] + lens[i] - 1).ToString("X6"),
							["isPalette"] = IsPaletteData(starts[i])
						});
					}
					return new JsonObject() { ["frame"] = frame, ["ringCount"] = DebugApi.SnesMapLoadRomReadRingCount(), ["count"] = got, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Dump the WramRomByte table for a WRAM region to see which ROM sources
		/// were recorded per WRAM byte (the reverse-search basis for WRAM->VRAM/CGRAM DMAs).
		/// </summary>
		public static JsonNode? MapLoadWramMap()
		{
			return RunExclusive(() => {
				try {
					JsonArray arr = new JsonArray();
					for(UInt32 a = 0x10000; a < 0x11000; a += 0x10) {
						UInt32 rom = DebugApi.SnesGetWramRomSource(a);
						arr.Add((JsonNode)new JsonObject() {
							["wram"] = "0x" + a.ToString("X5"),
							["rom"] = rom != 0xFFFFFFFF ? "0x" + rom.ToString("X6") : "(leer)"
						});
					}
					return new JsonObject() { ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Debug - CPU writes to WRAM with the ROM source (LastRomRead) at write time.
		/// Shows how the palette copy-loop fills WRAM (e.g. ROM 0x33EED4 -> WRAM 0x10600).
		/// </summary>
		public static JsonNode? MapLoadWramWrite(string addrLoHex, string addrHiHex)
		{
			return RunExclusive(() => {
				try {
					UInt32 lo = Convert.ToUInt32(addrLoHex.Trim().TrimStart('0', 'x', 'X'), 16);
					UInt32 hi = Convert.ToUInt32(addrHiHex.Trim().TrimStart('0', 'x', 'X'), 16);
					UInt32 total = DebugApi.SnesMapLoadWramWriteCount();
					UInt32 n = Math.Min(total, 512u);
					DebugApi.InteropWramWriteEntry[] entries = new DebugApi.InteropWramWriteEntry[n];
					UInt32 got = DebugApi.SnesMapLoadWramWrite(entries, 0, n);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropWramWriteEntry e = entries[i];
						if(e.wramAddr >= lo && e.wramAddr <= hi) {
							arr.Add((JsonNode)new JsonObject() {
								["wram"] = "0x" + e.wramAddr.ToString("X5"),
								["rom"] = e.romRead != 0xFFFFFFFF ? "0x" + e.romRead.ToString("X6") : "(kein ROM-Read)",
								["mem"] = e.memType
							});
						}
					}
					return new JsonObject() { ["count"] = total, ["shown"] = arr.Count, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Raw map-load trace - every DMA/write to VRAM/CGRAM with its true source.
		/// Shows whether the data came directly from ROM or via WRAM (decompression),
		/// so the exact ROM addresses for palette/tiles/tilemap can be identified.
		/// </summary>
		public static JsonNode? MapLoadRaw(int count)
		{
			return RunExclusive(() => {
				try {
					UInt32 total = DebugApi.SnesMapLoadLogGetCount();
					UInt32 n = Math.Min((UInt32)Math.Max(count, 1), Math.Min(total, 1u << 21));
					DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[n];
					UInt32 got = DebugApi.SnesGetMapLoadLog(entries, 0, n);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropMapLoadEntry e = entries[i];
						bool isWram = e.sourceType == 0 ? (e.sourceMem == 1) : (e.sourceMem == (byte)MemoryType.SnesWorkRam);
						string source = isWram
							? "WRAM 0x" + (e.sourceAddr & 0x1FFFF).ToString("X5") + " (nicht auf ROM aufgelöst)"
							: "ROM 0x" + e.sourceAddr.ToString("X6");
						string destHex = e.sourceType == 0 ? ("0x" + e.value.ToString("X2")) : "";
						string rawBus = e.sourceType == 0 && e.pc != 0 ? ("bus=0x" + e.pc.ToString("X6")) : "";
						arr.Add((JsonNode)new JsonObject() {
							["frame"] = e.frame,
							["src"] = source,
							["rawBus"] = rawBus,
							["destReg"] = destHex,
							["via"] = e.sourceType == 0 ? "dma ch" + e.channel : (e.sourceMem == (byte)MemoryType.SnesWorkRam ? "cpu wram" : "cpu rom"),
							["target"] = e.targetType == 1 ? "CGRAM" : "VRAM",
							["taddr"] = e.targetAddr,
							["len"] = e.length
						});
					}
					return new JsonObject() { ["count"] = total, ["shown"] = got, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Show CPU writes to CGRAM with their ROM source (LastRomRead at write time).
		/// This reveals the palette copy-loop (LDA $ROM,Y : STA $2122) that loads the
		/// map palette into CGRAM without DMA.
		/// </summary>
		public static JsonNode? MapLoadCpuCgram()
		{
			return RunExclusive(() => {
				try {
					UInt32 total = DebugApi.SnesMapLoadLogGetCount();
					UInt32 n = Math.Min(total, 1u << 21);
					DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[n];
					UInt32 got = DebugApi.SnesGetMapLoadLog(entries, 0, n);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropMapLoadEntry e = entries[i];
						if(e.targetType == 1 && e.sourceType == 1) {
							arr.Add((JsonNode)new JsonObject() {
								["src"] = e.sourceMem == (byte)MemoryType.SnesPrgRom ? "ROM 0x" + e.sourceAddr.ToString("X6") : "WRAM/?? 0x" + e.sourceAddr.ToString("X6"),
								["taddr"] = e.targetAddr,
								["len"] = e.length,
								["pc"] = "0x" + e.pc.ToString("X6")
							});
						}
					}
					return new JsonObject() { ["shown"] = arr.Count, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		private static UInt32[] ParseAddrList(string csv)
		{
			List<UInt32> list = new List<UInt32>();
			foreach(string part in csv.Split(',')) {
				string t = part.Trim();
				if(t.Length == 0) {
					continue;
				}
				try {
					if(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
						list.Add(Convert.ToUInt32(t.Substring(2), 16));
					} else {
						list.Add(Convert.ToUInt32(t, 16));
					}
				} catch {
					//skip invalid entries
				}
			}
			return list.ToArray();
		}

		/// <summary>
		/// R3.2: Debug - show the raw entries of the current auto-capture burst range,
		/// so we can see whether the burst actually contains the map load or only fades.
		/// </summary>
		public static JsonNode? MapLoadRawBurst()
		{
			return RunExclusive(() => {
				try {
					if(!DebugApi.SnesMapLoadLogHasAutoBurst()) {
						return new JsonObject() { ["hasBurst"] = false };
					}
					UInt64 start = DebugApi.SnesMapLoadLogGetAutoBurstFrame();
					UInt64 end = DebugApi.SnesMapLoadLogGetAutoBurstEndFrame();
					const int max = 200;
					DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[max];
					UInt32 got = DebugApi.SnesGetMapLoadLogFromFrame(entries, start, end, max);
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropMapLoadEntry e = entries[i];
						string src = e.sourceMem == 1 ? "WRAM" : "ROM";
						arr.Add((JsonNode)new JsonObject() {
							["frame"] = e.frame,
							["src"] = src,
							["srcAddr"] = "0x" + e.sourceAddr.ToString("X6"),
							["via"] = e.sourceType == 0 ? "dma" : "cpu",
							["target"] = e.targetType == 1 ? "CGRAM" : "VRAM",
							["taddr"] = e.targetAddr,
							["len"] = e.length
						});
					}
					return new JsonObject() { ["hasBurst"] = true, ["start"] = start, ["end"] = end, ["shown"] = got, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: For known-good ROM offsets, show whether they appear anywhere in the trace:
		/// as a DMA source, as a WRAM chain source, or as a tracked ROM read. This tells us
		/// whether the load path for each asset is even being captured.
		/// </summary>
		public static JsonNode? MapLoadProbe(string addrsCsv)
		{
			return RunExclusive(() => {
				try {
					UInt32[] addrs = ParseAddrList(addrsCsv);
					JsonArray arr = new JsonArray();
					foreach(UInt32 a in addrs) {
						JsonObject o = new JsonObject();
						o["addr"] = "0x" + a.ToString("X6");
						o["dma"] = DebugApi.SnesMapLoadDmaHasSource(a);
						o["romRead"] = DebugApi.SnesRomWasRead(a);
						o["wramChain"] = DebugApi.SnesMapLoadWramChainTarget(a);
						arr.Add((JsonNode)o);
					}
					return new JsonObject() { ["results"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: WRAM->ROM chain diagnostics - for a WRAM address, shows the ROM source
		/// that was recorded when the CPU wrote to this WRAM address (the deterministic
		/// data-source chain ROM -> WRAM -> CGRAM/VRAM).
		/// </summary>
		public static JsonNode? MapLoadWramChain(string addrHex)
		{
			return RunExclusive(() => {
				try {
					UInt32 addr = 0;
					addrHex = addrHex.Trim();
					if(addrHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
						addr = Convert.ToUInt32(addrHex.Substring(2), 16);
					} else {
						addr = Convert.ToUInt32(addrHex, 16);
					}
					JsonArray results = new JsonArray();
					for(UInt32 page = addr & 0xFF00; page < (addr & 0xFF00) + 0x100; page += 0x100) {
						UInt32 rom = DebugApi.SnesGetWramRomSource(page);
						results.Add((JsonNode)new JsonObject() {
							["wram"] = "0x" + page.ToString("X5"),
							["rom"] = rom != 0xFFFFFFFF ? "0x" + rom.ToString("X6") : "(leer)"
						});
					}
					return new JsonObject() { ["results"] = results };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Check whether specific ROM file offsets were actually read during the
		/// current capture. Addresses are passed as comma-separated hex ("0x33EED4,0x1FC1E6").
		/// The emulator sees every data read from the ROM; the bitmap proves whether a
		/// suspected map-data address (palette/tiles/tilemap) was touched during THIS capture.
		/// </summary>
		public static JsonNode? MapLoadVerify(string addrs)
		{
			return RunExclusive(() => {
				try {
					JsonArray results = new JsonArray();
					string[] parts = addrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if(parts.Length == 0) {
						return new JsonObject() { ["results"] = results, ["hint"] = "addr=0x...,0x..." };
					}
					foreach(string p in parts) {
						if(!ParseUInt(p, out UInt32 addr)) {
							continue;
						}
						bool hit = DebugApi.SnesRomWasRead(addr);
						UInt32 s, e;
						DebugApi.SnesGetRomReadRange(addr, 0x80, out s, out e);
						results.Add((JsonNode)new JsonObject() {
							["addr"] = "0x" + addr.ToString("X6"),
							["read"] = hit,
							["range"] = hit ? "0x" + s.ToString("X6") + "-0x" + e.ToString("X6") : ""
						});
					}
					return new JsonObject() { ["results"] = results };
				} catch {
					return null;
				}
			});
		}

		private static bool ParseUInt(string text, out UInt32 value)
		{
			value = 0;
			text = text.Trim();
			try {
				if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					value = Convert.ToUInt32(text.Substring(2), 16);
				} else {
					value = Convert.ToUInt32(text, 16);
				}
				return true;
			} catch {
				return false;
			}
		}

		public static JsonNode? MapLoadDisarm()
		{
			return RunExclusive(() => {
				try {
					DebugApi.SnesMapLoadLogSetEnabled(false);
					return new JsonObject() { ["ok"] = true };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: Structural validation - are the ROM bytes at this address real 15-bit SNES
		/// palette data? A palette's 16-bit color words never have bit 15 set (SNES colors
		/// are 15-bit: 0b0RRRRRGGGGGBBBBB). Code/compressed data fails this check. This is
		/// NOT filtering by known values - it's a property of the data itself, so it works
		/// on unknown maps too. Returns true if most of the first 16 words are palette-safe.
		/// </summary>
		private static bool IsPaletteData(UInt32 romOffset)
		{
			try {
				if(romOffset + 32 > 0x400000) {
					return false;
				}
				byte[] data = DebugApi.GetMemoryValues(MemoryType.SnesPrgRom, romOffset, romOffset + 31);
				if(data.Length < 32) {
					return false;
				}
				int ok = 0;
				for(int i = 0; i < 16; i++) {
					UInt16 w = (UInt16)(data[i * 2] | (data[i * 2 + 1] << 8));
					if((w & 0x8000) == 0) {
						ok++;
					}
				}
				//A real SNES palette has NO 16-bit word with bit 15 set over the whole range.
				//Code/compressed data shows 5-9+ words with the high bit set (e.g. 0x04A957
				//= 9/16, 0x300003 = 7/16). A fade-effect buffer can be palette-shaped too,
				//but the auto-capture already excludes continuous fades - this filter only
				//rejects non-palette ROM data. Allow at most 1 word with bit 15 set.
				return ok >= 15;
			} catch {
				return false;
			}
		}

		/// <summary>
		/// R3.2: Content-based reverse search for the palette - scan all ROM addresses that
		/// were actually read during the capture, and find the one whose bytes best match the
		/// current CGRAM content. This is the user's "ich weiss wo die palette liegt und suche
		/// rückwärts": no LastRomRead / WRAM tracing, purely content comparison. Returns
		/// (romAddr, matchBytes, bestRatio) or (0xFFFFFFFF, 0, 0) if nothing matched.
		/// </summary>
		private static (UInt32 addr, int match, double ratio) FindBestPaletteMatch(byte[] cgram)
		{
			UInt32 bestAddr = 0xFFFFFFFF;
			int bestMatch = 0;
			for(UInt32 off = 0; off < 0x400000; off++) {
				if(!DebugApi.SnesRomWasRead(off)) {
					continue;
				}
				if(off + 32 > 0x400000) {
					continue;
				}
				byte[] rom = DebugApi.GetMemoryValues(MemoryType.SnesPrgRom, off, off + 31);
				if(rom == null || rom.Length < 32) {
					continue;
				}
				//palette-shape check: no 16-bit word with bit 15 set in first 8 words
				bool shape = true;
				for(int i = 0; i < 8; i++) {
					UInt16 w = (UInt16)(rom[i * 2] | (rom[i * 2 + 1] << 8));
					if((w & 0x8000) != 0) {
						shape = false;
						break;
					}
				}
				if(!shape) {
					continue;
				}
				//test several byte offsets (first 1-2 colors can be animated in CGRAM)
				int best = 0;
				for(int startOff = 0; startOff < 6; startOff++) {
					int match = 0;
					for(int i = 0; i < 32 - startOff; i++) {
						if(rom[i + startOff] == cgram[i]) {
							match++;
						}
					}
					if(match > best) {
						best = match;
					}
				}
				if(best > bestMatch) {
					bestMatch = best;
					bestAddr = off;
				}
			}
			if(bestAddr == 0xFFFFFFFF) {
				return (0xFFFFFFFF, 0, 0);
			}
			return (bestAddr, bestMatch, Math.Round(bestMatch / 32.0, 2));
		}

		public static JsonNode? GetMapLoadReport(bool compact = false)
		{
			return RunExclusive(() => {
				try {
					SnesPpuState state = DebugApi.GetPpuState<SnesPpuState>(CpuType.Snes);
					UInt32 total = DebugApi.SnesMapLoadLogGetCount();
					//R3.2: auto-capture - the log FREEZES after a detected map-load burst,
					//so it holds exactly the map load. Read the whole (frozen) log, then
					//consume the burst to re-arm for the next map change.
					bool useAutoBurst = DebugApi.SnesMapLoadLogIsAutoCapture() && DebugApi.SnesMapLoadLogHasAutoBurst();

					//R3.2: classify every ROM source by its TARGET in the emulator:
					//  targetType==1 (CGRAM)  -> Palette
					//  targetType==0 (VRAM) in a Layer TilemapAddress range -> Tilemap
					//  targetType==0 (VRAM) in a Layer ChrAddress range     -> Tiles
					//Uses the EXACT layer addresses from the PPU (e.g. Layer 1 tilemap $3800,
					//chr $0000) - NOT a blanket "everything below $4000 is tilemap".
					//This is a 100% verification: the emulator knows where each copy lands.
					Dictionary<string, (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max)> tilemap = new();
					Dictionary<string, (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max)> tiles = new();
					Dictionary<string, (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max)> palette = new();

					//Layer tilemap/chr WORD-address ranges (VRAM log stores word addresses).
					//TilemapAddress/ChrAddress from the PPU are word addresses.
					//Tilemap size: 32x32 = 0x800 bytes = 0x400 words; 64x64 = 0x2000 bytes = 0x1000 words.
					List<(UInt32 Start, UInt32 End)> tilemapRanges = new();
					List<(UInt32 Start, UInt32 End)> chrRanges = new();
					for(int i = 0; i < 4; i++) {
						if(state.Layers[i].TilemapAddress == 0) {
							continue;  //layer not active
						}
						UInt32 baseWord = state.Layers[i].TilemapAddress;
						UInt32 mapWords = state.Layers[i].LargeTiles ? 0x1000u : 0x400u;
						tilemapRanges.Add(((UInt32)baseWord, (UInt32)baseWord + mapWords));
						//ChrAddress may be 0 (tile data at VRAM $0000) - still valid for active layers
						chrRanges.Add(((UInt32)state.Layers[i].ChrAddress, (UInt32)state.Layers[i].ChrAddress + 0x8000));
					}

					//R3.2: PALETTE via WRAM->ROM CHAIN (address-based, works for compressed data).
					//The decompressor writes ROM(compressed)->WRAM 0x10000->WRAM 0x10600, and the
					//CGRAM DMA reads WRAM 0x10600. The WramRomByte chain now propagates the ROM
					//source through WRAM->WRAM copies, so resolving the CGRAM DMA source gives the
					//exact compressed ROM address (e.g. 0x33EED4, 0x349100). No byte-matching.
					//Adjacent WRAM words with a contiguous ROM source are merged into ONE palette
					//block (16 bytes per color entry -> a real palette shows as one continuous run).
					{
						UInt32 wramStart = 0x10600;  //the palette WRAM buffer the CGRAM DMA reads
						UInt32 wramEnd = wramStart + 0x200;
						//collect (wramAddr, romAddr) for all resolved words
						List<(UInt32 wram, UInt32 rom)> resolved = new();
						for(UInt32 a = wramStart; a < wramEnd; a += 0x10) {
							UInt32 rom = DebugApi.SnesGetWramRomSource(a);
							if(rom == 0 || rom == 0xFFFFFFFF) {
								continue;
							}
							resolved.Add((a, rom));
						}
						//merge contiguous runs (romAddr advances by 0x10 per 0x10 WRAM step)
						int idx = 0;
						while(idx < resolved.Count) {
							UInt32 runStartWram = resolved[idx].wram;
							UInt32 runStartRom = resolved[idx].rom;
							int runLen = 1;
							UInt32 expectedWram = runStartWram + 0x10;
							UInt32 expectedRom = runStartRom + 0x10;
							int k = idx + 1;
							while(k < resolved.Count) {
								if(resolved[k].wram == expectedWram && resolved[k].rom == expectedRom) {
									runLen++;
									expectedWram += 0x10;
									expectedRom += 0x10;
									k++;
								} else if(resolved[k].wram < expectedWram) {
									k++;  //duplicate wram - skip
								} else {
									break;
								}
							}
							//structural check: the ROM source must be palette-shaped data
							//(15-bit colors). Code like 0x04A957 fails and is skipped - this is
							//NOT filtering by known values, it validates the data itself.
							if(!IsPaletteData(runStartRom)) {
								idx = k;
								continue;
							}
							string via = "wram-chain";
							string key = "0x" + runStartRom.ToString("X6") + "|" + via;
							UInt32 len = (UInt32)(runLen * 16);
							if(palette.TryGetValue(key, out var old)) {
								old.Length += len;
								old.Writes++;
								palette[key] = old;
							} else {
								palette[key] = ("0x" + runStartRom.ToString("X6"), via, len, 1, runStartWram, runStartWram + len);
							}
							idx = k;
						}
					}

					//R3.2: REVERSE LOOKUP for TILES - query each layer chr range directly.
					{
						const int max = 128;
						UInt32[] starts = new UInt32[max];
						UInt32[] wordsArr = new UInt32[max];
						foreach((UInt32 Start, UInt32 End) r in chrRanges) {
							UInt32 len = r.End - r.Start;
							if(len == 0 || len > 0x8000) {
								len = 0x8000;
							}
							UInt32 got = DebugApi.SnesMapLoadTargetRomSources(0, r.Start, len, starts, wordsArr, max);
							for(int i = 0; i < (int)got; i++) {
								if(starts[i] == 0 || starts[i] == 0xFFFFFFFF) {
									continue;
								}
								string via = "vram-chr";
								string key = "0x" + starts[i].ToString("X6") + "|" + via;
								tiles[key] = ("0x" + starts[i].ToString("X6"), via, wordsArr[i] * 2, 1, r.Start, r.End);
							}
						}
					}

					if(total > 0) {
						UInt32 n = Math.Min(total, 1u << 21);
						DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[n];
						UInt32 got = DebugApi.SnesGetMapLoadLog(entries, 0, n);
						for(int i = 0; i < (int)got; i++) {
							DebugApi.InteropMapLoadEntry e = entries[i];
							//Resolve the source: DMA/CPU from ROM is the exact hardware value.
							//WRAM sources are resolved through the per-byte WramRomByte[] chain
							//(the exact ROM address each WRAM byte was copied from) - this is how
							//the map palette (ROM 0x33EED4) reaches CGRAM: ROM -> WRAM -> CGRAM.
							UInt32 srcRom = 0xFFFFFFFF;
							if(e.sourceType == 0 && e.sourceMem == 0) {
								srcRom = e.sourceAddr;  //direct ROM DMA - exact register value
							} else if(e.sourceType == 1 && e.sourceMem == (byte)MemoryType.SnesPrgRom) {
								srcRom = e.sourceAddr;  //CPU read from ROM - NMI-safe LastRomRead
							} else if(e.sourceMem == 1 || e.sourceMem == (byte)MemoryType.SnesWorkRam || e.sourceMem == (byte)MemoryType.SnesSaveRam) {
								//WRAM source - resolve through the byte-exact ROM source chain.
								//The WRAM addr stored is linear; the ROM source per byte is exact.
								UInt32 wramOff = e.sourceAddr & 0x1FFFF;
								if(wramOff < 0x20000) {
									srcRom = DebugApi.SnesGetWramRomSource(wramOff);
									if(srcRom >= 0x400000) {
										srcRom = 0xFFFFFFFF;  //unresolved
									}
								}
							}
							if(srcRom == 0xFFFFFFFF) {
								continue;  //no exact ROM source (e.g. WRAM written by CPU without known ROM origin)
							}
							string via = e.sourceType == 0
								? (e.sourceMem == 0 ? "dma ch" + e.channel : "dma->wram ch" + e.channel)
								: "cpu rom";
							string key = "0x" + srcRom.ToString("X6") + "|" + via;
							string source = "0x" + srcRom.ToString("X6");

							Dictionary<string, (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max)>? dest = null;
							if(e.targetType == 1) {
								//Palette is covered by the CGRAM reverse-lookup (exact). The
								//forward Entry aggregation would only add stale LastRomRead
								//values - skip it here.
								continue;
							} else {
								//Tiles are covered by the VRAM reverse-lookup per chr range.
								//Only the tilemap area is aggregated from entries.
								bool isTilemap = false;
								foreach((UInt32 Start, UInt32 End) r in tilemapRanges) {
									if(e.targetAddr >= r.Start && e.targetAddr < r.End) {
										isTilemap = true;
										break;
									}
								}
								if(isTilemap) {
									dest = tilemap;
								}
								//else: chr range / unknown - covered by reverse lookup or not map data
							}

							if(dest == null) {
								continue;  //unknown target area - not map/tile/palette data
							}

							if(dest.TryGetValue(key, out (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max) a)) {
								a.Length += e.length;
								a.Writes++;
								if(e.targetAddr < a.Min) a.Min = e.targetAddr;
								if(e.targetAddr > a.Max) a.Max = e.targetAddr;
								dest[key] = a;
							} else {
								dest[key] = (source, via, e.length, 1, e.targetAddr, e.targetAddr);
							}
						}
					}

					JsonArray ToDetail(HashSet<string> keys, Dictionary<string, (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max)> dict)
					{
						//Show the EXACT ROM source addresses - each is the deterministic DMA source
						//value from the emulator (no grouping/smearing of nearby addresses).
						JsonArray arr = new JsonArray();
						List<(UInt32 Addr, UInt32 Length, int Writes, UInt32 Min, UInt32 Max, string Via)> items = new();
						foreach(string k in keys) {
							if(dict.TryGetValue(k, out (string Source, string Via, UInt32 Length, int Writes, UInt32 Min, UInt32 Max) v)) {
								UInt32 addr = Convert.ToUInt32(v.Source.Substring(2), 16);
								items.Add((addr, v.Length, v.Writes, v.Min, v.Max, v.Via));
							}
						}
						items.Sort((a, b) => a.Addr.CompareTo(b.Addr));
						foreach((UInt32 Addr, UInt32 Length, int Writes, UInt32 Min, UInt32 Max, string Via) it in items) {
							arr.Add((JsonNode)new JsonObject() {
								["source"] = "0x" + it.Addr.ToString("X6"),
								["via"] = it.Via,
								["length"] = JsonValue.Create(it.Length),
								["writes"] = it.Writes,
								["targetMin"] = JsonValue.Create(it.Min),
								["targetMax"] = JsonValue.Create(it.Max)
							});
						}
						return arr;
					}

					HashSet<string> tilemapKeys = new(tilemap.Keys);
					HashSet<string> tilesKeys = new(tiles.Keys);
					HashSet<string> paletteKeys = new(palette.Keys);

					//R3.2: ROM file-offset blocks copied to VRAM/CGRAM during this capture -
					//the exact, deterministic source ranges to extract from the ROM file.
					JsonArray romReads = new JsonArray();
					{
						const int max = 512;
						UInt32[] starts = new UInt32[max];
						UInt32[] lengths = new UInt32[max];
						UInt32 count = DebugApi.SnesGetRomTargetBlocks(starts, lengths, (UInt32)max, 0x100);
						for(int i = 0; i < (int)count; i++) {
							romReads.Add((JsonNode)new JsonObject() {
								["start"] = "0x" + starts[i].ToString("X6"),
								["length"] = lengths[i],
								["end"] = "0x" + (starts[i] + lengths[i] - 1).ToString("X6")
							});
						}
					}

					JsonNode result = new JsonObject() {
						["count"] = total,
						["enabled"] = DebugApi.SnesMapLoadLogIsEnabled(),
						["autoStopped"] = DebugApi.SnesMapLoadLogIsAutoStopped(),
						["autoCapture"] = DebugApi.SnesMapLoadLogIsAutoCapture(),
						["autoBurst"] = useAutoBurst,
						["compact"] = compact,
						["bgMode"] = state.BgMode,
						["tilemap"] = compact ? CompactSet(tilemapKeys) : ToDetail(tilemapKeys, tilemap),
						["tiles"] = compact ? CompactSet(tilesKeys) : ToDetail(tilesKeys, tiles),
						["palette"] = compact ? CompactSet(paletteKeys) : ToDetail(paletteKeys, palette),
						["romReads"] = romReads
					};
					if(useAutoBurst) {
						DebugApi.SnesMapLoadLogClearAutoBurst();  //consume -> re-arm for next map change
					}
					return result;
				} catch {
					return null;
				}
			});
		}

		//Helper: sorted plain address list for a category (compact mode)
		private static JsonArray CompactSet(HashSet<string> keys)
		{
			List<string> sorted = new(keys);
			sorted.Sort();
			JsonArray arr = new JsonArray();
			foreach(string a in sorted) {
				arr.Add((JsonNode)JsonValue.Create(a.Split('|')[0]));
			}
			return arr;
		}

		/// <summary>
		/// R3.1: WRAM/register write log (CPU writes with PC + target address).
		/// Address filter (start/end, 16-bit offsets) + minLen (run length) against flooding.
		/// </summary>
		public static JsonNode? GetWramWrites(string cpuType, int count, UInt64 since, string? startHex, string? endHex, UInt32 minLen, string? memTypeName)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null || cpu.Value != CpuType.Snes) {
				return null;
			}
			return RunExclusive(() => {
				try {
					MemoryType memType = ParseMemoryType(memTypeName ?? "SnesWorkRam") ?? MemoryType.SnesWorkRam;
					UInt32 start = ParseAddress(startHex ?? "0");
					UInt32 end = String.IsNullOrEmpty(endHex) ? 0xFFFF : ParseAddress(endHex);
					UInt16 minRun = (UInt16)Math.Max(minLen, 1);

					DebugApi.InitializeDebugger();
					DebugApi.SnesSetWramLogConfig(true, start, end, minRun, (Int32)memType);
					UInt32 total = DebugApi.SnesGetWramLogCount();
					if(total == 0 || count <= 0) {
						return new JsonObject() { ["count"] = total, ["entries"] = new JsonArray() };
					}

					UInt32 n = Math.Min((UInt32)count, total);
					DebugApi.InteropWramLogEntry[] entries = new DebugApi.InteropWramLogEntry[n];
					UInt32 got = DebugApi.SnesGetWramLogSince(entries, since, n);

					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropWramLogEntry e = entries[i];
						arr.Add((JsonNode)(new JsonObject() {
							["id"] = e.id,
							["frame"] = e.frame,
							["cycle"] = e.cycle,
							["pc"] = e.pc,
							["bank"] = $"{e.bank:X2}",
							["addr"] = e.addr,
							["value"] = e.value,
							["width"] = e.width,
							["memType"] = ((MemoryType)e.memType).ToString()
						}));
					}

					return new JsonObject() { ["count"] = total, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		private static string GetDmaModeText(int mode)
		{
			switch(mode) {
				case 0: return "1 Byte (B=1, A=1)";
				case 1: return "2 Bytes (B=2, A=2)";
				case 2: return "2 Bytes (B=2, A=1)";
				case 3: return "4 Bytes (B=4, A=4)";
				case 4: return "4 Bytes (B=4, A=2)";
				case 5: return "2 Bytes (B=2, A=1), Quelle +1";
				case 6: return "3 Bytes (B=3, A=1)";
				case 7: return "4 Bytes (B=4, A=1)";
				default: return mode.ToString();
			}
		}

		private static string GetDmaDestName(byte bbad)
		{
			switch(bbad) {
				case 0x18: return "VRAM Write (2118)";
				case 0x19: return "VRAM Write (2119)";
				case 0x39: return "VRAM Read (2139)";
				case 0x3A: return "VRAM Read (213A)";
				case 0x04: return "OAM (2104)";
				case 0x12: return "CG-RAM (2121)";
				case 0x13: return "CG-RAM (2122)";
				case 0x21: return "WRAM (2180)";
				case 0x22: return "WRAM (2180)";
				case 0x00: return "PPU (2118)";
				case 0x01: return "PPU (2118)";
				case 0x30: return "CPU/APU (2140)";
				case 0x40: return "CPU/APU (2140)";
				case 0x41: return "CPU/APU (2141)";
				case 0x42: return "CPU/APU (2142)";
				case 0x43: return "CPU/APU (2143)";
				default: return $"${bbad:X2}";
			}
		}

		/// <summary>
		/// Simulates a controller button of port 1. key: A, B, X, Y, L, R, Up, Down, Left, Right,
		/// Start, Select (logical) - or a physical key name ("K", "W", "Up", ...).
		/// holdMs &gt; 0: release the key automatically after holdMs (no blocking).
		/// </summary>
		public static JsonNode? SetInput(string key, bool pressed, int holdMs)
		{
			JsonNode? result = SetInput(key, pressed);
			if(pressed && holdMs > 0) {
				System.Threading.Timer releaseTimer = new System.Threading.Timer(_ => {
					SetInput(key, false);
					_releaseTimers.Remove(key);
				}, null, holdMs, Timeout.Infinite);
				_releaseTimers[key] = releaseTimer;
			}
			return result;
		}

		private static Dictionary<string, System.Threading.Timer> _releaseTimers = new Dictionary<string, System.Threading.Timer>();

		/// <summary>
		/// Simulates a controller button of port 1. All configured keyboard mappings
		/// (Mapping1-4) and the physical key code are set (only &lt; 0x205).
		/// </summary>
		private static bool _backgroundInputApplied;

		public static JsonNode? SetInput(string key, bool pressed)
		{
			try {
				if(!_backgroundInputApplied) {
					//Enable background input: when the Mesen window is not focused (the browser
					//controls the game), input would otherwise be disabled (IsInputEnabled == false).
					if(!ConfigManager.Config.Preferences.AllowBackgroundInput) {
						ConfigManager.Config.Preferences.AllowBackgroundInput = true;
						ConfigManager.Config.Preferences.ApplyConfig();
					}
					_backgroundInputApplied = true;
				}

				SnesControllerConfig cfg = ConfigManager.Config.Snes.Port1;
				Func<KeyMapping, UInt16>? getScanCode = key switch {
					"A" => m => m.A,
					"B" => m => m.B,
					"X" => m => m.X,
					"Y" => m => m.Y,
					"L" => m => m.L,
					"R" => m => m.R,
					"Up" => m => m.Up,
					"Down" => m => m.Down,
					"Left" => m => m.Left,
					"Right" => m => m.Right,
					"Start" => m => m.Start,
					"Select" => m => m.Select,
					_ => null
				};

				JsonArray set = new JsonArray();
				if(getScanCode != null) {
					foreach(KeyMapping mapping in new[] { cfg.Mapping1, cfg.Mapping2, cfg.Mapping3, cfg.Mapping4 }) {
						UInt16 scanCode = getScanCode(mapping);
						if(scanCode != 0 && scanCode < 0x205) {
							InputApi.SetKeyState(scanCode, pressed);
							set.Add((JsonNode)JsonValue.Create(scanCode));
						}
					}
				}

				//Additionally resolve physical key names (like the real UI key path)
				UInt16 physCode = InputApi.GetKeyCode(key);
				if(physCode != 0 && physCode < 0x205) {
					InputApi.SetKeyState(physCode, pressed);
					set.Add((JsonNode)JsonValue.Create(physCode));
				}

				return new JsonObject() {
					["ok"] = set.Count > 0,
					["key"] = key,
					["pressed"] = pressed,
					["codes"] = set
				};
			} catch {
				return new JsonObject() { ["ok"] = false };
			}
		}

		public static LiveApiDisasmLine[] GetDisassembly(string cpuType, UInt32 address, UInt32 count)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return Array.Empty<LiveApiDisasmLine>();
			}
			return RunExclusive(() => {
				try {
					CodeLineData[] lines = DebugApi.GetDisassemblyOutput(cpu.Value, address, count);
					List<LiveApiDisasmLine> result = new List<LiveApiDisasmLine>();
					foreach(CodeLineData line in lines) {
						result.Add(new LiveApiDisasmLine() {
							Address = line.Address,
							Text = line.Text,
							ByteCode = line.ByteCodeStr,
							Comment = line.Comment,
							Flags = (UInt16)line.Flags
						});
					}
					return result.ToArray();
				} catch {
					return Array.Empty<LiveApiDisasmLine>();
				}
			});
		}

		public static LiveApiEventInfo[] GetEvents(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return Array.Empty<LiveApiEventInfo>();
			}
			return RunExclusive(() => {
				try {
					EnsureEventViewerVisible();
					DebugEventInfo[] events = DebugApi.GetDebugEvents(cpu.Value);
					List<LiveApiEventInfo> result = new List<LiveApiEventInfo>();
					foreach(DebugEventInfo evt in events) {
						result.Add(new LiveApiEventInfo() {
							Type = evt.Type.ToString(),
							Cpu = cpu.Value.ToString(),
							PC = evt.ProgramCounter,
							Scanline = evt.Scanline,
							Cycle = evt.Cycle,
							BreakpointId = evt.BreakpointId,
							DmaChannel = evt.DmaChannel,
							Operation = new LiveApiOperationInfo() {
								Address = evt.Operation.Address,
								Value = evt.Operation.Value,
								Type = evt.Operation.Type.ToString(),
								MemType = evt.Operation.MemType.ToString()
							}
						});
					}
					return result.ToArray();
				} catch {
					return Array.Empty<LiveApiEventInfo>();
				}
			});
		}

		public static LiveApiStackFrame[] GetCallstack(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return Array.Empty<LiveApiStackFrame>();
			}
			return RunExclusive(() => {
				try {
					StackFrameInfo[] frames = DebugApi.GetCallstack(cpu.Value);
					List<LiveApiStackFrame> result = new List<LiveApiStackFrame>();
					foreach(StackFrameInfo frame in frames) {
						result.Add(new LiveApiStackFrame() {
							Source = frame.Source,
							Target = frame.Target,
							Return = frame.Return,
							Flags = frame.Flags.ToString()
						});
					}
					return result.ToArray();
				} catch {
					return Array.Empty<LiveApiStackFrame>();
				}
			});
		}

		public static LiveApiExpressionResult? EvaluateExpression(string expression, string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					Int64 value = DebugApi.EvaluateExpression(expression, cpu.Value, out EvalResultType resultType, true);
					return new LiveApiExpressionResult() {
						Expression = expression,
						Value = value,
						ResultType = resultType.ToString()
					};
				} catch {
					return null;
				}
			});
		}

		public static LiveApiBreakpoint[] GetBreakpoints()
		{
			return RunExclusive(() => {
				List<LiveApiBreakpoint> result = new List<LiveApiBreakpoint>();
				foreach(Breakpoint bp in BreakpointManager.Breakpoints) {
					result.Add(new LiveApiBreakpoint() {
						CpuType = bp.CpuType.ToString(),
						MemoryType = bp.MemoryType.ToString(),
						Type = bp.Type.ToString(),
						//SnesVideoRam is reported in word addresses (consistent with /api/vram/writes)
						StartAddress = bp.MemoryType == MemoryType.SnesVideoRam ? bp.StartAddress >> 1 : bp.StartAddress,
						EndAddress = bp.MemoryType == MemoryType.SnesVideoRam ? bp.EndAddress >> 1 : bp.EndAddress,
						Enabled = bp.Enabled,
						Condition = bp.Condition
					});
				}
				return result.ToArray();
			});
		}

		private static void EnsureEventViewerVisible()
		{
			try {
				//Ohne sichtbare Event-Kategorien filtert der Core alle Events aus `/api/events`
				//(Default: MarkedBreakpoints/Nmi/Irq unsichtbar) -> markEvent-Breakpoints liefern 0 Events.
				InteropSnesEventViewerConfig cfg = new InteropSnesEventViewerConfig() {
					Irq = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0xFF8040 },
					Nmi = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0x40C080 },
					MarkedBreakpoints = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0xFF4040 },
					PpuRegisterVramWrites = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0x8080FF },
					PpuRegisterCgramWrites = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0xC080FF },
					PpuRegisterOamWrites = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0x80FFFF },
					CpuRegisterWrites = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0xFFC080 },
					WorkRamRegisterWrites = new InteropEventViewerCategoryCfg() { Visible = true, Color = 0xC0FF80 },
					ShowPreviousFrameEvents = true
				};
				DebugApi.SetEventViewerConfig(CpuType.Snes, cfg);
			} catch {
			}
		}

		public static bool SetBreakpoint(LiveApiBreakpointSetRequest request)
		{
			return RunExclusive(() => {
				try {
					CpuType cpu = ParseCpuType(request.CpuType) ?? CpuType.Snes;
					MemoryType memType = ParseMemoryType(request.MemoryType) ?? MemoryType.SnesMemory;
					UInt32 start = request.StartAddress;
					UInt32 end = request.EndAddress;
					if(memType == MemoryType.SnesVideoRam) {
						//SnesVideoRam-Breakpoints verwenden Wort-Adressen (konsistent zu /api/vram/writes):
						//der Core matcht auf Byte-Adressen (vramAddr<<1), daher hier konvertieren.
						start = start << 1;
						end = (end << 1) | 1;
					}

					Breakpoint bp = new Breakpoint() {
						CpuType = cpu,
						MemoryType = memType,
						BreakOnRead = request.BreakOnRead,
						BreakOnWrite = request.BreakOnWrite,
						BreakOnExec = request.BreakOnExec,
						Forbid = request.Forbid,
						Enabled = request.Enabled,
						MarkEvent = request.MarkEvent,
						StartAddress = start,
						EndAddress = end,
						Condition = request.Condition
					};

					//Ensure the CPU type is passed to the core: without AddCpuType
					//(which is normally called by the debugger window when it opens)
					//SetBreakpoints() sends an empty array to the core -> breakpoints never fire (D1/D5).
					DebugApi.InitializeDebugger();
					EnsureEventViewerVisible();
					BreakpointManager.AddCpuType(cpu);
					BreakpointManager.AddBreakpoint(bp);
					return true;
				} catch {
					return false;
				}
			});
		}

		public static bool ClearBreakpoints()
		{
			return RunExclusive(() => {
				try {
					BreakpointManager.ClearBreakpoints();
					return true;
				} catch {
					return false;
				}
			});
		}

		private static string GetExportsFolder()
		{
			string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiExports");
			try {
				Directory.CreateDirectory(dir);
			} catch {
			}
			return dir;
		}

		/// <summary>
		/// Pause the emulator during a native mutation: the emulation thread writes into the
		/// tracker buffer (Append/WriteRamLine); Stop/Start frees the RAM buffer via free().
		/// Without a pause -> use-after-free (crash). RunExclusive does NOT pause the emulator.
		/// </summary>
		private static T WithEmuPaused<T>(Func<T> action)
		{
			bool wasPaused = EmuApi.IsPaused();
			if(!wasPaused) {
				EmuApi.Pause();
			}
			try {
				return action();
			} finally {
				if(!wasPaused) {
					DebugApi.ResumeExecution();
				}
			}
		}

		/// <summary>
		/// Start the universal tracker: trigger on memory read/write of a region; afterwards, a
		/// chronological trace (Exec/MemW/VRAM/DMA/Interrupt) is logged to ring + file.
		/// </summary>
		public static JsonNode? TrackerStart(string? memTypeName, string? startHex, string? endHex, bool onRead, bool onWrite, string? valueHex, bool valueSet, bool logExec, UInt64 maxBytes, string? mode, UInt64 bufferSizeMb)
		{
			return RunExclusive(() => {
				try {
					DebugApi.InitializeDebugger();
					MemoryType memType = ParseMemoryType(memTypeName ?? "SnesWorkRam") ?? MemoryType.SnesWorkRam;
					UInt32 start = ParseAddress(startHex ?? "0");
					UInt32 end = String.IsNullOrEmpty(endHex) ? 0xFFFF : ParseAddress(endHex);
					UInt32 value = ParseAddress(valueHex ?? "0");
					Int32 bufferMode = mode == "ram" ? 1 : 0;
					string filePath = Path.Combine(GetExportsFolder(), $"tracker_{DateTime.Now:yyyyMMdd_HHmmss}.log");
					WithEmuPaused(() => {
						DebugApi.SnesTrackerStart(filePath, (Int32)memType, start, end, onRead, onWrite, value, valueSet, logExec, maxBytes, bufferMode, bufferSizeMb);
						return true;
					});
					return new JsonObject() { ["ok"] = true, ["file"] = filePath };
				} catch {
					return new JsonObject() { ["ok"] = false };
				}
			});
		}

		public static JsonNode? TrackerStop()
		{
			return RunExclusive(() => {
				try {
					return WithEmuPaused(() => {
						UInt32 count = DebugApi.SnesTrackerGetCount();
						UInt64 triggerCount = DebugApi.SnesTrackerGetTriggerCount();
						DebugApi.SnesTrackerStop();
						return new JsonObject() { ["ok"] = true, ["count"] = count, ["triggerCount"] = triggerCount };
					});
				} catch {
					return new JsonObject() { ["ok"] = false };
				}
			});
		}

		public static JsonNode? TrackerStatus()
		{
			return RunExclusive(() => {
				try {
					return new JsonObject() {
						["enabled"] = DebugApi.SnesTrackerIsEnabled(),
						["tracking"] = DebugApi.SnesTrackerIsTracking(),
						["count"] = DebugApi.SnesTrackerGetCount(),
						["triggerCount"] = DebugApi.SnesTrackerGetTriggerCount(),
						["bufferLen"] = DebugApi.SnesTrackerGetBufferLen()
					};
				} catch {
					return new JsonObject() { ["ok"] = false };
				}
			});
		}

		public static JsonNode? GetTrackerLog(int count, UInt32 since)
		{
			return RunExclusive(() => {
				try {
					UInt32 total = DebugApi.SnesTrackerGetCount();
					if(total == 0 || count <= 0 || since >= total) {
						return new JsonObject() { ["count"] = total, ["entries"] = new JsonArray() };
					}
					UInt32 n = Math.Min((UInt32)count, total - since);
					DebugApi.InteropTrackerEntry[] entries = new DebugApi.InteropTrackerEntry[n];
					UInt32 got = DebugApi.SnesGetTrackerLog(entries, since, n);

					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropTrackerEntry e = entries[i];
						string type = e.type switch {
							0 => "Exec",
							2 => "MemW",
							3 => "VRAM",
							4 => "DMA",
							5 => "Nmi",
							6 => "Irq",
							_ => "?"
						};
						JsonObject entry = new JsonObject() {
							["id"] = e.id,
							["frame"] = e.frame,
							["cycle"] = e.cycle,
							["type"] = type,
							["pc"] = e.pc,
							["bank"] = $"{e.bank:X2}",
							["addr"] = e.addr,
							["value"] = e.value
						};
						if(e.type == 4) {
							//DMA: pc-Feld = vramAddr, addr = dest (BBAD), value = channel|hdma<<7, extra/extra2 = length
							entry["channel"] = e.value & 0x7F;
							entry["hdma"] = (e.value & 0x80) != 0;
							entry["vramAddr"] = e.pc;
							entry["dest"] = e.addr;
							entry["length"] = (UInt32)e.extra | ((UInt32)e.extra2 << 8);
						}
						arr.Add((JsonNode)entry);
					}

					return new JsonObject() { ["count"] = total, ["entries"] = arr };
				} catch {
					return new JsonObject() { ["ok"] = false };
				}
			});
		}

		public static bool Control(string action, string cpuType, UInt32 stepCount)
		{
			return RunExclusive(() => {
				try {


					switch(action) {
						case "pause":
							EmuApi.Pause();
							return true;
						case "resume":
							DebugApi.ResumeExecution();
							return true;
						case "step":
							DebugApi.Step(ParseCpuType(cpuType) ?? CpuType.Snes, (Int32)Math.Max(stepCount, 1), StepType.Step);
							return true;
						case "stepOver":
							DebugApi.Step(ParseCpuType(cpuType) ?? CpuType.Snes, 1, StepType.StepOver);
							return true;
						case "stepOut":
							DebugApi.Step(ParseCpuType(cpuType) ?? CpuType.Snes, 1, StepType.StepOut);
							return true;
						case "runSingleFrame":
							EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.RunSingleFrame });
							return true;
						case "toggleFastForward":
							EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.ToggleFastForward });
							return true;
						case "reset":
							EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.Reset });
							return true;
						case "powerCycle":
							EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.PowerCycle });
							return true;
						case "reload":
							EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = EmulatorShortcut.ReloadRom });
							return true;
						default:
							return false;
					}
				} catch {
					return false;
				}
			});
		}

		public static bool LoadRom(string path)
		{
			return RunExclusive(() => {
				try {
					return EmuApi.LoadRom(path);
				} catch {
					return false;
				}
			});
		}

		/// <summary>
		/// Save/load a savestate (slot 1-10), for reproducible tests.
		/// </summary>
		public static bool SaveState(UInt32 slot)
		{
			return RunExclusive(() => {
				try {
					if(slot < 1 || slot > 10) {
						return false;
					}
					EmuApi.SaveState(slot);
					return true;
				} catch {
					return false;
				}
			});
		}

		public static bool LoadState(UInt32 slot)
		{
			return RunExclusive(() => {
				try {
					if(slot < 1 || slot > 10) {
						return false;
					}
					EmuApi.LoadState(slot);
					return true;
				} catch {
					return false;
				}
			});
		}

		public static int GetMemorySize(string type)
		{
			MemoryType? memType = ParseMemoryType(type);
			if(memType == null) {
				return -1;
			}
			return RunExclusive(() => {
				try {
					return DebugApi.GetMemorySize(memType.Value);
				} catch {
					return -1;
				}
			});
		}

		public static MemoryType? ParseMemoryType(string type)
		{
			if(Enum.TryParse<MemoryType>(type, out MemoryType result)) {
				return result;
			}
			return null;
		}

		public static UInt32 ParseAddress(string value)
		{
			if(String.IsNullOrEmpty(value)) {
				return 0;
			}
			value = value.Trim();
			try {
				if(value.StartsWith("0x") || value.StartsWith("0X")) {
					return Convert.ToUInt32(value.Substring(2), 16);
				}
				return Convert.ToUInt32(value, 10);
			} catch {
				return 0;
			}
		}

		public static CpuType? ParseCpuType(string type)
		{
			if(Enum.TryParse<CpuType>(type, out CpuType result)) {
				return result;
			}
			return null;
		}

		public static string ToHex(byte[] data)
		{
			StringBuilder sb = new StringBuilder(data.Length * 2);
			foreach(byte b in data) {
				sb.Append(b.ToString("X2"));
			}
			return sb.ToString();
		}

		public static byte[] FromHex(string hex)
		{
			hex = hex.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
			if(hex.Length % 2 != 0) {
				return Array.Empty<byte>();
			}
			byte[] result = new byte[hex.Length / 2];
			for(int i = 0; i < result.Length; i++) {
				if(!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i])) {
					return Array.Empty<byte>();
				}
			}
			return result;
		}

		public static void MapLoadRingClear()
		{
			RunExclusive(() => {
				try {
					DebugApi.SnesMapLoadLogSetEnabled(true);
					DebugApi.SnesMapLoadLogSetAutoCapture(true);
					DebugApi.SnesMapLoadLogSetLiveTracking(true);
					DebugApi.SnesMapLoadRomReadRingClear();
				} catch {
				}
			});
		}

		public static UInt32 MapLoadRingCount()
		{
			UInt32 result = 0;
			RunExclusive(() => {
				try {
					DebugApi.SnesMapLoadLogSetEnabled(true);
					DebugApi.SnesMapLoadLogSetAutoCapture(true);
					DebugApi.SnesMapLoadLogSetLiveTracking(true);
					result = DebugApi.SnesMapLoadRomReadRingCount();
				} catch {
				}
			});
			return result;
		}

		public static JsonObject MapLoadRingResize(string entries)
		{
			UInt32 size = 0;
			if(UInt32.TryParse(entries, out UInt32 v) && v >= (1u << 16)) {
				size = v;
			}
			return RunExclusive(() => {
				try {
					if(size == 0) {
						size = DebugApi.SnesMapLoadRomReadRingSize();
					} else {
						DebugApi.SnesMapLoadLogSetEnabled(true);
						DebugApi.SnesMapLoadLogSetAutoCapture(true);
						DebugApi.SnesMapLoadLogSetLiveTracking(true);
						DebugApi.SnesMapLoadRomReadRingResize(size);
					}
					return new JsonObject() {
						["entries"] = size,
						["bytes"] = size * 16u,
						["actual"] = DebugApi.SnesMapLoadRomReadRingSize()
					};
				} catch {
					return new JsonObject() { ["error"] = "resize failed" };
				}
			});
		}

		public static JsonNode? MapLoadVramMap(string startHex, string wordsHex)
		{
			return RunExclusive(() => {
				try {
					UInt32 start = ParseUIntHex(startHex);
					UInt32 words = Math.Min(ParseUIntHex(wordsHex) == 0 ? 0x400u : ParseUIntHex(wordsHex), 0x2000u);
					JsonArray arr = new JsonArray();
					for(UInt32 w = 0; w < words; w++) {
						UInt32 rom = DebugApi.SnesMapLoadVramRomWord(start + w);
						arr.Add((JsonNode)new JsonObject() {
							["vram"] = "0x" + (start + w).ToString("X4"),
							["rom"] = rom == 0xFFFFFFFF ? "(leer)" : "0x" + rom.ToString("X6")
						});
					}
					return new JsonObject() { ["start"] = "0x" + start.ToString("X4"), ["words"] = words, ["entries"] = arr };
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: GENERISCHES natives Script-Modul (Table-Driven, AoT-sicher, KEIN JS/Reflection).
		/// Ein EXTERNES Spiel-Script (JSON-Schema) beschreibt, wie Pointer-Tabellen und
		/// Script-Kommandos im ROM interpretiert werden. Der Emulator fuehrt es nativ aus
		/// und liefert die extrahierten Ressourcen als JSON. Kein spiel-spezifischer Code im
		/// Emulator - das Schema kommt von aussen (Plugin-Datei / POST-Body).
		///
		/// Schema (JSON):
		/// {
		///   "mapIdWram": "0x047E",       // WRAM-Adresse der aktiven Map-ID (LE Word)
		///   "pointerTable": "0x06959C",  // Pointer-Tabelle: Map-ID * 3 Bytes (lo,hi,bank)
		///   "subTable": "0x06A28C",      // Sub-Script-Tabelle: Index * 3 Bytes
		///   "hirom": true,               // HiROM Bus->File Mapping
		///   "bankTransform": "b*2+0x1B", // ROM-Bank-Transformation
		///   "commands": [                // Kommandos: Opcode -> Feld-Layout
		///     { "op": "0x40", "name": "palette", "len": 7, "addrPos": 4, "bankPos": 6 },
		///     { "op": "0x80", "name": "tiles",   "len": 9, "addrPos": 4, "bankPos": 6 },
		///     { "op": "0x20", "name": "tilemap", "len": 8, "addrPos": 5, "bankPos": 7 },
		///     { "op": "0x10", "name": "raw",     "len": 5, "layerPos": 1, "addrPos": 2 },
		///     { "op": "0x02", "name": "spc",     "len": 5, "addrPos": 2, "bankPos": 4 },
		///     { "op": "0x08FC", "name": "sub",   "len": 4, "subIdxPos": 2 },
		///     { "op": "0x08FA", "name": "entity","len": 7, "typPos": 2, "idPos": 5 }
		///   ]
		/// }
		/// </summary>
		public static JsonObject? ExecuteNativeScript(string source)
		{
			if(string.IsNullOrWhiteSpace(source)) {
				return null;
			}
			return RunExclusive(() => {
				try {
					JsonObject? schema;
					try {
						schema = JsonSerializer.Deserialize<JsonObject>(source, LiveApiSerializerContext.Default.JsonObject);
					} catch {
						return new JsonObject() { ["ok"] = false, ["error"] = "Script ist kein gueltiges JSON-Schema" };
					}
					if(schema == null) {
						return new JsonObject() { ["ok"] = false, ["error"] = "leeres Schema" };
					}
					UInt32 mapIdWram = ReadSchemaAddr(schema, "mapIdWram", 0x047E);
					UInt32 pointerTable = ReadSchemaAddr(schema, "pointerTable", 0x06959C);
					UInt32 subTable = ReadSchemaAddr(schema, "subTable", 0x06A28C);
					bool hirom = ReadSchemaBool(schema, "hirom", true);
					string bankTransform = ReadSchemaString(schema, "bankTransform", "b*2+0x1B");

					List<ScriptCmd> commands = new List<ScriptCmd>();
					if(schema["commands"] is JsonArray cmdArr) {
						foreach(JsonNode? cn in cmdArr) {
							JsonObject? co = cn as JsonObject;
							if(co == null) continue;
							ScriptCmd cmd = new ScriptCmd();
							cmd.Op = ReadSchemaAddr(co, "op", 0);
							cmd.Name = ReadSchemaString(co, "name", "");
							// len: Zahl (fest) ODER "min-max" (auto-erkennung durch Opcode-Validierung)
							string lenStr = ReadSchemaString(co, "len", "");
							if(lenStr.Contains('-')) {
								string[] parts = lenStr.Split('-');
								cmd.Len = int.TryParse(parts[0], out int lmin) ? lmin : 1;
								cmd.MaxLen = int.TryParse(parts[1], out int lmax) ? lmax : lmin;
							} else {
								cmd.Len = (int)ReadSchemaAddr(co, "len", 1);
								cmd.MaxLen = cmd.Len;
							}
							cmd.AddrPos = (int)ReadSchemaAddr(co, "addrPos", 0xFFFFFFFF);
							cmd.BankPos = (int)ReadSchemaAddr(co, "bankPos", 0xFFFFFFFF);
							cmd.LayerPos = (int)ReadSchemaAddr(co, "layerPos", 0xFFFFFFFF);
							cmd.SubIdxPos = (int)ReadSchemaAddr(co, "subIdxPos", 0xFFFFFFFF);
							cmd.TypPos = (int)ReadSchemaAddr(co, "typPos", 0xFFFFFFFF);
							cmd.IdPos = (int)ReadSchemaAddr(co, "idPos", 0xFFFFFFFF);
							commands.Add(cmd);
						}
					}

					// ROM-Cache: ganze 4KB-Chunks laden statt Byte-fuer-Byte (schnell)
					Dictionary<UInt32, byte[]> cache = new Dictionary<UInt32, byte[]>();

					UInt32 mapId = ReadWram16(mapIdWram);
					UInt32 scriptBase = ReadPointerCached(pointerTable + mapId * 3, hirom, cache);
					JsonArray results = new JsonArray();
					HashSet<UInt32> seen = new HashSet<UInt32>();
					ScanScriptCached(scriptBase, hirom, bankTransform, commands, subTable, 0, results, seen, cache);

					return new JsonObject() {
						["ok"] = true,
						["mapId"] = mapId,
						["scriptBase"] = "0x" + scriptBase.ToString("X6"),
						["resources"] = results
					};
				} catch(Exception ex) {
					return new JsonObject() { ["ok"] = false, ["error"] = ex.Message };
				}
			});
		}

		private class ScriptCmd
		{
			public UInt32 Op;
			public string Name = "";
			public int Len = 1;
			public int MaxLen = 1;
			public int AddrPos = -1;
			public int BankPos = -1;
			public int LayerPos = -1;
			public int SubIdxPos = -1;
			public int TypPos = -1;
			public int IdPos = -1;
		}

		private static UInt32 ReadSchemaAddr(JsonObject obj, string key, UInt32 def)
		{
			JsonNode? n = obj[key];
			if(n == null) return def;
			if(n is JsonValue v) {
				try {
					if(v.TryGetValue<UInt32>(out UInt32 ui)) return ui;
					if(v.TryGetValue<string>(out string? s) && s != null) return ParseUIntHex(s);
				} catch { }
			}
			return def;
		}

		private static bool ReadSchemaBool(JsonObject obj, string key, bool def)
		{
			JsonNode? n = obj[key];
			if(n is JsonValue v && v.TryGetValue<bool>(out bool b)) return b;
			return def;
		}

		private static string ReadSchemaString(JsonObject obj, string key, string def)
		{
			JsonNode? n = obj[key];
			if(n is JsonValue v && v.TryGetValue<string>(out string? s) && s != null) return s;
			return def;
		}

		private static UInt32 ReadWram16(UInt32 addr)
		{
			byte[]? d = ReadMemoryRawNoLock(MemoryType.SnesWorkRam, addr, 2);
			if(d == null || d.Length < 2) return 0;
			return (UInt32)(d[0] | (d[1] << 8));
		}

		private static UInt32 ReadPointerCached(UInt32 off, bool hirom, Dictionary<UInt32, byte[]> cache)
		{
			byte[]? d = ReadCached(cache, off, 3);
			if(d == null || d.Length < 3) return 0xFFFFFFFF;
			UInt32 lo = d[0], hi = d[1], bk = d[2];
			UInt32 a16 = lo | (hi << 8);
			if(hirom) {
				return (bk >= 0x80 ? (bk - 0x80) * 0x10000 : bk * 0x10000) + a16;
			}
			return bk * 0x10000 + a16;
		}

		// Liest len Bytes ab addr, nutzt 4KB-Chunks aus dem Cache
		private static byte[]? ReadCached(Dictionary<UInt32, byte[]> cache, UInt32 addr, int len)
		{
			if(len <= 0) return null;
			byte[] result = new byte[len];
			int got = 0;
			while(got < len) {
				UInt32 chunkAddr = addr + (UInt32)got;
				UInt32 chunkBase = chunkAddr & ~0xFFFu;
				if(!cache.TryGetValue(chunkBase, out byte[]? chunk)) {
					chunk = ReadMemoryRawNoLock(MemoryType.SnesPrgRom, chunkBase, 0x1000);
					if(chunk == null || chunk.Length == 0) return null;
					cache[chunkBase] = chunk;
				}
				int off = (int)(chunkAddr - chunkBase);
				int take = Math.Min(len - got, chunk.Length - off);
				if(take <= 0) return null;
				Array.Copy(chunk, off, result, got, take);
				got += take;
			}
			return result;
		}

		private static UInt32 TransformBank(UInt32 bank, string transform)
		{
			UInt32 b = bank & 0xFF;
			string t = transform.Trim();
			if(t == "b*2+0x1B" || t == "b*2+27") return (UInt32)((b * 2 + 0x1B) & 0xFF) * 0x10000;
			return b * 0x10000;
		}

		private static void ScanScriptCached(UInt32 scriptStart, bool hirom, string bankTransform, List<ScriptCmd> commands, UInt32 subTable, int depth, JsonArray results, HashSet<UInt32> seen, Dictionary<UInt32, byte[]> cache)
		{
			if(depth > 8 || scriptStart == 0xFFFFFFFF || seen.Contains(scriptStart)) return;
			seen.Add(scriptStart);
			// ganzes Script einmal laden
			byte[]? data = ReadCached(cache, scriptStart, 0x4000);
			if(data == null) return;
			UInt32 pos = 0;
			const UInt32 maxLen = 0x4000;
			while(pos < maxLen) {
				byte b = data[pos];
				if(b == 0x08 && pos + 1 < maxLen) {
					byte sub = data[pos + 1];
					UInt32 combined = 0x0800u | sub;
					ScriptCmd? subCmd = commands.FirstOrDefault(c => c.Op == combined);
					if(subCmd != null) {
						pos = HandleCmdCached(subCmd, data, pos, hirom, bankTransform, subTable, depth, results, seen, commands, cache);
						continue;
					}
					UInt32 skip = sub switch {
						0xFE => 4u,
						0xFD => 6u,
						0xF8 => 2u,
						0xFF => 4u,
						0xF9 => 4u,
						_ => 2u
					};
					pos += skip;
					continue;
				}
				ScriptCmd? cmd = commands.FirstOrDefault(c => c.Op == b && c.Op < 0x100u);
				if(cmd != null) {
					pos = HandleCmdCached(cmd, data, pos, hirom, bankTransform, subTable, depth, results, seen, commands, cache);
					continue;
				}
				if(b == 0x00) {
					if(pos + 2 < maxLen && data[pos + 1] == 0xFF && data[pos + 2] == 0xFF) break;
					pos += 3;
					continue;
				}
				if(b >= 0x01 && b <= 0x0F) { pos += (UInt32)(b + 1); continue; }
				pos += 1;
			}
		}

		private static UInt32 HandleCmdCached(ScriptCmd cmd, byte[] data, UInt32 pos, bool hirom, string bankTransform, UInt32 subTable, int depth, JsonArray results, HashSet<UInt32> seen, List<ScriptCmd> commands, Dictionary<UInt32, byte[]> cache)
		{
			// DATENGETRIEBENE Laengen-Erkennung: Wenn MaxLen > Len, probiere jede Laenge und
			// waehle die, nach der der naechste Byte ein plausibler Opcode ist UND (wenn das
			// Kommando eine ROM-Adresse traegt) die ROM-Adresse im ROM-Bereich liegt.
			int bestLen = cmd.Len;
			bool addrOk = false;
			if(cmd.MaxLen > cmd.Len) {
				for(int tryLen = cmd.Len; tryLen <= cmd.MaxLen; tryLen++) {
					int next = (int)pos + tryLen;
					if(next >= data.Length) break;
					if(!IsValidOpcode(data[next])) continue;
					// ROM-Adress-Validierung (falls vorhanden)
					bool ok = true;
					if(cmd.AddrPos >= 0 && cmd.BankPos >= 0 && cmd.AddrPos + 1 < tryLen && cmd.BankPos < tryLen) {
						UInt32 addr = (UInt32)(data[pos + cmd.AddrPos] | (data[pos + cmd.AddrPos + 1] << 8));
						UInt32 bank = data[pos + cmd.BankPos];
						UInt32 rom = TransformBank(bank, bankTransform) + addr;
						ok = IsValidRomAddr(rom);
					}
					if(ok) { bestLen = tryLen; addrOk = ok; break; }
				}
			} else {
				// Feste Laenge: pruefe die ROM-Adresse; wenn ungueltig -> kein echtes Ressourcen-
				// Kommando, nur den Laengen-Offset zurueckgeben (kein Eintrag).
				addrOk = true;
				if(cmd.AddrPos >= 0 && cmd.BankPos >= 0 && cmd.AddrPos + 1 < cmd.Len && cmd.BankPos < cmd.Len) {
					UInt32 addr = (UInt32)(data[pos + cmd.AddrPos] | (data[pos + cmd.AddrPos + 1] << 8));
					UInt32 bank = data[pos + cmd.BankPos];
					UInt32 rom = TransformBank(bank, bankTransform) + addr;
					addrOk = IsValidRomAddr(rom);
				}
			}
			int cmdLen = Math.Max(bestLen, 1);
			// Wenn die ROM-Adresse ungueltig ist, ist dieses Byte KEIN echtes Ressourcen-Kommando
			// -> keinen Eintrag erzeugen, nur 1 Byte ueberspringen (Drift-Schutz).
			if(!addrOk && cmd.AddrPos >= 0 && cmd.BankPos >= 0) {
				return pos + 1;
			}
			JsonObject entry = new JsonObject() {
				["type"] = cmd.Name,
				["pos"] = "0x" + pos.ToString("X4")
			};
			if((int)pos + cmdLen <= data.Length) {
				if(cmd.AddrPos >= 0 && cmd.AddrPos + 1 < cmdLen) {
					UInt32 addr = (UInt32)(data[pos + cmd.AddrPos] | (data[pos + cmd.AddrPos + 1] << 8));
					entry["addr"] = "0x" + addr.ToString("X4");
					if(cmd.BankPos >= 0 && cmd.BankPos < cmdLen) {
						UInt32 bank = data[pos + cmd.BankPos];
						UInt32 rom = TransformBank(bank, bankTransform) + addr;
						entry["rom"] = "0x" + rom.ToString("X6");
					}
				}
				if(cmd.LayerPos >= 0 && cmd.LayerPos < cmdLen) entry["layer"] = data[pos + cmd.LayerPos];
				if(cmd.SubIdxPos >= 0 && cmd.SubIdxPos < cmdLen) {
					byte subIdx = data[pos + cmd.SubIdxPos];
					entry["sub"] = subIdx;
					UInt32 subBase = ReadPointerCached(subTable + (UInt32)subIdx * 3, hirom, cache);
					if(subBase != 0xFFFFFFFF) {
						ScanScriptCached(subBase, hirom, bankTransform, commands, subTable, depth + 1, results, seen, cache);
					}
				}
				if(cmd.TypPos >= 0 && cmd.TypPos < cmdLen) entry["typ"] = data[pos + cmd.TypPos];
				if(cmd.IdPos >= 0 && cmd.IdPos < cmdLen) entry["id"] = "0x" + data[pos + cmd.IdPos].ToString("X2");
			}
			results.Add((JsonNode)entry);
			return pos + (UInt32)cmdLen;
		}

		// Datentreiber: ist der Byte ein plausibler Script-Opcode?
		private static bool IsValidOpcode(byte b)
		{
			if(b == 0x08 || b == 0x40 || b == 0x80 || b == 0x20 || b == 0x10 || b == 0x02 || b == 0x00) return true;
			if(b >= 0x01 && b <= 0x0F) return true;
			return false;
		}

		// Datentreiber: passt die aufgeloeste ROM-Adresse in den ROM-Bereich?
		// Ungueltige Adressen (>0x400000 bei 4MB-ROM oder Bank-Overflow) bedeuten:
		// die Laenge war falsch -> diese Interpretation verwerfen.
		private static bool IsValidRomAddr(UInt32 rom)
		{
			return rom > 0 && rom < 0x400000;
		}
	}
}
