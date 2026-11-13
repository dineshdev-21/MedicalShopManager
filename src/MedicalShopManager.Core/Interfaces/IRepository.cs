namespace MedicalShopManager.Core.Interfaces;

/// <summary>
/// Base repository interface for common CRUD operations.
/// </summary>
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(long id);
    Task<IReadOnlyList<T>> GetAllAsync();
    Task<long> InsertAsync(T entity);
    Task<bool> UpdateAsync(T entity);
    Task<bool> DeleteAsync(long id);
    Task<int> CountAsync();
}
