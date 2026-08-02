using CommunityToolkit.Mvvm.ComponentModel;

namespace Mesen.Config
{
	public partial class LiveApiConfig : BaseConfig<LiveApiConfig>
	{
		[ObservableProperty] public partial bool Enabled { get; set; } = true;
		[ObservableProperty] public partial int Port { get; set; } = 8901;
	}
}
