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
