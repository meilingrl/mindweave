# 运行说明（激进轨）

根目录的 README 是 `main` 的共享文本，不在轨道上改；本轨道怎样构建、运行与测试写在这里。

## 需要

- .NET SDK 10（`global.json` 锁定主版本 10、允许更新的特性带）。macOS（Apple 芯片或 Intel）或 Windows 11 x64。
- 一个兼容 OpenAI Chat Completions 的服务地址、模型名与 API 密钥（例如 DeepSeek）。没有也能打开应用、浏览已有内容，只是不能提问。

## 构建与测试

```bash
dotnet test Mindweave.slnx -c Release
```

全量在 Mac 上一两分钟。另有需要显式开启的测试：真实服务（`scripts/real-api.sh test`，只在 macOS，密钥存在钥匙串）、系统凭据、完整规模性能基准（`scripts/perf.sh`）。

## 运行

```bash
dotnet run --project src/Mindweave.Desktop -c Release
```

- **数据目录**：macOS `~/Library/Application Support/Mindweave/claude-auto/`，Windows `%LOCALAPPDATA%\Mindweave\claude-auto\`。目录名里的 `claude-auto` 是轨道标识，所以两条轨道的数据互不干扰。试用时可以另指目录，不碰日常数据：

  ```bash
  MINDWEAVE_DATA_DIR=/tmp/mw-try dotnet run --project src/Mindweave.Desktop -c Release
  ```

- **第一次打开**：先建工作区，再到“设置”填服务地址、模型和 API 密钥。密钥只存进系统凭据（macOS 钥匙串、Windows 凭据管理器），数据库里只有引用；凭据服务不可用时提问会明确失败，不会改存明文。
- **升级**：数据库版本升级前会自动备份到数据目录下的 `backups/`，最多保留 5 份。升级失败时应用显示恢复页，不会自动用备份覆盖。
- **同一时间只能开一个**：第二次启动会把已打开的窗口带到前面。

## Windows 发布

在 Windows 上（或用 `scripts/windows.sh publish` 从 Mac 远程）：

```bash
dotnet publish src/Mindweave.Desktop -c Release -r win-x64 --self-contained -o publish/win-x64
```

产物是自带运行时的文件夹，直接运行其中的 `Mindweave.exe`，不需要另装 .NET。

## 源码包

```bash
scripts/package-source.sh
```

用 `git archive` 打包已提交的文件，所以不含 bin／obj 和运行数据。脚本会检查大小不超过 10 MB、没有构建产物或数据文件、必要文件齐全，并列出内容。

## 文档入口

- 进度、下一步、重要发现：根目录 `TRACK.md`。
- 各阶段设计：`docs/design/s0-…` 到 `s6-…`；实现决定：`docs/design/decisions.md`；各阶段怎样体验、验证了什么：`docs/design/stage-reports.md`。
- 需求与架构以 `main` 上的 `docs/requirements.md`、`docs/architecture.md` 为准。
