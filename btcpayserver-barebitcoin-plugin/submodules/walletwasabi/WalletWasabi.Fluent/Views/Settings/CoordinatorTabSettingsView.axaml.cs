using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views;

<<<<<<<< HEAD:WalletWasabi.Fluent/Views/SuccessAnimationView.axaml.cs
public class SuccessAnimationView : UserControl
{
	public SuccessAnimationView()
========
public class CoordinatorTabSettingsView : UserControl
{
	public CoordinatorTabSettingsView()
>>>>>>>> v2.0.8.1:WalletWasabi.Fluent/Views/Settings/CoordinatorTabSettingsView.axaml.cs
	{
		InitializeComponent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
