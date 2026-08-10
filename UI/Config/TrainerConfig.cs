using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mesen.Interop;
using Mesen.Utilities;

namespace Mesen.Config
{
	/// <summary>
	/// R3.2: TRAINER - WeMod-aehnliches Cheat-Tool, nativ im Emulator.
	/// Eine Trainer-Datei (JSON) pro Spiel beschreibt Cheats/An-Aus-Schalter, RAM-Felder
	/// (Geld/EXP/Inventar) und AR-Codes. Wird nur geladen, wenn die gameId (SHA1 der ROM)
	/// mit der aktuell geladenen ROM uebereinstimmt - kein falsches Spiel.
	/// </summary>
	public class TrainerConfig
	{
		// Interne ROM-ID (SNES-Produkt-Code aus dem Header, z.B. "AQTD" fuer Terranigma).
		// Stabil ueber ROM-Versionen/Regionen - zuverlaessiger als SHA1 (die sich mit jeder
		// ROM-Version/Region/Patch aendert).
		public string? GameId { get; set; }
		public string? GameName { get; set; }      // optional, nur Anzeige
		public List<TrainerCheat> Cheats { get; set; } = new List<TrainerCheat>();

		public static string TrainerFolder => Path.Combine(ConfigManager.HomeFolder, "trainers");

		public static TrainerConfig? Load(string gameId)
		{
			try {
				string path = Path.Combine(TrainerFolder, gameId + ".json");
				if(File.Exists(path)) {
					TrainerConfig? cfg = JsonSerializer.Deserialize<TrainerConfig>(File.ReadAllText(path), MesenSerializerContext.Default.TrainerConfig);
					if(cfg != null && cfg.GameId == gameId) {
						return cfg;
					}
				}
			} catch {
			}
			return null;
		}
	}

	/// <summary>Ein einzelner Trainer-Cheat.</summary>
	public class TrainerCheat
	{
		[JsonIgnore]
		public string Id { get; set; } = Guid.NewGuid().ToString("N");

		public string? Name { get; set; }
		// type: toggle (An/Aus-Schalter, RAM-Wert dauerhaft fixieren),
		//       ram (RAM-Feld, manuell setzbar), ar (Action-Replay-Code),
		//       romPatch (ROM-Schwellen patchen - fuer An/Aus wie z.B. Kollision)
		public string? Type { get; set; }

		// fuer type=toggle / type=ram: RAM-Adresse + Wert
		public string? RamAddress { get; set; }
		public int Size { get; set; } = 2;
		public string? Value { get; set; }        // fixierter Wert (hex oder dezimal)

		// fuer type=ar: der AR-Code
		public string? Code { get; set; }

		// fuer type=romPatch: Liste der ROM-Patches (Datei-Offsets)
		public List<RomPatch>? Patches { get; set; }

		// optional: nur zum Anzeigen (z.B. aktueller Wert wird live gelesen)
		public string? Label { get; set; }
	}

	/// <summary>Ein einzelner ROM-Patch: Address = Datei-Offset, Original/Patch = Hex-Bytes.</summary>
	public class RomPatch
	{
		public string? Address { get; set; }   // Datei-Offset, z.B. "0xC478"
		public string? Original { get; set; }  // Original-Bytes, z.B. "C9 A0 00"
		public string? Patch { get; set; }     // gepatchte Bytes, z.B. "C9 FF 00"
	}
}
