# Windows 1.0.4-rc.1 修复与测试记录

## 状态

- 修复与测试完成后，用户授权作为 GitHub 测试版发布；使用独立分支 `release/v1.0.4-rc.1` 和 Pre-release，不合入 `main`。
- 修改基于 `f0070f5` / v1.0.3，测试版标识：`1.0.4-rc.1`。
- [测试版发布页](https://github.com/seekstars306/muse-launcher-windows/releases/tag/v1.0.4-rc.1)，保留 Windows 真实控件尚未执行验收的说明。
- 原 v1.0.0–v1.0.3 压缩包与用户账号数据未修改。全部测试使用合成数据和本机模拟代理，不读取用户实际代理密码或 Cookie。

## 已实现

1. **真实逐账号代理桥接**：独立 loopback 随机端口与随机本机会话凭证；只连接指定上游，不进行 direct fallback。支持 HTTP Basic/CONNECT、HTTPS 上游 TLS、SOCKS5/RFC1929 认证，以及普通 HTTP、WebSocket、分块数据。删除错误的 `--proxy-auth-scheme=basic` 假设。
2. **统一代理解析和測活**：百分号编码凭证正确解码，严格拒绝无效地址、路径/query/fragment/控制字符/不支持协议。上游服务商账密不交给浏览器网站认证。测活用同一桥接，HTTPS 不降级，SOCKS5 不只测端口。
3. **配置和环境更新**：编辑采用独立草稿，保存成功才发布。网关、账密、country/session 用户名或绕过选项变化都会更新环境。关闭旧浏览器及桥接后等待旧进程退出，不清空账号目录或强制杀进程。
4. **会话所有权与 LRU**：创建/销毁受限，淘汰返回实际资源；限制包含正在初始化/销毁的资源。切换代次、旧事件身份校验、删除/关闭清理和创建失败重试，避免选中 B 却重载 A。
5. **界面状态**：WebView/Splash/错误遮罩由统一状态控制；旧切换回调不能关闭新过渡，Toast 仅订阅一个计时器处理器。
6. **文件和 ID 安全**：独立 `.bak`、损坏留底、原子候选写入、失败向上报告；完整文件损坏无备份时停止修改。正数/唯一 ID、陈旧计数修复、分配避让已有 `acct_N`、溢出检查，long 不再转 double。
7. **登录探测和启动参数**：独立不池化 SQLite 快照，精确域/路径/有效期，session 不可确认时未知；不读取 Cookie 值。命名管道按用户隔离、限长度 JSON 传参、UI线程分派、文本参数不被转换或换行注入。
8. **文档与测试工具**：README纠正代理和“全流量防泄漏”等承诺，提供真实 Windows WebView2 冒烟测试项目及自包含测试包。

## 测试结果

| 验证 | 结果 |
|---|---|
| Release 全部 xUnit（最终） | **245/245，通过；0 失败、0 跳过** |
| Release 重复运行 | **245/245，通过** |
| Debug 全部 xUnit（最终） | **245/245，通过；0 失败、0 跳过** |
| Release 解决方案构建，包含 WindowsSmoke | **0 警告、0 错误** |
| Debug 解决方案构建，包含 WindowsSmoke | **0 警告、0 错误** |
| win-x64 应用与 WindowsSmoke 自包含 publish | 成功 |
| 两个 ZIP `unzip -tq` | 无压缩数据错误 |
| `git diff --check` | 通过 |

245项分类：
- 代理协议与安全：HTTP 13、SOCKS5 9、安全21、配置解析43、解析回归11、旧ProxyUtil22；合计119。
- 会话协调16、LRU12、界面状态5。
- 账号编解码7、账号存储11、文件存储17、持久化回归9、修复集成9、Cookie15。
- 启动参数10、参数回归15。

### 红测到绿测

- 代理旧缺陷基线11项全部失败，修复后相关解析及协议测试通过。
- LRU旧路径复现4项中3失败；旧生命周期基线20项中18失败。正式协调/资源释放与回归现均通过。
- 首次全量集成236项中229通过、7失败；失败包含旧API/损坏静默清空契约、旧双Remove测试调用和损坏留底文件名。调整为新安全契约后236/236，再补9个集成回归，最终245/245。

## Windows 验证边界（未运行）

当前机器为 Linux，无 Windows WPF/WebView2 运行环境。**WindowsSmoke已编译，但未运行；Windows界面、真实WebView2认证事件、旧进程退出时序和对第三方代理供应商的实际出口均不能宣称已完成验收。**

在 Windows x64 安装 WebView2 Runtime 后，解压测试包，于 PowerShell 运行：

```powershell
.\windows-smoke-win-x64\MuseLauncher.WindowsSmoke.exe
```

只使用新临时目录和本机合成HTTP上游；检查：侧栏展开/收起、全屏、5账号真实LRU控件释放、localStorage隔离与重新加载恢复、同网关换账密重建、创建失败后的重试及关闭清理。成功退出码0，失败1，输出用于排查，无需上传真实账号目录。

## 本地产物

- 应用：`publish/MuseLauncher-win-x64-v1.0.4-rc.1.zip`（约69MB）
- Windows真实控件测试：`publish/MuseLauncher-WindowsSmoke-v1.0.4-rc.1.zip`（约69MB）
- 最终Release TRX：`tests/MuseLauncher.Core.Tests/TestResults/repair-test.trx`（未加入版本控制）

更新前退出旧应用并备份 `%APPDATA%\MuseLauncher`、`%LOCALAPPDATA%\MuseLauncher`。候选版兼容已有字段及账号目录，但网站会话可过期/撤销，不能保证永不掉登录。
