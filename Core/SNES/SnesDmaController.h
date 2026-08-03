#pragma once
#include "pch.h"
#include "SNES/DmaControllerTypes.h"
#include "Utilities/ISerializable.h"

class SnesMemoryManager;

class SnesDmaController final : public ISerializable
{
public:
	static constexpr int DmaLogSize = 1 << 15;

	struct DmaLogEntry
	{
		int64_t frame;
		int32_t cycle;
		uint8_t channel;
		uint8_t isHdma;
		uint8_t toCpu;
		uint8_t mode;
		uint32_t sourceBank;
		uint32_t sourceAddr;
		uint32_t destAddr;
		uint32_t length;
		uint16_t vramAddr;
	};

	static DmaLogEntry DmaLog[DmaLogSize];
	static uint32_t DmaLogHead;
	static uint32_t DmaLogCount;
	static bool DmaLogEnabled;

	static void SetDmaLogEnabled(bool enabled);
	static uint32_t GetDmaLogCount();
	static uint32_t GetDmaLog(DmaLogEntry* entries, uint32_t start, uint32_t count);

private:
	static constexpr uint8_t HdmaChannelFlag = 0x40;

	SnesDmaControllerState _state = {};

	bool _needToProcess = false;
	bool _hdmaPending = false;
	bool _hdmaInitPending = false;
	bool _dmaStartDelay = false;
	bool _dmaPending = false;
	uint32_t _dmaClockCounter = 0;
	uint8_t _stoppedHdmaChannels = 0;

	uint8_t _activeChannel = 0; //Used by debugger's event viewer

	SnesMemoryManager* _memoryManager;

	void AppendDmaLogEntry(uint8_t channel, bool isHdma, bool toCpu, DmaChannelConfig& config);

	void CopyDmaByte(uint32_t addressBusA, uint16_t addressBusB, bool fromBtoA);

	void RunDma(DmaChannelConfig& channel);

	void RunHdmaTransfer(DmaChannelConfig& channel);
	bool ProcessHdmaChannels();
	bool IsLastActiveHdmaChannel(uint8_t channel);
	bool InitHdmaChannels();

	void SyncStartDma();
	void SyncEndDma();
	void UpdateNeedToProcessFlag();

	bool HasActiveDmaChannel();
	uint8_t GetActiveHdmaChannels();
	bool IsHdmaChannelActive(int i);
	void StopHdmaChannel(int i);

public:
	SnesDmaController(SnesMemoryManager* memoryManager);

	SnesDmaControllerState& GetState();

	void Reset();

	void BeginHdmaTransfer();
	void BeginHdmaInit();

	__forceinline bool HasPendingTransfer() { return _needToProcess; }

	bool ProcessPendingTransfers();

	void Write(uint16_t addr, uint8_t value);
	uint8_t Read(uint16_t addr);

	uint8_t GetActiveChannel();
	DmaChannelConfig GetChannelConfig(uint8_t channel);

	void Serialize(Serializer& s) override;
};