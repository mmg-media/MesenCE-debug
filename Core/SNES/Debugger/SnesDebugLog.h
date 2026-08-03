#pragma once
#include "pch.h"
#include "Debugger/BaseEventManager.h"

// R2.1: Kumulative Event-Historie (Ring-Puffer, nur bei aktivem Logging gefüllt)
class SnesEventLog
{
public:
	static constexpr int LogSize = 1 << 14;

	struct Entry
	{
		uint64_t id;
		uint64_t frame;
		int32_t cycle;
		int16_t scanline;
		int32_t type;
		uint32_t pc;
		int16_t breakpointId;
		int8_t dmaChannel;
		uint32_t opAddress;
		int32_t opValue;
		int8_t opType;
		int8_t opMemType;
	};
	static_assert(sizeof(Entry) == 48, "SnesEventLog::Entry must be 48 bytes");

	static Entry Log[LogSize];
	static uint32_t Head;
	static uint32_t Count;
	static uint64_t NextId;
	static bool Enabled;

	static void SetEnabled(bool enabled)
	{
		if(enabled && !Enabled) {
			//(Re-)Aktivierung leert den Ring (verwirft Stale-Einträge)
			Head = 0;
			Count = 0;
		}
		Enabled = enabled;
	}

	static uint32_t GetCount()
	{
		return Count;
	}

	static void Append(DebugEventInfo& evt, uint32_t frame)
	{
		if(!Enabled) {
			return;
		}

		Entry& e = Log[Head];
		e.id = NextId++;
		e.frame = frame;
		e.cycle = evt.Cycle;
		e.scanline = evt.Scanline;
		e.type = (int32_t)evt.Type;
		e.pc = evt.ProgramCounter;
		e.breakpointId = evt.BreakpointId;
		e.dmaChannel = evt.DmaChannel;
		e.opAddress = evt.Operation.Address;
		e.opValue = evt.Operation.Value;
		e.opType = (int8_t)evt.Operation.Type;
		e.opMemType = (int8_t)evt.Operation.MemType;

		Head = (Head + 1) % LogSize;
		if(Count < LogSize) {
			Count++;
		}
	}

	static uint32_t Get(Entry* entries, uint32_t start, uint32_t count)
	{
		if(start >= Count || count == 0) {
			return 0;
		}

		uint32_t n = std::min(count, Count - start);
		uint32_t oldest = Count < LogSize ? 0 : Head;
		for(uint32_t i = 0; i < n; i++) {
			entries[i] = Log[(oldest + start + i) % LogSize];
		}
		return n;
	}

	//Inkrementelles Abholen ab einer Event-ID (ids sind monoton steigend)
	static uint32_t GetSince(Entry* entries, uint64_t sinceId, uint32_t count)
	{
		if(!Enabled || Count == 0 || count == 0) {
			return 0;
		}

		uint64_t firstId = NextId - Count;
		uint32_t start = sinceId > firstId ? (uint32_t)(sinceId - firstId) : 0;
		if(start >= Count) {
			return 0;
		}

		uint32_t n = std::min(count, Count - start);
		uint32_t oldest = Count < LogSize ? 0 : Head;
		for(uint32_t i = 0; i < n; i++) {
			entries[i] = Log[(oldest + start + i) % LogSize];
		}
		return n;
	}
};

// R2.2: VRAM-Write-Historie (Writes auf 0x2118/0x2119, mit PC und Zieladresse)
class SnesVramLog
{
public:
	static constexpr int LogSize = 1 << 14;

	struct Entry
	{
		uint64_t frame;
		int32_t cycle;
		uint32_t pc;
		int16_t scanline;
		int8_t isDma;
		uint8_t value;
		uint32_t vramAddr;
	};
	static_assert(sizeof(Entry) == 24, "SnesVramLog::Entry must be 24 bytes");

	static Entry Log[LogSize];
	static uint32_t Head;
	static uint32_t Count;
	static bool Enabled;

	static void SetEnabled(bool enabled)
	{
		if(enabled && !Enabled) {
			//(Re-)Aktivierung leert den Ring (verwirft Stale-Einträge)
			Head = 0;
			Count = 0;
		}
		Enabled = enabled;
	}

	static uint32_t GetCount()
	{
		return Count;
	}

	static void Append(uint32_t frame, int32_t cycle, int16_t scanline, uint32_t pc, uint32_t vramAddr, uint8_t value)
	{
		if(!Enabled) {
			return;
		}

		Entry& e = Log[Head];
		e.frame = frame;
		e.cycle = cycle;
		e.scanline = scanline;
		e.pc = pc;
		e.vramAddr = vramAddr;
		e.value = value;
		e.isDma = 0;

		Head = (Head + 1) % LogSize;
		if(Count < LogSize) {
			Count++;
		}
	}

	static uint32_t Get(Entry* entries, uint32_t start, uint32_t count)
	{
		if(start >= Count || count == 0) {
			return 0;
		}

		uint32_t n = std::min(count, Count - start);
		uint32_t oldest = Count < LogSize ? 0 : Head;
		for(uint32_t i = 0; i < n; i++) {
			entries[i] = Log[(oldest + start + i) % LogSize];
		}
		return n;
	}
};
