using Avalonia.Threading;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Mesen.Config
{
	/// <summary>
	/// R3.2: TRAINER-Service - wendet aktive Trainer-Cheats an. Fuer type=toggle wird
	/// der RAM-Wert per Frame-Timer dauerhaft fixiert (WriteMemory), fuer type=ar werden
	/// Action-Replay-Codes ueber den nativen CheatManager angewendet. Ram-Felder werden
	/// manuell geschrieben + live angezeigt.
	/// </summary>
	public static class TrainerService
	{
		private static DispatcherTimer? _timer;
		private static readonly object _lock = new object();

		private static bool _active = false;
		public static bool Active { get { return _active; } }

		// aktive Toggles: TrainerCheat -> zuletzt gesetzter Wert (fuer Aenderungs-Erkennung)
		private static Dictionary<string, TrainerCheat> _activeToggles = new Dictionary<string, TrainerCheat>();
		private static List<string> _appliedArCodes = new List<string>();

		public static event Action? Changed;

		public static void Start()
		{
			if(_active) {
				return;
			}
			_active = true;
			_activeToggles.Clear();
			_appliedArCodes.Clear();
			_timer = new DispatcherTimer();
			_timer.Interval = TimeSpan.FromMilliseconds(16);  // ~60fps
			_timer.Tick += (s, e) => ApplyToggles();
			_timer.Start();
			Changed?.Invoke();
		}

		public static void Stop()
		{
			if(!_active) {
				return;
			}
			_active = false;
			_timer?.Stop();
			_timer = null;
			// ROM-Patches zuruecksetzen (Original-Bytes wiederherstellen) bevor Toggles geloescht werden
			foreach(TrainerCheat cheat in _activeToggles.Values) {
				if(cheat.Type?.ToLowerInvariant() == "rompatch") {
					ApplyRomPatches(cheat, false);
				}
			}
			_activeToggles.Clear();
			ApplyArCodes(Enumerable.Empty<string>());
			Changed?.Invoke();
		}

		public static void SetToggle(string cheatId, TrainerCheat cheat, bool enabled)
		{
			lock(_lock) {
				if(enabled) {
					_activeToggles[cheatId] = cheat;
					ApplyCheatNow(cheat, true);
				} else {
					_activeToggles.Remove(cheatId);
					ApplyCheatNow(cheat, false);
				}
			}
		}

		// Ein Toggle direkt einmalig anwenden (z.B. beim Aktivieren sofort den Wert setzen)
		public static void ApplyToggleNow(TrainerCheat cheat)
		{
			ApplyCheatNow(cheat, true);
		}

		public static bool IsToggleActive(string cheatId)
		{
			lock(_lock) {
				return _activeToggles.ContainsKey(cheatId);
			}
		}

		private static void ApplyToggles()
		{
			lock(_lock) {
				foreach(TrainerCheat cheat in _activeToggles.Values) {
					ApplyCheatNow(cheat, true);
				}
			}
		}

		private static void ApplyCheatNow(TrainerCheat cheat, bool enabled)
		{
			if(cheat.Type?.ToLowerInvariant() == "rompatch") {
				ApplyRomPatches(cheat, enabled);
			} else {
				WriteCheatValue(cheat);
			}
		}

		// ROM-Patches anwenden (enabled=true) oder Original-Bytes wiederherstellen (enabled=false).
		// Adressen sind Datei-Offsets; geschrieben wird in SnesPrgRom (= ROM des Emulators).
		private static void ApplyRomPatches(TrainerCheat cheat, bool apply)
		{
			if(cheat.Patches == null) {
				return;
			}
			try {
				foreach(RomPatch patch in cheat.Patches) {
					UInt32 addr = ParseAddress(patch.Address);
					byte[]? bytes = ParseHexBytes(apply ? patch.Patch : patch.Original);
					if(bytes != null && bytes.Length > 0) {
						DebugApi.SetMemoryValues(MemoryType.SnesPrgRom, addr, bytes, bytes.Length);
					}
				}
			} catch {
			}
		}

		// Hex-Bytes parsen: "C9 A0 00" (Leerzeichen-getrennt) oder "C9A000"
		public static byte[]? ParseHexBytes(string? text)
		{
			if(string.IsNullOrWhiteSpace(text)) {
				return null;
			}
			try {
				string t = text.Trim().Replace(" ", "").Replace("\t", "").Replace("-", "");
				if(t.Length % 2 != 0) {
					return null;
				}
				byte[] result = new byte[t.Length / 2];
				for(int i = 0; i < result.Length; i++) {
					result[i] = Convert.ToByte(t.Substring(i * 2, 2), 16);
				}
				return result;
			} catch {
				return null;
			}
		}

		private static void WriteCheatValue(TrainerCheat cheat)
		{
			try {
				UInt32 addr = ParseAddress(cheat.RamAddress);
				int size = Math.Max(1, Math.Min(cheat.Size, 8));
				byte[]? value;
				if(cheat.Encoding == "bcd") {
					value = ParseBcdValue(cheat.Value, size);
				} else {
					value = ParseValue(cheat.Value, size);
				}
				if(value != null && addr < 0x20000) {
					DebugApi.SetMemoryValues(MemoryType.SnesWorkRam, addr, value, size);
				}
			} catch {
			}
		}

		// Dezimalwert als BCD-Bytes packen (LITTLE-endian, niedrigstwertige Ziffern zuerst):
		// z.B. "58" (size=1) -> 0x58, "1234" (size=2) -> 0x34 0x12, "99" -> 0x99
		public static byte[]? ParseBcdValue(string? text, int size)
		{
			if(string.IsNullOrWhiteSpace(text)) {
				return null;
			}
			try {
				string t = text.Trim();
				if(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					t = t.Substring(2);
				}
				// BCD-Eingabe wird als Dezimalzahl interpretiert (max 10^size Stellen)
				ulong value = Convert.ToUInt64(t, 10);
				byte[] result = new byte[size];
				for(int i = 0; i < size; i++) {
					byte low = (byte)(value % 10);
					value /= 10;
					byte high = (byte)(value % 10);
					value /= 10;
					result[i] = (byte)((high << 4) | low);
				}
				return result;
			} catch {
				return null;
			}
		}

		// AR-Codes anwenden (die aktiven Toggles + Ram-Felder bleiben, nur AR-Codes werden gesetzt)
		private static void ApplyArCodes(IEnumerable<string> codes)
		{
			_appliedArCodes = codes.ToList();
			List<InteropCheatCode> encoded = new List<InteropCheatCode>();
			foreach(string code in _appliedArCodes) {
				encoded.Add(new InteropCheatCode(CheatType.SnesProActionReplay, code));
			}
			EmuApi.SetCheats(encoded.ToArray(), (UInt32)encoded.Count);
		}

		public static UInt32 ParseAddress(string? hexOrDec)
		{
			if(string.IsNullOrWhiteSpace(hexOrDec)) {
				return 0;
			}
			hexOrDec = hexOrDec.Trim();
			try {
				if(hexOrDec.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					return Convert.ToUInt32(hexOrDec.Substring(2), 16);
				}
				return Convert.ToUInt32(hexOrDec, 16);
			} catch {
				return 0;
			}
		}

		public static byte[]? ParseValue(string? text, int size)
		{
			if(string.IsNullOrWhiteSpace(text)) {
				return null;
			}
			try {
				string t = text.Trim();
				if(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					t = t.Substring(2);
				}
				ulong v = Convert.ToUInt64(t, 16);
				byte[] result = new byte[size];
				for(int i = 0; i < size; i++) {
					result[i] = (byte)((v >> (8 * i)) & 0xFF);
				}
				return result;
			} catch {
				return null;
			}
		}
	}
}
