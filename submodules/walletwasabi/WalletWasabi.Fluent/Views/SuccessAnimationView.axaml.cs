using Avalonia.Controls;
using Avalonia.Markup.Xaml;

<<<<<<<< HEAD:WalletWasabi.Fluent/Views/Wallets/Buy/OrderInputView.axaml.cs
namespace WalletWasabi.Fluent.Views.Wallets.Buy;

public partial class OrderInputView : UserControl
{
	public OrderInputView()
========
namespace WalletWasabi.Fluent.Views;

public class SuccessAnimationView : UserControl
{
	public SuccessAnimationView()
>>>>>>>> v2.0.8.1:WalletWasabi.Fluent/Views/SuccessAnimationView.axaml.cs
	{
		InitializeComponent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
