# 日常版本（macOS）

开发者自己日常使用的版本，与发布版本、开发运行的数据都分开。不随源码包发布。

日常使用的版本由 `scripts/daily.sh` 构建。从 `~/Applications/Mindweave.app` 打开（可拖到程序坞，也能在启动台和聚焦搜索里找到）；构建存放在 `~/Applications/Mindweave-daily/`，数据在 `~/Library/Application Support/Mindweave/claude-auto/`：

```bash
scripts/daily.sh status                    # 当前与上一个版本、数据版本、最近的备份
scripts/daily.sh publish                   # 用 main 的最新提交构建新的日常版本
scripts/daily.sh run                       # 打开日常版本
scripts/daily.sh trial                     # 在日常数据的副本上试运行，不碰真实数据
scripts/daily.sh rollback                  # 换回上一个版本
scripts/daily.sh app                       # 用当前版本重装 Mindweave.app（首次安装或出问题时）
scripts/daily.sh backup                    # 手动备份数据库
```

- `publish` 只接受已合并进 `main` 的提交；日常版本开着时拒绝；换版本前把数据库备份到 `MINDWEAVE_DAILY_BACKUP_DIR`（默认 `~/Documents/Mindweave-daily-backups`，最好指到另一块盘），保留上一个版本，记下提交与数据库版本。
- 新版本会升级数据库时（数据库版本变大），`publish` 先拒绝：先 `trial` 看副本上一切正常，再加 `--accept-upgrade`。应用第一次打开时仍会先做一份核对过的备份再升级。
- `rollback` 只在上一个版本能打开当前数据时才换；数据已被升级时，要先在应用里恢复升级前的备份（之后写的内容会丢），再回退。
- **程序坞图标不会失效。** `Mindweave.app` 的位置永远不变；`publish`、`rollback` 只替换其中的 `Contents` 文件夹（先放好新的再改名替换），应用文件夹本身不变，所以钉在程序坞上的图标始终打开当前版本。复制用 APFS 克隆，不多占磁盘。不要把 `Mindweave-daily/` 里的任何东西拖到程序坞：程序坞会把 `current` 这样的符号链接解析成某个带日期的版本，之后的 `publish` 会删掉它（2026-11-17 就因此坏过一次：图标停在 11-13 的 v11 版本，打不开 v13 的数据）。
- 包标识固定为 `local.mindweave.daily`，不要改；没有自己的图标，用系统默认的应用图标。
