using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.Wallets.Buy;

<<<<<<<< HEAD:WalletWasabi.Fluent/Views/Wallets/Buy/OrderMessagesView.axaml.cs
public partial class OrderMessagesView : UserControl
{
	public OrderMessagesView()
========
public partial class OrderInputView : UserControl
{
	public OrderInputView()
>>>>>>>> v2.0.8.1:WalletWasabi.Fluent/Views/Wallets/Buy/OrderInputView.axaml.cs
	{
		InitializeComponent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
