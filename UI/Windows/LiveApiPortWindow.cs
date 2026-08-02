using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using System;
using System.Threading.Tasks;

namespace Mesen.Windows
{
	public class LiveApiPortWindow : MesenWindow
	{
		private readonly TextBox _portBox;

		public int? ResultPort { get; private set; }

		private LiveApiPortWindow(int currentPort)
		{
			Title = "Live API Port";
			Width = 340;
			Height = 130;
			CanResize = false;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;

			_portBox = new TextBox() {
				Text = currentPort.ToString(),
				Width = 130,
				Margin = new Thickness(5)
			};

			StackPanel inputPanel = new StackPanel() {
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 10, 0, 0)
			};
			inputPanel.Children.Add(new TextBlock() { Text = "Port:", VerticalAlignment = VerticalAlignment.Center });
			inputPanel.Children.Add(_portBox);

			Button okBtn = new Button() { Content = "OK", Width = 90, Margin = new Thickness(5) };
			okBtn.Click += (s, e) => Confirm();
			Button cancelBtn = new Button() { Content = "Abbrechen", Width = 90, Margin = new Thickness(5) };
			cancelBtn.Click += (s, e) => Close();

			StackPanel buttonPanel = new StackPanel() {
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Center,
				Margin = new Thickness(0, 10, 0, 0)
			};
			buttonPanel.Children.Add(okBtn);
			buttonPanel.Children.Add(cancelBtn);

			StackPanel root = new StackPanel();
			root.Children.Add(inputPanel);
			root.Children.Add(buttonPanel);
			Content = root;

			_portBox.KeyDown += (s, e) => {
				if(e.Key == Key.Enter) {
					Confirm();
				}
			};
		}

		private void Confirm()
		{
			if(int.TryParse(_portBox.Text, out int port) && port > 0 && port < 65536) {
				ResultPort = port;
				Close();
			} else {
				_portBox.Text = "";
				_portBox.Focus();
			}
		}

		public static async Task<int?> ShowPrompt(Window owner, int currentPort)
		{
			LiveApiPortWindow wnd = new LiveApiPortWindow(currentPort);
			if(owner != null) {
				await wnd.ShowDialog(owner);
			} else {
				wnd.Show();
			}
			return wnd.ResultPort;
		}
	}
}
