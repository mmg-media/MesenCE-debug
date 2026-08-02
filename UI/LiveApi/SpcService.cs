using Mesen.Interop;
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.LiveApi
{
	public static class SpcService
	{
		private static SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
		private static readonly object _recordLock = new object();
		private static string _recordPath = "";
		private static DateTime _recordStart = default;
		private static bool _recordWasPaused;

		private static T RunExclusive<T>(Func<T> action)
		{
			_gate.Wait();
			try {
				return action();
			} finally {
				_gate.Release();
			}
		}

		private static void WriteFixedLengthString(BinaryWriter writer, string text, int length)
		{
			byte[] textAsBytes = Encoding.ASCII.GetBytes(text ?? "");
			byte[] buffer = new byte[length];
			for(int i = 0; i < length; i++) {
				buffer[i] = (i < textAsBytes.Length) ? textAsBytes[i] : (byte)0;
			}
			writer.Write(buffer);
		}

		/// <summary>
		/// Erzeugt einen vollständigen .spc-Snapshot des aktuell geladenen Musikstücks/Sound-Sets
		/// (APU-RAM + DSP-Register + CPU-Register + ID666-Tags), identisch zum Mesen-UI-Export.
		/// </summary>
		public static byte[]? ExportSpc(string? songTitle, string? gameTitle, string? artist)
		{
			return RunExclusive(() => {
				try {
					SpcState cpu = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
					byte[] spcRam = DebugApi.GetMemoryState(MemoryType.SpcRam);
					byte[] spcMemory = DebugApi.GetMemoryState(MemoryType.SpcMemory);
					byte[] dspRegisters = DebugApi.GetMemoryState(MemoryType.SpcDspRegisters);
					if(spcRam == null || spcMemory == null || dspRegisters == null) {
						return null;
					}

					using(MemoryStream stream = new MemoryStream(0x10200)) {
						using(BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
							WriteFixedLengthString(writer, "SNES-SPC700 Sound File Data v0.30", 33);
							writer.Write((short)0x1a1a);
							writer.Write((byte)26); // Has ID666 tags
							writer.Write((byte)30); // Minor version number
							writer.Write(cpu.PC);
							writer.Write(cpu.A);
							writer.Write(cpu.X);
							writer.Write(cpu.Y);
							writer.Write((byte)cpu.PS);
							writer.Write(cpu.SP);
							writer.Write((short)0); // Reserved
							WriteFixedLengthString(writer, songTitle ?? "", 32);
							WriteFixedLengthString(writer, gameTitle ?? "", 32);
							WriteFixedLengthString(writer, "", 16); // Dumper
							WriteFixedLengthString(writer, "MesenCE Live API", 32); // Kommentar
							WriteFixedLengthString(writer, DateTime.Now.ToString("MM/dd/yyyy"), 11); // Datum
							WriteFixedLengthString(writer, "", 3);  // Sekunden bis Fade
							WriteFixedLengthString(writer, "", 5);  // Fade-Länge in ms
							WriteFixedLengthString(writer, artist ?? "", 32);
							writer.Write((short)0); // Keine deaktivierten Kanäle
							WriteFixedLengthString(writer, "", 45); // Reserviert

							// Zuletzt geschriebene Werte der write-only-Register übernehmen
							spcMemory[0xF0] = spcRam[0xF0];
							spcMemory[0xF1] = spcRam[0xF1];
							spcMemory[0xFA] = spcRam[0xFA];
							spcMemory[0xFB] = spcRam[0xFB];
							spcMemory[0xFC] = spcRam[0xFC];

							writer.Write(spcMemory, 0, 0x10000);
							writer.Write(dspRegisters, 0, 128);
							WriteFixedLengthString(writer, "", 64); // Unbenutzt
							writer.Write(spcRam, 0x10000 - 64, 64); // Letzte 64 RAM-Bytes (IPL)
						}
						return stream.ToArray();
					}
				} catch {
					return null;
				}
			});
		}

		/// <summary>
		/// Zeichnet das aktuell abgespielte Audio als WAV auf (16-bit Stereo PCM).
		/// Nutzt den eingebauten Mesen-WaveRecorder – dadurch ist kein eigener SPC-Emulator nötig.
		/// Hält den Emulator an/fort wie nötig; pausiert ihn danach wieder, falls er vorher pausiert war.
		/// </summary>
		public static byte[]? RecordWav(int seconds)
		{
			if(seconds <= 0) {
				seconds = 30;
			}
			if(seconds > 600) {
				seconds = 600;
			}

			try {
				string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiExports");
				Directory.CreateDirectory(dir);
				string wavPath = Path.Combine(dir, "spc_live_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");

				if(RecordApi.WaveIsRecording()) {
					RecordApi.WaveStop();
				}

				bool wasPaused = false;
				if(!EmuApi.IsRunning()) {
					return null;
				}
				if(EmuApi.IsPaused()) {
					wasPaused = true;
					DebugApi.ResumeExecution();
				}

				RecordApi.WaveRecord(wavPath);
				Thread.Sleep(seconds * 1000);
				RecordApi.WaveStop();

				if(wasPaused) {
					EmuApi.Pause();
				}

				if(!File.Exists(wavPath)) {
					return null;
				}
				return File.ReadAllBytes(wavPath);
			} catch {
				try {
					if(RecordApi.WaveIsRecording()) {
						RecordApi.WaveStop();
					}
				} catch {
				}
				return null;
			}
		}

		/// <summary>
		/// Startet eine asynchrone WAV-Aufnahme (für automatisierte/komplette Lied-Aufnahmen).
		/// Der Emulator wird bei Bedarf fortgesetzt und nach Stop wieder pausiert.
		/// </summary>
		public static JsonObject? StartRecording()
		{
			lock(_recordLock) {
				try {
					if(!EmuApi.IsRunning()) {
						return new JsonObject() { ["ok"] = false, ["error"] = "Emulator läuft nicht" };
					}
					if(RecordApi.WaveIsRecording()) {
						return new JsonObject() { ["ok"] = false, ["error"] = "Aufnahme läuft bereits" };
					}

					string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiExports");
					Directory.CreateDirectory(dir);
					_recordPath = Path.Combine(dir, "spc_recording.wav");
					if(File.Exists(_recordPath)) {
						File.Delete(_recordPath);
					}

					_recordWasPaused = EmuApi.IsPaused();
					if(_recordWasPaused) {
						DebugApi.ResumeExecution();
					}

					RecordApi.WaveRecord(_recordPath);
					_recordStart = DateTime.UtcNow;
					return new JsonObject() { ["ok"] = true, ["file"] = "spc_recording.wav" };
				} catch(Exception ex) {
					return new JsonObject() { ["ok"] = false, ["error"] = ex.Message };
				}
			}
		}

		public static JsonNode? GetRecordingStatus()
		{
			lock(_recordLock) {
				bool rec = RecordApi.WaveIsRecording();
				double elapsed = 0;
				if(rec && _recordStart != default) {
					elapsed = (DateTime.UtcNow - _recordStart).TotalSeconds;
				}
				return new JsonObject() {
					["recording"] = rec,
					["elapsed"] = Math.Round(elapsed, 1)
				};
			}
		}

		public static JsonObject? StopRecording()
		{
			lock(_recordLock) {
				try {
					if(RecordApi.WaveIsRecording()) {
						RecordApi.WaveStop();
					}
					if(_recordWasPaused) {
						EmuApi.Pause();
						_recordWasPaused = false;
					}
					bool exists = File.Exists(_recordPath);
					return new JsonObject() { ["ok"] = true, ["hasFile"] = exists };
				} catch(Exception ex) {
					return new JsonObject() { ["ok"] = false, ["error"] = ex.Message };
				}
			}
		}

		public static byte[]? GetRecordingFile()
		{
			lock(_recordLock) {
				try {
					if(RecordApi.WaveIsRecording()) {
						return null; //noch aktiv, noch nicht lesbar
					}
					if(!File.Exists(_recordPath)) {
						return null;
					}
					return File.ReadAllBytes(_recordPath);
				} catch {
					return null;
				}
			}
		}

		/// <summary>
		/// JSON-Übersicht über den aktuellen APU/DSP-Zustand: CPU-Register, Timer,
		/// DSP-Globalregister und alle 8 Voices (Quelle, Lautstärke, Pitch, ADSR, Envelope).
		/// </summary>
		public static JsonNode? GetSpcState()
		{
			return RunExclusive(() => {
				try {
					SpcState cpu = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
					byte[] dsp = DebugApi.GetMemoryState(MemoryType.SpcDspRegisters);
					if(dsp == null || dsp.Length < 128) {
						return new JsonObject() { ["error"] = "DSP-Register nicht verfügbar" };
					}

					JsonArray voices = new JsonArray();
					for(int v = 0; v < 8; v++) {
						byte vbit = (byte)(1 << v);
						bool keyOn = (dsp[0x4C] & vbit) != 0;        //KON
						bool keyOff = (dsp[0x5C] & vbit) != 0;       //KOFF
						bool pitchMod = (dsp[0x2D] & vbit) != 0;     //PMON
						bool noise = (dsp[0x3D] & vbit) != 0;        //NON
						bool echo = (dsp[0x4D] & vbit) != 0;         //EON
						ushort pitch = (ushort)(dsp[0x20 + v] | ((dsp[0x30 + v] & 0x3F) << 8)); //PITCHL/H (14-bit)
						bool active = keyOn || pitch != 0;

						voices.Add((JsonNode)(new JsonObject() {
							["voice"] = v,
							["active"] = active,
							["volumeLeft"] = dsp[0x00 + v],      //VOLL
							["volumeRight"] = dsp[0x10 + v],     //VOLR
							["pitch"] = pitch,
							["source"] = dsp[0x40 + v],          //SRCN
							["adsr1"] = dsp[0x50 + v],
							["adsr2"] = dsp[0x60 + v],
							["gain"] = dsp[0x70 + v],
							["keyOn"] = keyOn,
							["keyOff"] = keyOff,
							["pitchModulation"] = pitchMod,
							["noise"] = noise,
							["echo"] = echo
						}));
					}

					return new JsonObject() {
						["cpu"] = new JsonObject() {
							["pc"] = cpu.PC,
							["a"] = cpu.A,
							["x"] = cpu.X,
							["y"] = cpu.Y,
							["sp"] = cpu.SP,
							["ps"] = (byte)cpu.PS,
							["stopState"] = cpu.StopState.ToString(),
							["dspRegSelect"] = cpu.DspReg,
							["ipldisabled"] = cpu.RomEnabled == false
						},
						["timers"] = new JsonObject() {
							["t0"] = new JsonObject() { ["output"] = cpu.Timer0.Output, ["target"] = cpu.Timer0.Target, ["enabled"] = cpu.Timer0.Enabled },
							["t1"] = new JsonObject() { ["output"] = cpu.Timer1.Output, ["target"] = cpu.Timer1.Target, ["enabled"] = cpu.Timer1.Enabled },
							["t2"] = new JsonObject() { ["output"] = cpu.Timer2.Output, ["target"] = cpu.Timer2.Target, ["enabled"] = cpu.Timer2.Enabled }
						},
						["dsp"] = new JsonObject() {
							["mvoll"] = dsp[0x0C],  //MVOLL
							["mvolr"] = dsp[0x1C],  //MVOLR
							["evoll"] = dsp[0x2C],  //EVOLL
							["evolr"] = dsp[0x3C],  //EVOLR
							["kon"] = dsp[0x4C],
							["kof"] = dsp[0x5C],
							["flg"] = dsp[0x6C],
							["efb"] = dsp[0x0D],
							["pmon"] = dsp[0x2D],
							["non"] = dsp[0x3D],
							["eon"] = dsp[0x4D],
							["dir"] = dsp[0x5D],
							["esa"] = dsp[0x6D],
							["edl"] = dsp[0x7D],
							["voices"] = voices
						}
					};
				} catch(Exception ex) {
					return new JsonObject() { ["error"] = ex.Message };
				}
			});
		}
	}
}
