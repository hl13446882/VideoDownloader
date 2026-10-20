# 用户数据目录

安装根目录由真实启动器/主程序的包布局解析。持久用户数据统一使用 `<安装根目录>/userdata`，不依赖工作目录。

- `settings.json`、`license.json`、探测统计和更新状态位于 userdata 根目录。
- `browser` 保存原 WebView2Data，`data` 保存下载数据库。
- `logs`、`subtitles`、`models`、`thumbs`、`recovery`、`updates` 均位于 userdata 中。
- 设置中的 `%USERDATA%` 随安装根目录解析，旧 AppData 路径兼容转换；视频保存目录保持原设置。

首次启动在实例互斥锁取得后、数据库和 WebView2 打开前迁移。旧目录复制到同根临时目录，每个文件比对 SHA-256，成功后才重命名为 userdata。旧数据不删除；已有 userdata 不会被旧数据覆盖。自定义到旧目录以外的数据库或浏览器资料需要显式迁移，程序会报错停止，避免静默打开空资料。

更新器和启动器拒绝 userdata 下的替换/删除路径；发布脚本拒绝包含 userdata 的程序包。卸载程序仅移除安装器管理的程序文件，不删除运行时创建的 userdata。安装目录必须可写。

迁移不解除原有 Windows 用户/机器加密绑定。原目录为迁移时的回退副本，并非持续同步的备份。
