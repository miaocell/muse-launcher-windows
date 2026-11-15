using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using MuseLauncher.Core;

namespace MuseLauncher;

/// <summary>
/// 账号配置窗口：头像 / 名称 / 代理 / 标签，代理测活与 Clash 端口探测。
/// 行为镜像安卓 AccountConfigActivity。
/// </summary>
public partial class AccountConfigWindow : Window
{
    private const string TestUrl = "https://www.gstatic.com/generate_204";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly Account account;

    /// <summary>保存后是否有任意字段变化（调用方据此写盘）。</summary>
    public bool Changed { get; private set; }

    /// <summary>代理的生效目标是否变化（调用方据此驱逐并重建浏览器环境）。</summary>
    public bool ProxyChanged { get; private set; }

    private bool testing;
    private bool detecting;

    public AccountConfigWindow(Account account)
    {
        InitializeComponent();
        this.account = account;
        AvatarInput.Text = account.Avatar;
        NameInput.Text = account.Name;
        ProxyInput.Text = account.Proxy;
        TagsInput.Text = string.Join(", ", account.Tags);
        UpdateTile();
        Loaded += (_, _) => NameInput.Focus();
    }

    private void UpdateTile()
    {
        var av = AvatarInput.Text.Trim();
        if (av.Length > 2) av = av[..2];
        TileText.Text = string.IsNullOrEmpty(av) ? "m" : av;
    }

    private void OnAvatarChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateTile();

    // ---------- 保存 ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Persist();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>读取输入并写入 account（镜像安卓 persist）。真正落盘由调用方 store.Save() 完成。</summary>
    private void Persist()
    {
        var oldProxy = account.Proxy;
        var oldAvatar = account.Avatar;
        var oldName = account.Name;
        var oldTags = string.Join('\u0001', account.Tags);

        var avatar = AvatarInput.Text.Trim();
        if (avatar.Length == 0) avatar = "m";
        if (avatar.Length > 2) avatar = avatar[..2];
        account.Avatar = avatar;

        var name = NameInput.Text.Trim();
        if (name.Length > 0) account.Name = name.Length > 16 ? name[..16] : name;

        account.Proxy = ProxyInput.Text.Trim();

        var tags = new List<string>();
        foreach (var t in TagsInput.Text.Split(',', '，'))
        {
            var tag = t.Trim();
            if (tag.Length == 0) continue;
            tags.Add(tag.Length > 8 ? tag[..8] : tag);
        }
        account.Tags = tags;

        // 代理拍生效值比较：内嵌账密的写法差异不影响 --proxy-server，无需重建环境
        ProxyChanged = !string.Equals(
            ProxyServerOf(oldProxy), ProxyServerOf(account.Proxy), StringComparison.OrdinalIgnoreCase);

        Changed = ProxyChanged
                  || oldAvatar != account.Avatar
                  || oldName != account.Name
                  || oldProxy != account.Proxy
                  || oldTags != string.Join('\u0001', account.Tags);
    }

    /// <summary>代理的生效值：忽略内嵌账密（账密走 407 挑战应答，不影响代理目标）。</summary>
    private static string ProxyServerOf(string? proxy)
    {
        return string.IsNullOrWhiteSpace(proxy) ? "" : ProxyUtil.ProxyServerValue(proxy);
    }


    // ---------- 代理测活 ----------

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (testing) return;
        var proxy = ProxyInput.Text.Trim();
        if (proxy.Length == 0)
        {
            TestStatus.Text = Strings.ConfigProxyEmpty;
            return;
        }
        testing = true;
        TestBtn.IsEnabled = false;
        TestStatus.Text = Strings.ConfigProxyTesting;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var normalized = ProxyUtil.Normalize(proxy);
            var server = ProxyUtil.ProxyServerValue(proxy);
            bool ok;
            if (normalized.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
            {
                // .NET HttpClient 不支持 SOCKS：退化为端口连通性检测
                var uri = new Uri(server);
                ok = await Task.Run(() => TcpReachable(uri.Host, uri.Port, (int)TestTimeout.TotalMilliseconds));
                var ms = sw.ElapsedMilliseconds;
                TestStatus.Text = ok
                    ? $"{Strings.ConfigProxyOk}（{Strings.ConfigProxySocksReachable}，{ms} ms）"
                    : $"{Strings.ConfigProxyFail}{Strings.ConfigProxySocksUnreachable}";
            }
            else
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = BuildWebProxy(server, normalized),
                    UseProxy = true,
                    AllowAutoRedirect = false,
                };
                using var client = new HttpClient(handler) { Timeout = TestTimeout };
                using var cts = new CancellationTokenSource(TestTimeout);
                using var resp = await client.GetAsync(TestUrl, cts.Token);
                ok = (int)resp.StatusCode is 204 or 200;
                var ms = sw.ElapsedMilliseconds;
                TestStatus.Text = ok
                    ? $"{Strings.ConfigProxyOk}（{ms} ms）"
                    : $"{Strings.ConfigProxyFail}HTTP {(int)resp.StatusCode}";
            }
        }
        catch (OperationCanceledException)
        {
            TestStatus.Text = Strings.ConfigProxyTimeout;
        }
        catch (Exception ex)
        {
            // 逐层取 InnerException：HttpClient 常把真实原因（如 407）包在内层
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            TestStatus.Text = Strings.ConfigProxyFail + inner.Message;
        }
        finally
        {
            testing = false;
            TestBtn.IsEnabled = true;
        }
    }

    /// <summary>构建测试用 WebProxy；代理目标与原始地址分离，账密单独设置。</summary>
    private static WebProxy BuildWebProxy(string server, string original)
    {
        var uri = new Uri(server);
        var proxy = new WebProxy(uri.Host, uri.Port);
        var creds = ProxyUtil.ParseProxyCreds(original);
        if (creds != null) proxy.Credentials = new NetworkCredential(creds[0], creds[1]);
        return proxy;
    }

    private static bool TcpReachable(string host, int port, int timeoutMs)
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            var ar = s.BeginConnect(host, port, null, null);
            return ar.AsyncWaitHandle.WaitOne(timeoutMs) && s.Connected;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Clash 端口探测 ----------

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        detecting = true;
        DetectBtn.IsEnabled = false;
        TestStatus.Text = Strings.ConfigProxyDetecting;

        var port = await Task.Run(ProxyUtil.ProbeLocalProxy);
        detecting = false;
        DetectBtn.IsEnabled = true;
        if (port > 0)
        {
            ProxyInput.Text = ProxyUtil.ProxyUrlForPort(port);
            TestStatus.Text = string.Format(Strings.ConfigProxyDetectFound, port);
        }
        else
        {
            TestStatus.Text = Strings.ConfigProxyDetectNone;
        }
    }
}
