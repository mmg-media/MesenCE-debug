using Mesen.Config.Shortcuts;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
		private static readonly object _subscriberLock = new object();
		private static readonly List<LiveApiSubscriber> _subscribers = new List<LiveApiSubscriber>();
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
			lock(_subscriberLock) {
				foreach(LiveApiSubscriber subscriber in _subscribers) {
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
			lock(_subscriberLock) {
				foreach(LiveApiSubscriber subscriber in _subscribers) {
					subscriber.Snapshots.Clear();
				}
			}
		}

		private static void Broadcast(LiveApiEventMessage msg)
		{
			lock(_subscriberLock) {
				foreach(LiveApiSubscriber subscriber in _subscribers) {
					if(subscriber.Events.Contains(msg.Event)) {
						subscriber.Send?.Invoke(msg);
					}
				}
			}
		}

		public static void RegisterSubscriber(LiveApiSubscriber subscriber)
		{
			lock(_subscriberLock) {
				_subscribers.Add(subscriber);
			}
		}

		public static void UnregisterSubscriber(LiveApiSubscriber subscriber)
		{
			lock(_subscriberLock) {
				_subscribers.Remove(subscriber);
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
						StartAddress = bp.StartAddress,
						EndAddress = bp.EndAddress,
						Enabled = bp.Enabled,
						Condition = bp.Condition
					});
				}
				return result.ToArray();
			});
		}

		public static bool SetBreakpoint(LiveApiBreakpointSetRequest request)
		{
			return RunExclusive(() => {
				try {
					Breakpoint bp = new Breakpoint() {
						CpuType = ParseCpuType(request.CpuType) ?? CpuType.Snes,
						MemoryType = ParseMemoryType(request.MemoryType) ?? MemoryType.SnesMemory,
						BreakOnRead = request.BreakOnRead,
						BreakOnWrite = request.BreakOnWrite,
						BreakOnExec = request.BreakOnExec,
						Forbid = request.Forbid,
						Enabled = request.Enabled,
						MarkEvent = request.MarkEvent,
						StartAddress = request.StartAddress,
						EndAddress = request.EndAddress,
						Condition = request.Condition
					};
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
