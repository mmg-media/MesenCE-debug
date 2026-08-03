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
					// darüber (Sub = meist Wolken/Overlays via Color Math). Reihenfolge der Checkboxen
					// wird dabei respektiert, aber Sub-Ebenen landen immer über den Main-Ebenen.
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
							//Layer auf keinem Screen aktiv -> trotzdem als Main behandeln
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
		/// Nutzt den nativen "FinalScreenViewLayer" (Layer 6), der NACH der Farbverrechnung gefüllt wird.
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
		/// R2.4: Dekodierte Sprite-Liste (Position, Größe, Tile, Palette, Priority, Flip, Sichtbarkeit).
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
	}
}
