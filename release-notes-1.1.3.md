## Nexora v1.1.3

### 安全与认证
- **登录状态校验**：`IsAuthenticated` 现在会检查 access token 是否过期，不再仅凭 token 字符串是否存在判断已登录。
- **会话加密存储**：access token 与 refresh token 使用 Windows DPAPI（CurrentUser）加密后写入 `auth-session.json`，旧版明文会话会在加载时自动迁移。
- **刷新互斥**：refresh token 刷新逻辑增加 `SemaphoreSlim` 单飞锁，避免并发请求同时刷新导致其中一个使用旧 token 失败、甚至清空会话。
- **后台会话保活**：登录后每 30 分钟自动刷新 token，API 401 时支持强制刷新，无需等待 access token 过期。

### 云端订阅
- 拉取云端订阅时始终从 API 获取最新列表，并清理本地已不在云端的订阅地址与节点。
- 应用启动时先尝试恢复会话，再同步云端订阅，避免刷新过期本地地址。

### 其他改进
- 自定义路由规则编辑器增强。
- 应用内更新确认对话框。
- 订阅流量用尽检测与超时展示优化。
- API 请求超时与系统代理相关修复。

### 安装包
- Windows x64 安装包：`Nexora-Setup-1.1.3.exe`
