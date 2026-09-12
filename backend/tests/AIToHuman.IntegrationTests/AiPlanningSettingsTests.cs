using System.Net;
using AIToHuman.Application.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 验证 AI 集成改成“每次调用读配置”之后，运营后台改密钥或模型会立刻生效，
/// 而不是等到进程重启（旧实现把 IOptions 的值缓存在构造函数里）。
/// </summary>
public sealed class AiPlanningSettingsTests
{
    [Fact]
    public async Task Model_and_key_are_read_per_call()
    {
        var handler = new StubUpstreamHandler(_ => SettingsTestData.Json("{\"error\":{\"message\":\"boom\"}}", HttpStatusCode.BadGateway));
        var settings = new MutableSettingsProvider();
        settings.Set(SettingKeys.AiBaseUrl, "https://ark.invalid/api/v3");
        settings.Set(SettingKeys.AiInactivityTimeoutSeconds, "120");
        settings.Set(SettingKeys.AiApiKey, "key-1");
        settings.Set(SettingKeys.AiModel, "model-a");
        var service = new AiPlanningService(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, settings, new FixedTimeProvider(Harness.Now), NullLogger<AiPlanningService>.Instance);

        await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));
        settings.Set(SettingKeys.AiApiKey, "key-2");
        settings.Set(SettingKeys.AiModel, "model-b");
        await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("\"model\":\"model-a\"", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"model\":\"model-b\"", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal("Bearer key-2", handler.AuthorizationHeader);
    }

    [Fact]
    public async Task Base_url_from_settings_is_used_for_the_request()
    {
        var handler = new StubUpstreamHandler(_ => SettingsTestData.Json("{\"error\":{\"message\":\"boom\"}}", HttpStatusCode.BadGateway));
        var settings = new MutableSettingsProvider();
        settings.Set(SettingKeys.AiBaseUrl, "https://ark-alt.example.com/api/v3/");
        settings.Set(SettingKeys.AiApiKey, "key-1");
        settings.Set(SettingKeys.AiModel, "model-a");
        var service = new AiPlanningService(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, settings, new FixedTimeProvider(Harness.Now), NullLogger<AiPlanningService>.Instance);

        await Harness.DrainAsync(service, Harness.Messages(Harness.User("你好")));

        Assert.Equal("https://ark-alt.example.com/api/v3/chat/completions", handler.RequestUri);
    }

    [Fact]
    public async Task Missing_key_error_points_to_the_operator_console()
    {
        var handler = new StubUpstreamHandler(_ => SettingsTestData.Json("{}"));
        var settings = new MutableSettingsProvider();
        settings.Set(SettingKeys.AiModel, "model-a");
        settings.Set(SettingKeys.AiApiKey, "");
        var service = new AiPlanningService(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, settings, new FixedTimeProvider(Harness.Now), NullLogger<AiPlanningService>.Instance);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("你好")));

        Assert.NotNull(error);
        Assert.Contains("尚未配置 API Key", error!.Message, StringComparison.Ordinal);
        Assert.Contains(SettingKeys.AiApiKey, error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestBodies);
    }
}
