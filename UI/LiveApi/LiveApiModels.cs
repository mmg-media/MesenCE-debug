using Mesen.Interop;
using System;
using System.Text.Json.Serialization;

namespace Mesen.LiveApi
{
	public class LiveApiStatus
	{
		public bool Running { get; set; }
		public bool Paused { get; set; }
		public bool RomLoaded { get; set; }
		public string? RomName { get; set; }
		public string? RomPath { get; set; }
		public string? RomHash { get; set; }
		public string ConsoleType { get; set; } = "";
		public UInt64 Frame { get; set; }
	}

	public class LiveApiRomInfo
	{
		public string RomPath { get; set; } = "";
		public string RomName { get; set; } = "";
		public string Format { get; set; } = "";
		public string ConsoleType { get; set; } = "";
		public string Sha1 { get; set; } = "";
		public string[] CpuTypes { get; set; } = Array.Empty<string>();
	}

	public class LiveApiMemoryRead
	{
		public string Type { get; set; } = "";
		public UInt32 Start { get; set; }
		public UInt32 Length { get; set; }
		public string Data { get; set; } = "";
	}

	public class LiveApiMemoryWriteRequest
	{
		public string Type { get; set; } = "";
		public UInt32 Start { get; set; }
		public string? Data { get; set; }
		public byte[]? Values { get; set; }
	}

	public class LiveApiCpuState
	{
		public string Cpu { get; set; } = "";
		public UInt16 A { get; set; }
		public UInt16 X { get; set; }
		public UInt16 Y { get; set; }
		public UInt16 SP { get; set; }
		public UInt16 D { get; set; }
		public UInt16 PC { get; set; }
		public byte K { get; set; }
		public byte DBR { get; set; }
		public byte PS { get; set; }
		public bool EmulationMode { get; set; }
		public string StopState { get; set; } = "";
		public UInt64 CycleCount { get; set; }
	}

	public class LiveApiPpuState
	{
		public string Cpu { get; set; } = "";
		public UInt16 Scanline { get; set; }
		public UInt16 Cycle { get; set; }
		public UInt16 HClock { get; set; }
		public UInt32 FrameCount { get; set; }
		public bool ForcedBlank { get; set; }
		public byte ScreenBrightness { get; set; }
		public byte BgMode { get; set; }
		public byte MainScreenLayers { get; set; }
		public byte SubScreenLayers { get; set; }
		public UInt16 VramAddress { get; set; }
		public UInt16 OamRamAddress { get; set; }
		public byte CgramAddress { get; set; }
		public LiveApiMode7State Mode7 { get; set; } = new();
	}

	public class LiveApiMode7State
	{
		public Int16 HScroll { get; set; }
		public Int16 VScroll { get; set; }
		public Int16 CenterX { get; set; }
		public Int16 CenterY { get; set; }
		public bool LargeMap { get; set; }
		public Int16[] Matrix { get; set; } = Array.Empty<Int16>();
	}

	public class LiveApiTraceRow
	{
		public UInt32 PC { get; set; }
		public string Cpu { get; set; } = "";
		public string ByteCode { get; set; } = "";
		public string Output { get; set; } = "";
	}

	public class LiveApiDisasmLine
	{
		public Int32 Address { get; set; }
		public string Text { get; set; } = "";
		public string ByteCode { get; set; } = "";
		public string Comment { get; set; } = "";
		public UInt16 Flags { get; set; }
	}

	public class LiveApiOperationInfo
	{
		public UInt32 Address { get; set; }
		public Int32 Value { get; set; }
		public string Type { get; set; } = "";
		public string MemType { get; set; } = "";
	}

	public class LiveApiEventInfo
	{
		public string Type { get; set; } = "";
		public string Cpu { get; set; } = "";
		public UInt32 PC { get; set; }
		public Int16 Scanline { get; set; }
		public UInt16 Cycle { get; set; }
		public Int16 BreakpointId { get; set; }
		public sbyte DmaChannel { get; set; }
		public LiveApiOperationInfo Operation { get; set; } = new();
	}

	public class LiveApiStackFrame
	{
		public UInt32 Source { get; set; }
		public UInt32 Target { get; set; }
		public UInt32 Return { get; set; }
		public string Flags { get; set; } = "";
	}

	public class LiveApiExpressionResult
	{
		public string Expression { get; set; } = "";
		public Int64 Value { get; set; }
		public string ResultType { get; set; } = "";
	}

	public class LiveApiBreakpoint
	{
		public int Id { get; set; }
		public string CpuType { get; set; } = "";
		public string MemoryType { get; set; } = "";
		public string Type { get; set; } = "";
		public UInt32 StartAddress { get; set; }
		public UInt32 EndAddress { get; set; }
		public bool Enabled { get; set; }
		public string Condition { get; set; } = "";
	}

	public class LiveApiBreakpointSetRequest
	{
		public string CpuType { get; set; } = "";
		public string MemoryType { get; set; } = "";
		public bool BreakOnRead { get; set; }
		public bool BreakOnWrite { get; set; }
		public bool BreakOnExec { get; set; }
		public bool Forbid { get; set; }
		public bool Enabled { get; set; } = true;
		public bool MarkEvent { get; set; }
		public UInt32 StartAddress { get; set; }
		public UInt32 EndAddress { get; set; } = UInt32.MaxValue;
		public string Condition { get; set; } = "";
	}

	public class LiveApiControlRequest
	{
		public string Action { get; set; } = "";
		public string Cpu { get; set; } = "";
		public UInt32 StepCount { get; set; } = 1;
	}

	public class LiveApiLoadRequest
	{
		public string Path { get; set; } = "";
	}

	public class LiveApiRange
	{
		public string Type { get; set; } = "";
		public UInt32 Start { get; set; }
		public UInt32 Length { get; set; }
	}

	public class LiveApiSubscribeRequest
	{
		public string[]? Events { get; set; }
		public LiveApiRange[]? Ranges { get; set; }
	}

	public class LiveApiChange
	{
		public string Type { get; set; } = "";
		public UInt32 Start { get; set; }
		public string Data { get; set; } = "";
	}

	public class LiveApiEventMessage
	{
		public string Type { get; set; } = "event";
		public string Event { get; set; } = "";
		public UInt64 Frame { get; set; }
		public bool Paused { get; set; }
		public LiveApiChange[]? Changes { get; set; }
		public LiveApiRomInfo? Rom { get; set; }
	}

	public class LiveApiRpcRequest
	{
		public string JsonRpc { get; set; } = "2.0";
		public UInt64? Id { get; set; }
		public string Method { get; set; } = "";
		public System.Text.Json.JsonElement Params { get; set; }
	}

	public class LiveApiRpcResponse
	{
		public string JsonRpc { get; set; } = "2.0";
		public UInt64? Id { get; set; }
		public System.Text.Json.Nodes.JsonNode? Result { get; set; }
		public LiveApiError? Error { get; set; }
	}

	public class LiveApiError
	{
		public int Code { get; set; }
		public string Message { get; set; } = "";
	}
}
