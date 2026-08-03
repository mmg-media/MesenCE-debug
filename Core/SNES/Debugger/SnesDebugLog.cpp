#include "pch.h"
#include "SNES/Debugger/SnesDebugLog.h"

// R2.1: Ring-Puffer-Speicher für die Event-Historie
SnesEventLog::Entry SnesEventLog::Log[SnesEventLog::LogSize] = {};
uint32_t SnesEventLog::Head = 0;
uint32_t SnesEventLog::Count = 0;
uint64_t SnesEventLog::NextId = 0;
bool SnesEventLog::Enabled = false;

// R2.2: Ring-Puffer-Speicher für den VRAM-Write-Log
SnesVramLog::Entry SnesVramLog::Log[SnesVramLog::LogSize] = {};
uint32_t SnesVramLog::Head = 0;
uint32_t SnesVramLog::Count = 0;
bool SnesVramLog::Enabled = false;


// R3.1: WRAM/Register-Write-Log – Ring-Puffer-Speicher + Run-Coalescing-Zustand
SnesWramLog::Entry SnesWramLog::Log[SnesWramLog::LogSize] = {};
uint32_t SnesWramLog::Head = 0;
uint32_t SnesWramLog::Count = 0;
uint64_t SnesWramLog::NextId = 0;
bool SnesWramLog::Enabled = false;
uint32_t SnesWramLog::RangeStart = 0;
uint32_t SnesWramLog::RangeEnd = 0xFFFF;
uint16_t SnesWramLog::MinLen = 1;
int32_t SnesWramLog::FilterMemType = (int32_t)MemoryType::SnesWorkRam;

bool SnesWramLog::HasPending = false;
uint64_t SnesWramLog::PendingFrame = 0;
uint32_t SnesWramLog::PendingPc = 0;
uint32_t SnesWramLog::PendingStartAddr = 0;
uint32_t SnesWramLog::PendingAddr = 0;
int32_t SnesWramLog::PendingCycle = 0;
uint8_t SnesWramLog::PendingValue = 0;
uint16_t SnesWramLog::PendingWidth = 0;
int8_t SnesWramLog::PendingMemType = 0;

void SnesWramLog::Flush()
{
	if(!HasPending) {
		return;
	}

	HasPending = false;
	if(PendingWidth < MinLen) {
		//Run zu kurz: verwerfen (Anti-Flut)
		return;
	}

	Entry& e = Log[Head];
	e.id = NextId++;
	e.frame = PendingFrame;
	e.cycle = PendingCycle;
	e.pc = PendingPc;
	e.addr = (uint16_t)(PendingStartAddr & 0xFFFF);
	e.bank = (uint8_t)((PendingStartAddr >> 16) & 0xFF);
	e.value = PendingValue;
	e.width = PendingWidth;
	e.memType = PendingMemType;
	e.reserved = 0;

	Head = (Head + 1) % LogSize;
	if(Count < LogSize) {
		Count++;
	}
}


// Universal-Tracker – Ring + Datei-Log + Trigger
SnesTracker::Entry SnesTracker::Log[SnesTracker::LogSize] = {};
uint32_t SnesTracker::Head = 0;
uint32_t SnesTracker::Count = 0;
uint64_t SnesTracker::NextId = 0;
bool SnesTracker::Enabled = false;
bool SnesTracker::Tracking = false;
uint64_t SnesTracker::TriggerCount = 0;

int32_t SnesTracker::TriggerMemType = (int32_t)MemoryType::SnesWorkRam;
uint32_t SnesTracker::TriggerStart = 0;
uint32_t SnesTracker::TriggerEnd = 0xFFFF;
bool SnesTracker::TriggerOnRead = false;
bool SnesTracker::TriggerOnWrite = true;
uint32_t SnesTracker::TriggerValue = 0;
bool SnesTracker::TriggerValueSet = false;

char SnesTracker::FilePath[1024] = {};
FILE* SnesTracker::File = nullptr;
char SnesTracker::FileBuffer[8192] = {};
bool SnesTracker::LogExec = true;
uint64_t SnesTracker::MaxBytes = 100ULL * 1024 * 1024;
uint64_t SnesTracker::FileBytes = 0;
uint32_t SnesTracker::FileBufferLen = 0;
uint8_t* SnesTracker::RamBuffer = nullptr;
uint64_t SnesTracker::RamSize = 0;
uint64_t SnesTracker::RamLen = 0;
bool SnesTracker::RamWrapped = false;
bool SnesTracker::RamWrap = true;
uint8_t SnesTracker::BufferMode = 0;

void SnesTracker::Start(const char* filePath, int32_t memType, uint32_t start, uint32_t end, bool onRead, bool onWrite, uint32_t value, bool valueSet, bool logExec, uint64_t maxBytes, uint8_t bufferMode, uint64_t bufferSizeMb)
{
	Stop();

	TriggerMemType = memType;
	TriggerStart = start;
	TriggerEnd = end;
	TriggerOnRead = onRead;
	TriggerOnWrite = onWrite;
	TriggerValue = value;
	TriggerValueSet = valueSet;
	LogExec = logExec;
	MaxBytes = maxBytes;
	FileBytes = 0;
	BufferMode = bufferMode;
	RamLen = 0;
	RamWrapped = false;

	if(BufferMode == 1) {
		RamSize = bufferSizeMb * 1024 * 1024;
		if(RamSize < 1024 * 1024) {
			RamSize = 1024 * 1024;
		}
		//Großer virtueller Puffer: malloc nutzt auf Windows VirtualAlloc intern (lazy Commit)
		RamBuffer = (uint8_t*)malloc((size_t)RamSize);
	} else {
		RamBuffer = nullptr;
		RamSize = 0;
	}

	if(filePath && filePath[0]) {
		strncpy_s(FilePath, 1024, filePath, _TRUNCATE);
	} else {
		FilePath[0] = 0;
	}

	Enabled = true;
	Tracking = false;
	TriggerCount = 0;
	Head = 0;
	Count = 0;
}

void SnesTracker::Stop()
{
	FlushFile();
	if(File) {
		fclose(File);
		File = nullptr;
	}
	FileBufferLen = 0;

	if(BufferMode == 1 && RamBuffer) {
		//RAM-Puffer beim Stop nach Disk spiegeln (einmalig); bei Wrap: [RamLen..ende) + [0..RamLen)
		if(FilePath[0] && RamLen > 0) {
			FILE* f = nullptr;
			fopen_s(&f, FilePath, "wb");
			if(f) {
				if(RamWrapped) {
					fwrite(RamBuffer + RamLen, 1, (size_t)(RamSize - RamLen), f);
					fwrite(RamBuffer, 1, (size_t)RamLen, f);
				} else {
					fwrite(RamBuffer, 1, (size_t)RamLen, f);
				}
				fclose(f);
			}
		}
		free(RamBuffer);
		RamBuffer = nullptr;
		RamLen = 0;
		RamWrapped = false;
	}

	Enabled = false;
	Tracking = false;
}

void SnesTracker::WriteRamLine(const char* line, int len)
{
	if(!RamBuffer || RamSize == 0) {
		return;
	}
	if(RamLen >= RamSize) {
		if(!RamWrap) {
			return;
		}
		RamLen = 0;
		RamWrapped = true;
	}
	uint64_t avail = RamSize - RamLen;
	uint64_t copy = len < (int)avail ? (uint64_t)len : avail;
	memcpy(RamBuffer + RamLen, line, (size_t)copy);
	RamLen += copy;
	if(copy < (uint64_t)len) {
		RamWrapped = true;
	}
}

void SnesTracker::FlushFile()
{
	if(File && FileBufferLen > 0) {
		fwrite(FileBuffer, 1, FileBufferLen, File);
		fflush(File);
		FileBytes += FileBufferLen;
		FileBufferLen = 0;
	}
}

void SnesTracker::Trigger()
{
	if(!Enabled || Tracking) {
		return;
	}
	Tracking = true;
	TriggerCount = Count;

	if(FilePath[0] && BufferMode == 0) {
		fopen_s(&File, FilePath, "w");
		FileBufferLen = 0;
		if(File) {
			uint32_t len = (uint32_t)sprintf_s(FileBuffer, sizeof(FileBuffer), "# TRACKER START frame=%llu\n", (unsigned long long)(Count ? Log[(Head + Count - 1) % LogSize].frame : 0));
			FileBufferLen = len;
			FlushFile();
		}
	} else if(BufferMode == 1) {
		char hdr[64];
		int hn = sprintf_s(hdr, sizeof(hdr), "# TRACKER START frame=%llu\n", (unsigned long long)(Count ? Log[(Head + Count - 1) % LogSize].frame : 0));
		if(hn > 0) {
			WriteRamLine(hdr, hn);
		}
	}
}

void SnesTracker::Append(uint8_t type, uint32_t frame, int32_t cycle, uint32_t pc, uint32_t addr24, uint8_t value, uint8_t extra, uint16_t extra2)
{
	if(!Enabled) {
		return;
	}
	if(type == Exec && !LogExec) {
		return;
	}

	Entry& e = Log[Head];
	e.id = NextId++;
	e.frame = frame;
	e.cycle = cycle;
	e.type = type;
	e.bank = (uint8_t)((addr24 >> 16) & 0xFF);
	e.addr = (uint16_t)(addr24 & 0xFFFF);
	e.pc = pc;
	e.value = value;
	e.extra = extra;
	e.extra2 = extra2;

	Head = (Head + 1) % LogSize;
	if(Count < LogSize) {
		Count++;
	}

	if(Tracking) {
		//Chronologischer Log (RAM-Puffer oder Datei, gepuffert)
		char line[128];
		int n = 0;
		switch(type) {
			case Exec: n = sprintf_s(line, sizeof(line), "E %llu %d %06X\n", (unsigned long long)frame, cycle, pc); break;
			case MemW: n = sprintf_s(line, sizeof(line), "W %llu %d %06X %02X:%04X %02X\n", (unsigned long long)frame, cycle, pc, e.bank, e.addr, value); break;
			case Vram: n = sprintf_s(line, sizeof(line), "V %llu %d %06X %04X %02X\n", (unsigned long long)frame, cycle, pc, e.addr, value); break;
			case Dma: n = sprintf_s(line, sizeof(line), "D %llu %d %06X ch=%d%s dst=%02X vram=%04X len=%u\n", (unsigned long long)frame, cycle, pc, value & 0x7F, (value & 0x80) ? "h" : "", e.addr, extra2, (uint32_t)extra | ((uint32_t)extra2 << 8)); break;
			case Nmi: n = sprintf_s(line, sizeof(line), "I %llu %d %06X NMI\n", (unsigned long long)frame, cycle, pc); break;
			case Irq: n = sprintf_s(line, sizeof(line), "I %llu %d %06X IRQ\n", (unsigned long long)frame, cycle, pc); break;
		}
		if(n > 0) {
			if(BufferMode == 1) {
				WriteRamLine(line, n);
			} else if(File && FileBytes < MaxBytes) {
				memcpy(FileBuffer + FileBufferLen, line, (size_t)n);
				FileBufferLen += (uint32_t)n;
				if(FileBufferLen > 7000) {
					FlushFile();
				}
			}
		}
	}
}

void SnesTracker::CheckMemoryOp(uint8_t opType, uint32_t pc, uint32_t frame, int32_t cycle, uint32_t addr24, uint8_t value, MemoryType memType)
{
	if(!Enabled || Tracking) {
		return;
	}

	uint16_t addr = (uint16_t)(addr24 & 0xFFFF);
	bool inRange = (int32_t)memType == TriggerMemType && addr >= TriggerStart && addr <= TriggerEnd;
	if(!inRange) {
		return;
	}
	if(opType == 1 && TriggerOnRead && (!TriggerValueSet || value == TriggerValue)) {
		Trigger();
	}
	if(opType == 2 && TriggerOnWrite && (!TriggerValueSet || value == TriggerValue)) {
		Trigger();
	}
}

void SnesTracker::AppendExec(uint32_t frame, int32_t cycle, uint32_t pc)
{
	if(!Enabled || !Tracking) {
		return;
	}
	Append(Exec, frame, cycle, pc, 0, 0, 0, 0);
}

void SnesTracker::AppendInterrupt(uint8_t type, uint32_t frame, int32_t cycle, uint32_t pc)
{
	if(!Enabled || !Tracking) {
		return;
	}
	Append(type, frame, cycle, pc, 0, 0, 0, 0);
}

