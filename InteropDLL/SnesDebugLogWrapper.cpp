#include "Common.h"
#include "Core/SNES/Debugger/SnesDebugLog.h"

// R2.1/R2.2: Exports für die kumulative Event-Historie und den VRAM-Write-Log

extern "C" {
	DllExport void __stdcall snes_set_event_log_enabled(bool enabled)
	{
		SnesEventLog::SetEnabled(enabled);
	}

	DllExport uint32_t __stdcall snes_get_event_log_count()
	{
		return SnesEventLog::GetCount();
	}

	DllExport uint32_t __stdcall snes_get_event_log(SnesEventLog::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesEventLog::Get(entries, start, count);
	}

	DllExport uint32_t __stdcall snes_get_event_log_since(SnesEventLog::Entry* entries, uint64_t sinceId, uint32_t count)
	{
		return SnesEventLog::GetSince(entries, sinceId, count);
	}

	DllExport void __stdcall snes_set_vram_log_enabled(bool enabled)
	{
		SnesVramLog::SetEnabled(enabled);
	}

	DllExport uint32_t __stdcall snes_get_vram_log_count()
	{
		return SnesVramLog::GetCount();
	}

	DllExport uint32_t __stdcall snes_get_vram_log(SnesVramLog::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesVramLog::Get(entries, start, count);
	}
}
