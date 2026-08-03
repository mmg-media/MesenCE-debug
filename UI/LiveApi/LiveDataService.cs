using Mesen.Config;
using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
		/// Aktiviert/deaktiviert den Trace-Logger für eine CPU (damit /api/trace Daten liefert).
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
		/// DMA-Kanal-Zustand (Register $4300-$437F + $420B/$420C): Quelle, Ziel, Länge, Mode pro Kanal.
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
		/// Atomarer Snapshot aller Map-Scanning-Daten in EINEM Gate (konsistenter Frame):
		/// Scroll, Layer, Nametable (BG2 0x3800), CGRAM, VRAM-Tiles, DMA-Zustand.
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
		/// Kumulativer DMA-Log (Anforderung P1.1): Ring-Puffer mit jedem DMA/HDMA-Block.
		/// count = max. Einträge, since = inkrementelles Abholen (Index ab dem geliefert wird).
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
		/// R2.1: Kumulative Event-Historie (Ring-Puffer). Wird beim Aufruf automatisch aktiviert.
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
		/// R2.2: VRAM-Write-Historie (CPU-Writes auf 0x2118/0x2119) mit PC und Zieladresse.
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
		/// R3.1: WRAM/Register-Write-Log (CPU-Writes mit PC + Zieladresse).
		/// Adressfilter (start/end, 16-Bit-Offsets) + minLen (Run-Länge) gegen Anti-Flut.
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
		/// Simuliert eine Controller-Taste des Ports 1. key: A, B, X, Y, L, R, Up, Down, Left, Right,
		/// Start, Select (logisch) – oder ein physischer Tastennamen ("K", "W", "Up", ...).
		/// holdMs &gt; 0: Taste automatisch nach holdMs loslassen (kein Blockieren).
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
		/// Simuliert eine Controller-Taste des Ports 1. Alle konfigurierten Tastatur-Zuordnungen
		/// (Mapping1-4) und der physische Tastencode werden gesetzt (nur &lt; 0x205).
		/// </summary>
		private static bool _backgroundInputApplied;

		public static JsonNode? SetInput(string key, bool pressed)
		{
			try {
				if(!_backgroundInputApplied) {
					//Hintergrund-Eingabe aktivieren: Wenn das Mesen-Fenster nicht fokussiert ist (Browser
					//steuert das Spiel), ist Input sonst deaktiviert (IsInputEnabled == false).
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

				//Zusätzlich physischen Tastennamen auflösen (wie der echte UI-Key-Pfad)
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
						//SnesVideoRam wird in Wort-Adressen gemeldet (konsistent zu /api/vram/writes)
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

					//Sicherstellen, dass der CPU-Typ an den Core übertragen wird: Ohne AddCpuType
					//(das normalerweise das Debugger-Fenster beim Öffnen aufruft) sendet
					//SetBreakpoints() ein leeres Array an den Core -> Breakpoints feuern nie (D1/D5).
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
		/// Universal-Tracker starten: Trigger auf Memory-Lesen/Schreiben einer Region; danach wird ein
		/// chronologischer Ablauf (Exec/MemW/VRAM/DMA/Interrupt) in Ring + Datei geloggt.
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
					DebugApi.SnesTrackerStart(filePath, (Int32)memType, start, end, onRead, onWrite, value, valueSet, logExec, maxBytes, bufferMode, bufferSizeMb);
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
					UInt32 count = DebugApi.SnesTrackerGetCount();
					UInt64 triggerCount = DebugApi.SnesTrackerGetTriggerCount();
					DebugApi.SnesTrackerStop();
					return new JsonObject() { ["ok"] = true, ["count"] = count, ["triggerCount"] = triggerCount };
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
		/// Savestate speichern/laden (Slot 1-10), für reproduzierbare Tests.
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
	}
}
