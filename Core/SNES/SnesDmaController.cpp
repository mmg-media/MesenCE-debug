#include "pch.h"
#include "SNES/SnesDmaController.h"
#include "SNES/DmaControllerTypes.h"
#include "SNES/SnesMemoryManager.h"
#include "SNES/SnesConsole.h"
#include "SNES/SnesPpu.h"
#include "SNES/Debugger/SnesDebugLog.h"
#include "Shared/Emulator.h"
#include "Utilities/Serializer.h"

static constexpr uint8_t _transferByteCount[8] = { 1, 2, 2, 4, 4, 4, 2, 4 };
static constexpr uint8_t _transferOffset[8][4] = {
	{ 0, 0, 0, 0 },
	{ 0, 1, 0, 1 },
	{ 0, 0, 0, 0 },
	{ 0, 0, 1, 1 },
	{ 0, 1, 2, 3 },
	{ 0, 1, 0, 1 },
	{ 0, 0, 0, 0 },
	{ 0, 0, 1, 1 }
};

SnesDmaController::DmaLogEntry SnesDmaController::DmaLog[SnesDmaController::DmaLogSize] = {};
uint32_t SnesDmaController::DmaLogHead = 0;
uint32_t SnesDmaController::DmaLogCount = 0;
bool SnesDmaController::DmaLogEnabled = false;

void SnesDmaController::SetDmaLogEnabled(bool enabled)
{
	if(enabled && !DmaLogEnabled) {
		//(Re-)activation clears the ring (discards stale entries)
		DmaLogHead = 0;
		DmaLogCount = 0;
	}
	DmaLogEnabled = enabled;
}

uint32_t SnesDmaController::GetDmaLogCount()
{
	return DmaLogCount;
}

uint32_t SnesDmaController::GetDmaLog(DmaLogEntry* entries, uint32_t start, uint32_t count)
{
	if(!entries || start >= DmaLogCount) {
		return 0;
	}
	uint32_t available = DmaLogCount - start;
	uint32_t toCopy = count < available ? count : available;
	uint32_t oldest = (DmaLogHead + DmaLogSize - DmaLogCount) % DmaLogSize;
	for(uint32_t i = 0; i < toCopy; i++) {
		entries[i] = DmaLog[(oldest + start + i) % DmaLogSize];
	}
	return toCopy;
}

void SnesDmaController::AppendDmaLogEntry(uint8_t channel, bool isHdma, bool toCpu, DmaChannelConfig& config)
{
	DmaLogEntry entry = {};
	entry.frame = _memoryManager->GetEmu()->GetFrameCount();
	entry.cycle = (int32_t)(_memoryManager->GetMasterClock() & 0x7FFFFFFF);
	entry.channel = channel;
	entry.isHdma = isHdma ? 1 : 0;
	entry.toCpu = toCpu ? 1 : 0;
	entry.mode = config.TransferMode;
	entry.sourceBank = config.SrcBank;
	entry.sourceAddr = config.SrcAddress;
	entry.destAddr = config.DestAddress;
	entry.length = config.TransferSize;
	entry.vramAddr = 0;
	if(config.DestAddress == 0x18 || config.DestAddress == 0x19) {
		//VRAM target: current write address from 0x2116/0x2117 (start of the transfer)
		entry.vramAddr = _memoryManager->GetConsole()->GetPpu()->GetDebugVramAddress();
	}
	DmaLog[DmaLogHead] = entry;
	DmaLogHead = (DmaLogHead + 1) % DmaLogSize;
	if(DmaLogCount < DmaLogSize) {
		DmaLogCount++;
	}
}

SnesDmaController::SnesDmaController(SnesMemoryManager* memoryManager)
{
	_memoryManager = memoryManager;
	Reset();

	for(int j = 0; j < 8; j++) {
		for(int i = 0; i <= 0x0B; i++) {
			Write(0x4300 | i | (j << 4), 0xFF);
		}
	}
}

SnesDmaControllerState& SnesDmaController::GetState()
{
	return _state;
}

void SnesDmaController::Reset()
{
	_state.HdmaChannels = 0;

	_hdmaPending = false;
	_hdmaInitPending = false;
	_dmaStartDelay = false;
	_dmaPending = false;
	_needToProcess = false;
	_stoppedHdmaChannels = 0;

	for(int i = 0; i < 8; i++) {
		_state.Channel[i].DmaActive = false;
	}
}

void SnesDmaController::CopyDmaByte(uint32_t addressBusA, uint16_t addressBusB, bool fromBtoA)
{
	if(fromBtoA) {
		if(addressBusB != 0x2180 || !_memoryManager->IsWorkRam(addressBusA)) {
			uint8_t valToWrite = _memoryManager->ReadDma(addressBusB, false);
			_memoryManager->WriteDma(addressBusA, valToWrite, true);
		} else {
			//$2180->WRAM do cause a write to occur (but no read), but the value written is invalid
			_memoryManager->IncMasterClock4();
			_memoryManager->WriteDma(addressBusA, 0xFF, true);
		}
	} else {
		if(addressBusB != 0x2180 || !_memoryManager->IsWorkRam(addressBusA)) {
			uint8_t valToWrite = _memoryManager->ReadDma(addressBusA, true);
			_memoryManager->WriteDma(addressBusB, valToWrite, false);
		} else {
			//WRAM->$2180 does not cause a write to occur
			_memoryManager->IncMasterClock8();
		}
	}
}

void SnesDmaController::RunDma(DmaChannelConfig& channel)
{
	if(!channel.DmaActive) {
		return;
	}

	//"Then perform the DMA: 8 master cycles overhead and 8 master cycles per byte per channel"
	_dmaClockCounter += 8;
	_memoryManager->IncMasterClock8();
	ProcessPendingTransfers();

	if(DmaLogEnabled) {
		AppendDmaLogEntry(_activeChannel & 0x07, (_activeChannel & HdmaChannelFlag) != 0, channel.InvertDirection, channel);
	}

	//Universal tracker: DMA entry independent of the DmaLog (pc=vramAddr, addr=dest, value=channel|hdma<<7)
	uint16_t trackerVram = 0;
	if(channel.DestAddress == 0x18 || channel.DestAddress == 0x19) {
		trackerVram = _memoryManager->GetConsole()->GetPpu()->GetDebugVramAddress();
	}
	SnesTracker::Append(
		SnesTracker::Dma,
		_memoryManager->GetEmu()->GetFrameCount(),
		(int32_t)(_memoryManager->GetMasterClock() & 0x7FFFFFFF),
		trackerVram,
		channel.DestAddress,
		(_activeChannel & 0x07) | ((_activeChannel & HdmaChannelFlag) ? 0x80 : 0),
		(uint8_t)(channel.TransferSize & 0xFF),
		(uint16_t)(channel.TransferSize >> 8));

	//R3.2: map-load source trace - DMA to VRAM (0x18/0x19), CGRAM (0x22) or WRAM (0x80->$2180)
	//records the source that provides the tile/tilemap/palette data. The source is
	//resolved to its absolute (linear ROM/WRAM) address - NOT the SNES bus address -
	//so it can be used directly as a file offset into the ROM. DMA from ROM to WRAM is
	//recorded into the WRAM->ROM resolution table, so a later DMA from that WRAM address
	//to VRAM/CGRAM can be resolved back to the real ROM source.
	{
		uint32_t srcBusAddr = (channel.SrcBank << 16) | channel.SrcAddress;
		AddressInfo srcInfo = _memoryManager->GetMemoryMappings()->GetAbsoluteAddress(srcBusAddr);
		//R3.2: robust WRAM detection - the SNES WRAM banks $7E/$7F are sometimes reported
		//as ROM by GetAbsoluteAddress (bus mirror confusion). Detect them directly from the
		//bus address: bank $7E/$7F (or $7E:$0000-$7F:$FFFF mirrors) is the 128KB WRAM.
		uint8_t srcBankLow = (uint8_t)(srcBusAddr >> 16);
		bool bankIsWram = (srcBankLow == 0x7E || srcBankLow == 0x7F);
		bool srcIsRom = srcInfo.Address >= 0 && srcInfo.Type == MemoryType::SnesPrgRom;
		bool srcIsWram = srcInfo.Address >= 0 && (srcInfo.Type == MemoryType::SnesWorkRam || srcInfo.Type == MemoryType::SnesSaveRam);
		if(bankIsWram) {
			srcIsWram = true;
			srcIsRom = false;
		}
		//Linear ROM file offset for the DMA source. GetAbsoluteAddress returns the exact
		//linear address the cartridge maps this bus address to (HiROM/LoROM aware) - that
		//is the true file offset. Only the linear address (not the bus address) is a valid
		//ROM file offset.
		uint32_t srcLinear = srcInfo.Address >= 0 ? (uint32_t)srcInfo.Address : srcBusAddr;
		if(bankIsWram) {
			//$7E/$7F WRAM: linear offset is 0x00000-0x1FFFF (128KB, two banks)
			srcLinear = ((srcBankLow == 0x7F ? 0x10000 : 0) | (srcBusAddr & 0xFFFF)) & 0x1FFFF;
		}
		SnesMapLoadLog::DebugDmaResolved(srcBusAddr, srcLinear, srcIsRom, srcIsWram);

		bool destIsVram = channel.DestAddress == 0x18 || channel.DestAddress == 0x19;
		bool destIsCgram = channel.DestAddress == 0x22;
		bool destIsWram = channel.DestAddress == 0x80;  //0x2100|0x80 = $2180 (WMDATA/WRAM)

		if((destIsVram || destIsCgram) && (srcIsRom || srcIsWram)) {
			//R3.2: diagnostics - log the RAW hardware DMA source (before mapping).
			SnesMapLoadLog::AppendDmaSrc(_memoryManager->GetEmu()->GetFrameCount(), srcBusAddr, channel.DestAddress, _activeChannel & 0x07);
			uint8_t targetType = destIsCgram ? 1 : 0;
			uint32_t srcBankTag = srcIsWram ? 1 : 0;
			uint32_t targetAddr = targetType == 0 ? trackerVram : _memoryManager->GetConsole()->GetPpu()->GetDebugCgAddress();
			SnesMapLoadLog::AppendDma(
				_memoryManager->GetEmu()->GetFrameCount(),
				(int32_t)(_memoryManager->GetMasterClock() & 0x7FFFFFFF),
				targetType,
				targetAddr,
				srcBankTag,
				srcLinear,
				channel.TransferSize,
				_activeChannel & 0x07,
				srcBusAddr,
				channel.DestAddress);
		}

		if(destIsWram && srcIsRom) {
			//R3.2: diagnostics - log the ROM->WRAM DMA (raw source bus).
			SnesMapLoadLog::AppendDmaSrc(_memoryManager->GetEmu()->GetFrameCount(), srcBusAddr, 0x80, _activeChannel & 0x07);
			//DMA ROM -> WRAM: remember the ROM source for each WRAM page so a later
			//DMA from that WRAM to VRAM/CGRAM resolves back to the real ROM address.
			//The DMA writes TransferSize bytes at the current WRAM address ($2181-$2183).
			uint32_t wramAddr = _memoryManager->GetWramPosition();
			if(wramAddr < 0x20000) {
				SnesMapLoadLog::TrackWramDma(wramAddr, srcLinear, channel.TransferSize);
			}
		}

		if(destIsWram && srcIsWram) {
			//R3.2: DMA WRAM -> WRAM (decompression buffer copies). Record the transfer so
			//the reverse-search chain can walk WRAM -> WRAM -> ROM through the ring.
			uint32_t wramAddr = _memoryManager->GetWramPosition();
			if(wramAddr < 0x20000 && srcLinear < 0x20000) {
				SnesMapLoadLog::TrackWramDmaToWram(wramAddr, srcLinear, channel.TransferSize);
			}
		}
	}

	const uint8_t* transferOffsets = _transferOffset[channel.TransferMode];

	uint32_t i = 0;
	do {
		//Manual DMA transfers run to the end of the transfer when started
		CopyDmaByte(
			(channel.SrcBank << 16) | channel.SrcAddress,
			0x2100 | (channel.DestAddress + transferOffsets[i & 0x03]),
			channel.InvertDirection);

		if(!channel.FixedTransfer) {
			channel.SrcAddress += channel.Decrement ? -1 : 1;
		}

		channel.TransferSize--;
		i++;
		ProcessPendingTransfers();
	} while(channel.TransferSize > 0 && channel.DmaActive);

	_dmaClockCounter += 8 * i;

	channel.DmaActive = false;
}

bool SnesDmaController::InitHdmaChannels()
{
	_hdmaInitPending = false;

	//Reset internal flags on every frame, whether or not the channels are enabled
	for(int i = 0; i < 8; i++) {
		_state.Channel[i].DoTransfer = false; //not resetting this causes graphical glitches in some games (Aladdin, Super Ghouls and Ghosts)
	}
	_stoppedHdmaChannels = 0;

	if(!_state.HdmaChannels) {
		//No channels are enabled, no more processing needs to be done
		UpdateNeedToProcessFlag();
		return false;
	}

	bool needSync = !HasActiveDmaChannel();
	if(needSync) {
		SyncStartDma();
	}
	_dmaClockCounter += 8;
	_memoryManager->IncMasterClock8();

	for(int i = 0; i < 8; i++) {
		DmaChannelConfig& ch = _state.Channel[i];

		//Set DoTransfer to true for all channels if any HDMA channel is enabled
		ch.DoTransfer = true;

		if(_state.HdmaChannels & (1 << i)) {
			//"1. Copy AAddress into Address."
			ch.HdmaTableAddress = ch.SrcAddress;
			ch.DmaActive = false;

			//"2. Load $43xA (Line Counter and Repeat) from the table. I believe $00 will terminate this channel immediately."
			ch.HdmaLineCounterAndRepeat = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress, true);
			_memoryManager->IncMasterClock4();
			_dmaClockCounter += 8;

			ch.HdmaTableAddress++;
			bool stopped = ch.HdmaLineCounterAndRepeat == 0;
			if(stopped) {
				StopHdmaChannel(i);
			}

			//3. Load Indirect Address, if necessary.
			if(ch.HdmaIndirectAddressing) {
				uint8_t lsb = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress++, true);
				_memoryManager->IncMasterClock4();
				_dmaClockCounter += 8;

				if(!stopped) {
					uint8_t msb = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress++, true);
					_memoryManager->IncMasterClock4();
					_dmaClockCounter += 8;
					ch.TransferSize = (msb << 8) | lsb;
				} else {
					ch.TransferSize = (lsb << 8);
				}
			}
		}
	}

	if(needSync) {
		SyncEndDma();
	}

	UpdateNeedToProcessFlag();
	return true;
}

void SnesDmaController::RunHdmaTransfer(DmaChannelConfig& channel)
{
	const uint8_t* transferOffsets = _transferOffset[channel.TransferMode];
	uint8_t transferByteCount = _transferByteCount[channel.TransferMode];
	channel.DmaActive = false;

	uint8_t i = 0;
	if(channel.HdmaIndirectAddressing) {
		do {
			CopyDmaByte(
				(channel.HdmaBank << 16) | channel.TransferSize,
				0x2100 | (channel.DestAddress + transferOffsets[i]),
				channel.InvertDirection);
			channel.TransferSize++;
			i++;
		} while(i < transferByteCount);
	} else {
		do {
			CopyDmaByte(
				(channel.SrcBank << 16) | channel.HdmaTableAddress,
				0x2100 | (channel.DestAddress + transferOffsets[i]),
				channel.InvertDirection);
			channel.HdmaTableAddress++;
			i++;
		} while(i < transferByteCount);
	}

	_dmaClockCounter += 8 * i;
}

void SnesDmaController::SyncStartDma()
{
	//"after the pause, wait 2-8 master cycles to reach a whole multiple of 8 master cycles since reset"
	_dmaClockCounter = 8 - (_memoryManager->GetMasterClock() & 0x07);
	_memoryManager->IncrementMasterClockValue(_dmaClockCounter);
}

void SnesDmaController::SyncEndDma()
{
	//"Then wait 2-8 master cycles to reach a whole number of CPU Clock cycles since the pause"
	uint8_t cpuSpeed = _memoryManager->GetCpuSpeed();
	_memoryManager->IncrementMasterClockValue(cpuSpeed - (_dmaClockCounter % cpuSpeed));
}

bool SnesDmaController::HasActiveDmaChannel()
{
	for(int i = 0; i < 8; i++) {
		if(_state.Channel[i].DmaActive) {
			return true;
		}
	}
	return false;
}

uint8_t SnesDmaController::GetActiveHdmaChannels()
{
	return _state.HdmaChannels & ~_stoppedHdmaChannels;
}

bool SnesDmaController::IsHdmaChannelActive(int i)
{
	return GetActiveHdmaChannels() & (1 << i);
}

void SnesDmaController::StopHdmaChannel(int i)
{
	_stoppedHdmaChannels |= (1 << i);
}

bool SnesDmaController::ProcessHdmaChannels()
{
	_hdmaPending = false;

	if(!GetActiveHdmaChannels()) {
		UpdateNeedToProcessFlag();
		return false;
	}

	bool needSync = !HasActiveDmaChannel();
	if(needSync) {
		SyncStartDma();
	}
	_dmaClockCounter += 8;
	_memoryManager->IncMasterClock8();

	uint8_t originalActiveChannel = _activeChannel;

	//Run all the DMA transfers for each channel first, before fetching data for the next scanline
	for(int i = 0; i < 8; i++) {
		DmaChannelConfig& ch = _state.Channel[i];
		if(!IsHdmaChannelActive(i)) {
			continue;
		}

		ch.DmaActive = false;

		//1. If DoTransfer is false, skip to step 3.
		if(ch.DoTransfer) {
			//2. For the number of bytes (1, 2, or 4) required for this Transfer Mode...
			_activeChannel = SnesDmaController::HdmaChannelFlag | i;
			RunHdmaTransfer(ch);
		}
	}

	//Update the channel's state & fetch data for the next scanline
	for(int i = 0; i < 8; i++) {
		DmaChannelConfig& ch = _state.Channel[i];
		if(!IsHdmaChannelActive(i)) {
			continue;
		}

		//3. Decrement $43xA.
		ch.HdmaLineCounterAndRepeat--;

		//4. Set DoTransfer to the value of Repeat.
		ch.DoTransfer = (ch.HdmaLineCounterAndRepeat & 0x80) != 0;

		//"a. Read the next byte from Address into $43xA (thus, into both Line Counter and Repeat)."
		//This value is discarded if the line counter isn't 0
		uint8_t newCounter = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress, true);
		_memoryManager->IncMasterClock4();
		_dmaClockCounter += 8;

		//5. If Line Counter is zero...
		if((ch.HdmaLineCounterAndRepeat & 0x7F) == 0) {
			ch.HdmaLineCounterAndRepeat = newCounter;
			ch.HdmaTableAddress++;

			//"b. If Addressing Mode is Indirect, read two bytes from Address into Indirect Address(and increment Address by two bytes)."
			if(ch.HdmaIndirectAddressing) {
				if(ch.HdmaLineCounterAndRepeat == 0 && IsLastActiveHdmaChannel(i)) {
					//"One oddity: if $43xA is 0 and this is the last active HDMA channel for this scanline, only load one byte for Address,
					//and use the $00 for the low byte.So Address ends up incremented one less than otherwise expected, and one less CPU Cycle is used."
					uint8_t msb = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress++, true);
					_memoryManager->IncMasterClock4();
					_dmaClockCounter += 8;
					ch.TransferSize = (msb << 8);
				} else {
					//"If a new indirect address is required, 16 master cycles are taken to load it."
					uint8_t lsb = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress++, true);
					_memoryManager->IncMasterClock4();

					uint8_t msb = _memoryManager->ReadDma((ch.SrcBank << 16) | ch.HdmaTableAddress++, true);
					_memoryManager->IncMasterClock4();

					_dmaClockCounter += 16;

					ch.TransferSize = (msb << 8) | lsb;
				}
			}

			//"c. If $43xA is zero, terminate this HDMA channel for this frame. The bit in $420c is not cleared, though, so it may be automatically restarted next frame."
			if(ch.HdmaLineCounterAndRepeat == 0) {
				StopHdmaChannel(i);
			}

			//"d. Set DoTransfer to true."
			ch.DoTransfer = true;
		}
	}

	if(needSync) {
		//If we ran a HDMA transfer, sync
		SyncEndDma();
	}

	_activeChannel = originalActiveChannel;
	UpdateNeedToProcessFlag();

	return true;
}

bool SnesDmaController::IsLastActiveHdmaChannel(uint8_t channel)
{
	uint8_t mask = (1 << (channel + 1)) - 1;
	return (GetActiveHdmaChannels() & ~mask) == 0;
}

void SnesDmaController::UpdateNeedToProcessFlag()
{
	//Slightly faster execution time by doing this rather than processing all 4 flags on each cycle
	_needToProcess = _hdmaPending || _hdmaInitPending || _dmaStartDelay || _dmaPending;
}

void SnesDmaController::BeginHdmaTransfer()
{
	if(GetActiveHdmaChannels()) {
		_hdmaPending = true;
		_dmaStartDelay = true;
		UpdateNeedToProcessFlag();
	}
}

void SnesDmaController::BeginHdmaInit()
{
	_dmaStartDelay = true;
	_hdmaInitPending = true;
	UpdateNeedToProcessFlag();
}

bool SnesDmaController::ProcessPendingTransfers()
{
	if(!_needToProcess) {
		return false;
	}

	if(_dmaStartDelay) {
		_dmaStartDelay = false;
		return false;
	}

	if(_hdmaPending) {
		return ProcessHdmaChannels();
	} else if(_hdmaInitPending) {
		return InitHdmaChannels();
	} else if(_dmaPending) {
		_dmaPending = false;

		SyncStartDma();
		_memoryManager->IncMasterClock8();
		_dmaClockCounter += 8;
		ProcessPendingTransfers();

		for(int i = 0; i < 8; i++) {
			if(_state.Channel[i].DmaActive) {
				_activeChannel = i;
				RunDma(_state.Channel[i]);
			}
		}

		SyncEndDma();
		UpdateNeedToProcessFlag();

		return true;
	}

	return false;
}

void SnesDmaController::Write(uint16_t addr, uint8_t value)
{
	switch(addr) {
		case 0x420B: {
			//MDMAEN - DMA Enable
			for(int i = 0; i < 8; i++) {
				if(value & (1 << i)) {
					_state.Channel[i].DmaActive = true;
				}
			}

			if(value) {
				_dmaPending = true;
				_dmaStartDelay = true;
				UpdateNeedToProcessFlag();
			}
			break;
		}

		case 0x420C:
			//HDMAEN - HDMA Enable
			_state.HdmaChannels = value;
			break;

		case 0x4300:
		case 0x4310:
		case 0x4320:
		case 0x4330:
		case 0x4340:
		case 0x4350:
		case 0x4360:
		case 0x4370: {
			//DMAPx - DMA Control for Channel x
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.InvertDirection = (value & 0x80) != 0;
			channel.HdmaIndirectAddressing = (value & 0x40) != 0;
			channel.UnusedControlFlag = (value & 0x20) != 0;
			channel.Decrement = (value & 0x10) != 0;
			channel.FixedTransfer = (value & 0x08) != 0;
			channel.TransferMode = value & 0x07;
			break;
		}

		case 0x4301:
		case 0x4311:
		case 0x4321:
		case 0x4331:
		case 0x4341:
		case 0x4351:
		case 0x4361:
		case 0x4371: {
			//BBADx - DMA Destination Register for Channel x
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.DestAddress = value;
			break;
		}

		case 0x4302:
		case 0x4312:
		case 0x4322:
		case 0x4332:
		case 0x4342:
		case 0x4352:
		case 0x4362:
		case 0x4372: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.SrcAddress = (channel.SrcAddress & 0xFF00) | value;
			break;
		}

		case 0x4303:
		case 0x4313:
		case 0x4323:
		case 0x4333:
		case 0x4343:
		case 0x4353:
		case 0x4363:
		case 0x4373: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.SrcAddress = (channel.SrcAddress & 0xFF) | (value << 8);
			break;
		}

		case 0x4304:
		case 0x4314:
		case 0x4324:
		case 0x4334:
		case 0x4344:
		case 0x4354:
		case 0x4364:
		case 0x4374: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.SrcBank = value;
			break;
		}

		case 0x4305:
		case 0x4315:
		case 0x4325:
		case 0x4335:
		case 0x4345:
		case 0x4355:
		case 0x4365:
		case 0x4375: {
			//DASxL - DMA Size / HDMA Indirect Address low byte(x = 0 - 7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.TransferSize = (channel.TransferSize & 0xFF00) | value;
			break;
		}

		case 0x4306:
		case 0x4316:
		case 0x4326:
		case 0x4336:
		case 0x4346:
		case 0x4356:
		case 0x4366:
		case 0x4376: {
			//DASxL - DMA Size / HDMA Indirect Address low byte(x = 0 - 7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.TransferSize = (channel.TransferSize & 0xFF) | (value << 8);
			break;
		}

		case 0x4307:
		case 0x4317:
		case 0x4327:
		case 0x4337:
		case 0x4347:
		case 0x4357:
		case 0x4367:
		case 0x4377: {
			//DASBx - HDMA Indirect Address bank byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.HdmaBank = value;
			break;
		}

		case 0x4308:
		case 0x4318:
		case 0x4328:
		case 0x4338:
		case 0x4348:
		case 0x4358:
		case 0x4368:
		case 0x4378: {
			//A2AxL - HDMA Table Address low byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.HdmaTableAddress = (channel.HdmaTableAddress & 0xFF00) | value;
			break;
		}

		case 0x4309:
		case 0x4319:
		case 0x4329:
		case 0x4339:
		case 0x4349:
		case 0x4359:
		case 0x4369:
		case 0x4379: {
			//A2AxH - HDMA Table Address high byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.HdmaTableAddress = (value << 8) | (channel.HdmaTableAddress & 0xFF);
			break;
		}

		case 0x430A:
		case 0x431A:
		case 0x432A:
		case 0x433A:
		case 0x434A:
		case 0x435A:
		case 0x436A:
		case 0x437A: {
			//DASBx - HDMA Indirect Address bank byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.HdmaLineCounterAndRepeat = value;
			break;
		}

		case 0x430B:
		case 0x431B:
		case 0x432B:
		case 0x433B:
		case 0x434B:
		case 0x435B:
		case 0x436B:
		case 0x437B:
		case 0x430F:
		case 0x431F:
		case 0x432F:
		case 0x433F:
		case 0x434F:
		case 0x435F:
		case 0x436F:
		case 0x437F: {
			//$43xB (+ mirrors at $43xF) both contain hold a byte that can be read/written to and has no effect
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			channel.UnusedRegister = value;
			break;
		}
	}
}

uint8_t SnesDmaController::Read(uint16_t addr)
{
	switch(addr) {
		case 0x4300:
		case 0x4310:
		case 0x4320:
		case 0x4330:
		case 0x4340:
		case 0x4350:
		case 0x4360:
		case 0x4370: {
			//DMAPx - DMA Control for Channel x
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return (
				(channel.InvertDirection ? 0x80 : 0) |
				(channel.HdmaIndirectAddressing ? 0x40 : 0) |
				(channel.UnusedControlFlag ? 0x20 : 0) |
				(channel.Decrement ? 0x10 : 0) |
				(channel.FixedTransfer ? 0x08 : 0) |
				(channel.TransferMode & 0x07));
		}

		case 0x4301:
		case 0x4311:
		case 0x4321:
		case 0x4331:
		case 0x4341:
		case 0x4351:
		case 0x4361:
		case 0x4371: {
			//BBADx - DMA Destination Register for Channel x
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.DestAddress;
		}

		case 0x4302:
		case 0x4312:
		case 0x4322:
		case 0x4332:
		case 0x4342:
		case 0x4352:
		case 0x4362:
		case 0x4372: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.SrcAddress & 0xFF;
		}

		case 0x4303:
		case 0x4313:
		case 0x4323:
		case 0x4333:
		case 0x4343:
		case 0x4353:
		case 0x4363:
		case 0x4373: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return (channel.SrcAddress >> 8) & 0xFF;
		}

		case 0x4304:
		case 0x4314:
		case 0x4324:
		case 0x4334:
		case 0x4344:
		case 0x4354:
		case 0x4364:
		case 0x4374: {
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.SrcBank;
		}

		case 0x4305:
		case 0x4315:
		case 0x4325:
		case 0x4335:
		case 0x4345:
		case 0x4355:
		case 0x4365:
		case 0x4375: {
			//DASxL - DMA Size / HDMA Indirect Address low byte(x = 0 - 7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.TransferSize & 0xFF;
		}

		case 0x4306:
		case 0x4316:
		case 0x4326:
		case 0x4336:
		case 0x4346:
		case 0x4356:
		case 0x4366:
		case 0x4376: {
			//DASxL - DMA Size / HDMA Indirect Address low byte(x = 0 - 7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return (channel.TransferSize >> 8) & 0xFF;
		}

		case 0x4307:
		case 0x4317:
		case 0x4327:
		case 0x4337:
		case 0x4347:
		case 0x4357:
		case 0x4367:
		case 0x4377: {
			//DASBx - HDMA Indirect Address bank byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.HdmaBank;
		}

		case 0x4308:
		case 0x4318:
		case 0x4328:
		case 0x4338:
		case 0x4348:
		case 0x4358:
		case 0x4368:
		case 0x4378: {
			//A2AxL - HDMA Table Address low byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.HdmaTableAddress & 0xFF;
		}

		case 0x4309:
		case 0x4319:
		case 0x4329:
		case 0x4339:
		case 0x4349:
		case 0x4359:
		case 0x4369:
		case 0x4379: {
			//A2AxH - HDMA Table Address high byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return (channel.HdmaTableAddress >> 8) & 0xFF;
		}

		case 0x430A:
		case 0x431A:
		case 0x432A:
		case 0x433A:
		case 0x434A:
		case 0x435A:
		case 0x436A:
		case 0x437A: {
			//DASBx - HDMA Indirect Address bank byte (x=0-7)
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.HdmaLineCounterAndRepeat;
		}

		case 0x430B:
		case 0x431B:
		case 0x432B:
		case 0x433B:
		case 0x434B:
		case 0x435B:
		case 0x436B:
		case 0x437B:
		case 0x430F:
		case 0x431F:
		case 0x432F:
		case 0x433F:
		case 0x434F:
		case 0x435F:
		case 0x436F:
		case 0x437F: {
			//$43xB (+ mirrors at $43xF) both contain hold a byte that can be read/written to and has no effect
			DmaChannelConfig& channel = _state.Channel[(addr & 0x70) >> 4];
			return channel.UnusedRegister;
		}
	}
	return _memoryManager->GetOpenBus();
}

uint8_t SnesDmaController::GetActiveChannel()
{
	return _activeChannel;
}

DmaChannelConfig SnesDmaController::GetChannelConfig(uint8_t channel)
{
	return _state.Channel[channel];
}

void SnesDmaController::Serialize(Serializer& s)
{
	SV(_hdmaPending);
	SV(_state.HdmaChannels);
	SV(_dmaPending);
	SV(_dmaClockCounter);
	SV(_hdmaInitPending);
	SV(_dmaStartDelay);
	SV(_needToProcess);
	SV(_stoppedHdmaChannels);

	for(int i = 0; i < 8; i++) {
		SVI(_state.Channel[i].Decrement);
		SVI(_state.Channel[i].DestAddress);
		SVI(_state.Channel[i].DoTransfer);
		SVI(_state.Channel[i].FixedTransfer);
		SVI(_state.Channel[i].HdmaBank);
		SVI(_state.Channel[i].HdmaIndirectAddressing);
		SVI(_state.Channel[i].HdmaLineCounterAndRepeat);
		SVI(_state.Channel[i].HdmaTableAddress);
		SVI(_state.Channel[i].InvertDirection);
		SVI(_state.Channel[i].SrcAddress);
		SVI(_state.Channel[i].SrcBank);
		SVI(_state.Channel[i].TransferMode);
		SVI(_state.Channel[i].TransferSize);
		SVI(_state.Channel[i].UnusedControlFlag);
		SVI(_state.Channel[i].DmaActive);
		SVI(_state.Channel[i].UnusedRegister);
	}
}
