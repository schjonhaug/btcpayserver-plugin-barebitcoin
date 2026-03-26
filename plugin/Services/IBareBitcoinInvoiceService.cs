#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public interface IBareBitcoinInvoiceService
{
    Task TrackInvoice(string invoiceId, CancellationToken cancellation = default);
    Task UntrackInvoice(string invoiceId, CancellationToken cancellation = default);
    Task<IReadOnlyCollection<string>> GetTrackedInvoices(CancellationToken cancellation = default);
}
