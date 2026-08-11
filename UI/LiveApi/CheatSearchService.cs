using Mesen.Config;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mesen.LiveApi
{
	/// <summary>
	/// R3.3: CHEAT-SEARCH - Cheat-Engine-artige RAM-Suche ueber die Live-API.
	/// Kandidaten-Liste + bis zu 10 persistente Snapshots (HomeFolder/cheatsearch/snap_N.bin),
	/// damit man nach einem Fehler nicht von vorne starten muss.
	/// </summary>
	public static class CheatSearchService
	{
		private const int MaxSnapshots = 10;
		private static readonly object _lock = new object();

		private static string _memType = "SnesWorkRam";
		private static int _valueSize = 2;
		private static string _format = "hex";
		private static int _totalAddresses = 0;

		// Kandidaten-Adressen (Index = Adresse im Speicher)
		private static HashSet<int> _candidates = new HashSet<int>();

		// Basis-Snapshot des letzten Filters (fuer "previousSnapshot"-Vergleiche)
		private static byte[]? _prevSnapshot = null;
		// Letzter Filter-Stand (fuer Ergebnisse)
		private static byte[]? _lastFilteredState = null;

		// Persistente Snapshots (Slot 0-9)
		private static readonly byte[]?[] _snapshots = new byte[MaxSnapshots][];
		private static readonly ulong[] _snapshotFrames = new ulong[MaxSnapshots];

		private static string SnapshotFolder => Path.Combine(ConfigManager.HomeFolder, "cheatsearch");
		private static string SnapshotPath(int slot) => Path.Combine(SnapshotFolder, $"snap_{slot}.bin");

		public static void ResetSearch()
		{
			lock(_lock) {
				_memType = "SnesWorkRam";
				_valueSize = 2;
				_format = "hex";
				_totalAddresses = 0;
				_candidates.Clear();
				_prevSnapshot = null;
				_lastFilteredState = null;
			}
		}

		public static LiveApiCheatSearchState InitSearch(LiveApiCheatSearchInitRequest request)
		{
			lock(_lock) {
				_memType = request.MemType ?? "SnesWorkRam";
				_valueSize = Math.Max(1, Math.Min(request.ValueSize, 4));
				_format = request.Format ?? "hex";

				MemoryType? memType = ParseMemoryType(_memType);
				if(memType == null) {
					return GetStateInternal();
				}

				int size = GetMemSize(memType.Value);
				_totalAddresses = Math.Max(0, size - _valueSize + 1);
				_candidates = new HashSet<int>(Enumerable.Range(0, _totalAddresses));

				// Persistente Snapshots laden
				LoadSnapshots();

				// Basis-Snapshot = aktueller Zustand
				byte[] state = ReadFullMemory(memType.Value);
				_prevSnapshot = state;
				_lastFilteredState = state;
				return GetStateInternal();
			}
		}

		public static bool SaveSnapshot(int slot)
		{
			lock(_lock) {
				if(slot < 0 || slot >= MaxSnapshots) {
					return false;
				}
				MemoryType? memType = ParseMemoryType(_memType);
				if(memType == null) {
					return false;
				}
				byte[] state = ReadFullMemory(memType.Value);
				_snapshots[slot] = state;
				_snapshotFrames[slot] = GetFrame();

				try {
					Directory.CreateDirectory(SnapshotFolder);
					// Format: [8 Byte Frame (LE)][Speicher-Bytes]
					byte[] fileData = new byte[8 + state.Length];
					BitConverter.GetBytes(_snapshotFrames[slot]).CopyTo(fileData, 0);
					state.CopyTo(fileData, 8);
					File.WriteAllBytes(SnapshotPath(slot), fileData);
					return true;
				} catch {
					return false;
				}
			}
		}

		public static bool DeleteSnapshot(int slot)
		{
			lock(_lock) {
				if(slot < 0 || slot >= MaxSnapshots) {
					return false;
				}
				_snapshots[slot] = null;
				_snapshotFrames[slot] = 0;
				try {
					if(File.Exists(SnapshotPath(slot))) {
						File.Delete(SnapshotPath(slot));
					}
					return true;
				} catch {
					return false;
				}
			}
		}

		public static LiveApiCheatSearchState GetState()
		{
			lock(_lock) {
				LoadSnapshots();
				return GetStateInternal();
			}
		}

		private static LiveApiCheatSearchState GetStateInternal()
		{
			LoadSnapshots();
			LiveApiCheatSearchSnapshotInfo[] snapshots = new LiveApiCheatSearchSnapshotInfo[MaxSnapshots];
			for(int i = 0; i < MaxSnapshots; i++) {
				snapshots[i] = new LiveApiCheatSearchSnapshotInfo() {
					Slot = i,
					Frame = _snapshotFrames[i],
					Size = _snapshots[i]?.Length ?? 0,
					HasData = _snapshots[i] != null
				};
			}
			return new LiveApiCheatSearchState() {
				MemType = _memType,
				ValueSize = _valueSize,
				Format = _format,
				CandidateCount = _candidates.Count,
				TotalAddresses = _totalAddresses,
				Snapshots = snapshots
			};
		}

		public static int ApplyFilter(LiveApiCheatSearchFilterRequest request)
		{
			lock(_lock) {
				MemoryType? memType = ParseMemoryType(_memType);
				if(memType == null || _candidates.Count == 0) {
					return _candidates.Count;
				}

				byte[] current = ReadFullMemory(memType.Value);
				byte[]? baseSnapshot = null;

				switch(request.CompareTo) {
					case "specificValue":
						// Basis ist ein fester Zahlenwert - direkt vergleichen, keine Speicher-Basis noetig
						return ApplyValueFilter(current, request);

					case "specificSnapshot":
						if(request.SnapshotSlot >= 0 && request.SnapshotSlot < MaxSnapshots) {
							baseSnapshot = _snapshots[request.SnapshotSlot];
						}
						if(baseSnapshot == null) {
							return _candidates.Count;
						}
						break;

					case "previousSnapshot":
					default:
						baseSnapshot = _prevSnapshot;
						if(baseSnapshot == null) {
							return _candidates.Count;
						}
						break;
				}

				// Basis festhalten (fuer Ergebnisanzeige)
				byte[] baseState = baseSnapshot;

				HashSet<int> survivors = new HashSet<int>();
				foreach(int addr in _candidates) {
					Int64 curVal = ReadValue(current, addr);
					Int64 baseVal = ReadValue(baseSnapshot, addr);
					if(Compare(curVal, baseVal, request.Operator)) {
						survivors.Add(addr);
					}
				}

				_candidates = survivors;
				// Neuer "previousSnapshot" = aktueller Zustand (fuer naechsten Filter)
				_prevSnapshot = current;
				_lastFilteredState = current;
				_prevBaseForResults = baseState;
				return _candidates.Count;
			}
		}

		private static byte[]? _prevBaseForResults = null;

		private static int ApplyValueFilter(byte[] current, LiveApiCheatSearchFilterRequest request)
		{
			HashSet<int> survivors = new HashSet<int>();
			foreach(int addr in _candidates) {
				Int64 curVal = ReadValue(current, addr);
				if(Compare(curVal, request.Value, request.Operator)) {
					survivors.Add(addr);
				}
			}
			_candidates = survivors;
			_prevSnapshot = current;
			_lastFilteredState = current;
			_prevBaseForResults = null;
			return _candidates.Count;
		}

		public static LiveApiCheatSearchResult[] GetResults(int count, int start)
		{
			lock(_lock) {
				MemoryType? memType = ParseMemoryType(_memType);
				if(memType == null || _candidates.Count == 0) {
					return Array.Empty<LiveApiCheatSearchResult>();
				}
				byte[] current = _lastFilteredState ?? ReadFullMemory(memType.Value);

				List<LiveApiCheatSearchResult> results = new List<LiveApiCheatSearchResult>();
				int index = 0;
				foreach(int addr in _candidates.OrderBy(a => a)) {
					if(index < start) {
						index++;
						continue;
					}
					if(results.Count >= count) {
						break;
					}
					index++;
					Int64 value = ReadValue(current, addr);
					Int64 baseVal = _prevBaseForResults != null ? ReadValue(_prevBaseForResults, addr) : 0;
					results.Add(new LiveApiCheatSearchResult() {
						Address = (UInt32)addr,
						Value = value,
						BaseValue = baseVal,
						HexValue = ToHexString(value, _valueSize)
					});
				}
				return results.ToArray();
			}
		}

		public static bool WriteValue(UInt32 address, Int64 value, int valueSize)
		{
			lock(_lock) {
				MemoryType? memType = ParseMemoryType(_memType);
				if(memType == null) {
					return false;
				}
				int size = Math.Max(1, Math.Min(valueSize > 0 ? valueSize : _valueSize, 8));
				byte[] data = new byte[size];
				for(int i = 0; i < size; i++) {
					data[i] = (byte)((value >> (8 * i)) & 0xFF);
				}
				DebugApi.SetMemoryValues(memType.Value, address, data, size);
				return true;
			}
		}

		private static void LoadSnapshots()
		{
			for(int slot = 0; slot < MaxSnapshots; slot++) {
				if(_snapshots[slot] != null) {
					continue;
				}
				string path = SnapshotPath(slot);
				if(File.Exists(path)) {
					try {
						byte[] fileData = File.ReadAllBytes(path);
						if(fileData.Length >= 8) {
							_snapshotFrames[slot] = BitConverter.ToUInt64(fileData, 0);
							byte[] mem = new byte[fileData.Length - 8];
							Array.Copy(fileData, 8, mem, 0, mem.Length);
							_snapshots[slot] = mem;
						}
					} catch {
					}
				}
			}
		}

		private static Int64 ReadValue(byte[] mem, int addr)
		{
			int size = _valueSize;
			ulong value = 0;
			for(int i = 0; i < size && addr + i < mem.Length; i++) {
				value |= (ulong)mem[addr + i] << (8 * i);
			}
			if(_format == "signed") {
				switch(size) {
					case 1: return (sbyte)value;
					case 2: return (short)value;
					default: return (int)value;
				}
			}
			return (Int64)value;
		}

		private static bool Compare(Int64 current, Int64 compare, string op)
		{
			switch(op) {
				case "notEqual": return current != compare;
				case "lessThan": return current < compare;
				case "lessThanOrEqual": return current <= compare;
				case "greaterThan": return current > compare;
				case "greaterThanOrEqual": return current >= compare;
				default: return current == compare; // equal
			}
		}

		private static string ToHexString(Int64 value, int size)
		{
			StringBuilder sb = new StringBuilder(size * 2);
			for(int i = 0; i < size; i++) {
				sb.Append(((value >> (8 * i)) & 0xFF).ToString("X2"));
			}
			return sb.ToString();
		}

		private static byte[] ReadFullMemory(MemoryType type)
		{
			try {
				int size = GetMemSize(type);
				if(size <= 0) {
					return Array.Empty<byte>();
				}
				return DebugApi.GetMemoryValues(type, 0, (UInt32)(size - 1));
			} catch {
				return Array.Empty<byte>();
			}
		}

		private static int GetMemSize(MemoryType type)
		{
			try {
				return Math.Max(0, DebugApi.GetMemorySize(type));
			} catch {
				return 0;
			}
		}

		private static ulong GetFrame()
		{
			try {
				return (ulong)LiveDataService.GetStatus().Frame;
			} catch {
				return 0;
			}
		}

		private static MemoryType? ParseMemoryType(string type)
		{
			return LiveDataService.ParseMemoryType(type);
		}
	}
}
