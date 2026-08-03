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


// R3.1: WRAM/Register-Write-Historie (CPU-Writes mit PC, Adressfilter + minLen-Coalescing)
// Analog zu DMA-Log / events-history, aber für beliebige Speicher-Domänen (default WRAM).
// Aufeinanderfolgende Writes (gleicher PC, benachbarte Adressen, gleicher Frame) werden zu
// einem "Run" (width) zusammengefasst; nur Runs mit width >= MinLen werden geloggt (Anti-Flut).
class SnesWramLog
{
public:
	static constexpr int LogSize = 1 << 14;

	struct Entry
	{
		uint64_t id;
		uint64_t frame;
		int32_t cycle;
		uint32_t pc;
		uint16_t addr;
		uint8_t bank;
		uint8_t value;
		uint16_t width;
		int8_t memType;
		uint8_t reserved;
	};
	static_assert(sizeof(Entry) == 32, "SnesWramLog::Entry must be 32 bytes");

	static Entry Log[LogSize];
	static uint32_t Head;
	static uint32_t Count;
	static uint64_t NextId;
	static bool Enabled;
	static uint32_t RangeStart;
	static uint32_t RangeEnd;
	static uint16_t MinLen;
	static int32_t FilterMemType;

	static bool HasPending;
	static uint32_t PendingStartAddr;
	static uint64_t PendingFrame;
	static uint32_t PendingPc;
	static uint32_t PendingAddr;
	static int32_t PendingCycle;
	static uint8_t PendingValue;
	static uint16_t PendingWidth;
	static int8_t PendingMemType;

	static void Flush();

	static void SetEnabled(bool enabled, uint32_t start, uint32_t end, uint16_t minLen, int32_t memType)
	{
		Flush();
		bool configChanged = enabled != Enabled || start != RangeStart || end != RangeEnd || minLen != MinLen || memType != FilterMemType;
		if(configChanged) {
			//Config-Änderung (oder Re-Aktivierung) leert den Ring (verwirft Stale-Einträge)
			Head = 0;
			Count = 0;
		}
		Enabled = enabled;
		RangeStart = start;
		RangeEnd = end;
		MinLen = minLen;
		FilterMemType = memType;
	}

	static uint32_t GetCount()
	{
		Flush();
		return Count;
	}

	static void Append(uint32_t frame, int32_t cycle, uint32_t pc, uint32_t addr24, uint8_t value, MemoryType memType)
	{
		if(!Enabled) {
			return;
		}

		uint16_t addr = (uint16_t)(addr24 & 0xFFFF);
		if((int32_t)memType != FilterMemType || addr < RangeStart || addr > RangeEnd) {
			//Nicht im Filter: laufenden Run beenden (Kontinuitätsbruch)
			Flush();
			return;
		}

		if(HasPending) {
			if(frame != PendingFrame) {
				Flush();
			} else if(pc == PendingPc && (int8_t)memType == PendingMemType && addr24 == PendingAddr + 1) {
				//Kontinuierlicher Run erweitern
				PendingWidth++;
				PendingAddr = addr24;
				return;
			} else {
				Flush();
			}
		}

		//Neuen Run starten
		HasPending = true;
		PendingFrame = frame;
		PendingPc = pc;
		PendingStartAddr = addr24;
		PendingAddr = addr24;
		PendingCycle = cycle;
		PendingValue = value;
		PendingWidth = 1;
		PendingMemType = (int8_t)memType;
	}

	static uint32_t Get(Entry* entries, uint32_t start, uint32_t count)
	{
		Flush();
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
		Flush();
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


// Universal-Tracker: Trigger auf Memory-Lesen/Schreiben einer Region; danach wird ein
// chronologischer Ablauf (Exec/MemWrite/VRAM/DMA/Interrupt) in Ring + Datei geloggt.
class SnesTracker
{
public:
	static constexpr int LogSize = 1 << 16;

	static constexpr uint8_t Exec = 0;
	static constexpr uint8_t MemW = 2;
	static constexpr uint8_t Vram = 3;
	static constexpr uint8_t Dma = 4;
	static constexpr uint8_t Nmi = 5;
	static constexpr uint8_t Irq = 6;

	struct Entry
	{
		uint64_t id;
		uint64_t frame;
		int32_t cycle;
		uint8_t type;
		uint8_t bank;
		uint16_t addr;
		uint32_t pc;
		uint8_t value;   // DMA: channel | (hdma<<7)
		uint8_t extra;   // DMA: length low 8
		uint16_t extra2; // DMA: length high 16
	};
	static_assert(sizeof(Entry) == 32, "SnesTracker::Entry must be 32 bytes");

	static Entry Log[LogSize];
	static uint32_t Head;
	static uint32_t Count;
	static uint64_t NextId;
	static bool Enabled;
	static bool Tracking;
	static uint64_t TriggerCount;

	static int32_t TriggerMemType;
	static uint32_t TriggerStart;
	static uint32_t TriggerEnd;
	static bool TriggerOnRead;
	static bool TriggerOnWrite;
	static uint32_t TriggerValue;
	static bool TriggerValueSet;

	static char FilePath[1024];
	static uint8_t** Chunks;
	static uint64_t ChunkSize;
	static uint32_t ChunkCount;
	static uint32_t CurrentChunk;
	static uint64_t ChunkOffset;
	static uint64_t ChunksFilled;
	static uint32_t FirstChunk;
	static uint64_t RamLen;
	static bool RamWrapped;
	static bool RamWrap;
	static uint8_t BufferMode;
	static bool LogExec;
	static uint64_t MaxBytes;
	static uint64_t FileBytes;
	static FILE* File;
	static char FileBuffer[8192];
	static std::atomic<long> WriteCount;
	static uint32_t FileBufferLen;

	static void Start(const char* filePath, int32_t memType, uint32_t start, uint32_t end, bool onRead, bool onWrite, uint32_t value, bool valueSet, bool logExec, uint64_t maxBytes, uint8_t bufferMode, uint64_t bufferSizeMb);
	static void Stop();
	static void WriteRamLine(const char* line, int len);
	static void FlushFile();
	static void Trigger();

	static void Append(uint8_t type, uint32_t frame, int32_t cycle, uint32_t pc, uint32_t addr24, uint8_t value, uint8_t extra, uint16_t extra2);
	static void CheckMemoryOp(uint8_t opType, uint32_t pc, uint32_t frame, int32_t cycle, uint32_t addr24, uint8_t value, MemoryType memType);
	static void AppendExec(uint32_t frame, int32_t cycle, uint32_t pc);
	static void AppendInterrupt(uint8_t type, uint32_t frame, int32_t cycle, uint32_t pc);

	static uint32_t GetCount() { return Count; }
	static uint64_t GetBufferLen() { return BufferMode == 1 ? RamLen : 0; }
	static bool IsEnabled() { return Enabled; }
	static bool IsTracking() { return Tracking; }
	static uint64_t GetTriggerCount() { return TriggerCount; }

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
