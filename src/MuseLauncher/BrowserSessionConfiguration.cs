namespace MuseLauncher;

/// <summary>创建浏览器时使用不可变快照，任何代理或凭证变化都需要新环境。</summary>
public sealed record BrowserSessionConfiguration(
    string Proxy,
    bool ForceProxy,
    string? ProfileDirectory = null);
