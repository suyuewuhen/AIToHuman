using System.Globalization;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Settings;

/// <summary>设置键常量：消费方代码只引用这里的常量，避免拼写漂移。</summary>
public static class SettingKeys
{    public const string AiProvider = "ai.provider";
    public const string AiBaseUrl = "ai.baseUrl";
    public const string AiApiKey = "ai.apiKey";
    public const string AiModel = "ai.model";
    public const string AiInactivityTimeoutSeconds = "ai.inactivityTimeoutSeconds";

    public const string StorageProvider = "storage.provider";
    public const string StorageLocalRoot = "storage.localRoot";
    public const string StorageS3Endpoint = "storage.s3.endpoint";
    public const string StorageS3Region = "storage.s3.region";
    public const string StorageS3Bucket = "storage.s3.bucket";
    public const string StorageS3AccessKeyId = "storage.s3.accessKeyId";
    public const string StorageS3SecretAccessKey = "storage.s3.secretAccessKey";
    public const string StorageS3Prefix = "storage.s3.prefix";

    public const string EvidenceScannerProvider = "evidence.scanner.provider";
    public const string EvidenceScannerEndpoint = "evidence.scanner.endpoint";
    public const string EvidenceScannerApiKey = "evidence.scanner.apiKey";
    public const string EvidenceScannerTimeoutSeconds = "evidence.scanner.timeoutSeconds";
    public const string EvidenceScannerFailMode = "evidence.scanner.failMode";
    public const string EvidenceScannerClamAvHost = "evidence.scanner.clamavHost";
    public const string EvidenceScannerClamAvPort = "evidence.scanner.clamavPort";

    public const string EvidenceMaxSizeBytes = "evidence.maxSizeBytes";
    public const string EvidenceMaxPerOrder = "evidence.maxPerOrder";
    public const string EvidenceDownloadUrlLifetimeSeconds = "evidence.downloadUrlLifetimeSeconds";
    public const string EvidenceStripMetadata = "evidence.stripMetadata";
    public const string EvidenceUploadsPerUserPerHour = "evidence.uploadsPerUserPerHour";

    public const string NotificationFanoutEnabled = "notifications.fanout.enabled";
}

/// <summary>
/// 运营可配置项的目录（白名单 + 校验规则 + 默认值 + 兼容的环境变量名）。
/// 解析顺序是「数据库覆盖 → 这里登记的 <see cref="SettingDefinition.ConfigurationKey"/> → <see cref="SettingDefinition.DefaultValue"/>」。
/// </summary>
public static class SettingCatalog
{
    /// <summary>取值表示“未配置”时使用的空字符串。</summary>
    public const string Empty = "";

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(SettingKeys.AiProvider, "AI 服务商", "AI 协议", "OpenAI 兼容协议的服务商类型。切换服务商不需要改代码，只需要改这里的地址与密钥。", SettingValueKind.Choice, "volcengine", "Settings:ai:provider", Choices: ["volcengine", "openai-compatible"]),
        new(SettingKeys.AiBaseUrl, "AI 服务商", "接口地址", "聊天补全接口的根地址，服务端会拼上 /chat/completions。", SettingValueKind.Url, "https://ark.cn-beijing.volces.com/api/v3", "VolcengineAI:BaseUrl"),
        new(SettingKeys.AiApiKey, "AI 服务商", "API Key", "模型服务的密钥。保存后只在服务端解密使用，运营后台与审计日志只显示掩码与指纹。", SettingValueKind.String, SettingCatalog.Empty, "VolcengineAI:ApiKey", IsSecret: true, MaxLength: 400),
        new(SettingKeys.AiModel, "AI 服务商", "模型 / 接入点", "火山控制台中已启用的模型 ID 或 ep-... 推理接入点 ID。", SettingValueKind.String, "glm-4-7-251222", "VolcengineAI:Model", MaxLength: 120),
        new(SettingKeys.AiInactivityTimeoutSeconds, "AI 服务商", "无活动超时（秒）", "连续多久没有收到上游数据就中断本轮对话，每收到数据后重新计时。", SettingValueKind.Int, "120", "VolcengineAI:TimeoutSeconds", MinInt: 5, MaxInt: 600),

        new(SettingKeys.StorageProvider, "对象存储", "存储类型", "local 写本机私有目录（开发与试点），s3 走 S3 兼容对象存储。", SettingValueKind.Choice, "local", "Settings:storage:provider", Choices: ["local", "s3"]),
        new(SettingKeys.StorageLocalRoot, "对象存储", "本机存储目录", "local 类型的根目录；留空则使用应用目录下的 evidence。", SettingValueKind.String, SettingCatalog.Empty, "ObjectStorage:LocalRoot", MaxLength: 400),
        new(SettingKeys.StorageS3Endpoint, "对象存储", "S3 端点", "S3/OSS 兼容服务的访问地址，例如 https://oss-cn-beijing.aliyuncs.com。", SettingValueKind.Url, SettingCatalog.Empty, "Settings:storage:s3:endpoint"),
        new(SettingKeys.StorageS3Region, "对象存储", "S3 区域", "签名所需的区域标识，例如 cn-beijing。", SettingValueKind.String, SettingCatalog.Empty, "Settings:storage:s3:region", MaxLength: 64),
        new(SettingKeys.StorageS3Bucket, "对象存储", "S3 Bucket", "存放凭证的私有 Bucket 名称。", SettingValueKind.String, SettingCatalog.Empty, "Settings:storage:s3:bucket", MaxLength: 128),
        new(SettingKeys.StorageS3AccessKeyId, "对象存储", "S3 AccessKeyId", "对象存储的访问密钥 ID。", SettingValueKind.String, SettingCatalog.Empty, "Settings:storage:s3:accessKeyId", MaxLength: 200),
        new(SettingKeys.StorageS3SecretAccessKey, "对象存储", "S3 SecretAccessKey", "对象存储的访问密钥。保存后只以掩码形式展示。", SettingValueKind.String, SettingCatalog.Empty, "Settings:storage:s3:secretAccessKey", IsSecret: true, MaxLength: 400),
        new(SettingKeys.StorageS3Prefix, "对象存储", "S3 对象前缀", "所有凭证对象的前缀，便于按环境隔离。", SettingValueKind.String, "evidence", "Settings:storage:s3:prefix", MaxLength: 200),

        new(SettingKeys.EvidenceMaxSizeBytes, "凭证上传", "单份上限（字节）", $"单份凭证的最大字节数，硬上限 {OrderEvidence.AbsoluteMaxSizeBytes / 1024 / 1024} MB：只能往里收紧，不能突破。", SettingValueKind.Int, OrderEvidence.MaxSizeBytes.ToString(CultureInfo.InvariantCulture), "Settings:evidence:maxSizeBytes", MinInt: (int)EvidenceLimits.MinSizeBytes, MaxInt: (int)OrderEvidence.AbsoluteMaxSizeBytes),
        new(SettingKeys.EvidenceMaxPerOrder, "凭证上传", "每单份数上限", $"每个订单最多几份凭证，硬上限 {OrderEvidence.AbsoluteMaxPerOrder} 份。", SettingValueKind.Int, OrderEvidence.MaxPerOrder.ToString(CultureInfo.InvariantCulture), "Settings:evidence:maxPerOrder", MinInt: 1, MaxInt: OrderEvidence.AbsoluteMaxPerOrder),
        new(SettingKeys.EvidenceDownloadUrlLifetimeSeconds, "凭证上传", "直连下载有效期（秒）", "对象存储直连下载地址的有效期：越短越安全，但下载大文件或网络慢时容易过期。本机目录存储不使用这个配置。", SettingValueKind.Int, "120", "Settings:evidence:downloadUrlLifetimeSeconds", MinInt: 5, MaxInt: 900),
        new(SettingKeys.EvidenceStripMetadata, "凭证上传", "上传时去除元数据", "开启后，JPEG 的 EXIF/XMP 与注释、PNG 的文本/时间/EXIF 块、WebP 的 EXIF/XMP 块会被剥离（像素数据不动），避免顺手上传就把拍摄地点与设备信息暴露出去。关闭会保留原始文件，适合需要完整取证链的场景。", SettingValueKind.Bool, "true", "Settings:evidence:stripMetadata"),
        new(SettingKeys.EvidenceUploadsPerUserPerHour, "凭证上传", "每用户每小时上传上限", "同一个上传者一小时内最多提交几份凭证；按数据库计数，多实例部署同样生效。", SettingValueKind.Int, EvidenceUploadQuota.DefaultPerUserPerHour.ToString(CultureInfo.InvariantCulture), "Settings:evidence:uploadsPerUserPerHour", MinInt: EvidenceUploadQuota.MinPerUserPerHour, MaxInt: EvidenceUploadQuota.MaxPerUserPerHour),

        new(SettingKeys.EvidenceScannerProvider, "内容扫描", "扫描方式", "none 表示未接入扫描（显式放行并打警告日志）；http 调用下面配置的扫描服务；clamav 直连 clamd 的 INSTREAM 端口。", SettingValueKind.Choice, "none", "Settings:evidence:scanner:provider", Choices: ["none", "http", "clamav"]),
        new(SettingKeys.EvidenceScannerClamAvHost, "内容扫描", "clamd 地址", "provider=clamav 时连接的 clamd 主机名或 IP。", SettingValueKind.String, "127.0.0.1", "Settings:evidence:scanner:clamavHost", MaxLength: 200),
        new(SettingKeys.EvidenceScannerClamAvPort, "内容扫描", "clamd 端口", "clamd 的 INSTREAM 端口，默认 3310。", SettingValueKind.Int, "3310", "Settings:evidence:scanner:clamavPort", MinInt: 1, MaxInt: 65535),
        new(SettingKeys.EvidenceScannerEndpoint, "内容扫描", "扫描服务地址", "接收凭证内容并返回判定结果的服务地址；留空等于停用。", SettingValueKind.Url, SettingCatalog.Empty, "Settings:evidence:scanner:endpoint"),
        new(SettingKeys.EvidenceScannerApiKey, "内容扫描", "扫描服务 API Key", "调用扫描服务时放在 X-Api-Key 请求头里的密钥。", SettingValueKind.String, SettingCatalog.Empty, "Settings:evidence:scanner:apiKey", IsSecret: true, MaxLength: 400),
        new(SettingKeys.EvidenceScannerTimeoutSeconds, "内容扫描", "扫描超时（秒）", "单次扫描的等待上限，超时按失败模式处理。", SettingValueKind.Int, "15", "Settings:evidence:scanner:timeoutSeconds", MinInt: 1, MaxInt: 120),
        new(SettingKeys.EvidenceScannerFailMode, "内容扫描", "扫描失败模式", "closed：扫描不可用时凭证保持“待扫描、不可下载”，不丢文件；open：扫描不可用时直接放行，只建议在开发环境使用。", SettingValueKind.Choice, "closed", "Settings:evidence:scanner:failMode", Choices: ["closed", "open"]),

        new(SettingKeys.NotificationFanoutEnabled, "通知推送", "多实例扇出", "多实例部署时用 Redis 发布/订阅广播通知，用户连在哪个实例上都能实时收到。启用前部署配置里必须有 ConnectionStrings__Redis；没配连接串时这个开关不生效，应用按单实例推送运行。远端实例重启或 Redis 抖动时通知仍会落库，客户端重连后从收件箱补齐。", SettingValueKind.Bool, "false", "Settings:notifications:fanout:enabled")
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey = All.ToDictionary(item => item.Key, StringComparer.Ordinal);

    public static SettingDefinition? TryGet(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>
    /// 未注册的设置键一律拒绝，返回 400。
    /// 这是防止有人通过后台改写部署级配置（连接串、日志级别、密钥环路径）的白名单闸门。
    /// </summary>
    public static SettingDefinition Require(string? key) =>
        TryGet(key) ?? throw new ArgumentException($"未注册的设置键：{key}。只有运营可配置项才能通过后台修改。", nameof(key));
}
