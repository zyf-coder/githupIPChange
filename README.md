# GitHub 自动刷新

单文件桌面程序，自动探测可用 GitHub IP 并写入 hosts，失效自动切换。

## 使用

1. 双击桌面 **「GitHub自动刷新」**，或直接运行  
   `C:\Users\lenovo\Desktop\githup访问IP\GitHub自动刷新.exe`
2. 首次会弹 UAC，点「是」（写 hosts 必须管理员）
3. 需要开机自启：托盘图标右键 → 勾选「开机自动启动」

## 行为

- **关掉窗口** → 隐藏到托盘，后台继续监控
- **托盘双击** → 打开状态面板
- **每 3 分钟** 检查；异常自动换 IP 并提示
- **托盘「退出」** → 真正退出

## 文件

| 文件 | 说明 |
|------|------|
| `GitHub自动刷新.exe` | 主程序（单文件） |
| `GitHubHostsAuto.cs` | 源码，可自行改再编译 |

日志：`%AppData%\GitHubHostsAuto\github-hosts.log`

## 重新编译（可选）

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /nologo /target:winexe /optimize+ ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:System.Net.dll ^
  GitHubHostsAuto.cs
```
