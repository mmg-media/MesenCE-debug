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
	static constexpr int LogSize = 1 << 21;  //2M entries (~80MB) - keeps the full map change
	static constexpr uint8_t Cpu = 1;
	static constexpr uint8_t Dma = 0;
	//R3.2: auto-stop - only after a real map-load burst. A load writes many VRAM words
	//in quick succession (VramBurst climbs past AutoStopVramThreshold). Once the burst has
	//ended (no VRAM write for AutoStopQuietFrames) the capture stops so the following CGRAM
	//palette fades can't overwrite the load from the ring. Stray VRAM writes during normal
	//gameplay (gap > BurstGapFrames) reset the burst counter and never trigger the auto-stop.
	static constexpr uint32_t AutoStopVramThreshold = 512;
	static constexpr uint32_t BurstGapFrames = 30;
	static constexpr uint32_t AutoStopQuietFrames = 30;
	//R3.2: consecutive CPU writes to VRAM/CGRAM with a consistent source (a copy loop
	//reading ROM/WRAM) are coalesced into ONE entry so a real map/tile/palette load
	//appears as a single ROM source with the correct length. Short runs (< MinCpuRunLen)
	//are discarded - those are palette fades / single tile patches, not loads.
	static constexpr uint32_t MinCpuRunLen = 8;
	//R3.2: ROM read blocks - sequential data reads from the ROM (decompression/copy
	//loops) are coalesced into contiguous blocks. These are the exact ROM address ranges
	//that provide the map data, so they can be extracted from the ROM file directly.
	static constexpr int RomReadLogSize = 256;
	//R3.2: deterministic ROM read bitmap - every data read from the ROM marks the
	//corresponding byte (0x400000 = 4MB ROM -> 0x400000 bits -> 512KB bitmap). This
	//guarantees no read is missed, regardless of access pattern.
	static constexpr int RomReadBitmapSize = 0x400000 / 8;
	//R3.2: target bitmap - marks ROM addresses whose data was actually copied to
	//VRAM/CGRAM during the capture (via DMA or CPU write). This filters out plain
	//code/table reads and shows ONLY the real map-data sources (palette/tiles/tilemap).
	static constexpr int RomTargetBitmapSize = 0x400000 / 8;

	struct RomReadBlock
	{
		uint64_t frame;
		uint32_t startAddr;   // linear ROM address (bus addr) of the first byte
		uint32_t length;
		uint8_t targetType;   // 0=unknown, 1=VRAM, 2=CGRAM (from the next write)
		uint8_t pad[3];
	};  //sizeof = 24 (8-aligned)

	struct Entry
	{
		uint64_t frame;
		int32_t cycle;
		uint8_t sourceType;   // 0=DMA, 1=CPU
		uint8_t targetType;   // 0=VRAM, 1=CGRAM
		uint32_t targetAddr;  // VRAM word address / CGRAM index
		uint8_t value;
		uint32_t sourceAddr;  // DMA: linear ROM/WRAM offset (resolved); CPU: last read
		uint8_t sourceMem;    // CPU: MemoryType of source read; DMA: 0=ROM, 1=WRAM-unresolved
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
	static uint32_t LastVramFrame;
	static uint32_t VramBurst;
	static bool AutoStopped;
	//R3.2: auto-capture - the log runs without manual arm. When a real map-load burst
	//(a VRAM load burst) is detected AND ends, the log FREEZES (Enabled=false) so it holds
	//exactly the map load. The report consumes the frozen entries and re-arms the capture,
	//so the next map change is captured automatically.
	static bool AutoCapture;
	static uint64_t AutoCaptureBurstStartFrame;   //frame of the first VRAM write of the current burst
	static uint32_t AutoCaptureBurstVram;         //VRAM bytes in the current burst window
	static uint64_t AutoCaptureLastBurstStartFrame;  //frame range of the last completed burst
	static uint64_t AutoCaptureLastBurstEndFrame;
	static bool AutoCaptureHasBurst;
	//R3.2: LIVE tracking - keep the reverse-lookup tables (CgramRomWord/VramRomWord)
	//continuously up to date so the palette viewer shows current ROM sources without
	//needing a map-load report. When set, the log never freezes and TrackTargetRom runs
	//for every DMA even outside a detected burst.
	static bool LiveTracking;
	//R3.2: WRAM->ROM resolution - see TrackWramWrite below (majority-vote, NMI-robust).

	static RomReadBlock RomReadLog[RomReadLogSize];
	static uint32_t RomReadHead;
	static uint32_t RomReadCount;
	static uint32_t RomReadPendingAddr;
	static uint32_t RomReadPendingLen;
	static uint8_t RomReadPendingTarget;
	static uint32_t RomReadPendingFrame;
	//R3.2: REVERSE LOOKUP tables - for every target position (VRAM word / CGRAM word)
	//remember the ROM file offset that filled it. This is the user's "rückwärtssuche":
	//we KNOW where the palette sits in CGRAM and the tiles/tilemap in VRAM, so we can
	//look up directly which ROM offset each target position came from - no need to trace
	//the fragile LastRomRead forward chain. Updated only while Enabled, so the frozen
	//auto-capture state reflects exactly the map-load burst.
	static uint32_t VramRomWord[0x8000];  //VRAM word address -> ROM byte offset that filled it
	static uint32_t CgramRomWord[0x100];  //CGRAM word index   -> ROM byte offset that filled it

	//Record that targetAddr (VRAM word / CGRAM word) was filled with wordCount words
	//starting at ROM offset romSource (which increments by 2 bytes per SNES color word).
	//romSource is the EXACT linear ROM offset (already WRAM-resolved for WRAM sources).
	static void TrackTargetRom(uint8_t targetType, uint32_t targetAddr, uint32_t romSource, uint32_t wordCount)
	{
		if(!Enabled && !LiveTracking) {
			return;
		}
		if(targetType == 1) {  //CGRAM
			for(uint32_t i = 0; i < wordCount && targetAddr + i < 0x100; i++) {
				CgramRomWord[targetAddr + i] = romSource + i * 2;
			}
		} else {  //VRAM (word address space, 0x0000-0x7FFF)
			for(uint32_t i = 0; i < wordCount && targetAddr + i < 0x8000; i++) {
				VramRomWord[targetAddr + i] = romSource + i * 2;
			}
		}
	}

	//R3.2: reverse lookup - the ROM offsets that filled a contiguous target range.
	//Returns the distinct contiguous ROM sources (start + word count) for the range.
	static uint32_t GetTargetRomSources(uint8_t targetType, uint32_t targetStart, uint32_t wordCount, uint32_t* outStart, uint32_t* outWords, uint32_t maxResults)
	{
		uint32_t results = 0;
		uint32_t* table = targetType == 1 ? CgramRomWord : VramRomWord;
		uint32_t tableSize = targetType == 1 ? 0x100 : 0x8000;
		if(targetStart >= tableSize) {
			return 0;
		}
		uint32_t end = std::min<uint32_t>(tableSize, targetStart + wordCount);
		uint32_t i = targetStart;
		while(i < end && results < maxResults) {
			uint32_t src = table[i];
			if(src == 0xFFFFFFFF || src == 0) {
				i++;
				continue;  //unfilled position
			}
			//walk the contiguous run (source increments by 2 per word)
			uint32_t runStart = src - (i - targetStart) * 2;
			uint32_t j = i;
			while(j < end && table[j] != 0xFFFFFFFF && table[j] != 0
				&& table[j] == runStart + (j - targetStart) * 2) {
				j++;
			}
			outStart[results] = runStart;
			outWords[results] = j - i;
			results++;
			i = j;
		}
		return results;
	}
	static uint32_t GetVramRomWord(uint32_t wordAddr) { return wordAddr < 0x8000 ? VramRomWord[wordAddr] : 0xFFFFFFFF; }
	static uint32_t GetCgramRomWord(uint32_t wordIdx) { return wordIdx < 0x100 ? CgramRomWord[wordIdx] : 0xFFFFFFFF; }

	static uint8_t RomReadBitmap[RomReadBitmapSize];  //1 bit per ROM byte read since arm
	static uint8_t RomTargetBitmap[RomTargetBitmapSize];  //1 bit per ROM byte copied to VRAM/CGRAM

	//R3.2: DMA source diagnostics - the RAW bus address (SrcBank:SrcAddress) the game
	//programmed for each DMA to VRAM/CGRAM/WRAM, BEFORE mapping to linear offsets.
	//This shows exactly what the game hardware register value was.
	static constexpr int DmaSrcLogSize = 512;
	struct DmaSrcEntry
	{
		uint64_t frame;
		uint32_t srcBus;      // SrcBank<<16 | SrcAddress (raw register value)
		uint8_t destAddr;     // DestAddress (0x18 VRAM, 0x22 CGRAM, 0x80 WRAM)
		uint8_t channel;
		uint8_t pad[2];
	};  //16 bytes
	//Interop struct for GetDmaSrc - flat 16-byte layout that marshals 1:1 to C#.
	#pragma pack(push, 1)
	struct DmaSrcInterop
	{
		uint64_t frame;
		uint32_t srcBus;
		uint8_t destAddr;
		uint8_t channel;
		uint8_t pad0;
		uint8_t pad1;
	};
	#pragma pack(pop)
	static_assert(sizeof(DmaSrcInterop) == 16, "DmaSrcInterop must be 16 bytes");
	static DmaSrcEntry DmaSrcLog[DmaSrcLogSize];
	static uint32_t DmaSrcHead;
	static uint32_t DmaSrcCount;
	static void AppendDmaSrc(uint64_t frame, uint32_t srcBus, uint8_t destAddr, uint8_t channel)
	{
		DmaSrcEntry& e = DmaSrcLog[DmaSrcHead];
		e.frame = frame;
		e.srcBus = srcBus;
		e.destAddr = destAddr;
		e.channel = channel;
		DmaSrcHead = (DmaSrcHead + 1) % DmaSrcLogSize;
		if(DmaSrcCount < DmaSrcLogSize) {
			DmaSrcCount++;
		}
	}
	static uint32_t GetDmaSrc(DmaSrcInterop* entries, uint32_t start, uint32_t count)
	{
		if(start >= DmaSrcCount || count == 0) {
			return 0;
		}
		uint32_t n = std::min(count, DmaSrcCount - start);
		uint32_t oldest = DmaSrcCount < DmaSrcLogSize ? 0 : DmaSrcHead;
		for(uint32_t i = 0; i < n; i++) {
			DmaSrcEntry& s = DmaSrcLog[(oldest + start + i) % DmaSrcLogSize];
			entries[i].frame = s.frame;
			entries[i].srcBus = s.srcBus;
			entries[i].destAddr = s.destAddr;
			entries[i].channel = s.channel;
			entries[i].pad0 = 0;
			entries[i].pad1 = 0;
		}
		return n;
	}
	static uint32_t GetDmaSrcCount() { return DmaSrcCount; }
	static uint32_t DebugDmaSrcFirst() { return DmaSrcCount > 0 ? DmaSrcLog[0].srcBus : 0xDEADBEEF; }

	//R3.2: WRAM write trace - CPU writes to WRAM with the LastRomRead at write time.
	//This reveals the palette copy-loop source (ROM 0x33EED4 -> WRAM 0x10600).
	static constexpr int WramWriteLogSize = 512;
	struct WramWriteEntry
	{
		uint64_t frame;
		uint32_t wramAddr;   // linear WRAM address (0x00000-0x1FFFF)
		uint32_t romRead;    // LastRomRead at write time (or 0xFFFFFFFF if none)
		uint8_t memType;     // MemoryType of the source read
		uint8_t pad[3];
	};  //16 bytes
	static WramWriteEntry WramWriteLog[WramWriteLogSize];
	static uint32_t WramWriteHead;
	static uint32_t WramWriteCount;
	static void AppendWramWrite(uint64_t frame, uint32_t wramAddr, uint32_t romRead, uint8_t memType)
	{
		if(wramAddr >= 0x20000) {
			return;
		}
		WramWriteEntry& e = WramWriteLog[WramWriteHead];
		e.frame = frame;
		e.wramAddr = wramAddr;
		e.romRead = romRead;
		e.memType = memType;
		WramWriteHead = (WramWriteHead + 1) % WramWriteLogSize;
		if(WramWriteCount < WramWriteLogSize) {
			WramWriteCount++;
		}
	}
	static uint32_t GetWramWriteCount() { return WramWriteCount; }
	#pragma pack(push, 1)
	struct WramWriteInterop
	{
		uint64_t frame;
		uint32_t wramAddr;
		uint32_t romRead;
		uint8_t memType;
		uint8_t pad0;
		uint8_t pad1;
		uint8_t pad2;
	};
	#pragma pack(pop)
	static_assert(sizeof(WramWriteInterop) == 20, "WramWriteInterop must be 20 bytes");
	static uint32_t GetWramWrite(WramWriteInterop* entries, uint32_t start, uint32_t count)
	{
		if(start >= WramWriteCount || count == 0) {
			return 0;
		}
		uint32_t n = std::min(count, WramWriteCount - start);
		uint32_t oldest = WramWriteCount < WramWriteLogSize ? 0 : WramWriteHead;
		for(uint32_t i = 0; i < n; i++) {
			WramWriteEntry& s = WramWriteLog[(oldest + start + i) % WramWriteLogSize];
			entries[i].frame = s.frame;
			entries[i].wramAddr = s.wramAddr;
			entries[i].romRead = s.romRead;
			entries[i].memType = s.memType;
		}
		return n;
	}

	static void MarkRomRead(uint32_t addr)
	{
		if(addr < 0x400000) {
			RomReadBitmap[addr >> 3] |= (uint8_t)(1 << (addr & 7));
		}
	}

	static void MarkRomTarget(uint32_t addr, uint32_t length)
	{
		for(uint32_t a = addr; a < addr + length && a < 0x400000; a++) {
			RomTargetBitmap[a >> 3] |= (uint8_t)(1 << (a & 7));
		}
	}

	static void FlushRomReadBlock()
	{
		if(RomReadPendingLen == 0) {
			return;
		}
		RomReadBlock& b = RomReadLog[RomReadHead];
		b.frame = RomReadPendingFrame;
		b.startAddr = RomReadPendingAddr - RomReadPendingLen;
		b.length = RomReadPendingLen;
		b.targetType = RomReadPendingTarget;
		RomReadHead = (RomReadHead + 1) % RomReadLogSize;
		if(RomReadCount < RomReadLogSize) {
			RomReadCount++;
		}
		RomReadPendingLen = 0;
	}

	//wramAddr is the LINEAR WRAM address (0x00000-0x1FFFF: bank 0x7E = 0x00000-0x0FFFF,
	//bank 0x7F = 0x10000-0x1FFFF) - the linear index into the 128KB work RAM.
	//Deterministic mapping with NMI-robustness: a copy loop reads ROM addr R+i and
	//writes WRAM addr W+i synchronously. For each WRAM page we store the ROM source of
	//the page START = R + (W&0xFF) - (W&0xFF)... i.e. romRead - (wramAddr & 0xFF) gives the
	//same value for every byte of a synchronous copy, so the majority vote is exact.
	//A single NMI between read and write produces a wrong romRead (the interrupt vector);
	//it loses the vote. No filtering by known values.
	//BYTE-EXACT map: every WRAM byte stores the exact ROM byte it was copied from.
	static uint32_t WramRomByte[0x20000];  //128KB WRAM -> exact ROM source per byte
	//R3.2: STABILITY voting - a real map-load writes a consistent ROM source per WRAM byte
	//and keeps it. Fades/effects rewrite the same WRAM position with CHANGING sources every
	//frame. Each byte counts how many consecutive writes agreed on the current source; a
	//different source only takes over after it has been observed enough to displace it.
	static constexpr int8_t VoteReset = 3;  //number of agreeing writes to lock a source
	static int8_t WramRomVote[0x20000];

	//R3.2: ROM-read ring WITH frame timestamps - for reverse-search of decompressed data.
	//A decompression routine reads a contiguous ROM block and writes decompressed data to
	//WRAM asynchronously, so a single LastRomRead is unreliable. This ring keeps the last
	//N ROM reads with their frames; the WRAM->ROM resolution can then walk back to the
	//reads that happened while a WRAM buffer was being filled.
	static constexpr int RomReadRingSize = 8192;
	struct RomReadRingEntry
	{
		uint64_t frame;
		uint32_t addr;
		uint8_t isDma;   //1 = read by DMA, 0 = read by CPU
		uint8_t pad[3];
	};  //16 bytes
	static RomReadRingEntry RomReadRing[RomReadRingSize];
	static uint32_t RomReadRingHead;
	static uint32_t RomReadRingCount;
	static void AppendRomReadRing(uint64_t frame, uint32_t addr, bool isDma)
	{
		if(addr >= 0x400000) {
			return;
		}
		//R3.2: in auto-capture mode, only keep reads while a map-load burst is being
		//detected (AutoCaptureBurstVram > 0) - so the ring holds exactly the load reads,
		//not the endless object/animation reads of normal gameplay.
		if(AutoCapture && AutoCaptureBurstVram == 0) {
			return;
		}
		RomReadRingEntry& e = RomReadRing[RomReadRingHead];
		e.frame = frame;
		e.addr = addr;
		e.isDma = isDma ? 1 : 0;
		RomReadRingHead = (RomReadRingHead + 1) % RomReadRingSize;
		if(RomReadRingCount < RomReadRingSize) {
			RomReadRingCount++;
		}
	}

	//R3.2: reverse-search - for a WRAM buffer filled around frame W, find the contiguous
	//ROM reads that happened within a window before it. Returns the distinct contiguous
	//ROM ranges (start + length) from the ring reads in [fromFrame, toFrame].
	static uint32_t GetRomReadsInFrames(uint64_t fromFrame, uint64_t toFrame, uint32_t* outStart, uint32_t* outLen, uint32_t maxResults)
	{
		if(RomReadRingCount == 0) {
			return 0;
		}
		uint32_t oldest = RomReadRingCount < RomReadRingSize ? 0 : RomReadRingHead;
		uint32_t n = std::min<uint32_t>(RomReadRingCount, RomReadRingSize);
		uint32_t results = 0;
		uint32_t i = 0;
		while(i < n && results < maxResults) {
			RomReadRingEntry& e = RomReadRing[(oldest + i) % RomReadRingSize];
			if(e.frame < fromFrame || e.frame > toFrame || e.addr >= 0x400000) {
				i++;
				continue;
			}
			//collect a contiguous ascending run
			uint32_t startAddr = e.addr;
			uint32_t len = 1;
			uint32_t expected = e.addr + 1;
			uint32_t j = i + 1;
			while(j < n && results < maxResults) {
				RomReadRingEntry& f = RomReadRing[(oldest + j) % RomReadRingSize];
				if(f.frame < fromFrame || f.frame > toFrame) {
					break;  //out of window - stop the run
				}
				if(f.addr == expected) {
					len++;
					expected++;
					j++;
				} else if(f.addr < expected) {
					j++;  //duplicate/out-of-order - skip
				} else {
					break;  //gap - stop the run
				}
			}
			outStart[results] = startAddr;
			outLen[results] = len;
			results++;
			i = j > i ? j : i + 1;
		}
		return results;
	}
	static uint32_t GetRomReadRingCount() { return RomReadRingCount; }

	//R3.2: THE reverse-search core - for a WRAM->VRAM/CGRAM DMA at 'frame', find the
	//largest contiguous ROM block read within [frame-WindowFrames, frame]. A decompression
	//routine reads a contiguous ROM block (the compressed source) around the time it fills
	//the WRAM buffer, so this block IS the ROM source of the loaded data. This replaces the
	//fragile LastRomRead chain with a deterministic, content-independent search.
	static constexpr int RomWindowFrames = 40;
	static uint32_t FindBestRomBlockInWindow(uint64_t frame, uint32_t* outLength)
	{
		*outLength = 0;
		if(RomReadRingCount == 0) {
			return 0xFFFFFFFF;
		}
		uint32_t oldest = RomReadRingCount < RomReadRingSize ? 0 : RomReadRingHead;
		uint32_t n = std::min<uint32_t>(RomReadRingCount, RomReadRingSize);
		uint32_t bestStart = 0xFFFFFFFF;
		uint32_t bestLen = 0;
		uint32_t i = 0;
		while(i < n) {
			RomReadRingEntry& e = RomReadRing[(oldest + i) % RomReadRingSize];
			if(e.addr >= 0x400000) {
				i++;
				continue;
			}
			//window check: e.frame in [frame-Window, frame]
			if(frame > RomWindowFrames) {
				if(e.frame < frame - RomWindowFrames || e.frame > frame) {
					i++;
					continue;
				}
			}
			//collect a contiguous ascending run
			uint32_t startAddr = e.addr;
			uint32_t len = 1;
			uint32_t expected = e.addr + 1;
			uint32_t j = i + 1;
			while(j < n) {
				RomReadRingEntry& f = RomReadRing[(oldest + j) % RomReadRingSize];
				if(f.addr >= 0x400000) {
					break;
				}
				if(frame > RomWindowFrames) {
					if(f.frame < frame - RomWindowFrames || f.frame > frame) {
						break;
					}
				}
				if(f.addr == expected) {
					len++;
					expected++;
					j++;
				} else if(f.addr < expected) {
					j++;  //duplicate/out-of-order - skip
				} else {
					break;  //gap - stop the run
				}
			}
			if(len > bestLen) {
				bestLen = len;
				bestStart = startAddr;
			}
			i = j > i ? j : i + 1;
		}
		*outLength = bestLen;
		return bestStart;
	}

	static void TrackWramWrite(uint32_t wramAddr, uint32_t romRead)
	{
		if(wramAddr >= 0x20000 || romRead == 0xFFFFFFFF) {
			return;
		}
		//R3.2: stability voting - a fade writes a different source every frame; a real
		//load writes the same source repeatedly. Only displace the current source after
		//enough conflicting writes (VoteReset) have been seen.
		if(WramRomByte[wramAddr] != romRead) {
			if(WramRomVote[wramAddr] > 0) {
				WramRomVote[wramAddr]--;
				return;  //keep the stable source, ignore this conflicting fade write
			}
			//vote exhausted - accept the new source (a real map change)
		} else {
			if(WramRomVote[wramAddr] < VoteReset) {
				WramRomVote[wramAddr]++;
			}
			return;  //already correct - nothing to change
		}
		//Exact: this WRAM byte was copied from ROM address romRead.
		WramRomByte[wramAddr] = romRead;
		WramRomVote[wramAddr] = VoteReset;
		AppendWramWrite(0, wramAddr, romRead, (uint8_t)MemoryType::SnesPrgRom);
	}

	//R3.2: WRAM->WRAM copy chain - a decompressor reads a compressed WRAM buffer and writes
	//the decompressed result to another WRAM buffer. The write is NOT preceded by a ROM read
	//(LastRomRead==-1), but by a WRAM read (LastWramRead). The ROM source of the destination
	//is the ROM source recorded for the source WRAM buffer (via WramRomByte chain). This is
	//how 0x33EA4B (compressed ROM) -> WRAM 0x10000 -> WRAM 0x10600 -> CGRAM resolves.
	static void TrackWramWriteFromWram(uint32_t wramAddr, uint32_t srcWramAddr)
	{
		if(wramAddr >= 0x20000 || srcWramAddr >= 0x20000) {
			return;
		}
		uint32_t srcRom = WramRomByte[srcWramAddr];
		if(srcRom != 0xFFFFFFFF) {
			//R3.2: same stability voting as TrackWramWrite (fades lose, loads win)
			if(WramRomByte[wramAddr] != srcRom) {
				if(WramRomVote[wramAddr] > 0) {
					WramRomVote[wramAddr]--;
					return;
				}
			} else {
				if(WramRomVote[wramAddr] < VoteReset) {
					WramRomVote[wramAddr]++;
				}
				return;
			}
			WramRomByte[wramAddr] = srcRom;
			WramRomVote[wramAddr] = VoteReset;
			AppendWramWrite(0, wramAddr, srcRom, (uint8_t)MemoryType::SnesPrgRom);
		}
	}

	static uint32_t ResolveWramSource(uint32_t wramAddr)
	{
		if(wramAddr >= 0x20000) {
			return 0xFFFFFFFF;
		}
		return WramRomByte[wramAddr];
	}

	//R3.2: diagnostics - the ROM source recorded for a WRAM byte
	static uint32_t GetWramRomSource(uint32_t wramAddr)
	{
		if(wramAddr >= 0x20000) {
			return 0xFFFFFFFF;
		}
		return WramRomByte[wramAddr];
	}

	//R3.2: DMA ROM->WRAM - the ROM source address writes TransferSize bytes starting
	//at linear WRAM address wramAddr. Store the exact ROM source per WRAM byte so a
	//later DMA from that WRAM to VRAM/CGRAM can be resolved to the real ROM address.
	static void TrackWramDma(uint32_t wramAddr, uint32_t romSource, uint32_t length)
	{
		if(wramAddr >= 0x20000 || length == 0) {
			return;
		}
		uint32_t end = std::min<uint32_t>(0x20000, wramAddr + length);
		for(uint32_t a = wramAddr; a < end; a++) {
			uint32_t src = romSource + (a - wramAddr);
			//R3.2: stability voting (same as TrackWramWrite) - fades lose, loads win
			if(WramRomByte[a] != src) {
				if(WramRomVote[a] > 0) {
					WramRomVote[a]--;
					continue;
				}
			} else {
				if(WramRomVote[a] < VoteReset) {
					WramRomVote[a]++;
				}
				continue;
			}
			WramRomByte[a] = src;
			WramRomVote[a] = VoteReset;
		}
	}

	static void SetEnabled(bool enabled)
	{
		if(enabled && !Enabled) {
			Head = 0;
			Count = 0;
			LastRomRead = 0xFFFFFFFF;
			LastWramRead = 0xFFFFFFFF;
			LastVramFrame = 0;
			VramBurst = 0;
			AutoStopped = false;
			AutoCaptureBurstStartFrame = 0;
			AutoCaptureBurstVram = 0;
			AutoCaptureLastBurstStartFrame = 0;
			AutoCaptureLastBurstEndFrame = 0;
			AutoCaptureHasBurst = false;
			PendingLen = 0;
			RomReadHead = 0;
			RomReadCount = 0;
			RomReadPendingLen = 0;
			for(uint32_t i = 0; i < 0x20000; i++) {
				WramRomByte[i] = 0xFFFFFFFF;
				WramRomVote[i] = 0;
			}
			memset(RomReadBitmap, 0, sizeof(RomReadBitmap));
			memset(RomTargetBitmap, 0, sizeof(RomTargetBitmap));
	} else if(!enabled && Enabled) {
		FlushPending();       //commit the last coalesced run on disarm
		FlushRomReadBlock();  //commit the last ROM read block on disarm
	}
	Enabled = enabled;
}

	//R3.2: an interrupt (NMI/IRQ) breaks the data-copy chain - the reads of the interrupt
	//handler are NOT the data source for a following VRAM/CGRAM write. Reset the last-read
	//trackers so the interrupt handler's reads are not mistaken for copied data.
	static void OnInterrupt()
	{
		LastRomRead = 0xFFFFFFFF;
		LastWramRead = 0xFFFFFFFF;
	}

	static uint32_t GetCount() { return Count; }
	static bool IsEnabled() { return Enabled; }
	static bool IsAutoStopped() { return AutoStopped; }

	//R3.2: auto-capture mode - the log always runs (Enabled stays true), the last VRAM
	//load burst is tracked automatically. SetAutoCapture(true) keeps the log running and
	//lets the report query exactly the last burst range.
	static void SetAutoCapture(bool enabled)
	{
		AutoCapture = enabled;
		if(enabled) {
			Enabled = true;
		}
	}
	static bool IsAutoCapture() { return AutoCapture; }
	static void SetLiveTracking(bool enabled) { LiveTracking = enabled; if(enabled) { Enabled = true; } }
	static bool IsLiveTracking() { return LiveTracking; }
	static bool HasAutoBurst() { return AutoCaptureHasBurst; }
	static uint64_t GetAutoBurstStartFrame() { return AutoCaptureLastBurstStartFrame; }
	static uint64_t GetAutoBurstEndFrame() { return AutoCaptureLastBurstEndFrame; }
	//Consume the frozen burst: re-arm the capture for the next map change.
	static void ConsumeAutoBurst()
	{
		AutoCaptureHasBurst = false;
		if(AutoCapture) {
			Head = 0;
			Count = 0;
			LastRomRead = 0xFFFFFFFF;
			LastWramRead = 0xFFFFFFFF;
			LastVramFrame = 0;
			VramBurst = 0;
			PendingLen = 0;
			AutoCaptureBurstStartFrame = 0;
			AutoCaptureBurstVram = 0;
			for(uint32_t i = 0; i < 0x20000; i++) {
				WramRomByte[i] = 0xFFFFFFFF;
				WramRomVote[i] = 0;
			}
			memset(RomReadBitmap, 0, sizeof(RomReadBitmap));
			memset(RomTargetBitmap, 0, sizeof(RomTargetBitmap));
			Enabled = true;
		}
	}
	static void ClearAutoBurst() { AutoCaptureHasBurst = false; }

	//R3.2: diagnostics - did any DMA entry use this ROM address as its source?
	static bool DmaHasSource(uint32_t addr)
	{
		uint32_t n = (uint32_t)Count;
		for(uint32_t i = 0; i < n; i++) {
			Entry& e = Log[(Head + i) & (LogSize - 1)];
			if(e.sourceMem == 0 && e.sourceAddr == addr) {
				return true;
			}
		}
		return false;
	}

	//R3.2: diagnostics - does any WRAM byte carry this ROM address as its source?
	static bool WramChainTarget(uint32_t addr)
	{
		uint32_t test = (uint32_t)(addr - 1);
		for(uint32_t a = 0; a < 0x20000; a++) {
			if(WramRomByte[a] == addr) {
				return true;
			}
			if(test < 0x20000 && WramRomByte[a] == test) {
				return true;
			}
		}
		return false;
	}

	//Remember the last ROM/WRAM address the CPU read, so a following VRAM/CGRAM
	//write can be attributed to the data source. Also coalesces sequential ROM reads
	//into contiguous blocks (the real ROM address ranges of the loaded data).
	static void TrackRead(MemoryType memType, uint32_t addr24, uint64_t frame = 0)
	{
		if(memType == MemoryType::SnesPrgRom) {
			LastRomRead = addr24;
			if(Enabled) {
				AppendRomReadRing(frame, addr24, false);  //R3.2: timestamped read for reverse search
				MarkRomRead(addr24);
				if(RomReadPendingLen == 0) {
					RomReadPendingAddr = addr24 + 1;
					RomReadPendingLen = 1;
					RomReadPendingFrame = 0;
				} else if(addr24 == RomReadPendingAddr) {
					RomReadPendingAddr++;
					RomReadPendingLen++;
				} else {
					FlushRomReadBlock();
					RomReadPendingAddr = addr24 + 1;
					RomReadPendingLen = 1;
					RomReadPendingFrame = 0;
				}
			}
		} else if(memType == MemoryType::SnesWorkRam || memType == MemoryType::SnesSaveRam) {
			LastWramRead = addr24;
		}
	}

	//R3.2: auto-stop - only after a real map-load burst. A load writes many VRAM words
	//in quick succession (VramBurst climbs past AutoStopVramThreshold). Once the burst has
	//ended (no VRAM write for AutoStopQuietFrames) the capture stops so the following CGRAM
	//palette fades can't overwrite the load from the ring. Stray VRAM writes during normal
	//gameplay (gap > BurstGapFrames) reset the burst counter and never trigger the auto-stop.
	static void CheckAutoStop(uint32_t frame, uint8_t targetType, uint32_t vramBytes = 1)
	{
		if(!Enabled) {
			return;
		}
		if(targetType == 0) {
			if(LastVramFrame != 0 && frame - LastVramFrame > BurstGapFrames) {
				VramBurst = 0;  //gap too large - not part of a load burst
			}
			VramBurst += vramBytes;
			LastVramFrame = frame;
			return;
		}
		if(AutoCapture) {
			//auto-capture mode: never stop permanently - keep logging so the next map
			//change is captured too. The burst tracking in AutoCaptureMark handles framing.
			return;
		}
		if(VramBurst >= AutoStopVramThreshold && frame - LastVramFrame > AutoStopQuietFrames) {
			Enabled = false;
			AutoStopped = true;
		}
	}

	//R3.2: auto-capture - detect a map-load burst WITHOUT requiring manual arm. The log
	//always runs; when a VRAM burst (many VRAM writes in quick succession) is followed by
	//a quiet gap, we FREEZE the log so it holds exactly the map load (tiles/tilemap/palette
	//of the map change). The report consumes the frozen range and re-arms.
	//Call this from AppendDma/AppendCpuWrite BEFORE the entry is appended (frame known).
	static void AutoCaptureMark(uint32_t frame, uint8_t targetType, uint32_t vramBytes = 1)
	{
		if(!AutoCapture || !Enabled) {
			return;
		}
		if(targetType == 0) {
			//VRAM write: is this a new burst?
			if(LastVramFrame != 0 && frame - LastVramFrame > BurstGapFrames) {
				//gap too large - close the previous burst and start a new one
				if(AutoCaptureBurstVram >= AutoStopVramThreshold) {
					AutoCaptureLastBurstStartFrame = AutoCaptureBurstStartFrame;
					AutoCaptureLastBurstEndFrame = frame;
					AutoCaptureHasBurst = true;
				}
				//A new map-load burst started: clear the log so it holds ONLY this burst
				//(the old fades/intro are discarded - the load is what we want to show).
				Head = 0;
				Count = 0;
				RomReadRingHead = 0;  //R3.2: clear the read ring too - only this load's reads
				RomReadRingCount = 0;
				AutoCaptureBurstStartFrame = frame;
				AutoCaptureBurstVram = 0;
			}
			if(AutoCaptureBurstVram == 0) {
				AutoCaptureBurstStartFrame = frame;
			}
			AutoCaptureBurstVram += vramBytes;
		} else {
			//CGRAM write: if a substantial VRAM burst just ended (a quiet gap since the
			//last VRAM write) FREEZE the log now - the palette fades that follow the load
			//must not keep extending the capture. Enabled=false keeps the load entries.
			if(AutoCaptureBurstVram >= AutoStopVramThreshold
				&& LastVramFrame != 0 && frame - LastVramFrame > AutoStopQuietFrames) {
				AutoCaptureLastBurstStartFrame = AutoCaptureBurstStartFrame;
				AutoCaptureLastBurstEndFrame = frame;
				AutoCaptureHasBurst = true;
				Enabled = false;  //freeze - the log now holds exactly the map load
			}
		}
	}

	//R3.2: coalesce a consecutive copy loop (same source, adjacent target+source addresses)
	//into one entry so a real load appears as a single source with the correct length.
	static uint8_t PendingTargetType;
	static uint32_t PendingTargetAddr;   // next expected target addr
	static uint32_t PendingSourceAddr;   // next expected source addr
	static uint8_t PendingSourceMem;
	static uint32_t PendingLen;
	static uint32_t PendingPc;
	static uint32_t PendingFrame;
	static int32_t PendingCycle;

	static void AppendCpuWrite(uint32_t frame, int32_t cycle, uint8_t targetType, uint32_t targetAddr, uint8_t value, uint32_t pc)
	{
		if(!Enabled) {
			return;
		}
		AutoCaptureMark(frame, targetType, 1);
		CheckAutoStop(frame, targetType);
		if(!Enabled) {
			return;
		}
		if(targetType == 0) {
			LastVramFrame = frame;
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

		if(PendingLen > 0 && targetType == PendingTargetType && targetAddr == PendingTargetAddr
			&& sourceAddr == PendingSourceAddr && sourceMem == PendingSourceMem && pc == PendingPc) {
			//Same source, adjacent target+source addresses, same PC: extend the run
			PendingTargetAddr++;
			PendingSourceAddr++;
			PendingLen++;
			PendingCycle = cycle;
			return;
		}

		//Run ended - flush the previous one if it was a real load
		FlushPending();
		if(sourceAddr != 0) {
			PendingTargetType = targetType;
			PendingTargetAddr = targetAddr + 1;
			PendingSourceAddr = sourceAddr + 1;
			PendingSourceMem = sourceMem;
			PendingLen = 1;
			PendingPc = pc;
			PendingFrame = frame;
			PendingCycle = cycle;
		}
	}

	static void FlushPending()
	{
		if(PendingLen == 0) {
			return;
		}
		uint32_t len = PendingLen;
		PendingLen = 0;
		if(len < MinCpuRunLen) {
			return;  //too short - palette fade / single patch, not a load
		}
		Entry& e = Log[Head];
		e.frame = PendingFrame;
		e.cycle = PendingCycle;
		e.sourceType = Cpu;
		e.targetType = PendingTargetType;
		e.targetAddr = PendingTargetAddr - len;  //start of the run
		e.value = 0;
		e.sourceAddr = PendingSourceAddr - len;  //start of the run
		e.sourceMem = PendingSourceMem;
		e.channel = 0xFF;
		e.length = len;
		e.pc = PendingPc;

		//R3.2: reverse lookup - CPU copy loops also fill the target->ROM table.
		if(PendingSourceMem == (uint8_t)MemoryType::SnesPrgRom && PendingSourceAddr - len < 0x400000) {
			uint32_t wordCount = len / 2;
			if(wordCount > 0) {
				TrackTargetRom(PendingTargetType, PendingTargetAddr - len, PendingSourceAddr - len, wordCount);
			}
		}

		Head = (Head + 1) % LogSize;
		if(Count < LogSize) {
			Count++;
		}
	}

	static void AppendDma(uint32_t frame, int32_t cycle, uint8_t targetType, uint32_t targetAddr, uint32_t srcBank, uint32_t srcAddr, uint32_t length, uint8_t channel, uint32_t srcBusAddr = 0, uint8_t destAddr = 0)
	{
		if(!Enabled) {
			return;
		}
		//R3.2: resolve the source FIRST (so auto-capture can skip pure-WRAM fades).
		uint32_t sourceAddr24 = srcAddr;
		uint8_t sourceResolved = 0;  //0=ROM (direct or resolved), 1=WRAM (not resolved to ROM)
		if(srcBank == 1) {
			uint32_t romAddr = ResolveWramSource(srcAddr);
			if(romAddr != 0xFFFFFFFF) {
				sourceAddr24 = romAddr;
			} else {
				//WRAM source: keep the WRAM address as the direct source. The report / palette
				//viewer resolves it one more step via the WramRomByte chain to the real ROM
				//offset. (The ring-based FindBestRomBlockInWindow often picked the wrong block
				//- e.g. code/fade data - so it is no longer used here.)
				sourceResolved = 1;  //WRAM - resolved later through the chain
			}
		}
		if(AutoCapture && !LiveTracking && sourceResolved == 1) {
			//Auto-capture: an unresolved WRAM source is a palette fade / effect, not a
			//map-load. Skip logging it so the log holds only real ROM-sourced loads.
			//(In live-tracking mode we keep everything so the palette viewer stays current.)
			AutoCaptureMark(frame, targetType, length);  //keep burst timing correct
			return;
		}
		AutoCaptureMark(frame, targetType, length);
		CheckAutoStop(frame, targetType, length);
		if(!Enabled && !LiveTracking) {
			return;
		}
		//R3.2: timestamped ROM read for reverse search of the load source (only while a
		//burst is active in auto-capture mode, so the ring holds exactly the load reads).
		if(srcBank == 0 && sourceAddr24 < 0x400000) {
			AppendRomReadRing(frame, sourceAddr24, true);
		}
		if(targetType == 0) {
			LastVramFrame = frame;
		}

		Entry& e = Log[Head];
		e.frame = frame;
		e.cycle = cycle;
		e.sourceType = Dma;
		e.targetType = targetType;
		e.targetAddr = targetAddr;
		e.value = destAddr;  //R3.2: store the DMA DestAddress (0x18 VRAM / 0x22 CGRAM / 0x80 WRAM)
		e.sourceAddr = sourceAddr24;
		e.sourceMem = sourceResolved;
		e.channel = channel;
		e.length = length;
		e.pc = srcBusAddr;  //R3.2: store the RAW DMA source bus address (SrcBank<<16|SrcAddress)

		//R3.2: mark the ROM source range as "copied to VRAM/CGRAM" (real map data).
		if(sourceAddr24 < 0x400000 && (srcBank == 0 || (srcBank == 1 && !sourceResolved))) {
			MarkRomTarget(sourceAddr24, length);
		}

		//R3.2: REVERSE LOOKUP - remember which ROM offset filled each target position.
		//This is the key to the user's "rückwärtssuche": knowing the palette sits in CGRAM
		//0x00-0xFF / tiles in VRAM, we can directly read back the ROM source per word.
		if(sourceAddr24 < 0x400000) {
			uint32_t wordCount = length / 2;
			if(wordCount > 0) {
				TrackTargetRom(targetType, targetAddr, sourceAddr24, wordCount);
			}
		}

		Head = (Head + 1) % LogSize;
		if(Count < LogSize) {
			Count++;
		}
	}

	static uint32_t Get(Entry* entries, uint32_t start, uint32_t count)
	{
		if(Enabled) {
			FlushPending();       //commit the in-progress coalesced run so it's included
			FlushRomReadBlock();  //commit the in-progress ROM read block
		}
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

	//R3.2: auto-capture - return the entries whose frame is in [startFrame, endFrame]
	//(the map-load burst range for the report). Scans from the oldest entry forward.
	static uint32_t GetFrameRange(Entry* entries, uint64_t startFrame, uint64_t endFrame, uint32_t maxCount)
	{
		if(Enabled) {
			FlushPending();       //commit the in-progress coalesced run so it's included
			FlushRomReadBlock();  //commit the in-progress ROM read block
		}
		if(Count == 0 || maxCount == 0) {
			return 0;
		}
		uint32_t oldest = Count < LogSize ? 0 : Head;
		uint32_t out = 0;
		for(uint32_t i = 0; i < Count && out < maxCount; i++) {
			Entry& e = Log[(oldest + i) % LogSize];
			if(e.frame < startFrame) {
				continue;
			}
			if(e.frame > endFrame) {
				break;
			}
			entries[out++] = e;
		}
		return out;
	}

	static uint32_t GetRomReadCount() { return RomReadCount; }

	static bool WasRomRead(uint32_t romOffset)
	{
		if(romOffset >= 0x400000) {
			return false;
		}
		return (RomReadBitmap[romOffset >> 3] & (1 << (romOffset & 7))) != 0;
	}

	//R3.2: extract the contiguous ROM range around a target address that was read since
	//arming. Starting at addr, walk backwards/forwards while the bitmap is set (with small
	//gaps <= gapBytes tolerated). Returns the range via outStart/outEnd (inclusive).
	static void GetRomReadRangeAround(uint32_t addr, uint32_t gapBytes, uint32_t* outStart, uint32_t* outEnd)
	{
		uint32_t start = addr;
		uint32_t end = addr;
		//walk back
		uint32_t gap = 0;
		uint32_t a = addr;
		while(a > 0) {
			a--;
			if(WasRomRead(a)) {
				start = a;
				gap = 0;
			} else {
				gap++;
				if(gap > gapBytes) {
					break;
				}
			}
		}
		//walk forward
		gap = 0;
		a = addr;
		while(a < 0x400000 - 1) {
			a++;
			if(WasRomRead(a)) {
				end = a;
				gap = 0;
			} else {
				gap++;
				if(gap > gapBytes) {
					break;
				}
			}
		}
		*outStart = start;
		*outEnd = end;
	}

	//R3.2: extract contiguous ROM blocks that were read since arming, from the bitmap.
	//Blocks with gaps <= gapBytes are merged. Returns the number of blocks written.
	static uint32_t GetRomReadBlocks(uint32_t* outStart, uint32_t* outLength, uint32_t maxBlocks, uint32_t gapBytes = 0x100)
	{
		uint32_t count = 0;
		uint32_t runStart = 0xFFFFFFFF;
		uint32_t runEnd = 0;
		for(uint32_t a = 0; a < 0x400000; a++) {
			bool read = (RomReadBitmap[a >> 3] & (1 << (a & 7))) != 0;
			if(read) {
				if(runStart == 0xFFFFFFFF) {
					runStart = a;
				}
				runEnd = a + 1;
			} else if(runStart != 0xFFFFFFFF && a - runEnd > gapBytes) {
				if(count < maxBlocks) {
					outStart[count] = runStart;
					outLength[count] = runEnd - runStart;
					count++;
				}
				runStart = 0xFFFFFFFF;
			}
		}
		if(runStart != 0xFFFFFFFF && count < maxBlocks) {
			outStart[count] = runStart;
			outLength[count] = runEnd - runStart;
			count++;
		}
		return count;
	}

	//R3.2: extract contiguous ROM blocks whose data was actually copied to VRAM/CGRAM.
	//These are the real map-data sources; code/table reads are not included.
	static uint32_t GetRomTargetBlocks(uint32_t* outStart, uint32_t* outLength, uint32_t maxBlocks, uint32_t gapBytes = 0x100)
	{
		uint32_t count = 0;
		uint32_t runStart = 0xFFFFFFFF;
		uint32_t runEnd = 0;
		for(uint32_t a = 0; a < 0x400000; a++) {
			bool read = (RomTargetBitmap[a >> 3] & (1 << (a & 7))) != 0;
			if(read) {
				if(runStart == 0xFFFFFFFF) {
					runStart = a;
				}
				runEnd = a + 1;
			} else if(runStart != 0xFFFFFFFF && a - runEnd > gapBytes) {
				if(count < maxBlocks) {
					outStart[count] = runStart;
					outLength[count] = runEnd - runStart;
					count++;
				}
				runStart = 0xFFFFFFFF;
			}
		}
		if(runStart != 0xFFFFFFFF && count < maxBlocks) {
			outStart[count] = runStart;
			outLength[count] = runEnd - runStart;
			count++;
		}
		return count;
	}

	static uint32_t GetRomReadLog(RomReadBlock* blocks, uint32_t start, uint32_t count)
	{
		if(start >= RomReadCount || count == 0) {
			return 0;
		}
		uint32_t n = std::min(count, RomReadCount - start);
		uint32_t oldest = RomReadCount < RomReadLogSize ? 0 : RomReadHead;
		for(uint32_t i = 0; i < n; i++) {
			blocks[i] = RomReadLog[(oldest + start + i) % RomReadLogSize];
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

