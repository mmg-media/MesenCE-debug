#include "Common.h"
#include "Core/SNES/SnesDmaController.h"
#include "Core/SNES/DmaControllerTypes.h"
#include "Core/Shared/Emulator.h"

// DMA-Log-Exports für den kumulativen DMA-Ring-Puffer (Anforderung P1.1)

extern "C" {
	DllExport void __stdcall snes_set_dma_log_enabled(bool enabled)
	{
		SnesDmaController::SetDmaLogEnabled(enabled);
	}

	DllExport uint32_t __stdcall snes_get_dma_log_count()
	{
		return SnesDmaController::GetDmaLogCount();
	}

	DllExport uint32_t __stdcall snes_get_dma_log(SnesDmaController::DmaLogEntry* entries, uint32_t start, uint32_t count)
	{
		return SnesDmaController::GetDmaLog(entries, start, count);
	}
}
