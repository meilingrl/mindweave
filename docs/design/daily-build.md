# 日常版本（macOS）

开发者自己日常使用的版本，与发布版本、开发运行的数据都分开。不随源码包发布。

日常使用的版本由 `scripts/daily.sh` 构建，放在 `~/Applications/Mindweave-daily/`，数据在 `~/Library/Application Support/Mindweave/claude-auto/`：

```bash
scripts/daily.sh status                    # 当前与上一个版本、数据版本、最近的备份
scripts/daily.sh publish                   # 用 main 的最新提交构建新的日常版本
scripts/daily.sh run                       # 打开日常版本
scripts/daily.sh trial                     # 在日常数据的副本上试运行，不碰真实数据
scripts/daily.sh rollback                  # 换回上一个版本
scripts/daily.sh backup                    # 手动备份数据库
```

- `publish` 只接受已合并进 `main` 的提交；日常版本开着时拒绝；换版本前把数据库备份到 `MINDWEAVE_DAILY_BACKUP_DIR`（默认 `~/Documents/Mindweave-daily-backups`，最好指到另一块盘），保留上一个版本，记下提交与数据库版本。
- 新版本会升级数据库时（数据库版本变大），`publish` 先拒绝：先 `trial` 看副本上一切正常，再加 `--accept-upgrade`。应用第一次打开时仍会先做一份核对过的备份再升级。
- `rollback` 只在上一个版本能打开当前数据时才换；数据已被升级时，要先在应用里恢复升级前的备份（之后写的内容会丢），再回退。
