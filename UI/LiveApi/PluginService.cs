using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Mesen.LiveApi
{
	/// <summary>
	/// Manages the WebUI plugin files (LiveApiPlugins/*.js) and the export area
	/// (LiveApiExports/) for logs, screenshots and other data that plugins export.
	/// </summary>
	public static class PluginService
	{
		private static readonly object Lock = new object();

		private static string PluginsDir
		{
			get
			{
				string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiPlugins");
				try {
					Directory.CreateDirectory(dir);
				} catch {
				}
				return dir;
			}
		}

		private static string ExportsDir
		{
			get
			{
				string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiExports");
				try {
					Directory.CreateDirectory(dir);
				} catch {
				}
				return dir;
			}
		}

		private static string SanitizeName(string name)
		{
			StringBuilder sb = new StringBuilder();
			foreach(char c in name ?? "") {
				if(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') {
					sb.Append(c);
				}
			}
			return sb.ToString();
		}

		public static JsonNode? ListPlugins()
		{
			try {
				JsonArray arr = new JsonArray();
				string dir = Path.Combine(AppContext.BaseDirectory, "LiveApiPlugins");
				if(Directory.Exists(dir)) {
					foreach(string file in Directory.GetFiles(dir, "*.js")) {
						string displayName = Path.GetFileNameWithoutExtension(file);
						string description = "";
						try {
							string[] lines = File.ReadAllLines(file);
							foreach(string line in lines) {
								string t = line.Trim();
								if(t.StartsWith("// @name", StringComparison.Ordinal)) {
									displayName = t.Substring(8).Trim();
								} else if(t.StartsWith("// @description", StringComparison.Ordinal)) {
									description = t.Substring(15).Trim();
								}
								if(t != "" && !t.StartsWith("//")) {
									break;
								}
							}
						} catch {
						}
						arr.Add((JsonNode)(new JsonObject() {
							["name"] = displayName,
							["file"] = Path.GetFileName(file),
							["description"] = description
						}));
					}
				}
				return new JsonObject() { ["plugins"] = arr };
			} catch {
				return null;
			}
		}

		public static byte[]? GetPlugin(string fileName)
		{
			try {
				string safe = SanitizeName(fileName);
				if(safe == "") {
					return null;
				}
				string path = Path.Combine(PluginsDir, safe + ".js");
				if(!File.Exists(path)) {
					return null;
				}
				return File.ReadAllBytes(path);
			} catch {
				return null;
			}
		}

		public static bool SavePlugin(string fileName, string content)
		{
			try {
				string safe = SanitizeName(fileName);
				if(safe == "") {
					return false;
				}
				string path = Path.Combine(PluginsDir, safe + ".js");
				File.WriteAllText(path, content ?? "", new UTF8Encoding(false));
				return true;
			} catch {
				return false;
			}
		}

		public static bool DeletePlugin(string fileName)
		{
			try {
				string safe = SanitizeName(fileName);
				if(safe == "") {
					return false;
				}
				string path = Path.Combine(PluginsDir, safe + ".js");
				if(File.Exists(path)) {
					File.Delete(path);
				}
				return true;
			} catch {
				return false;
			}
		}

		/// <summary>
		/// Writes data from a plugin to the export folder.
		/// mode: "text"/"append" = UTF-8 text, "png"/"base64" = base64 binary data (e.g. screenshot).
		/// </summary>
		public static bool Export(string fileName, string data, string mode)
		{
			try {
				string safe = SanitizeName(fileName);
				if(safe == "") {
					return false;
				}
				string path = Path.Combine(ExportsDir, safe);
				lock(Lock) {
					switch(mode) {
						case "png":
						case "base64":
							byte[] bytes = Convert.FromBase64String(data ?? "");
							File.WriteAllBytes(path, bytes);
							break;
						case "append":
							File.AppendAllText(path, data ?? "", new UTF8Encoding(false));
							break;
						default:
							File.WriteAllText(path, data ?? "", new UTF8Encoding(false));
							break;
					}
				}
				return true;
			} catch {
				return false;
			}
		}
	}
}
