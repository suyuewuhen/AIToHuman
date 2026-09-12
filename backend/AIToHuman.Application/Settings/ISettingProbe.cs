using AIToHuman.Contracts.Settings;

namespace AIToHuman.Application.Settings;

/// <summary>
/// 运营后台的“测试连接”：只做只读自检（目录是否可写、对方服务是否可达），
/// 不修改业务数据，也不保存任何东西。
/// </summary>
public interface ISettingProbe
{
    Task<SettingTestResponse> ProbeAsync(string key, CancellationToken cancellationToken = default);
}
