#include "Common.h"
#include "Core/SNES/Debugger/SnesDebugLog.h"

// R2.1/R2.2: Exports for the cumulative event history and the VRAM write log

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

	DllExport void __stdcall snes_set_wram_log_config(bool enabled, uint32_t start, uint32_t end, uint16_t minLen, int32_t memType)
	{
		SnesWramLog::SetEnabled(enabled, start, end, minLen, memType);
	}

	DllExport uint32_t __stdcall snes_get_wram_log_count()
	{
		return SnesWramLog::GetCount();
	}

	DllExport uint32_t __stdcall snes_get_wram_log(SnesWramLog::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesWramLog::Get(entries, start, count);
	}

	DllExport uint32_t __stdcall snes_get_wram_log_since(SnesWramLog::Entry* entries, uint64_t sinceId, uint32_t count)
	{
		return SnesWramLog::GetSince(entries, sinceId, count);
	}

	DllExport void __stdcall snes_tracker_start(const char* filePath, int32_t memType, uint32_t start, uint32_t end, bool onRead, bool onWrite, uint32_t value, bool valueSet, bool logExec, uint64_t maxBytes, int32_t bufferMode, uint64_t bufferSizeMb)
	{
		SnesTracker::Start(filePath, memType, start, end, onRead, onWrite, value, valueSet, logExec, maxBytes, (uint8_t)bufferMode, bufferSizeMb);
	}

	DllExport void __stdcall snes_tracker_stop()
	{
		SnesTracker::Stop();
	}

	DllExport bool __stdcall snes_tracker_is_enabled()
	{
		return SnesTracker::IsEnabled();
	}

	DllExport bool __stdcall snes_tracker_is_tracking()
	{
		return SnesTracker::IsTracking();
	}

	DllExport uint32_t __stdcall snes_tracker_get_count()
	{
		return SnesTracker::GetCount();
	}

	DllExport uint64_t __stdcall snes_tracker_get_trigger_count()
	{
		return SnesTracker::GetTriggerCount();
	}

	DllExport uint64_t __stdcall snes_tracker_get_buffer_len()
	{
		return SnesTracker::GetBufferLen();
	}

	DllExport uint32_t __stdcall snes_get_tracker_log(SnesTracker::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesTracker::Get(entries, start, count);
	}

	DllExport void __stdcall snes_map_load_log_set_enabled(bool enabled)
	{
		SnesMapLoadLog::SetEnabled(enabled);
	}

	DllExport bool __stdcall snes_map_load_log_is_enabled()
	{
		return SnesMapLoadLog::IsEnabled();
	}

	DllExport uint32_t __stdcall snes_map_load_log_get_count()
	{
		return SnesMapLoadLog::GetCount();
	}

	DllExport uint32_t __stdcall snes_get_map_load_log(SnesMapLoadLog::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesMapLoadLog::Get(entries, start, count);
	}
}
