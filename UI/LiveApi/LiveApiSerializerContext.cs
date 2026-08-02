using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mesen.LiveApi
{
	[JsonSerializable(typeof(LiveApiStatus))]
	[JsonSerializable(typeof(LiveApiRomInfo))]
	[JsonSerializable(typeof(LiveApiMemoryRead))]
	[JsonSerializable(typeof(LiveApiMemoryWriteRequest))]
	[JsonSerializable(typeof(LiveApiCpuState))]
	[JsonSerializable(typeof(LiveApiPpuState))]
	[JsonSerializable(typeof(LiveApiMode7State))]
	[JsonSerializable(typeof(LiveApiTraceRow))]
	[JsonSerializable(typeof(LiveApiTraceRow[]))]
	[JsonSerializable(typeof(LiveApiDisasmLine))]
	[JsonSerializable(typeof(LiveApiDisasmLine[]))]
	[JsonSerializable(typeof(LiveApiOperationInfo))]
	[JsonSerializable(typeof(LiveApiEventInfo))]
	[JsonSerializable(typeof(LiveApiEventInfo[]))]
	[JsonSerializable(typeof(LiveApiStackFrame))]
	[JsonSerializable(typeof(LiveApiStackFrame[]))]
	[JsonSerializable(typeof(LiveApiExpressionResult))]
	[JsonSerializable(typeof(LiveApiBreakpoint))]
	[JsonSerializable(typeof(LiveApiBreakpoint[]))]
	[JsonSerializable(typeof(LiveApiBreakpointSetRequest))]
	[JsonSerializable(typeof(LiveApiControlRequest))]
	[JsonSerializable(typeof(LiveApiLoadRequest))]
	[JsonSerializable(typeof(LiveApiRange))]
	[JsonSerializable(typeof(LiveApiRange[]))]
	[JsonSerializable(typeof(LiveApiSubscribeRequest))]
	[JsonSerializable(typeof(LiveApiChange))]
	[JsonSerializable(typeof(LiveApiChange[]))]
	[JsonSerializable(typeof(LiveApiEventMessage))]
	[JsonSerializable(typeof(LiveApiRpcRequest))]
	[JsonSerializable(typeof(LiveApiRpcResponse))]
	[JsonSerializable(typeof(LiveApiError))]
	[JsonSerializable(typeof(string[]))]
	[JsonSerializable(typeof(JsonNode))]
	[JsonSerializable(typeof(JsonObject))]
	[JsonSerializable(typeof(JsonArray))]
	[JsonSerializable(typeof(System.Text.Json.JsonElement))]
	[JsonSourceGenerationOptions(
		WriteIndented = false,
		IgnoreReadOnlyProperties = true,
		UseStringEnumConverter = true,
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
	)]
	public partial class LiveApiSerializerContext : JsonSerializerContext
	{
	}
}
