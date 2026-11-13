using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface ISaleRepository : IRepository<Sale>
{
    Task<Sale?> GetWithItemsAsync(long id);
    Task<Sale?> GetByInvoiceNumberAsync(string invoiceNumber);
    Task<IReadOnlyList<Sale>> GetByDateRangeAsync(DateTime from, DateTime to);
    Task<IReadOnlyList<Sale>> GetByCustomerAsync(long customerId);
    Task<IReadOnlyList<Sale>> SearchAsync(string query);

    /// <summary>
    /// Posts the sale in one transaction: sale, items, stock deduction per item (fails when the batch no longer has
    /// enough units) and SaleDispatch stock movements. When <see cref="Sale.InvoiceNumber"/> is blank the next
    /// <see cref="DocumentType.Sale"/> number is allocated inside that same transaction and written back.
    /// </summary>
    Task<long> InsertWithItemsAsync(Sale sale);
}
