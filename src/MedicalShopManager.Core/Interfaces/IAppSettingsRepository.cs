using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.Core.Interfaces;

public interface IAppSettingsRepository
{
    Task<string?> GetValueAsync(string key);
    Task SetValueAsync(string key, string value, string? description = null);
    Task<IReadOnlyList<AppSetting>> GetAllAsync();
}
