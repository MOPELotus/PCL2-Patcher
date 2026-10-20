# PCL2 Universal Patcher

使用 C# / .NET 8 / dnlib 的独立第三方 CLI，恢复官方 PCL2 中因 `RestrictMcDownload = True` 被移除的 Minecraft 下载与安装入口。不重新编译 PCL2，不使用 PCL CE，不修改微软登录、身份验证、正版检查或赞助授权逻辑。

**当前实测目标是官方 2.99.0.0。五处功能代码已恢复并通过实际 EXE 的 IL 验证；尚未完成 Windows 图形界面、真实下载/安装和微软登录的端到端操作验证。** 后续版本只有通过结构校验才允许修补，不能保证兼容所有未来版本。详细证据见 [验证报告](docs/validation.md) 和 [二进制调查](docs/research.md)。

## 构建与使用

构建需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。补丁工具可在 Windows / Linux 上处理 EXE；PCL2 本身需要 Windows / .NET Framework 4.8。

```sh
git clone https://github.com/MOPELotus/PCL2-Patcher.git
cd PCL2-Patcher
dotnet build -c Release
```

先下载两个官方原版。`fetch` 从固定的官方 Git 提交下载发行包并校验 EXE 的 SHA-256，不使用第三方启动器或修改版。发现同名文件内容不符时拒绝覆盖。

```sh
dotnet run --project src/PclPatcher.Cli -c Release -- fetch .artifacts/official
```

将以下命令中的 `PCL.exe` 换成要处理的官方启动器路径；路径含空格时加引号。运行 `patch` 前关闭 PCL。`--donor` 指限制前官方 2.14.0.0 EXE，用于提供编译器已经删除的方法体，不能用 2.99.0.0 或 PCL CE 代替。

```sh
# 只检测，不修改文件
dotnet run --project src/PclPatcher.Cli -c Release -- check PCL.exe --donor .artifacts/official/PCL-2.14.0.0.exe

# 原位修补，自动备份与写后校验
dotnet run --project src/PclPatcher.Cli -c Release -- patch PCL.exe --donor .artifacts/official/PCL-2.14.0.0.exe

# 重新核验五个功能点；有恢复记录时同时检查文件与备份 SHA-256
dotnet run --project src/PclPatcher.Cli -c Release -- verify PCL.exe --donor .artifacts/official/PCL-2.14.0.0.exe

# 精确恢复原始字节，不需要 donor
dotnet run --project src/PclPatcher.Cli -c Release -- restore PCL.exe
```

`check` 返回 `restricted`（五处均受限）、`partial`（部分已恢复）或 `restored`（五处均为可识别的无限制实现）。`restored` 也包括原本不受限的兼容官方旧版，不等同于本工具曾处理过该文件。结构不兼容时返回错误，不写入。重复执行 `patch` 不会再次改写已经恢复的 EXE。CLI 输出 JSON，退出码：0 成功；2 结构/完整性错误；3 文件或网络错误；64 参数错误。`check` 成功检测到受限也返回 0。

修补时在 EXE 同目录保留：

- `PCL.exe.pclpatcher.original`：本次输入的完整原文件，不覆盖内容不同的旧备份。
- `PCL.exe.pclpatcher.json`：原始与修补后 SHA-256、版本和修改方法清单。

备份/恢复记录应与 EXE 一起保留。修补采用同目录临时文件、重新加载校验、持久化备份与恢复记录、原子替换。替换后报错会回滚原始字节；强制中断留下的任一已知状态可通过 `restore` 恢复。补丁后被其他程序修改的 EXE、损坏的备份或冲突的恢复记录会被拒绝处理，避免误覆盖。`partial` 输入的备份是该部分修补状态，恢复也回到该状态。

启动器更新后，应检测新的官方 EXE 并使用新文件自己的备份；不要将旧版本的 sidecar 文件混到新版本目录。

## 已恢复的代码

| 功能 | 恢复方式 |
| --- | --- |
| Minecraft 自动安装/版本列表 | 恢复真实 `DlClientListLoader` 与完成回调 |
| 本地整合包安装入口 | 恢复文件选择、捕获闭包和后台安装调用 |
| 整合包类型分派 | 恢复 CurseForge、HMCL、MMC、MCBBS、Modrinth、带启动器压缩包、普通压缩包共七种分派 |
| 资源详情页整合包安装按钮 | 恢复按资源类型选择 Install/Save 委托的分支 |
| 版本修改页 Minecraft 卡片 | 恢复 `Visibility.Visible` |

这些结论指已恢复的 IL 和调用结构，不表示七种格式均已实际安装成功。`RestrictMcDownload` 的常量值及官方更新日志保留原样；不会因为改动一个常量就声称恢复成功。

## 适配原则

先以明确的类型、方法签名缩小范围，再匹配规范化的 IL、调用关系、局部变量、控制流和异常处理区域。哈希仅用于官方文件下载校验和文件完整性，补丁定位不用固定偏移、文件哈希或字符串替换。

对于删除的代码，从官方旧版 EXE 恢复对应方法体，并验证目标保留的代码是否等于旧版施加已知限制后的结构。VB 适配器按签名及方法体语义重绑定，允许编号变化和多个等价适配器。所有恢复引用必须对应目标中已有的类型、字段、方法及兼容依赖。写入后检查所有非目标方法、程序集结构和嵌入资源保持一致。

当前只支持这一组已审查的限制模式；不尝试猜测混淆、未知新增限制或已变化的安装算法。相关代码即使仅发生其他无害变化，也可能被保守拒绝。未来版本需要新的实际 EXE 和新的验证证据，详见 [实现说明](docs/architecture.md)。

## 测试与独立验证

```sh
# 测试输入来自真实官方 EXE；缺少输入时测试失败，不静默跳过
python scripts/fetch-official.py
dotnet test -c Release

# 创建测试输出，然后以独立 Microsoft ILVerify 检查
# 不要在官方输入上做这一演示，否则下次 fetch 会拒绝该文件
# 先自行将原版复制为 PCL-test.exe
dotnet run --project src/PclPatcher.Cli -c Release -- patch PCL-test.exe --donor .artifacts/official/PCL-2.14.0.0.exe
python scripts/verify-il.py PCL-test.exe.pclpatcher.original PCL-test.exe
```

独立验证脚本自动恢复固定版本的 ILVerify/ILSpy 工具，从 EXE 资源提取 CLR 依赖，并使用微软 .NET Framework 4.8 参考程序集。五个恢复方法必须全部通过；全程序集诊断必须与原版一致。官方原版存在的一处 ILVerify 泛型栈诊断会明确保留在报告里。

Windows PowerShell / .NET Framework 下还可在上一步生成依赖后运行：

```powershell
powershell -NoProfile -File scripts/verify-clr.ps1 -Executable PCL-test.exe -ReferenceDirectory .artifacts/ilverify/references
```

这只加载程序集并 JIT 准备五个方法，不操作界面或执行下载。CI 包括 Linux、Windows 测试、ILVerify 和 Windows CLR 准备检查；CI 结果与真实 GUI 验收分别报告。

## 发布独立 CLI

```sh
dotnet publish src/PclPatcher.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .artifacts/publish/win-x64
```

运行输出目录中的 `pcl-patcher.exe`，子命令与上述示例相同，不再需要 `dotnet run --project … --`。不启用 trimming，以保留 dnlib 的完整元数据处理能力。发布目录不包含官方 PCL EXE；使用 `fetch` 获取。

原作者为龙腾猫跃，官方项目：[Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)，[官方主页与赞助入口](https://meloong.com/PCL)。本工具不代表官方，也不提供或分发 Minecraft 游戏文件。官方程序集及其资源的权利和许可属于各自权利人；本仓库不提交官方/修补后的 EXE 或从中反编译的方法体。
