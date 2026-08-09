using Mesen.Interop;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.LiveApi
{
	public static class GfxService
	{
		private static SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private static bool _liveTrackingInit;

		private static T RunExclusive<T>(Func<T> action)
		{
			_gate.Wait();
			try {
				return action();
			} finally {
				_gate.Release();
			}
		}

		private static CpuType? ParseCpuType(string cpuType)
		{
			if(Enum.TryParse<CpuType>(cpuType, out CpuType result)) {
				return result;
			}
			return null;
		}

		private static T ParseEnum<T>(string value, T defaultValue) where T : struct
		{
			return Enum.TryParse<T>(value, true, out T result) ? result : defaultValue;
		}

		private static SnesGfxData PrepareData(CpuType cpu)
		{
			SnesPpuState ppuState = DebugApi.GetPpuState<SnesPpuState>(cpu);
			BaseState ppuToolsState = DebugApi.GetPpuToolsState(cpu);
			byte[] vram = DebugApi.GetMemoryState(MemoryType.SnesVideoRam);
			byte[] spriteRam = DebugApi.GetMemoryState(MemoryType.SnesSpriteRam);
			DebugPaletteInfo paletteInfo = DebugApi.GetPaletteInfo(cpu);
			UInt32[] palette = paletteInfo.GetRgbPalette();
			return new SnesGfxData(ppuState, ppuToolsState, vram, spriteRam, palette);
		}

		private static byte[] EncodePng(int width, int height, UInt32[] pixels)
		{
			byte[] bgra = new byte[width * height * 4];
			Buffer.BlockCopy(pixels, 0, bgra, 0, bgra.Length);
			return EncodePngBytes(width, height, bgra);
		}

		private static byte[] EncodePngBytes(int width, int height, byte[] bgraBytes)
		{
			using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
			Marshal.Copy(bgraBytes, 0, bitmap.GetPixels(), bgraBytes.Length);
			using SKImage image = SKImage.FromBitmap(bitmap);
			using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
			return data.ToArray();
		}

		public static JsonNode? GetGfxState(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					SnesPpuState state = data.PpuState;

					JsonArray layerArray = new JsonArray();
					for(int i = 0; i < 4; i++) {
						LayerConfig layer = state.Layers[i];
						int bpp = GetLayerBpp(state, i);
						layerArray.Add((JsonNode)(new JsonObject() {
							["index"] = i,
							["name"] = "BG" + (i + 1),
							["enabled"] = bpp != 0,
							["bpp"] = bpp,
							["tilemapAddress"] = layer.TilemapAddress,
							["chrAddress"] = layer.ChrAddress,
							["hScroll"] = layer.HScroll,
							["vScroll"] = layer.VScroll,
							["doubleWidth"] = layer.DoubleWidth,
							["doubleHeight"] = layer.DoubleHeight,
							["largeTiles"] = layer.LargeTiles
						}));
					}

					JsonArray mainLayers = new JsonArray();
					JsonArray subLayers = new JsonArray();
					for(int i = 0; i < 4; i++) {
						if((state.MainScreenLayers & (1 << i)) != 0) {
							mainLayers.Add((JsonNode)JsonValue.Create(i));
						}
						if((state.SubScreenLayers & (1 << i)) != 0) {
							subLayers.Add((JsonNode)JsonValue.Create(i));
						}
					}

					JsonArray colors = new JsonArray();
					DebugPaletteInfo paletteInfo = DebugApi.GetPaletteInfo(cpu.Value);
					UInt32[] rgb = paletteInfo.GetRgbPalette();
					int colorCount = Math.Min(rgb.Length, 256);
					for(int i = 0; i < colorCount; i++) {
						colors.Add((JsonNode)JsonValue.Create($"#{rgb[i] & 0xFFFFFF:X6}"));
					}

					return new JsonObject() {
						["cpu"] = cpu.Value.ToString(),
						["bgMode"] = state.BgMode,
						["mode1Bg3Priority"] = state.Mode1Bg3Priority,
						["mainScreenLayers"] = state.MainScreenLayers,
						["subScreenLayers"] = state.SubScreenLayers,
						["mainLayers"] = mainLayers,
						["subLayers"] = subLayers,
						["forcedBlank"] = state.ForcedBlank,
						["screenBrightness"] = state.ScreenBrightness,
						["overscanMode"] = state.OverscanMode,
						["hiResMode"] = state.HiResMode,
						["screenInterlace"] = state.ScreenInterlace,
						["directColorMode"] = state.DirectColorMode,
						["objInterlace"] = state.ObjInterlace,
						["enableOamPriority"] = state.EnableOamPriority,
						["oamMode"] = state.OamMode,
						["oamBaseAddress"] = state.OamBaseAddress,
						["oamAddressOffset"] = state.OamAddressOffset,
						["layers"] = layerArray,
						["mode7"] = new JsonObject() {
							["hScroll"] = state.Mode7.HScroll,
							["vScroll"] = state.Mode7.VScroll,
							["centerX"] = state.Mode7.CenterX,
							["centerY"] = state.Mode7.CenterY,
							["largeMap"] = state.Mode7.LargeMap,
							["horizontalMirroring"] = state.Mode7.HorizontalMirroring,
							["verticalMirroring"] = state.Mode7.VerticalMirroring,
							["matrix"] = new JsonArray(
								JsonValue.Create(state.Mode7.Matrix[0]),
								JsonValue.Create(state.Mode7.Matrix[1]),
								JsonValue.Create(state.Mode7.Matrix[2]),
								JsonValue.Create(state.Mode7.Matrix[3]))
						},
						["palette"] = new JsonObject() {
							["colorCount"] = paletteInfo.ColorCount,
							["bgColorCount"] = paletteInfo.BgColorCount,
							["spriteColorCount"] = paletteInfo.SpriteColorCount,
							["spritePaletteOffset"] = paletteInfo.SpritePaletteOffset,
							["colorsPerPalette"] = paletteInfo.ColorsPerPalette,
							["colors"] = colors
						}
					};
				} catch {
					return null;
				}
			});
		}

		private static int GetLayerBpp(SnesPpuState state, int layer)
		{
			if(state.BgMode == 0) {
				return 2;
			}
			if(state.BgMode == 1) {
				switch(layer) {
					case 0: return 4;
					case 1: return 4;
					case 2: return state.Mode1Bg3Priority ? 8 : 2;
					case 3: return 0;
				}
			} else if(state.BgMode == 2) {
				switch(layer) {
					case 0: return 4;
					case 1: return 4;
					default: return 0;
				}
			} else if(state.BgMode == 3) {
				switch(layer) {
					case 0: return 8;
					case 1: return 4;
					default: return 0;
				}
			} else if(state.BgMode == 4) {
				switch(layer) {
					case 0: return 8;
					case 1: return 2;
					default: return 0;
				}
			} else if(state.BgMode == 5) {
				switch(layer) {
					case 0: return 4;
					case 1: return 2;
					default: return 0;
				}
			} else if(state.BgMode == 6) {
				switch(layer) {
					case 0: return 4;
					default: return 0;
				}
			} else if(state.BgMode == 7) {
				switch(layer) {
					case 0: return 7;
					case 1: return state.ExtBgEnabled ? 7 : 0;
					default: return 0;
				}
			}
			return 0;
		}

		/// <summary>
		/// R3.2: Return the CGRAM palettes as structured JSON - 16 palettes of 16 colors
		/// each (first 8 = background, last 8 = sprites), with CSS hex colors per entry.
		/// Shows how many palettes are loaded in CGRAM at the moment. Colors that changed
		/// since the previous call are flagged "animated" - this reveals HDMA/mid-frame
		/// palette animation (same CGRAM address, different color during the frame).
		/// </summary>
		private static bool HasAnimatedColor(bool[] animated, int paletteIndex, int colorsPerPalette)
		{
			int start = paletteIndex * colorsPerPalette;
			for(int i = start; i < start + colorsPerPalette && i < animated.Length; i++) {
				if(animated[i]) {
					return true;
				}
			}
			return false;
		}

		public static JsonNode? GetPalettesJson(string cpuType, bool live = false, string? filterType = null, int? filterSlot = null)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					if(live && !_liveTrackingInit) {
						//R3.2: live tracking - keep the reverse-lookup tables current so the
						//palette viewer shows ROM sources continuously. NOTE: no
						//InitializeDebugger() here - it is not thread-safe when called from
						//the webui poll thread while the emulator runs, and it is already
						//initialized by the LiveApi server / debugger setup.
						_liveTrackingInit = true;
						DebugApi.SnesMapLoadLogSetEnabled(true);
						DebugApi.SnesMapLoadLogSetAutoCapture(true);
						DebugApi.SnesMapLoadLogSetLiveTracking(true);
					}
					DebugPaletteInfo paletteInfo = DebugApi.GetPaletteInfo(cpu.Value);
					UInt32[] rgb = paletteInfo.GetRgbPalette();
					int colorCount = Math.Min((int)rgb.Length, (int)paletteInfo.ColorCount);
					int colorsPerPalette = Math.Max(1, (int)paletteInfo.ColorsPerPalette);
					int paletteCount = colorCount / colorsPerPalette;
					int bgPalettes = Math.Min(paletteCount, (int)(paletteInfo.BgColorCount / colorsPerPalette));
					int spriteOffset = (int)(paletteInfo.SpritePaletteOffset / colorsPerPalette);

					//R3.2: the webui compares colors between polls to mark animated palettes -
					//the backend just returns the current colors fast (no sleeps, no blinking).
					bool[] animated = new bool[colorCount];

					JsonArray palettes = new JsonArray();
					for(int p = 0; p < paletteCount; p++) {
						string pType = p < bgPalettes ? "bg" : "sprite";
						int pSlot = p < bgPalettes ? p : (p - bgPalettes);
						//filter: requested type/slot - skip non-matching palettes
						if(filterType != null && !pType.Equals(filterType, StringComparison.OrdinalIgnoreCase)) {
							continue;
						}
						if(filterSlot != null && pSlot != filterSlot.Value) {
							continue;
						}
						JsonArray colors = new JsonArray();
						for(int c = 0; c < colorsPerPalette; c++) {
							int idx = p * colorsPerPalette + c;
							if(idx >= rgb.Length) {
								break;
							}
							UInt32 col = rgb[idx];
							byte r = (byte)((col >> 16) & 0xFF);
							byte g = (byte)((col >> 8) & 0xFF);
							byte b = (byte)(col & 0xFF);
							colors.Add((JsonNode)new JsonObject() {
								["hex"] = "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2"),
								["animated"] = animated[idx]
							});
						}
						//R3.2: SOURCE of this palette - pure WRAM->ROM chain (NO byte-matching:
						//that only works for uncompressed data). The CGRAM palette buffer lives
						//in WRAM around 0x10600; each palette block is resolved through the
						//WramRomByte chain to its exact ROM source. Positions whose chain was
						//clobbered by a fade (e.g. BG0 -> 0x04A957) are skipped, and a dominant
						//contiguous ROM run is reported.
						JsonArray srcList = new JsonArray();
						List<UInt32> wramSources = new List<UInt32>();
						//R3.2: GENERIC palette source - resolve each CGRAM color back through the
						//TRANSFER capture ring (CGRAM -> WRAM -> ROM) instead of a game-specific
						//WRAM palette-buffer offset. Works for any game: the trace returns the
						//chain of transfers that produced the CGRAM word, ending at the ROM source.
						for(int c = 0; c < colorsPerPalette && p * colorsPerPalette + c < 0x100; c++) {
							int cgramIdx = p * colorsPerPalette + c;
							UInt32 direct = 0xFFFFFFFF;
							const int maxT = 4;
							DebugApi.TransferInterop[] tchain = new DebugApi.TransferInterop[maxT];
							UInt32 tGot = DebugApi.SnesMapLoadTrace(4, (UInt32)cgramIdx, tchain, maxT);
							//walk the chain to the LAST step - the ROM source (srcMem==0).
							//If no ROM step, use the first (nearest) source.
							direct = 0xFFFFFFFF;
							for(int ti = (int)tGot - 1; ti >= 0; ti--) {
								if(tchain[ti].srcMem == 0) {
									direct = tchain[ti].srcAddr;
									break;
								}
								if(direct == 0xFFFFFFFF) {
									direct = tchain[ti].srcAddr;
								}
							}
							if(direct == 0xFFFFFFFF || direct == 0 || IsCodeSource(direct)) {
								continue;
							}
							wramSources.Add(direct);
						}
						wramSources.Sort();
						int wi = 0;
						while(wi < wramSources.Count) {
							UInt32 start = wramSources[wi];
							UInt32 end = start;
							int wj = wi + 1;
							while(wj < wramSources.Count && wramSources[wj] == end + 1) {
								end = wramSources[wj];
								wj++;
							}
							string label = start == end ? "0x" + start.ToString("X6") : "0x" + start.ToString("X6") + "-0x" + end.ToString("X6");
							srcList.Add((JsonNode)JsonValue.Create(label));
							wi = wj;
						}
						palettes.Add((JsonNode)new JsonObject() {
							["index"] = p,
							["type"] = pType,
							["slot"] = pSlot,
							["cgram"] = "0x" + (p * colorsPerPalette * 2).ToString("X4"),
							["sources"] = srcList,
							["sourceCount"] = srcList.Count,
							["hasMultipleSources"] = srcList.Count > 1,
							["animated"] = HasAnimatedColor(animated, p, colorsPerPalette),
							["colors"] = colors
						});
					}
					int animCount = 0;
					for(int i = 0; i < animated.Length; i++) {
						if(animated[i]) {
							animCount++;
						}
					}
					return new JsonObject() {
						["colorCount"] = colorCount,
						["colorsPerPalette"] = colorsPerPalette,
						["paletteCount"] = paletteCount,
						["bgPaletteCount"] = bgPalettes,
						["spritePaletteCount"] = paletteCount - bgPalettes,
						["spritePaletteOffset"] = spriteOffset,
						["animatedColorCount"] = animCount,
						["palettes"] = palettes
					};
				} catch {
					return null;
				}
			});
		}

		public static byte[]? GetTilemapPng(string cpuType, string layer, string bg)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			int layerIndex;
			switch(layer.Trim().ToLowerInvariant()) {
				case "main": layerIndex = 4; break;
				case "sub": layerIndex = 5; break;
				default:
					if(!int.TryParse(layer, out layerIndex) || layerIndex < 0 || layerIndex > 3) {
						return null;
					}
					break;
			}

			TilemapBackground background = ParseEnum(QueryString(bg, "Black"), TilemapBackground.Black);

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetTilemapOptions options = new GetTilemapOptions() {
						Layer = (byte)layerIndex,
						Background = background
					};

					FrameInfo size = DebugApi.GetTilemapSize(cpu.Value, options, data.PpuState);
					if(size.Width == 0 || size.Height == 0) {
						return null;
					}

					int byteCount = (int)(size.Width * size.Height * 4);
					IntPtr buffer = Marshal.AllocHGlobal(byteCount);
					try {
						DebugApi.GetTilemap(cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.Palette, buffer);
						byte[] outBytes = new byte[byteCount];
						Marshal.Copy(buffer, outBytes, 0, byteCount);
						return EncodePngBytes((int)size.Width, (int)size.Height, outBytes);
					} finally {
						Marshal.FreeHGlobal(buffer);
					}
				} catch {
					return null;
				}
			});
		}

		public static byte[]? GetOverlayPng(string cpuType, string layer, string bg)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			int layerIndex;
			switch(layer.Trim().ToLowerInvariant()) {
				case "main": layerIndex = 4; break;
				case "sub": layerIndex = 5; break;
				default:
					if(!int.TryParse(layer, out layerIndex) || layerIndex < 0 || layerIndex > 3) {
						return null;
					}
					break;
			}

			TilemapBackground background = ParseEnum(QueryString(bg, "Black"), TilemapBackground.Black);

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetTilemapOptions options = new GetTilemapOptions() {
						Layer = (byte)layerIndex,
						Background = background
					};

					FrameInfo size = DebugApi.GetTilemapSize(cpu.Value, options, data.PpuState);
					if(size.Width == 0 || size.Height == 0) {
						return null;
					}

					int byteCount = (int)(size.Width * size.Height * 4);
					IntPtr buffer = Marshal.AllocHGlobal(byteCount);
					try {
						DebugTilemapInfo tilemapInfo = DebugApi.GetTilemap(cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.Palette, buffer);
						byte[] outBytes = new byte[byteCount];
						Marshal.Copy(buffer, outBytes, 0, byteCount);
						byte[] overlay = DrawScreenOverlay(outBytes, (int)size.Width, (int)size.Height, data.PpuState, (SnesPpuToolsState)data.PpuToolsState, tilemapInfo, layerIndex);
						return EncodePngBytes((int)size.Width, (int)size.Height, overlay);
					} finally {
						Marshal.FreeHGlobal(buffer);
					}
				} catch {
					return null;
				}
			});
		}

		public static JsonNode? GetOverlayMode7Json(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesPpuState state = DebugApi.GetPpuState<SnesPpuState>(cpu.Value);
					SnesPpuToolsState tools = DebugApi.GetPpuToolsState<SnesPpuToolsState>(cpu.Value);
					(float X, float Y)[] corners = GetMode7ScreenCorners(tools);

					GetTilemapOptions options = new GetTilemapOptions() {
						Layer = 0,
						Background = TilemapBackground.Black
					};
					FrameInfo tilemapSize = DebugApi.GetTilemapSize(cpu.Value, options, state);

					return new JsonObject() {
						["bgMode"] = state.BgMode,
						["tilemapSize"] = new JsonObject() { ["w"] = tilemapSize.Width, ["h"] = tilemapSize.Height },
						["screenSize"] = new JsonObject() { ["w"] = 256, ["h"] = 224 },
						["corners"] = new JsonArray(
							new JsonObject() { ["x"] = corners[0].X, ["y"] = corners[0].Y },
							new JsonObject() { ["x"] = corners[1].X, ["y"] = corners[1].Y },
							new JsonObject() { ["x"] = corners[2].X, ["y"] = corners[2].Y },
							new JsonObject() { ["x"] = corners[3].X, ["y"] = corners[3].Y }),
						["hScroll"] = state.Mode7.HScroll,
						["vScroll"] = state.Mode7.VScroll,
						["centerX"] = state.Mode7.CenterX,
						["centerY"] = state.Mode7.CenterY,
						["largeMap"] = state.Mode7.LargeMap,
						["horizontalMirroring"] = state.Mode7.HorizontalMirroring,
						["verticalMirroring"] = state.Mode7.VerticalMirroring,
						["matrix"] = new JsonArray(
							JsonValue.Create(state.Mode7.Matrix[0]),
							JsonValue.Create(state.Mode7.Matrix[1]),
							JsonValue.Create(state.Mode7.Matrix[2]),
							JsonValue.Create(state.Mode7.Matrix[3]))
					};
				} catch {
					return null;
				}
			});
		}

		private static (float X, float Y)[] GetMode7ScreenCorners(SnesPpuToolsState tools)
		{
			//The core records the fixed-point map coordinate at the left and right edge of every
			//rendered scanline (Mode7Start/End). Derive the 4 screen corners from the first and
			//last scanline that actually rendered mode7 data.
			int first = 0;
			while(first < 239 && tools.Mode7StartX[first] == 0 && tools.Mode7EndX[first] == 0) {
				first++;
			}
			int last = 238;
			while(last > first && tools.Mode7StartX[last] == 0 && tools.Mode7EndX[last] == 0) {
				last--;
			}
			if(first >= last) {
				first = 0;
				last = 238;
			}

			return new[] {
				((float)(tools.Mode7StartX[first] >> 8), (float)(tools.Mode7StartY[first] >> 8)),
				((float)(tools.Mode7EndX[first] >> 8), (float)(tools.Mode7EndY[first] >> 8)),
				((float)(tools.Mode7EndX[last] >> 8), (float)(tools.Mode7EndY[last] >> 8)),
				((float)(tools.Mode7StartX[last] >> 8), (float)(tools.Mode7StartY[last] >> 8))
			};
		}

		private static byte[] DrawScreenOverlay(byte[] bgra, int width, int height, SnesPpuState state, SnesPpuToolsState tools, DebugTilemapInfo tilemapInfo, int layerIndex)
		{
			//Draws the same screen viewport rect as the built-in tilemap viewer
			//("BG Scroll Position"): a rectangle at (ScrollX, ScrollY) with the
			//screen dimensions, taken directly from the core's DebugTilemapInfo.
			//For mode7, ScrollX/ScrollY are the mode7 hScroll/vScroll registers.
			if(layerIndex >= 4) {
				return bgra;
			}

			float sx = tilemapInfo.ScrollX % (uint)width;
			float sy = tilemapInfo.ScrollY % (uint)height;
			float sw = tilemapInfo.ScrollWidth;
			float sh = tilemapInfo.ScrollHeight;

			List<(float X1, float Y1, float X2, float Y2)> segments = new() {
				(sx, sy, sx + sw, sy),
				(sx + sw, sy, sx + sw, sy + sh),
				(sx + sw, sy + sh, sx, sy + sh),
				(sx, sy + sh, sx, sy)
			};

			using SKBitmap bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
			Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
			using SKCanvas canvas = new SKCanvas(bitmap);
			using SKPaint paint = new SKPaint() {
				Color = SKColors.Red,
				Style = SKPaintStyle.Stroke,
				StrokeWidth = 2,
				IsAntialias = false
			};
			foreach((float X1, float Y1, float X2, float Y2) seg in segments) {
				canvas.DrawLine(seg.X1, seg.Y1, seg.X2, seg.Y2, paint);
			}
			canvas.Flush();

			byte[] outBytes = new byte[bgra.Length];
			Marshal.Copy(bitmap.GetPixels(), outBytes, 0, outBytes.Length);
			return outBytes;
		}

		public static byte[]? GetScreenPng(string cpuType, string layers, bool includeSprites, string bg)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			TilemapBackground background = ParseEnum(QueryString(bg, "Black"), TilemapBackground.Black);
			List<int> enabledLayers = ParseLayerList(layers);
			if(enabledLayers.Count == 0) {
				enabledLayers.AddRange(new[] { 0, 1, 2, 3 });
			}

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					SnesPpuState state = data.PpuState;

					int height = state.OverscanMode ? 239 : 224;
					int width = (state.HiResMode || state.BgMode == 5 || state.BgMode == 6) ? 512 : 256;

					UInt32[] output = new UInt32[width * height];

					UInt32 bgColor = GetTilemapBackgroundColor(background, data.Palette);
					if(bgColor != 0) {
						for(int i = 0; i < output.Length; i++) {
							output[i] = bgColor;
						}
					}

					// SNES-Renderingmodell: Main-Screen-Ebenen zuerst, dann Sprites, dann Sub-Screen-Ebenen
					// on top (Sub is usually clouds/overlays via color math). The order of the checkboxes
					// is respected, but Sub layers always end up on top of the Main layers.
					List<int> mainOrder = new List<int>();
					List<int> subOrder = new List<int>();
					foreach(int layerIndex in enabledLayers) {
						if(layerIndex < 0 || layerIndex > 3) {
							continue;
						}
						if(((state.MainScreenLayers >> layerIndex) & 1) != 0) {
							mainOrder.Add(layerIndex);
						} else if(((state.SubScreenLayers >> layerIndex) & 1) != 0) {
							subOrder.Add(layerIndex);
						} else {
							//Layer not active on any screen -> treat it as Main anyway
							mainOrder.Add(layerIndex);
						}
					}

					foreach(int layerIndex in mainOrder) {
						RenderLayerCrop(data, layerIndex, width, height, output, cpu.Value);
					}

					if(includeSprites) {
						CompositeSprites(data, width, height, output, cpu.Value);
					}

					foreach(int layerIndex in subOrder) {
						RenderLayerCrop(data, layerIndex, width, height, output, cpu.Value);
					}

					return EncodePng(width, height, output);
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// Echter gerenderter Frame (Main+Sub via Color Math + Helligkeit), direkt aus dem PPU.
		/// Uses the native "FinalScreenViewLayer" (layer 6), which is filled AFTER color math.
		/// </summary>
		public static byte[]? GetLivePng(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					byte brightness = data.PpuState.ScreenBrightness;

					const int w = 256, h = 239;
					int pixelCount = w * h;
					IntPtr buffer = Marshal.AllocHGlobal(pixelCount * 4);
					try {
						DebugApi.GetTilemap(cpu.Value, new GetTilemapOptions() {
							Layer = 6,
							Background = TilemapBackground.Transparent
						}, data.PpuState, data.PpuToolsState, data.Vram, data.Palette, buffer);

						byte[] bytes = new byte[pixelCount * 4];
						Marshal.Copy(buffer, bytes, 0, bytes.Length);
						UInt32[] pixels = new UInt32[pixelCount];
						Buffer.BlockCopy(bytes, 0, pixels, 0, bytes.Length);

						if(brightness != 15) {
							for(int i = 0; i < pixels.Length; i++) {
								int r = (int)((pixels[i] >> 16) & 0xFF) * brightness / 15;
								int g = (int)((pixels[i] >> 8) & 0xFF) * brightness / 15;
								int b = (int)(pixels[i] & 0xFF) * brightness / 15;
								pixels[i] = 0xFF000000u | ((UInt32)r << 16) | ((UInt32)g << 8) | (UInt32)b;
							}
						}

						return EncodePng(w, h, pixels);
					} finally {
						Marshal.FreeHGlobal(buffer);
					}
				} catch {
					return null;
				}
			});
		}

		private static void RenderLayerCrop(SnesGfxData data, int layerIndex, int viewportWidth, int viewportHeight, UInt32[] output, CpuType cpu)
		{
			GetTilemapOptions options = new GetTilemapOptions() {
				Layer = (byte)layerIndex,
				Background = TilemapBackground.Transparent
			};

			FrameInfo size = DebugApi.GetTilemapSize(cpu, options, data.PpuState);
			if(size.Width == 0 || size.Height == 0) {
				return;
			}

			int byteCount = (int)(size.Width * size.Height * 4);
			IntPtr buffer = Marshal.AllocHGlobal(byteCount);
			try {
				DebugTilemapInfo info = DebugApi.GetTilemap(cpu, options, data.PpuState, data.PpuToolsState, data.Vram, data.Palette, buffer);
				byte[] tilemapBytes = new byte[byteCount];
				Marshal.Copy(buffer, tilemapBytes, 0, byteCount);
				UInt32[] tilemap = new UInt32[size.Width * size.Height];
				Buffer.BlockCopy(tilemapBytes, 0, tilemap, 0, tilemapBytes.Length);

				int mapW = (int)size.Width;
				int mapH = (int)size.Height;
				int scrollX = (int)info.ScrollX;
				int scrollY = (int)info.ScrollY;

				for(int y = 0; y < viewportHeight; y++) {
					int srcY = (scrollY + y) % mapH;
					if(srcY < 0) {
						srcY += mapH;
					}
					for(int x = 0; x < viewportWidth; x++) {
						int srcX = (scrollX + x) % mapW;
						if(srcX < 0) {
							srcX += mapW;
						}
						UInt32 color = tilemap[srcY * mapW + srcX];
						if(color != 0) {
							output[y * viewportWidth + x] = color;
						}
					}
				}
			} finally {
				Marshal.FreeHGlobal(buffer);
			}
		}

		private static void CompositeSprites(SnesGfxData data, int viewportWidth, int viewportHeight, UInt32[] output, CpuType cpu)
		{
			GetSpritePreviewOptions options = new GetSpritePreviewOptions() {
				Background = SpriteBackground.Transparent
			};

			DebugSpritePreviewInfo previewInfo = DebugApi.GetSpritePreviewInfo(cpu, options, data.PpuState, data.PpuToolsState);
			int bufferW = (int)previewInfo.Width;
			int bufferH = (int)previewInfo.Height;
			if(bufferW == 0 || bufferH == 0) {
				return;
			}

			IntPtr screenPreview = Marshal.AllocHGlobal(bufferW * bufferH * 4);
			try {
				DebugSpriteInfo[] sprites = Array.Empty<DebugSpriteInfo>();
				UInt32[] spritePreviews = Array.Empty<UInt32>();
				DebugApi.GetSpriteList(ref sprites, ref spritePreviews, cpu, options, data.PpuState, data.PpuToolsState, data.Vram, data.SpriteRam, data.Palette, screenPreview);

				byte[] previewBytes = new byte[bufferW * bufferH * 4];
				Marshal.Copy(screenPreview, previewBytes, 0, previewBytes.Length);
				UInt32[] preview = new UInt32[bufferW * bufferH];
				Buffer.BlockCopy(previewBytes, 0, preview, 0, previewBytes.Length);

				int offsetX = (int)previewInfo.VisibleX;
				int visibleWidth = (int)previewInfo.VisibleWidth;
				int visibleHeight = (int)previewInfo.VisibleHeight;
				if(visibleHeight == 0) {
					visibleHeight = bufferH;
				}

				for(int y = 0; y < Math.Min(visibleHeight, viewportHeight); y++) {
					for(int x = 0; x < Math.Min(visibleWidth, viewportWidth); x++) {
						int sx = offsetX + x;
						if(sx >= bufferW) {
							continue;
						}
						UInt32 color = preview[y * bufferW + sx];
						if(color != 0) {
							output[y * viewportWidth + x] = color;
						}
					}
				}
			} finally {
				Marshal.FreeHGlobal(screenPreview);
			}
		}

		public static byte[]? GetSpritesPng(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetSpritePreviewOptions options = new GetSpritePreviewOptions() {
						Background = SpriteBackground.Transparent
					};

					DebugSpritePreviewInfo previewInfo = DebugApi.GetSpritePreviewInfo(cpu.Value, options, data.PpuState, data.PpuToolsState);
					int bufferW = (int)previewInfo.Width;
					int bufferH = (int)previewInfo.Height;
					if(bufferW == 0 || bufferH == 0) {
						return null;
					}

					IntPtr screenPreview = Marshal.AllocHGlobal(bufferW * bufferH * 4);
					try {
						DebugSpriteInfo[] sprites = Array.Empty<DebugSpriteInfo>();
						UInt32[] spritePreviews = Array.Empty<UInt32>();
						DebugApi.GetSpriteList(ref sprites, ref spritePreviews, cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.SpriteRam, data.Palette, screenPreview);

						int offsetX = (int)previewInfo.VisibleX;
						int visibleWidth = (int)previewInfo.VisibleWidth;
						int visibleHeight = (int)previewInfo.VisibleHeight;
						if(visibleHeight == 0) {
							visibleHeight = bufferH;
						}

						byte[] previewBytes = new byte[bufferW * bufferH * 4];
						Marshal.Copy(screenPreview, previewBytes, 0, previewBytes.Length);
						UInt32[] preview = new UInt32[bufferW * bufferH];
						Buffer.BlockCopy(previewBytes, 0, preview, 0, previewBytes.Length);

						UInt32[] cropped = new UInt32[visibleWidth * visibleHeight];
						for(int y = 0; y < visibleHeight; y++) {
							for(int x = 0; x < visibleWidth; x++) {
								int sx = offsetX + x;
								cropped[y * visibleWidth + x] = sx < bufferW ? preview[y * bufferW + sx] : 0;
							}
						}
						return EncodePng(visibleWidth, visibleHeight, cropped);
					} finally {
						Marshal.FreeHGlobal(screenPreview);
					}
				} catch {
					return null;
				}
			});
		}

		public static JsonNode? GetSpritesJson(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetSpritePreviewOptions options = new GetSpritePreviewOptions() {
						Background = SpriteBackground.Transparent
					};

					DebugSpritePreviewInfo previewInfo = DebugApi.GetSpritePreviewInfo(cpu.Value, options, data.PpuState, data.PpuToolsState);

					IntPtr screenPreview = Marshal.AllocHGlobal((int)(previewInfo.Width * previewInfo.Height) * 4);
					try {
						DebugSpriteInfo[] sprites = Array.Empty<DebugSpriteInfo>();
						UInt32[] spritePreviews = Array.Empty<UInt32>();
						DebugApi.GetSpriteList(ref sprites, ref spritePreviews, cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.SpriteRam, data.Palette, screenPreview);

						JsonArray spriteArray = new JsonArray();
						for(int i = 0; i < sprites.Length; i++) {
							DebugSpriteInfo s = sprites[i];
							spriteArray.Add((JsonNode)(new JsonObject() {
								["index"] = s.SpriteIndex,
								["x"] = s.X,
								["y"] = s.Y,
								["rawX"] = s.RawX,
								["rawY"] = s.RawY,
								["width"] = s.Width,
								["height"] = s.Height,
								["tileIndex"] = s.TileIndex,
								["tileAddress"] = s.TileAddress,
								["palette"] = s.Palette,
								["paletteAddress"] = s.PaletteAddress,
								["priority"] = s.Priority.ToString(),
								["visibility"] = s.Visibility.ToString(),
								["horizontalMirror"] = s.HorizontalMirror.ToString(),
								["verticalMirror"] = s.VerticalMirror.ToString(),
								["useSecondTable"] = s.UseSecondTable.ToString(),
								["tileCount"] = s.TileCount
							}));
						}

						return new JsonObject() {
							["cpu"] = cpu.Value.ToString(),
							["count"] = sprites.Length,
							["width"] = previewInfo.VisibleWidth,
							["height"] = previewInfo.VisibleHeight,
							["sprites"] = spriteArray
						};
					} finally {
						Marshal.FreeHGlobal(screenPreview);
					}
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R2.4: Decoded sprite list (position, size, tile, palette, priority, flip, visibility).
		/// </summary>
		public static JsonNode? GetSpritesDecoded(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetSpritePreviewOptions options = new GetSpritePreviewOptions() {
						Background = SpriteBackground.Transparent
					};

					DebugSpritePreviewInfo previewInfo = DebugApi.GetSpritePreviewInfo(cpu.Value, options, data.PpuState, data.PpuToolsState);

					IntPtr screenPreview = Marshal.AllocHGlobal((int)(previewInfo.Width * previewInfo.Height) * 4);
					try {
						DebugSpriteInfo[] sprites = Array.Empty<DebugSpriteInfo>();
						UInt32[] spritePreviews = Array.Empty<UInt32>();
						DebugApi.GetSpriteList(ref sprites, ref spritePreviews, cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.SpriteRam, data.Palette, screenPreview);

						JsonArray spriteArray = new JsonArray();
						for(int i = 0; i < sprites.Length; i++) {
							DebugSpriteInfo s = sprites[i];
							bool visible = s.Visibility == SpriteVisibility.Visible;
							spriteArray.Add((JsonNode)(new JsonObject() {
								["index"] = s.SpriteIndex,
								["x"] = s.X,
								["y"] = s.Y,
								["rawX"] = s.RawX,
								["rawY"] = s.RawY,
								["width"] = s.Width,
								["height"] = s.Height,
								["tileIndex"] = s.TileIndex,
								["tileCount"] = s.TileCount,
								["palette"] = s.Palette,
								["priority"] = s.Priority.ToString(),
								["hflip"] = s.HorizontalMirror == NullableBoolean.True,
								["vflip"] = s.VerticalMirror == NullableBoolean.True,
								["visible"] = visible,
								["visibility"] = s.Visibility.ToString()
							}));
						}

						return new JsonObject() {
							["cpu"] = cpu.Value.ToString(),
							["count"] = sprites.Length,
							["visibleCount"] = spriteArray.Count(x => x != null && x["visible"]?.GetValue<bool>() == true),
							["width"] = previewInfo.VisibleWidth,
							["height"] = previewInfo.VisibleHeight,
							["sprites"] = spriteArray
						};
					} finally {
						Marshal.FreeHGlobal(screenPreview);
					}
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: SOURCES of the sprites on screen - for each sprite, the ROM offset that
		/// filled its tile (via TileAddress -> VramRomWord) and its palette (via the WRAM
		/// palette buffer chain). Same reverse-search method as palettes/tiles: we know where
		/// the sprite data sits in VRAM/CGRAM, so we look up which ROM bytes produced it.
		/// </summary>
		public static JsonNode? GetSpriteSourcesJson(string cpuType)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					SnesGfxData data = PrepareData(cpu.Value);
					GetSpritePreviewOptions options = new GetSpritePreviewOptions() { Background = SpriteBackground.Transparent };
					DebugSpritePreviewInfo previewInfo = DebugApi.GetSpritePreviewInfo(cpu.Value, options, data.PpuState, data.PpuToolsState);
					IntPtr screenPreview = Marshal.AllocHGlobal((int)(previewInfo.Width * previewInfo.Height) * 4);
					try {
						DebugSpriteInfo[] sprites = Array.Empty<DebugSpriteInfo>();
						UInt32[] spritePreviews = Array.Empty<UInt32>();
						DebugApi.GetSpriteList(ref sprites, ref spritePreviews, cpu.Value, options, data.PpuState, data.PpuToolsState, data.Vram, data.SpriteRam, data.Palette, screenPreview);

						JsonArray arr = new JsonArray();
						for(int i = 0; i < sprites.Length; i++) {
							DebugSpriteInfo s = sprites[i];
							//sprite tile VRAM source (bytes). TileAddress is a VRAM byte address.
							UInt32 vramWord = (UInt32)(s.TileAddress / 2);
							UInt32 tileRom = vramWord < 0x8000 ? DebugApi.SnesMapLoadVramRomWord(vramWord) : 0xFFFFFFFF;
							string tileSrc = tileRom != 0xFFFFFFFF && tileRom != 0 ? "0x" + tileRom.ToString("X6") : "-";
							//sprite palette source - sprites use the 8 sprite palettes at
							//CGRAM words 0x80-0xFF (byte addr $0100-$01FF), palette index 0-7.
							//Use the CgramRomWord reverse-lookup (same as /api/gfx/palettes).
							UInt32 spritePalWord = (UInt32)(0x80 + (s.Palette & 7) * 16);
							UInt32 palRom = DebugApi.SnesMapLoadCgramRomWord(spritePalWord);
							string palSrc = palRom != 0xFFFFFFFF && palRom != 0 ? "0x" + palRom.ToString("X6") : "-";
							arr.Add((JsonNode)new JsonObject() {
								["index"] = s.SpriteIndex,
								["x"] = s.X,
								["y"] = s.Y,
								["width"] = s.Width,
								["height"] = s.Height,
								["tileIndex"] = s.TileIndex,
								["tileVram"] = "0x" + s.TileAddress.ToString("X4"),
								["tileSource"] = tileSrc,
								["palette"] = s.Palette,
								["paletteSource"] = palSrc
							});
						}
						return new JsonObject() {
							["cpu"] = cpu.Value.ToString(),
							["count"] = sprites.Length,
							["sprites"] = arr
						};
					} finally {
						Marshal.FreeHGlobal(screenPreview);
					}
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: SOURCES of the tilemap/map - the VRAM tilemap area (per active layer) is
		/// reverse-looked-up: which ROM offsets filled the tilemap word area. This reveals
		/// where the current map data in VRAM comes from in the ROM file.
		/// </summary>
		public static JsonNode? GetMapSourcesJson(string cpuType, string layer)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					//R3.2: enable live tracking so VramRomWord stays current (the map sources
					//are read from it; without live tracking the table may be stale/empty).
					if(!_liveTrackingInit) {
						_liveTrackingInit = true;
						DebugApi.SnesMapLoadLogSetEnabled(true);
						DebugApi.SnesMapLoadLogSetAutoCapture(true);
						DebugApi.SnesMapLoadLogSetLiveTracking(true);
					}
					SnesPpuState state = DebugApi.GetPpuState<SnesPpuState>(cpu.Value);
					int layerIdx = int.TryParse(layer, out int li) ? li : 0;
					if(layerIdx < 0 || layerIdx > 3) {
						layerIdx = 0;
					}
					LayerConfig lc = state.Layers[layerIdx];
					UInt32 baseWord = lc.TilemapAddress;
					UInt32 mapWords = lc.LargeTiles ? 0x1000u : 0x400u;
					//Mode-7 world map is a 128x128 tile grid (0x4000 bytes = 0x2000 words for
					//16-bit entries, or 1024x1024 = 0x1000 words for large maps). The DMA that
					//fills it may start a bit after TilemapAddress - scan a wider window so we
					//catch the real writes (observed at 0x6C00-0x6FE0 for a 0x6800 base).
					if(state.BgMode == 7 || state.Mode7.LargeMap) {
						mapWords = state.Mode7.LargeMap ? 0x2000u : 0x1000u;
					}
					UInt32 chrWord = lc.ChrAddress;
					string tilemapLabel = "0x" + baseWord.ToString("X4");
					string chrLabel = "0x" + chrWord.ToString("X4");
					//Mode-7 detection: BgMode==7, OR a large mode-7 map config is active
					//(Terranigma uses the mode-7 style world map with large tiles).
					bool isMode7 = state.BgMode == 7 || state.Mode7.LargeMap;

					//R3.2: resolve the tilemap source. PRIMARY: the VramRomWord reverse-lookup
					//(decompressed VRAM words map back to their ROM source - this reflects the
					//ACTUAL tilemap currently rendered, so it is always map-specific and works
					//for normal BG and mode-7 maps alike; verified: world map -> 0x01F6xx,
					//Crysta -> 0x1FC3xx). FALLBACK: the ROM-read ring's largest blocks (the
					//decompression source reads) + DMA log.
					UInt32 total = DebugApi.SnesMapLoadLogGetCount();
					UInt32 n = Math.Min(total, 1u << 21);
					DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[n];
					UInt32 got = DebugApi.SnesGetMapLoadLog(entries, 0, n);

					JsonArray srcList = new JsonArray();
					{
						//vram reverse-lookup coverage (majority). For a tilemap the entries are
						//2 bytes apart in VRAM but the ROM source advances by one word per entry
						//(0x04AACA, 0x04AACC... for normal, or the map base + entry*2). We collect
						//all distinct sources and take the DOMINANT contiguous run as the map base.
						Dictionary<UInt32, int> srcCoverage = new Dictionary<UInt32, int>();
						for(UInt32 w = 0; w < mapWords && baseWord + w < 0x8000; w++) {
							UInt32 rom = DebugApi.SnesMapLoadVramRomWord(baseWord + w);
							if(IsCodeSource(rom)) {
								continue;
							}
							//if the stored value is a WRAM address (0x00000-0x1FFFF), resolve it
							//through the WramRomByte chain to the real ROM source (mode-7 tilemap
							//is decompressed via WRAM).
							if(rom < 0x20000) {
								UInt32 rom2 = DebugApi.SnesGetWramRomSource(rom);
								if(!IsCodeSource(rom2)) {
									rom = rom2;
								}
							}
							srcCoverage.TryGetValue(rom, out int c);
							srcCoverage[rom] = c + 1;
						}
						//also count DMA-log entries into the tilemap range
						for(int i = 0; i < (int)got; i++) {
							DebugApi.InteropMapLoadEntry e = entries[i];
							if(e.targetType != 0 || e.targetAddr < baseWord || e.targetAddr >= baseWord + mapWords) {
								continue;
							}
							UInt32 rom = 0xFFFFFFFF;
							if(e.sourceType == 0 && e.sourceMem == 0) {
								rom = e.sourceAddr;
							} else if(e.sourceType == 1 && e.sourceMem == (byte)MemoryType.SnesPrgRom) {
								rom = e.sourceAddr;
							}
							if(IsCodeSource(rom)) {
								continue;
							}
							srcCoverage.TryGetValue(rom, out int c);
							srcCoverage[rom] = c + 1;
						}
						//dominant source by coverage - the real tilemap base. Report it plus its
						//contiguous extent (entries 2 bytes apart = adjacent ROM words).
						var top = srcCoverage.OrderByDescending(kv => kv.Value).FirstOrDefault();
						if(top.Key != 0) {
							UInt32 baseRom = top.Key;
							//find the contiguous ROM run through consecutive entries
							UInt32 runStart = baseRom, runEnd = baseRom;
							bool grow = true;
							while(grow) {
								grow = false;
								if(srcCoverage.ContainsKey(runStart - 2)) { runStart -= 2; grow = true; }
								if(srcCoverage.ContainsKey(runEnd + 2)) { runEnd += 2; grow = true; }
							}
							//also grow by the +0x20 mode-7 map stride (VRAM word 0x10 -> ROM +0x20)
							bool grow2 = true;
							while(grow2) {
								grow2 = false;
								if(srcCoverage.ContainsKey(runStart - 0x20)) { runStart -= 0x20; grow2 = true; }
								if(srcCoverage.ContainsKey(runEnd + 0x20)) { runEnd += 0x20; grow2 = true; }
							}
							srcList.Add((JsonNode)JsonValue.Create(runStart == runEnd ? "0x" + runStart.ToString("X6") : "0x" + runStart.ToString("X6") + "-0x" + runEnd.ToString("X6")));
						}
					}
					if(srcList.Count == 0) {
						//ring-based fallback: largest contiguous ROM-read blocks from the recent
						//frame window (the decompression source reads). Require a decent size
						//(>= 512 B) so per-palette/fade reads don't pollute the map source.
						const int maxR = 16;
						UInt32[] rStarts = new UInt32[maxR];
						UInt32[] rLens = new UInt32[maxR];
						UInt32 rGot = DebugApi.SnesMapLoadRomReadRingLargest(rStarts, rLens, maxR, 0xFFFFFFFF, 1800);
						for(int i = 0; i < (int)rGot; i++) {
							if(rLens[i] < 512 || rStarts[i] >= 0x400000) {
								continue;
							}
							srcList.Add((JsonNode)JsonValue.Create("0x" + rStarts[i].ToString("X6") + "-0x" + (rStarts[i] + rLens[i] - 1).ToString("X6")));
							if(srcList.Count >= 4) break;
						}
					}
					//Mode-7 tileset source: the tileset is 8bpp (64 bytes/tile); report the
					//ROM sources of the tileset area too. PRIMARY: the ring's largest blocks
					//(the compressed package may hold tiles+map together). FALLBACK: a
					//VramRomWord majority + DMA log.
					JsonArray tilesetSrcList = new JsonArray();
					if(isMode7) {
						const int maxR2 = 8;
						UInt32[] rStarts2 = new UInt32[maxR2];
						UInt32[] rLens2 = new UInt32[maxR2];
						UInt32 rGot2 = DebugApi.SnesMapLoadRomReadRingLargest(rStarts2, rLens2, maxR2, 0xFFFFFFFF, 1800);
						for(int i = 0; i < (int)rGot2; i++) {
							if(rLens2[i] < 512 || rStarts2[i] >= 0x400000) {
								continue;
							}
							tilesetSrcList.Add((JsonNode)JsonValue.Create("0x" + rStarts2[i].ToString("X6") + "-0x" + (rStarts2[i] + rLens2[i] - 1).ToString("X6")));
							if(tilesetSrcList.Count >= 4) break;
						}
					}
					if(isMode7 && tilesetSrcList.Count == 0) {
						Dictionary<UInt32, int> tsCoverage = new Dictionary<UInt32, int>();
						for(UInt32 w = 0; w < 0x8000 && chrWord + w < 0x8000; w++) {
							UInt32 rom = DebugApi.SnesMapLoadVramRomWord(chrWord + w);
							if(IsCodeSource(rom)) {
								continue;
							}
							tsCoverage.TryGetValue(rom, out int c);
							tsCoverage[rom] = c + 1;
						}
						for(int i = 0; i < (int)got; i++) {
							DebugApi.InteropMapLoadEntry e = entries[i];
							if(e.targetType != 0) {
								continue;
							}
							if(e.targetAddr < chrWord || e.targetAddr >= chrWord + 0x8000) {
								continue;
							}
							UInt32 rom = (e.sourceType == 0 && e.sourceMem == 0) || (e.sourceType == 1 && e.sourceMem == (byte)MemoryType.SnesPrgRom) ? e.sourceAddr : 0xFFFFFFFF;
							if(IsCodeSource(rom)) {
								continue;
							}
							tsCoverage.TryGetValue(rom, out int c);
							tsCoverage[rom] = c + 1;
						}
						var tsSorted = tsCoverage.OrderByDescending(kv => kv.Value).Take(40).Select(kv => kv.Key).Distinct().OrderBy(x => x).ToList();
						int tsi = 0;
						while(tsi < tsSorted.Count) {
							UInt32 s = tsSorted[tsi];
							UInt32 e = s;
							int tsj = tsi + 1;
							while(tsj < tsSorted.Count && tsSorted[tsj] == e + 2) {
								e = tsSorted[tsj];
								tsj++;
							}
							tilesetSrcList.Add((JsonNode)JsonValue.Create(s == e ? "0x" + s.ToString("X6") : "0x" + s.ToString("X6") + "-0x" + e.ToString("X6")));
							tsi = tsj;
						}
					}
					return new JsonObject() {
						["layer"] = layerIdx,
						["name"] = "BG" + (layerIdx + 1),
						["mode7"] = isMode7,
						["tilemapAddress"] = tilemapLabel,
						["mapWords"] = mapWords,
						["largeTiles"] = lc.LargeTiles,
						["chrAddress"] = chrLabel,
						["sourceCount"] = srcList.Count,
						["sources"] = srcList,
						["tilesetSourceCount"] = tilesetSrcList.Count,
						["tilesetSources"] = tilesetSrcList
					};
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: DIAGNOSTICS - show what the emulator actually recorded for the tilemap area
		/// of a layer: the raw DMA/CPU log entries targeting that VRAM range (with their ROM
		/// sources), the VramRomWord table, and the WRAM->ROM chain. This reveals the true
		/// data flow (e.g. mode-7 map loaded via DMA, CPU copy, or through WRAM) so the
		/// reverse-search can be corrected instead of guessed.
		/// </summary>
		public static JsonNode? GetMapDiagJson(string cpuType, string layer)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					if(!_liveTrackingInit) {
						_liveTrackingInit = true;
						DebugApi.SnesMapLoadLogSetEnabled(true);
						DebugApi.SnesMapLoadLogSetAutoCapture(true);
						DebugApi.SnesMapLoadLogSetLiveTracking(true);
					}
					SnesPpuState state = DebugApi.GetPpuState<SnesPpuState>(cpu.Value);
					int layerIdx = int.TryParse(layer, out int li) ? li : 0;
					if(layerIdx < 0 || layerIdx > 3) layerIdx = 0;
					LayerConfig lc = state.Layers[layerIdx];
					UInt32 baseWord = lc.TilemapAddress;
					UInt32 mapWords = lc.LargeTiles ? 0x1000u : 0x400u;
					bool isMode7 = state.BgMode == 7 || state.Mode7.LargeMap;

					//raw log entries targeting the tilemap range
					UInt32 total = DebugApi.SnesMapLoadLogGetCount();
					UInt32 n = Math.Min(total, 1u << 21);
					DebugApi.InteropMapLoadEntry[] entries = new DebugApi.InteropMapLoadEntry[n];
					UInt32 got = DebugApi.SnesGetMapLoadLog(entries, 0, n);
					JsonArray logArr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.InteropMapLoadEntry e = entries[i];
						if(e.targetType != 0 || e.targetAddr < baseWord || e.targetAddr >= baseWord + mapWords) {
							continue;
						}
						string src = e.sourceMem == 0 ? "ROM 0x" + e.sourceAddr.ToString("X6") : "WRAM 0x" + (e.sourceAddr & 0x1FFFF).ToString("X5");
						logArr.Add((JsonNode)new JsonObject() {
							["frame"] = e.frame,
							["via"] = e.sourceType == 0 ? "dma ch" + e.channel : "cpu",
							["src"] = src,
							["rawBus"] = "0x" + e.pc.ToString("X6"),
							["taddr"] = e.targetAddr,
							["len"] = e.length
						});
					}
					//VramRomWord sample over the tilemap range (non-empty)
					JsonArray vramArr = new JsonArray();
					for(UInt32 w = 0; w < mapWords && baseWord + w < 0x8000; w += 0x10) {
						UInt32 rom = DebugApi.SnesMapLoadVramRomWord(baseWord + w);
						if(rom != 0xFFFFFFFF && rom != 0) {
							vramArr.Add((JsonNode)new JsonObject() {
								["vram"] = "0x" + (baseWord + w).ToString("X4"),
								["rom"] = "0x" + rom.ToString("X6")
							});
						}
					}
					//ROM reads of the last map-load burst (the compressed tilemap source
					//candidates) - the decompression reads these from the ROM. The ring
					//always runs (1M entries), so we take the LARGEST contiguous blocks.
					JsonArray ringArr = new JsonArray();
					{
						const int maxR = 64;
						UInt32[] rStarts = new UInt32[maxR];
						UInt32[] rLens = new UInt32[maxR];
						UInt32 rGot = DebugApi.SnesMapLoadRomReadRingLargest(rStarts, rLens, maxR, 0xFFFFFFFF, 1800);
						for(int i = 0; i < (int)rGot; i++) {
							ringArr.Add((JsonNode)new JsonObject() {
								["rom"] = "0x" + rStarts[i].ToString("X6") + "-0x" + (rStarts[i] + rLens[i] - 1).ToString("X6"),
								["len"] = rLens[i]
							});
						}
					}
					return new JsonObject() {
						["layer"] = layerIdx,
						["name"] = "BG" + (layerIdx + 1),
						["mode7"] = isMode7,
						["tilemapAddress"] = "0x" + baseWord.ToString("X4"),
						["mapWords"] = mapWords,
						["chrAddress"] = "0x" + lc.ChrAddress.ToString("X4"),
						["logCount"] = logArr.Count,
						["log"] = logArr,
						["vramNonEmpty"] = vramArr.Count,
						["vram"] = vramArr,
						["ringCount"] = ringArr.Count,
						["ringReads"] = ringArr,
						["dmaDebugBus"] = "0x" + DebugApi.SnesMapLoadDmaDebugBus().ToString("X6"),
						["dmaDebugLinear"] = "0x" + DebugApi.SnesMapLoadDmaDebugLinear().ToString("X6"),
						["dmaDebugIsRom"] = DebugApi.SnesMapLoadDmaDebugIsRom(),
						["dmaDebugIsWram"] = DebugApi.SnesMapLoadDmaDebugIsWram()
					};
				} catch {
					return null;
				}
			});
		}

		public static byte[]? GetTilesPng(string cpuType, string format, string memType, int columns, int rows, int paletteIndex, string startAddress, string bg)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}

			TileFormat tileFormat = ParseEnum(QueryString(format, "Bpp4"), TileFormat.Bpp4);
			MemoryType source = Enum.TryParse<MemoryType>(QueryString(memType, "SnesVideoRam"), true, out MemoryType parsedType) ? parsedType : MemoryType.SnesVideoRam;
			TileBackground background = ParseEnum(QueryString(bg, "Black"), TileBackground.Black);

			int colCount = Math.Max(columns, 1);
			int rowCount = Math.Max(rows, 1);
			UInt32 startAddr = ParseUInt(QueryString(startAddress, "0"));

			return RunExclusive(() => {
				try {
					byte[] sourceData = DebugApi.GetMemoryState(source);
					if(sourceData == null || sourceData.Length == 0) {
						return null;
					}

					DebugPaletteInfo paletteInfo = DebugApi.GetPaletteInfo(cpu.Value);
					UInt32[] palette = paletteInfo.GetRgbPalette();

					int width = colCount * 8;
					int height = rowCount * 8;
					int byteCount = width * height * 4;
					IntPtr buffer = Marshal.AllocHGlobal(byteCount);
					try {
						DebugApi.GetTileView(cpu.Value, new GetTileViewOptions() {
							MemType = source,
							Format = tileFormat,
							Layout = TileLayout.Normal,
							Filter = TileFilter.None,
							Background = background,
							Width = colCount,
							Height = rowCount,
							StartAddress = (Int32)startAddr,
							Palette = paletteIndex,
							UseGrayscalePalette = false
						}, sourceData, sourceData.Length, palette, buffer);

						byte[] outBytes = new byte[byteCount];
						Marshal.Copy(buffer, outBytes, 0, byteCount);
						return EncodePngBytes(width, height, outBytes);
					} finally {
						Marshal.FreeHGlobal(buffer);
					}
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: SOURCES of the tile set shown in the tile viewer - for each 8x8 tile in the
		/// current viewer range (start address + cols*rows), report the ROM file offset that
		/// filled that VRAM area (reverse-lookup through the VramRomWord chain). Same method
		/// as the palette viewer: we know where the tiles sit in VRAM, so we look up which
		/// ROM bytes produced them - no byte-matching, works for compressed tiles too.
		/// </summary>
		public static JsonNode? GetTileSourcesJson(string cpuType, string memType, int columns, int rows, string startAddress, string format)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					int colCount = Math.Max(columns, 1);
					int rowCount = Math.Max(rows, 1);
					UInt32 startAddr = ParseUInt(QueryString(startAddress, "0"));
					//bytes per 8x8 tile depends on the format: Bpp2=16, Bpp4=32, Bpp8=64
					int bytesPerTile = format switch {
						"Bpp2" => 16,
						"Bpp8" => 64,
						_ => 32
					};
					int wordsPerTile = bytesPerTile / 2;  //VRAM word address space

					//group the VRAM word range per tile and resolve each via VramRomWord
					JsonArray tiles = new JsonArray();
					UInt32 tileIndex = 0;
					for(int r = 0; r < rowCount; r++) {
						for(int c = 0; c < colCount; c++) {
							UInt32 vramWordStart = startAddr / 2 + tileIndex * (UInt32)wordsPerTile;
							HashSet<UInt32> srcSet = new HashSet<UInt32>();
							for(int w = 0; w < wordsPerTile; w++) {
								UInt32 rom = DebugApi.SnesMapLoadVramRomWord(vramWordStart + (UInt32)w);
								if(rom != 0xFFFFFFFF && rom != 0) {
									srcSet.Add(rom);
								}
							}
							List<UInt32> sorted = srcSet.OrderBy(x => x).ToList();
							JsonArray srcList = new JsonArray();
							int si = 0;
							while(si < sorted.Count) {
								UInt32 s = sorted[si];
								UInt32 e = s;
								int sj = si + 1;
								while(sj < sorted.Count && sorted[sj] == e + 2) {
									e = sorted[sj];
									sj++;
								}
								srcList.Add((JsonNode)JsonValue.Create(s == e ? "0x" + s.ToString("X6") : "0x" + s.ToString("X6") + "-0x" + e.ToString("X6")));
								si = sj;
							}
							tiles.Add((JsonNode)new JsonObject() {
								["tile"] = tileIndex,
								["vram"] = "0x" + vramWordStart.ToString("X5"),
								["sources"] = srcList,
								["sourceCount"] = srcList.Count,
								["hasMultipleSources"] = srcList.Count > 1
							});
							tileIndex++;
						}
					}
					return new JsonObject() {
						["tileCount"] = tileIndex,
						["bytesPerTile"] = bytesPerTile,
						["start"] = "0x" + startAddr.ToString("X4"),
						["tiles"] = tiles
					};
				} catch {
					return null;
				}
			});
		}

		private static List<int> ParseLayerList(string layers)
		{
			List<int> result = new List<int>();
			foreach(string part in layers.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
				if(part.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) {
					result.AddRange(new[] { 0, 1, 2, 3 });
				} else if(int.TryParse(part.Trim(), out int value) && value >= 0 && value <= 3) {
					result.Add(value);
				}
			}
			return result;
		}

		private static UInt32 GetTilemapBackgroundColor(TilemapBackground bg, UInt32[] palette)
		{
			switch(bg) {
				case TilemapBackground.Default: return palette.Length > 0 ? palette[0] : 0xFF000000;
				case TilemapBackground.Transparent: return 0;
				case TilemapBackground.Black: return 0xFF000000;
				case TilemapBackground.White: return 0xFFFFFFFF;
				case TilemapBackground.Magenta: return 0xFFFF00FF;
				default: return 0xFF000000;
			}
		}

		private static UInt32 ParseUInt(string text)
		{
			text = text.Trim();
			try {
				if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					return Convert.ToUInt32(text.Substring(2), 16);
				}
				return Convert.ToUInt32(text, 10);
			} catch {
				return 0;
			}
		}

		//R3.2: GENERIC - is this ROM offset a CODE address (per the Code-Data-Logger)?
		//Used to keep code reads out of the reverse-lookup sources without hardcoding
		//game-specific addresses.
		private static bool IsCodeSource(UInt32 rom)
		{
			if(rom == 0xFFFFFFFF || rom == 0 || rom >= 0x400000) {
				return true;  //invalid - treat as "not a usable source"
			}
			// Nur eindeutig ungueltige Adressen filtern. Der CDL (IsRomCode) ist hier NICHT
			// zuverlaessig: er markiert echte komprimierte Daten faelschlich als Code und
			// verursacht leere Palette-Listen. Die zuverlaessige Ressourcen-Quelle liefert
			// das native Script-Modul (/api/script/run, spiel-spezifisch), nicht die generische
			// Runtime-Reverse-Suche.
			return false;
		}

		private static string QueryString(string value, string defaultValue)
		{
			return string.IsNullOrEmpty(value) ? defaultValue : value;
		}

		private sealed class SnesGfxData
		{
			public SnesPpuState PpuState;
			public BaseState PpuToolsState;
			public byte[] Vram;
			public byte[] SpriteRam;
			public UInt32[] Palette;

			public SnesGfxData(SnesPpuState ppuState, BaseState ppuToolsState, byte[] vram, byte[] spriteRam, UInt32[] palette)
			{
				PpuState = ppuState;
				PpuToolsState = ppuToolsState;
				Vram = vram;
				SpriteRam = spriteRam;
				Palette = palette;
			}
		}

		/// <summary>
		/// R3.2: reverse-search via the TRANSFER capture ring. Walks BACK from a target
		/// (e.g. a VRAM tilemap word / CGRAM palette index / WRAM buffer) through the
		/// recorded memory transfers to the ROM source. Generic - works for any game.
		/// mem: 0=ROM 1=WRAM 2=SaveRAM 3=VRAM 4=CGRAM. addr: linear (VRAM word, CGRAM idx,
		/// WRAM 0x00000-0x1FFFF). Returns the chain newest-first.
		/// </summary>
		public static JsonNode? GetTraceJson(string cpuType, string mem, string addr)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					if(!_liveTrackingInit) {
						_liveTrackingInit = true;
						DebugApi.SnesMapLoadLogSetEnabled(true);
						DebugApi.SnesMapLoadLogSetAutoCapture(true);
						DebugApi.SnesMapLoadLogSetLiveTracking(true);
					}
					byte memType = (byte)(int.TryParse(mem, out int mv) ? mv : 3);
					UInt32 target = ParseUInt(addr);
					const int maxE = 32;
					DebugApi.TransferInterop[] entries = new DebugApi.TransferInterop[maxE];
					UInt32 got = DebugApi.SnesMapLoadTrace(memType, target, entries, maxE);
					DebugApi.TransferInterop peek = new DebugApi.TransferInterop();
					DebugApi.SnesMapLoadTransferPeek(out peek);
					JsonArray chain = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						DebugApi.TransferInterop e = entries[i];
						chain.Add((JsonNode)new JsonObject() {
							["step"] = i,
							["via"] = e.via == 0 ? "dma" : "cpu",
							["srcMem"] = e.srcMem,
							["src"] = "0x" + e.srcAddr.ToString("X6"),
							["dstMem"] = e.dstMem,
							["dst"] = "0x" + e.dstAddr.ToString("X6"),
							["len"] = e.len
						});
					}
					return new JsonObject() {
						["mem"] = memType,
						["addr"] = "0x" + target.ToString("X6"),
						["steps"] = got,
						["transferCount"] = DebugApi.SnesMapLoadTransferCount(),
						["peekSrc"] = "0x" + peek.srcAddr.ToString("X6"),
						["peekDst"] = "0x" + peek.dstAddr.ToString("X6"),
						["peekLen"] = peek.len,
						["peekSrcMem"] = peek.srcMem,
						["peekDstMem"] = peek.dstMem,
						["chain"] = chain
					};
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// R3.2: diagnostics - show the NEWEST recorded transfers to a destination memory
		/// type (3=VRAM, 4=CGRAM, 1=WRAM). Reveals which addresses the game is currently
		/// writing, so the reverse-search targets can be verified.
		/// </summary>
		public static JsonNode? GetVramTransfersJson(string cpuType, string mem)
		{
			CpuType? cpu = ParseCpuType(cpuType);
			if(cpu == null) {
				return null;
			}
			return RunExclusive(() => {
				try {
					byte memType = (byte)(int.TryParse(mem, out int mv) ? mv : 3);
					UInt32 rangeStart = 0;
					UInt32 rangeEnd = 0xFFFFFFFF;
					string range = QueryString("range", "");
					if(range != "" && range.Contains('-')) {
						string[] parts = range.Split('-');
						rangeStart = ParseUInt(parts[0].Trim());
						rangeEnd = ParseUInt(parts[1].Trim());
					}
					const int maxR = 64;
					UInt32[] srcs = new UInt32[maxR];
					UInt32[] dsts = new UInt32[maxR];
					byte[] vias = new byte[maxR];
					UInt32 got;
					if(range != "" && range.Contains('-')) {
						got = DebugApi.SnesMapLoadTransfersToRange(memType, rangeStart, rangeEnd, srcs, dsts, vias, maxR);
					} else {
						got = DebugApi.SnesMapLoadTransfersToMem(memType, srcs, dsts, vias, maxR);
					}
					JsonArray arr = new JsonArray();
					for(int i = 0; i < (int)got; i++) {
						arr.Add((JsonNode)new JsonObject() {
							["via"] = vias[i] == 0 ? "dma" : "cpu",
							["src"] = "0x" + srcs[i].ToString("X6"),
							["dst"] = "0x" + dsts[i].ToString("X6")
						});
					}
					return new JsonObject() {
						["mem"] = memType,
						["count"] = got,
						["transfers"] = arr
					};
				} catch {
					return null;
				}
			});
		}
	}
}
