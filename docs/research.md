# 官方二进制调查

调查日期：2026-10-20。上游：龙腾猫跃 / [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)。本项目为独立第三方二进制工具，不是 PCL 官方或 PCL CE，不重新编译启动器。

官方 release 2.99.0.0 无附件，EXE 来自官方 Git 仓库随版本提交的 `最新正式版.zip`。下载脚本固定官方提交，SHA-256 只校验下载来源/测试输入，不作为补丁匹配规则。

| 版本 | 官方提交 | EXE SHA-256 |
| --- | --- | --- |
| 2.14.0.0 | `5ef81e490ae46b1100d61e33d0183094c7a80ca8` | `5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36` |
| 2.99.0.0 | `6b9a50df911c9d68b69b24c979efba477fb14b67` | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |

限制引入提交：[`e4b0489`](https://github.com/Meloong-Git/PCL/commit/e4b0489a22cfd4bc57a18fc1e2ac66d5c18e7fc1)，正式版提交 `6b9a50d`。对比基线 `5ef81e4`。

使用 dnlib 4.5.0 直接读取两个实际 PE/CLR 程序集，并使用 ILSpy 9.1 反编译。两个 EXE 均为 PE32 / ILOnly。观察到编译器常量折叠与不可达分支删除：

- `ModModpack.ModpackInstall()`：41 条 IL 变成 7 条；闭包实例构造仍在，文件对话框和启动后台线程的代码被删除，闭包类型及其工作函数仍存在。
- `ModModpack.ModpackInstall(string,string,string)`：330 条变成 254 条；压缩包识别代码仍在，七种安装分派及正常返回尾部被删除，替换为 `ldstr / newobj Exception / throw`。外层 finally 的 HandlerEnd 随之变成方法末尾。
- `PageDownloadInstall.LoaderInit()`：18 条变成 30 条；真实 `DlClientListLoader` 被新的异常 LoaderTask 替代。VB 生成的适配器编号发生变化，不能按原 lambda 名直接移植。
- `PageDownloadModify.LoadInstanceFinished(...)`：325 条，Minecraft 卡片 `set_Visibility` 参数从 0 (Visible) 折叠成 2 (Collapsed)。
- 资源版本列表中的整合包 Install_Click 委托及条件分支被删除。源码文件叫 PageDownloadCompDetail，但实际二进制类型为 `PCL.PageDownloadResourceDetail`；匹配必须以真实程序集为准。
- `FormMain` 增加对限制消息的捕获。恢复产生异常的入口后，无需修改这个通用异常处理器，更不需要触碰登录/授权逻辑。

不能通过修改 `ModSecret.RestrictMcDownload` 常量恢复以上已删除的 IL。补丁必须恢复缺失方法体，并根据目标程序集重新绑定引用。
