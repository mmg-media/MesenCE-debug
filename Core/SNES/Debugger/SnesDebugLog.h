#pragma once
#include "pch.h"
#include "Debugger/BaseEventManager.h"

// R2.1: Cumulative event history (ring buffer, only filled while logging is active)
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
			//(Re-)activation clears the ring (discards stale entries)
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

	//Incremental fetch starting from an event ID (ids are monotonically increasing)
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

// R2.2: VRAM write history (writes to 0x2118/0x2119, with PC and target address)
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
			//(Re-)activation clears the ring (discards stale entries)
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

// R3.2: Map-load source tracing - captures every VRAM/CGRAM write (CPU or DMA)
// together with the ROM/WRAM address the data was read from, so a map change can
// be traced back to the exact ROM address that provides tilemap/tiles/palette.
// sourceType: 0 = DMA (sourceAddr = SrcBank:SrcAddr), 1 = CPU (sourceAddr = last ROM/WRAM read).
class SnesMapLoadLog
{
public:
	static constexpr int LogSize = 1 << 16;
	static constexpr uint8_t Cpu = 1;
	static constexpr uint8_t Dma = 0;

	struct Entry
	{
		uint64_t frame;
		int32_t cycle;
		uint8_t sourceType;   // 0=DMA, 1=CPU
		uint8_t targetType;   // 0=VRAM, 1=CGRAM
		uint32_t targetAddr;  // VRAM word address / CGRAM index
		uint8_t value;
		uint32_t sourceAddr;  // DMA: SrcBank<<16|SrcAddr; CPU: last ROM/WRAM read
		uint8_t sourceMem;    // CPU: MemoryType of source read; DMA: 0
		uint8_t channel;      // DMA channel, 0xFF for CPU
		uint32_t length;      // DMA transfer size, 1 for CPU
		uint32_t pc;          // CPU write PC
	};

	static Entry Log[LogSize];
	static uint32_t Head;
	static uint32_t Count;
	static bool Enabled;
	static uint32_t LastRomRead;
	static uint32_t LastWramRead;

	static void SetEnabled(bool enabled)
	{
		if(enabled && !Enabled) {
			Head = 0;
			Count = 0;
		}
		Enabled = enabled;
	}

	static uint32_t GetCount() { return Count; }
	static bool IsEnabled() { return Enabled; }

	//Remember the last ROM/WRAM address the CPU read, so a following VRAM/CGRAM
	//write can be attributed to the data source.
	static void TrackRead(MemoryType memType, uint32_t addr24)
	{
		if(memType == MemoryType::SnesPrgRom) {
			LastRomRead = addr24;
		} else if(memType == MemoryType::SnesWorkRam || memType == MemoryType::SnesSaveRam) {
			LastWramRead = addr24;
		}
	}

	static void AppendCpuWrite(uint32_t frame, int32_t cycle, uint8_t targetType, uint32_t targetAddr, uint8_t value, uint32_t pc)
	{
		if(!Enabled) {
			return;
		}

		uint8_t sourceMem = 0;
		uint32_t sourceAddr = 0;
		if(LastRomRead != 0xFFFFFFFF) {
			sourceAddr = LastRomRead;
			sourceMem = (uint8_t)MemoryType::SnesPrgRom;
		} else if(LastWramRead != 0xFFFFFFFF) {
			sourceAddr = LastWramRead;
			sourceMem = (uint8_t)MemoryType::SnesWorkRam;
		}

		Entry& e = Log[Head];
		e.frame = frame;
		e.cycle = cycle;
		e.sourceType = Cpu;
		e.targetType = targetType;
		e.targetAddr = targetAddr;
		e.value = value;
		e.sourceAddr = sourceAddr;
		e.sourceMem = sourceMem;
		e.channel = 0xFF;
		e.length = 1;
		e.pc = pc;

		Head = (Head + 1) % LogSize;
		if(Count < LogSize) {
			Count++;
		}
	}

	static void AppendDma(uint32_t frame, int32_t cycle, uint8_t targetType, uint32_t targetAddr, uint32_t srcBank, uint32_t srcAddr, uint32_t length, uint8_t channel)
	{
		if(!Enabled) {
			return;
		}

		Entry& e = Log[Head];
		e.frame = frame;
		e.cycle = cycle;
		e.sourceType = Dma;
		e.targetType = targetType;
		e.targetAddr = targetAddr;
		e.value = 0;
		e.sourceAddr = (srcBank << 16) | (srcAddr & 0xFFFF);
		e.sourceMem = 0;
		e.channel = channel;
		e.length = length;
		e.pc = 0;

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

// R3.1: WRAM/register write history (CPU writes with PC, address filter + minLen coalescing)
// Similar to the DMA log / events-history, but for arbitrary memory domains (default WRAM).
// Consecutive writes (same PC, adjacent addresses, same frame) are merged into
// one "run" (width); only runs with width >= MinLen are logged (anti-flood).
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
			//Config change (or re-activation) clears the ring (discards stale entries)
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
			//Not in the filter: end the current run (continuity break)
			Flush();
			return;
		}

		if(HasPending) {
			if(frame != PendingFrame) {
				Flush();
			} else if(pc == PendingPc && (int8_t)memType == PendingMemType && addr24 == PendingAddr + 1) {
				//Extend a continuous run
				PendingWidth++;
				PendingAddr = addr24;
				return;
			} else {
				Flush();
			}
		}

		//Start a new run
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

	//Incremental fetch starting from an event ID (ids are monotonically increasing)
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


// Universal tracker: trigger on memory read/write of a region; afterwards, a
// chronological trace (Exec/MemWrite/VRAM/DMA/Interrupt) is logged to ring + file.
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
