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

	DllExport bool __stdcall snes_map_load_log_is_auto_stopped()
	{
		return SnesMapLoadLog::IsAutoStopped();
	}

	DllExport void __stdcall snes_map_load_log_set_auto_capture(bool enabled)
	{
		SnesMapLoadLog::SetAutoCapture(enabled);
	}

	DllExport void __stdcall snes_map_load_log_set_live_tracking(bool enabled)
	{
		//R3.2: LiveTracking allein aktiviert NICHT den Tracing-Master-Schalter - das machen
		//explizit die Debug-Endpoints (mapdiag/trace/palettes?live), damit Tracing sauber
		//wieder abgeschaltet werden kann (normaler Emulator-Betrieb ohne PC-Last).
		SnesMapLoadLog::SetLiveTracking(enabled);
	}

	DllExport bool __stdcall snes_map_load_log_is_live_tracking()
	{
		return SnesMapLoadLog::IsLiveTracking();
	}

	DllExport bool __stdcall snes_map_load_log_is_auto_capture()
	{
		return SnesMapLoadLog::IsAutoCapture();
	}

	DllExport bool __stdcall snes_map_load_log_has_auto_burst()
	{
		return SnesMapLoadLog::HasAutoBurst();
	}

	DllExport uint64_t __stdcall snes_map_load_log_get_auto_burst_frame()
	{
		return SnesMapLoadLog::GetAutoBurstStartFrame();
	}

	DllExport uint64_t __stdcall snes_map_load_log_get_auto_burst_end_frame()
	{
		return SnesMapLoadLog::GetAutoBurstEndFrame();
	}

	DllExport void __stdcall snes_map_load_log_clear_auto_burst()
	{
		SnesMapLoadLog::ConsumeAutoBurst();
	}

	DllExport uint32_t __stdcall snes_get_map_load_log_from_frame(SnesMapLoadLog::Entry* entries, uint64_t startFrame, uint64_t endFrame, uint32_t maxCount)
	{
		return SnesMapLoadLog::GetFrameRange(entries, startFrame, endFrame, maxCount);
	}

	DllExport uint32_t __stdcall snes_map_load_dma_src_count()
	{
		return SnesMapLoadLog::GetDmaSrcCount();
	}

	DllExport uint32_t __stdcall snes_map_load_dma_src_first()
	{
		return SnesMapLoadLog::DebugDmaSrcFirst();
	}

	DllExport uint32_t __stdcall snes_map_load_dma_src(SnesMapLoadLog::DmaSrcInterop* entries, uint32_t start, uint32_t count)
	{
		return SnesMapLoadLog::GetDmaSrc(entries, start, count);
	}

	DllExport uint32_t __stdcall snes_map_load_wram_write_count()
	{
		return SnesMapLoadLog::GetWramWriteCount();
	}

	DllExport uint32_t __stdcall snes_map_load_wram_write(SnesMapLoadLog::WramWriteInterop* entries, uint32_t start, uint32_t count)
	{
		return SnesMapLoadLog::GetWramWrite(entries, start, count);
	}

	DllExport uint32_t __stdcall snes_map_load_target_rom_sources(uint8_t targetType, uint32_t targetStart, uint32_t wordCount, uint32_t* outStart, uint32_t* outWords, uint32_t maxResults)
	{
		return SnesMapLoadLog::GetTargetRomSources(targetType, targetStart, wordCount, outStart, outWords, maxResults);
	}

	DllExport uint32_t __stdcall snes_map_load_vram_rom_word(uint32_t wordAddr)
	{
		return SnesMapLoadLog::GetVramRomWord(wordAddr);
	}

	DllExport uint32_t __stdcall snes_map_load_cgram_rom_word(uint32_t wordIdx)
	{
		return SnesMapLoadLog::GetCgramRomWord(wordIdx);
	}

	DllExport uint32_t __stdcall snes_map_load_rom_read_ring_count()
	{
		return SnesMapLoadLog::GetRomReadRingCount();
	}

	DllExport void __stdcall snes_map_load_rom_read_ring_resize(uint32_t size)
	{
		SnesMapLoadLog::SetRomReadRingSize(size);
	}

	DllExport uint32_t __stdcall snes_map_load_rom_read_ring_size()
	{
		return SnesMapLoadLog::GetRomReadRingSize();
	}

	DllExport uint32_t __stdcall snes_map_load_transfer_count()
	{
		return SnesMapLoadLog::GetTransferLogCount();
	}

	//R3.2: MASTER-SCHALTER fuer die gesamte Reverse-Search-Tracing-Logik. Standardmaessig
	//AUS, damit der Emulator ohne Debug-Tools normal performant laeuft.
	DllExport void __stdcall snes_map_load_set_tracing(bool enabled)
	{
		SnesMapLoadLog::SetTracingEnabled(enabled);
	}

	DllExport bool __stdcall snes_map_load_is_tracing()
	{
		return SnesMapLoadLog::IsTracingEnabled();
	}

	DllExport bool __stdcall snes_map_load_is_rom_code(uint32_t addr)
	{
		if(SnesMapLoadLog::IsRomCode) {
			return SnesMapLoadLog::IsRomCode(addr);
		}
		return false;
	}

	DllExport uint32_t __stdcall snes_map_load_dma_debug_bus() { return SnesMapLoadLog::GetDebugDmaBus(); }
	DllExport uint32_t __stdcall snes_map_load_dma_debug_linear() { return SnesMapLoadLog::GetDebugDmaLinear(); }
	DllExport bool __stdcall snes_map_load_dma_debug_isrom() { return SnesMapLoadLog::GetDebugDmaIsRom(); }
	DllExport bool __stdcall snes_map_load_dma_debug_iswram() { return SnesMapLoadLog::GetDebugDmaIsWram(); }

	DllExport void __stdcall snes_map_load_transfer_peek(SnesMapLoadLog::TransferInterop* outEntry)
	{
		outEntry->srcAddr = 0xDEADBEEF;
		outEntry->dstAddr = 0xCAFEBABE;
		outEntry->len = 0xFFFFFFFF;
		outEntry->srcMem = 0x11;
		outEntry->dstMem = 0x22;
		outEntry->via = 0x33;
		outEntry->pad = 0;
		if(SnesMapLoadLog::GetTransferLogCount() == 0) {
			return;
		}
		outEntry->srcAddr = SnesMapLoadLog::GetNewestTransferSrc();
		outEntry->dstAddr = SnesMapLoadLog::GetNewestTransferDst();
		outEntry->len = SnesMapLoadLog::GetNewestTransferLen();
		outEntry->srcMem = SnesMapLoadLog::GetNewestTransferSrcMem();
		outEntry->dstMem = SnesMapLoadLog::GetNewestTransferDstMem();
		outEntry->via = SnesMapLoadLog::GetNewestTransferVia();
	}

	DllExport void __stdcall snes_map_load_transfer_resize(uint32_t size)
	{
		SnesMapLoadLog::SetTransferLogSize(size);
	}

	DllExport void __stdcall snes_map_load_transfer_clear()
	{
		SnesMapLoadLog::ClearTransferLog();
	}

	DllExport uint32_t __stdcall snes_map_load_transfers_to_mem(uint8_t dstMem, uint32_t* outSrc, uint32_t* outDst, uint8_t* outVia, uint32_t maxResults)
	{
		return SnesMapLoadLog::GetTransfersToMem(dstMem, outSrc, outDst, outVia, maxResults);
	}

	DllExport uint32_t __stdcall snes_map_load_transfers_to_range(uint8_t dstMem, uint32_t dstStart, uint32_t dstEnd, uint32_t* outSrc, uint32_t* outDst, uint8_t* outVia, uint32_t maxResults)
	{
		return SnesMapLoadLog::GetTransfersToMemRange(dstMem, dstStart, dstEnd, outSrc, outDst, outVia, maxResults);
	}

	DllExport uint32_t __stdcall snes_map_load_trace(uint8_t dstMem, uint32_t dstAddr, SnesMapLoadLog::TransferInterop* outEntries, uint32_t maxEntries)
	{
		return SnesMapLoadLog::GetTrace(dstMem, dstAddr, outEntries, maxEntries);
	}

	DllExport void __stdcall snes_map_load_rom_read_ring_clear()
	{
		SnesMapLoadLog::ClearRomReadRing();
	}

	DllExport uint32_t __stdcall snes_map_load_rom_read_ring_largest(uint32_t* outStart, uint32_t* outLen, uint32_t maxBlocks, uint32_t scanLimit, uint64_t frameWindow)
	{
		return SnesMapLoadLog::GetLargestRomReads(outStart, outLen, maxBlocks, scanLimit, frameWindow);
	}

	DllExport uint32_t __stdcall snes_map_load_rom_reads_in_frames(uint64_t fromFrame, uint64_t toFrame, uint32_t* outStart, uint32_t* outLen, uint32_t maxResults)
	{
		return SnesMapLoadLog::GetRomReadsInFrames(fromFrame, toFrame, outStart, outLen, maxResults);
	}

	DllExport uint32_t __stdcall snes_map_load_log_get_count()
	{
		return SnesMapLoadLog::GetCount();
	}

	DllExport uint32_t __stdcall snes_get_map_load_log(SnesMapLoadLog::Entry* entries, uint32_t start, uint32_t count)
	{
		return SnesMapLoadLog::Get(entries, start, count);
	}

	DllExport uint32_t __stdcall snes_get_rom_read_log(SnesMapLoadLog::RomReadBlock* blocks, uint32_t start, uint32_t count)
	{
		return SnesMapLoadLog::GetRomReadLog(blocks, start, count);
	}

	DllExport uint32_t __stdcall snes_get_rom_read_count()
	{
		return SnesMapLoadLog::GetRomReadCount();
	}

	DllExport bool __stdcall snes_rom_was_read(uint32_t romOffset)
	{
		return SnesMapLoadLog::WasRomRead(romOffset);
	}

	DllExport bool __stdcall snes_map_load_dma_has_source(uint32_t romOffset)
	{
		return SnesMapLoadLog::DmaHasSource(romOffset);
	}

	DllExport bool __stdcall snes_map_load_wram_chain_target(uint32_t romOffset)
	{
		return SnesMapLoadLog::WramChainTarget(romOffset);
	}

	DllExport void __stdcall snes_get_rom_read_range(uint32_t romOffset, uint32_t gapBytes, uint32_t* outStart, uint32_t* outEnd)
	{
		SnesMapLoadLog::GetRomReadRangeAround(romOffset, gapBytes, outStart, outEnd);
	}

	DllExport uint32_t __stdcall snes_get_rom_read_blocks(uint32_t* outStart, uint32_t* outLength, uint32_t maxBlocks, uint32_t gapBytes)
	{
		return SnesMapLoadLog::GetRomReadBlocks(outStart, outLength, maxBlocks, gapBytes);
	}

	DllExport uint32_t __stdcall snes_get_wram_rom_source(uint32_t wramAddr)
	{
		return SnesMapLoadLog::GetWramRomSource(wramAddr);
	}

	DllExport uint32_t __stdcall snes_get_rom_target_blocks(uint32_t* outStart, uint32_t* outLength, uint32_t maxBlocks, uint32_t gapBytes)
	{
		return SnesMapLoadLog::GetRomTargetBlocks(outStart, outLength, maxBlocks, gapBytes);
	}

}
