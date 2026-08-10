using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Config;
using Mesen.Interop;
using System;

namespace Mesen.Windows
{
	/// <summary>
	/// R3.2: TRAINER-Fenster (WeMod-aehnlich). Laedt die Trainer-Datei fuer das aktuell
	/// geladene Spiel (gameId = SHA1 der ROM), rendert An-Aus-Schalter, RAM-Felder und
	/// AR-Codes, und wendet sie an. Der TrainerService macht die per-Frame-RAM-Fixierung.
	/// </summary>
	public class TrainerWindow : MesenWindow
	{
		private TextBlock _statusText;
		private ItemsControl _cheatList;
		private TrainerConfig? _config;
		private string _gameId = "";

		public TrainerWindow()
		{
			InitializeComponent();

			_statusText = this.GetControl<TextBlock>("statusText");
			_cheatList = this.GetControl<ItemsControl>("cheatList");

			// Interne ROM-ID (SNES-Produkt-Code, z.B. "AQTD") - stabil ueber Versionen
			_gameId = EmuApi.GetRomGameCode();
			_config = TrainerConfig.Load(_gameId);

			if(_config == null) {
				_statusText.Text = "Kein Trainer für dieses Spiel gefunden.\n" +
					$"Game-ID (SHA1): {_gameId}\n\n" +
					"Lege eine Datei 'trainers/" + _gameId + ".json' an.";
				return;
			}

			_statusText.Text = $"Trainer: {_config.GameName ?? _gameId}";
			BuildUi();
		}

		private void BuildUi()
		{
			StackPanel panel = new StackPanel { Spacing = 6 };

			if(_config == null) {
				return;
			}

			foreach(TrainerCheat cheat in _config.Cheats) {
				string? type = cheat.Type?.ToLowerInvariant();
				if(type == "toggle") {
					panel.Children.Add(BuildToggle(cheat));
				} else if(type == "ram") {
					panel.Children.Add(BuildRamField(cheat));
				} else if(type == "ar") {
					panel.Children.Add(BuildArField(cheat));
				}
			}

			if(panel.Children.Count == 0) {
				panel.Children.Add(new TextBlock { Text = "Keine Cheats definiert." });
			}

			// Schalter zum Starten/Stoppen des TrainerService
			Button startBtn = new Button { Content = "Trainer aktivieren", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
			Button stopBtn = new Button { Content = "Trainer deaktivieren", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsVisible = false };
			startBtn.Click += (s, e) => {
				TrainerService.Start();
				startBtn.IsVisible = false;
				stopBtn.IsVisible = true;
			};
			stopBtn.Click += (s, e) => {
				TrainerService.Stop();
				stopBtn.IsVisible = false;
				startBtn.IsVisible = true;
			};
			panel.Children.Add(startBtn);
			panel.Children.Add(stopBtn);

			_cheatList.ItemsSource = new[] { panel };
		}

		private Control BuildToggle(TrainerCheat cheat)
		{
			CheckBox cb = new CheckBox {
				Content = cheat.Name ?? "Toggle",
				IsChecked = false
			};
			cb.IsCheckedChanged += (s, e) => {
				bool on = cb.IsChecked == true;
				TrainerService.SetToggle(cheat.Id, cheat, on);
				if(on) {
					TrainerService.ApplyToggleNow(cheat);
				}
			};
			return cb;
		}

		private Control BuildRamField(TrainerCheat cheat)
		{
			DockPanel row = new DockPanel { Margin = new Avalonia.Thickness(0, 2, 0, 2) };
			StackPanel left = new StackPanel { Width = 140 };
			left.Children.Add(new TextBlock { Text = cheat.Name ?? cheat.Label ?? "RAM" });
			if(!string.IsNullOrEmpty(cheat.RamAddress)) {
				left.Children.Add(new TextBlock { Text = $"@ {cheat.RamAddress}", FontSize = 10, Foreground = Avalonia.Media.Brushes.Gray });
			}
			DockPanel.SetDock(left, Avalonia.Controls.Dock.Left);
			row.Children.Add(left);

			TextBox tb = new TextBox {
				Text = cheat.Value ?? "",
				Width = 120,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
				HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
			};
			row.Children.Add(tb);

			Button setBtn = new Button {
				Content = "Set",
				Width = 48,
				Margin = new Avalonia.Thickness(4, 0, 0, 0),
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
			};
			DockPanel.SetDock(setBtn, Avalonia.Controls.Dock.Right);
			row.Children.Add(setBtn);
			setBtn.Click += (s, e) => {
				cheat.Value = tb.Text;
				TrainerService.ApplyToggleNow(cheat);
			};
			row.Children.Add(setBtn);

			return row;
		}

		private Control BuildArField(TrainerCheat cheat)
		{
			CheckBox cb = new CheckBox {
				Content = (cheat.Name ?? "AR-Code") + (string.IsNullOrEmpty(cheat.Code) ? "" : "  [" + cheat.Code + "]")
			};
			cb.IsCheckedChanged += (s, e) => {
				bool on = cb.IsChecked == true;
				// AR-Codes werden ueber den nativen CheatManager angewendet
				var activeCodes = new System.Collections.Generic.List<string>();
				foreach(TrainerCheat c in _config?.Cheats ?? new System.Collections.Generic.List<TrainerCheat>()) {
					if(c.Type?.ToLowerInvariant() == "ar" && !string.IsNullOrEmpty(c.Code) && c == cheat ? on : false) {
						activeCodes.Add(c.Code);
					}
				}
				// vereinfacht: der TrainerService verwaltet AR-Codes separat
				EmuApi.SetCheats(on ? new[] { new InteropCheatCode(CheatType.SnesProActionReplay, cheat.Code ?? "") } : new InteropCheatCode[0], on ? 1u : 0u);
			};
			return cb;
		}

		private void Close_OnClick(object sender, RoutedEventArgs e)
		{
			Close();
		}

		protected override void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}
	}
}
