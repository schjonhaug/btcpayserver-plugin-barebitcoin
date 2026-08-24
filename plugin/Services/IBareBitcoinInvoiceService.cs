#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public interface IBareBitcoinInvoiceService
{
    Task TrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default);
    Task UntrackInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default);
    Task ResolveLegacyInvoice(BareBitcoinInvoiceScope scope, string invoiceId, CancellationToken cancellation = default);
    Task<IReadOnlyCollection<string>> GetTrackedInvoices(BareBitcoinInvoiceScope scope, CancellationToken cancellation = default);
}
