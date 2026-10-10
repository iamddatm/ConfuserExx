# ConfuserExx 框架支持现状与升级路线分析

> **元信息**
> - 生成日期:2026-10-10
> - 对应代码:`x` 分支 @ `aa329fb`(工作区干净)
> - 语言:中文。本仓库上游文档(`docs/*.md`)为英文,这份是个人分析文档,若要提上游需另译。
> - 证据约定:`路径:行号` 为仓库内代码证据;标「需验证」的条目为静态阅读结论,未做运行时验证;外部时间线来自 Microsoft 官方发布说明与 .NET 博客。
> - 行号基于上述提交;代码改动后行号可能漂移,以符号名(类型/方法)为准。

---

## 0. 结论速览

1. **混淆器自身**跑在 .NET 10(CLI 跨平台 / GUI 仅 Windows),而**被混淆目标的框架适配**几乎全部发生在 Core 层,不靠改注入实现。
2. 升级到 **.NET 11 的"支持目标"成本很低**——运行时探测是按目录枚举 `dotnet/shared/**`,装上新运行时即可工作;真正的工作量在**保护矩阵的实测验证**。
3. 但 .NET 11 有一个**结构性变化必须提前评估**:Runtime Async 让 async 方法不再生成状态机,而当前 System.Text.Json 分析器正依赖状态机元数据工作。
4. 有两条"现在就该做"的低风险改动:把 `Compress` 的 `compat` 对现代 .NET 默认置位;把框架判定从 `IsClr40` 换成读 `TargetFrameworkAttribute`。
5. `.NET 11` 是 STS(支持到 2028-11-09),`.NET 10` 是 LTS(支持到 2028-11-14),两者同月 EOL——生产基线不必抢 11。

---

## 1. 两条必须分开的"框架轴"

看这套代码最容易混的一点,后续所有结论都建立在此之上。

### 轴 A:混淆器自己跑在什么框架上(可随时改,风险低)

| 组件 | TFM | 证据 |
|---|---|---|
| GUI `ConfuserEx` | `net10.0-windows`(WPF,仅 Windows) | `ConfuserEx/ConfuserEx.csproj:7` |
| CLI `Confuser.CLI` | `net10.0`(跨平台) | `Confuser.CLI/Confuser.CLI.csproj:7` |
| Core / Protections / Renamer / DynCipher | `net48;netstandard2.0` | `Confuser.Core/Confuser.Core.csproj:7` 等 |
| MSBuild Tasks | `net48;netstandard2.0` | `Confuser.MSBuild.Tasks/Confuser.MSBuild.Tasks.csproj:7` |
| Analyzers(CX001–CX004) | `netstandard2.0` | `Confuser.Analyzers/Confuser.Analyzers.csproj:7` |
| Runtime(注入用) | **`net20`** | `Confuser.Runtime/Confuser.Runtime.csproj:7` |

SDK 由 `global.json` 钉住(`10.0.201` + `rollForward: latestMinor` + `allowPrerelease: true`)。

### 轴 B:被混淆程序集是什么框架(必须能处理,风险高)

- 注入的 `Confuser.Runtime.dll` **只有 net20 一个构建**,通过 dnlib 加载、按类型全名注入,**不是 Roslyn 现场编译**(`Confuser.Core/Services/RuntimeService.cs:18-40`);`TryToLoadPdbFromDisk = true` 以支持符号注入。
- 该 DLL 必须与混淆器可执行文件同目录,靠构建期 Copy 目标保证,而非 ProjectReference:`Confuser.CLI.csproj:30-36`、`ConfuserEx.csproj:38-44`、`Tests/Directory.Build.props:28-37`。
- 现代框架的适配靠 Core 层三件事:
  1. **corlib 重映射**——net20 代码引用的是 `mscorlib 2.0.0.0`,注入时按目标已有 AssemblyRef 重定向到 `System.Runtime` / `netstandard` / `mscorlib 4.0.0.0`(`Confuser.Core/Helpers/InjectHelper.cs:291-316`)。
  2. **运行时程序集探测**——`.NET Core` 路径解析与 `dotnet/shared/**` 全量探测(`Confuser.Core/DotNetCorePathResolver.cs:17-30,141-161`)。
  3. **netstandard 门面处理**(`Confuser.Core/ConfuserAssemblyResolver.cs:47-96`)。

---

## 2. 框架识别:引擎目前靠什么判断

| 机制 | 位置 | 说明 |
|---|---|---|
| `IsClr40` | 元数据 `RuntimeVersion` 以 `v4` 开头 | **唯一的框架旗标**;现代 .NET 全部命中 v4 分支,却仍按 mscorlib 4.0 语义解析 |
| `.runtimeconfig.json` 存在性 | `Confuser.Core/ConfuserEngine.cs:483-488` | 区分现代 .NET 与 .NET Framework 的唯一运行时标志 |
| `FullyQualifiedName[0] == '<'` | `AntiTamper.Normal.cs:13`、`AntiDump.cs:24,108`、`RuntimeService.cs:24` | 区分 Mapped / Flat(单文件、`Assembly.Load(byte[])`、内存加载) |
| 注册表 `NDP` 枚举 | `ConfuserEngine.cs:531-579` | 仅 Windows 且仅 .NET Framework;硬编码 `"v4.5 "` 标签(:577) |
| `TargetFrameworkAttribute` | `Diagnostics/DiagnosticReport.cs:205-231` | 仅用于诊断报告输出,未参与决策 |

> **改进点**:`IsClr40` 是近似判断,建议改为读 `TargetFrameworkAttribute` 得到真实 TFM,再决定 WPF KnownThings、junk code 等分支。

---

## 3. 逐版本:每个版本各欠了什么

### 3.1 .NET Framework 2.0 – 4.8

| 事项 | 现状 / 证据 |
|---|---|
| 引用程序集 | net20/35/40 用 `Microsoft.NETFramework.ReferenceAssemblies` 1.0.2;net48 依赖本机 4.8 Developer Pack |
| 语言版本 | net20–net48 一律 C# 7.3(SDK 对 .NET Framework 的默认值) |
| 绑定重定向 | 仅 net48 开 `AutoGenerateBindingRedirects`(`ConfuserEx.Common.props:5`) |
| 强名称 | 完整支持(Enhanced StrongName / delay-sign,`ConfuserEngine.cs:350-369`) |
| 混合模式 | `!IsILOnly \|\| VTableFixups != null` → `RequestNative(true)`,走 `NativeEraser` 擦原始 IL(`ConfuserEngine.cs:347-348`) |
| 反调试 antinet | **仅 .NET Framework 2.0–4.5**,按 `Environment.Version.Major` 取偏移;CoreCLR 的 CLR 模块名是 `coreclr`(不是 `clr`/`mscorwks`),找不到 → `Environment.FailFast`(`Confuser.Runtime/antinet/AntiManagedDebugger.cs:14-16`、`AntiDebug.Antinet.cs:6-8`、`antinet/PEInfo.cs:44-48`) |

### 3.2 .NET Core 3.x / .NET 5+

| 事项 | 现状 / 证据 |
|---|---|
| 程序集解析 | 新增 `.runtimeconfig.json` 探测 + `dotnet/shared/**` 全量探测,以解决"net10 应用引用 net8 库"这类跨版本依赖 |
| netstandard 门面 | 门面扁平化:把转发类型搬进 netstandard,使模块只引用 netstandard |
| 单文件 / 内存加载 | 靠 `FullyQualifiedName[0] == '<'` 走 Flat 偏移分支 |

### 3.3 .NET 6 / .NET 8

| 事项 | 现状 / 证据 |
|---|---|
| **打包(Compress)默认走错路径** | 默认 `compat=false` 使用旧的 netmodule 模型:主模块改名 `koi`、`Kind=NetModule`、File+ExportedType 表、`Assembly.LoadModule`(`Confuser.Protections/Compress/ExtractPhase.cs:43-55`、`Confuser.Runtime/Compressor.cs:51-60`)。CoreCLR 不支持从 `byte[]` 加载 netmodule,**现代 .NET 必须 `compat=true`**(`Compressor.cs:212`、`Compressor.Compat.cs:47-68`) |
| 混淆产物可运行性 | 必须把 `.runtimeconfig.json` / `.deps.json` / apphost `.exe` 拷到输出目录,否则产物跑不起来(`ConfuserEngine.cs:470-508`,项目级开关 `CopyRuntimeFiles`,默认 true) |
| 可测试性 | `Tests/Shared/*SelfTestHost.cs` 让混淆产物以 `--selftest` 离屏启动 UI 并自校验退出码 42 |

> **遗留问题**:`compat` 默认值仍是 `false`,现代 .NET 用户不加参数必踩。

### 3.4 .NET 9 —— 影响最大的一次(CLR 元数据校验变严)

| 事项 | 修复 |
|---|---|
| **`<Module>` 全局类型不能再挂嵌套类型**:CoreCLR 校验变严后,原有做法导致混淆产物加载失败 | 把 helper 类型从 `<Module>` 提升为顶层类型:`InjectHelper.cs:238-250`(`IsGlobalModuleType` 时把嵌套类型移出并改可见性);`Compressor.cs` 的 `DataType` 同处理(私有嵌套 → `NotPublic` 顶层) |
| 资源保护的解析链 | 现代 .NET 需 `ResourceResolve` 而非仅 `AssemblyResolve`:补 handler 并按 `GetManifestResourceNames()` 匹配(`Confuser.Runtime/Resource.cs:39-46`) |

**升级模型的关键洞察**:新版本出的问题往往不在"工具功能",而在"CLR 变严导致原本能加载的 IL 不能加载了"。

### 3.5 .NET 9 / 10 —— netstandard 门面的两种形态

- 经典门面转发到 `mscorlib`(类型真身在那)→ 扁平化正确。
- CoreCLR 发布产物的 netstandard **只转发到 `System.Runtime`**,再转到 `System.Private.CoreLib`;此时扁平化会**把 `System.Object` 藏起来**,后续分析全崩 → 增加 `DefinesSystemObject` 判定后跳过(`ConfuserAssemblyResolver.cs:47-96`)。

### 3.6 .NET 10 —— 工具自身的一次整体搬迁

| 项 | 证据 |
|---|---|
| `ConfuserEx` / `Confuser.CLI` 从 .NET Framework 4.6.1 迁到 `net10.0` / `net10.0-windows` | 提交 `4755b98`(#71) |
| CI 换 `windows-2025` runner、CodeQL 适配 MSBuild 18 / .NET 10 | 提交 `fb9f8b5` |
| SDK 钉 `10.0.201` + `rollForward: latestMinor` + `allowPrerelease: true` | `global.json` |

---

## 4. 每加一个新框架版本:固定动作清单

### 4.1 必改(机械项,漏一个就编译/测试不过)

1. `Tests/CrossFramework/{Console,WinForms,WPF,Library}/NetN/` 各加一份样本工程(csproj + 入口);现代 .NET 记得链接对应 `Tests/Shared/*SelfTestHost.cs`。
2. `Tests/CrossFramework/Test/CrossFramework.Test.csproj:11-37` 加对应 `<ProjectReference>`。
3. 四个测试类各加一个 `[Fact]` + `[Trait("TFM","netN.0")]`(`ConsoleFrameworkTest.cs`、`WinFormsFrameworkTest.cs`、`WpfFrameworkTest.cs`、`LibraryFrameworkTest.cs`);现代 .NET 传 `.dll`(apphost `.exe` 不是托管程序集)。
4. `.github/workflows/test.yml:42-44` 的 `dotnet-version` 追加该运行时(跑混淆产物要用)。
5. 若要同步升级工具自身:`ConfuserEx.csproj:7`、`Confuser.CLI.csproj:7`、`global.json`,以及 `ci.yml` / `release.yml` / `scripts/local-ci.sh` 里的 `net10.0*` 产物路径。

### 4.2 必查(实现点,按风险排序)

| # | 检查点 | 位置 |
|---|---|---|
| 1 | helper 类型是否仍在 `<Module>` 下(别回退 #101 的修复) | `InjectHelper.cs:238-250` |
| 2 | AntiTamper 的 PE 节表 + kernel32 方案是否仍成立;与 ReadyToRun / NativeAOT / 单文件 / 裁剪的冲突 | `AntiTamper/NormalMode.cs`、`AntiMode.cs`、`AntiTamperProtection.cs:64-70` |
| 3 | Compress `compat` 默认值 | `ExtractPhase.cs:43` |
| 4 | x86 原生化三处(清 `ILOnly`、注入 Windows x86/x64 机器码 → ARM64 / 非 Windows 无效) | `Constants/InjectPhase.cs:54-58`、`ControlFlowPhase.cs:48-49`、`ReferenceProxyPhase.cs:71-76` |
| 5 | `IsClr40` 近似判断的影响面(junk code 对现代 .NET 全跳过) | `ControlFlow/CFContext.cs:35,77-79`、`TypeScrambler/.../FieldDefInstructionRewriter.cs:16`、`BAML/BAMLAnalyzer.cs:103-108` |
| 6 | WPF KnownThings 硬编码的版本串(`mscorlib 4.0.0.0`、`PresentationFramework 4.0.0.0`) | `BAML/KnownThingsv4.cs:45-53`、`KnownThingsv3.cs:45-52` |
| 7 | Renamer 的框架分析器触发条件(按 AssemblyRef 名判定) | `AnalyzePhase.cs:83-110` |
| 8 | `SystemTextJsonAnalyzer` 的覆盖边界 | `Analyzers/SystemTextJsonAnalyzer.cs:27-30,126-147` |

### 4.3 已知文档漂移(顺手修)

- `docs/protections.md:81` 仍写 "AntiTamper … via a JIT hook"——JIT 模式已删除(提交 `2c025f1`),现在只有 `Normal` / `Anti`(`AntiTamperProtection.cs:127-130`)。
- `docs/protections.md:226-228` 声称 "System.Text.Json … is not detected automatically"——实际已有 `SystemTextJsonAnalyzer`(有边界)。
- `build.sh:29-47` 仍校验 `net461` 输出路径,与现状(`net10.0`)不一致。

---

## 5. 升级到 .NET 11

### 5.1 关键事实(截至 2026-10-10)

| 版本 | 类型 | 发布 | 支持终止 | 语言 |
|---|---|---|---|---|
| .NET 8 | LTS | — | **2026-11-10** | — |
| .NET 9 | STS | — | **2026-11-10** | — |
| .NET 10 | LTS | 2025-11-11 | 2028-11-14 | C# 14 |
| **.NET 11** | **STS** | **2026-11-10** | 2028-11-09 | **C# 15** |

要点:
- **`.NET 11` RC1 已于 2026-09-08 发布并支持 go-live**——现在起分支验证是合适时机。
- `.NET 8` / `.NET 9` 的 EOL 与 `.NET 11` GA **同一天**,升级窗口拥挤。
- `.NET 11` 是 STS,`.NET 10` 是 LTS,两者同月 EOL——**生产基线仍应留在 net10**。

### 5.2 判断:支持"混淆 net11 产物"几乎不用改代码

`DotNetCorePathResolver.ProbeAllInstalledRuntimes` 是**按目录枚举 `dotnet/shared/**` 下所有已安装运行时**,不认版本白名单(`DotNetCorePathResolver.cs:141-161`)。因此只要机器上装了 .NET 11 运行时,工具就能解析 net11 程序集的引用、注入、改名。

**轴 B 的支持成本 ≈ 装运行时 + 加测试样本 + 逐个保护做矩阵验证。**

真正可能出问题的是"CLR 变严"那类(如 §3.4 的 net9)。

### 5.3 风险表(按需要动手的顺序)

| # | 项 | 为什么 | 要做什么 |
|---|---|---|---|
| 1 | **Runtime Async** | .NET 11 起 async 方法**不再生成状态机**,改由运行时挂起/恢复,IL 里是 `AsyncHelpers.Await(...)`;RC1 起运行时库自身也以 runtime-async 编译。而 `SystemTextJsonAnalyzer` 靠 `[AsyncStateMachine]` 把状态机方法归还原方法(`SystemTextJsonAnalyzer.cs:126-147`)——对 runtime-async 编译的用户程序集,这条链会**空转漏保**,序列化契约可能被改坏;Renamer 里其余状态机假设同样要复核 | 先确认 runtime-async 方法在元数据上的标记方式;分析器按新标记识别或退化为"方法本体可见的调用"分析;补 runtime-async 回归测试 |
| 2 | **C# 15 union types + STJ 的 union / 闭合类型多态** | .NET 11 的 STJ 新增 union 与闭合类型多态支持;新类型形态进入序列化契约 | 扩 `AnalyzePhase` / STJ 分析器的类型识别,确保 union 成员名被保留 |
| 3 | **AntiTamper 与 R2R / 单文件 / AOT** | 官方继续投入 NativeAOT 并**更新了最低硬件要求**;AntiTamper 是 PE 节表 + kernel32 方案,对 R2R/AOT/单文件无任何处理 | 至少在文档写明"与 R2R/AOT 互斥";核对混淆产物在最低硬件 / ARM64 上的行为 |
| 4 | **Compress 默认 `compat=false`** | 老 netmodule 路径在 CoreCLR 上不成立,net11 不会变好 | 把现代 .NET 的默认改为 `compat=true`(改动小、收益明确,建议现在做) |
| 5 | **dnlib 4.5.0 能否读 net11 元数据** | 若 C# 15 引入新元数据/标记(union、extension members 一类新形态),需新版 dnlib 正确解析 | 用 net11 RC1 编译样本跑通读/写链路;必要时升 dnlib |
| 6 | **WPF on .NET 11** | `KnownThingsv4.cs:45-53` 硬编码 `PresentationFramework 4.0.0.0` / `mscorlib 4.0.0.0`,`ResolveThrow` 按名字+版本精确解析 | 核对 net11-windows 的 WPF 程序集解析是否命中 |
| 7 | **`IsClr40` 单一旗标** | 现代 .NET 全落 v4 分支 | 中期待办:改读 `TargetFrameworkAttribute` |
| 8 | **C# 14 extension members(net10 起)** | 编译期生成 `<Extension>$` 嵌套类型,是新的类型形态 | **需验证**:实测改名/分析是否受影响(仓库内未找到对应处理) |

### 5.4 落地步骤

**阶段一:现在 → GA(用 RC1 起分支)**
1. 装 11.0 RC1 SDK + 运行时。**`global.json` 必须显式改版本号**——`rollForward: latestMinor` 只在 10.x 内滚动,不会跨 major 到 11;`allowPrerelease: true` 已就绪。
2. 工具自身 TFM:`Confuser.CLI.csproj:7` → `net11.0`,`ConfuserEx.csproj:7` → `net11.0-windows`。**Core / Protections / Renamer 保持 `net48;netstandard2.0`、Runtime 保持 `net20`、MSBuild.Tasks 保持 `netstandard2.0` 不动**——为跟版本抬高库的 TFM 只会拉高工具自身运行要求,没有收益。
3. 按 §4.1 加 `Tests/CrossFramework/**/Net11` 样本 + `[Fact]` + CI 的 `dotnet-version: 11.0.x`。
4. 按 §5.3 逐条过,重点实测四个组合:`AntiTamper`、`Compress`、`STJ + async`、WPF(现有 selftest 基建可直接复用)。
5. 全量测试跑一遍。

**阶段二:GA 之后**
- 切稳定 SDK;同步 `ci.yml` / `release.yml` / `scripts/local-ci.sh` 的产物路径。
- 若把工具自身基线换到 net11,记得同步 `README` 的运行要求与 `docs/building.md` 的 VS / Developer Pack 要求。

**阶段三:长期**
- 处理 §5.3 #4、#7 两项低风险改造。
- 建议把"框架支持矩阵"做成 CI 的显式矩阵(样本工程 × 保护组合 × 运行时版本),把 §4.2 的检查点变成自动化断言,而不是每次升级靠人工过一遍。

---

## 6. 仓库内硬编码清单(升级时全文搜索这些)

| 硬编码内容 | 位置 | 影响 |
|---|---|---|
| `net10.0` / `net10.0-windows` | `Confuser.CLI.csproj:7`、`ConfuserEx.csproj:7`、CI 脚本产物路径 | 工具自身 TFM |
| `bin\$(Configuration)\net20\Confuser.Runtime.dll` | `Confuser.CLI.csproj:31`、`ConfuserEx.csproj:39`、`Tests/Directory.Build.props` | 运行时拷贝路径,改 Runtime TFM 时三处同改 |
| `"shared"`、`DOTNET_ROOT`、`%ProgramFiles%\dotnet`、`/usr/share/dotnet` | `DotNetCorePathResolver.cs:70,113-134,148` | dotnet 布局探测 |
| `.runtimeconfig.json` / `.deps.json` / `.exe` 三件套 | `ConfuserEngine.cs:495` | 不支持 SingleFile 的额外文件 |
| `"v4.5 "` 注册表标签、`NET Framework Setup\NDP` | `ConfuserEngine.cs:531-579` | .NET Framework 版本枚举 |
| `"netstandard"` | `ConfuserAssemblyResolver.cs:47` | 门面处理 |
| `mscorlib 4.0.0.0` / `PresentationFramework 4.0.0.0` 等版本串 | `BAML/KnownThingsv3.cs:45-52`、`KnownThingsv4.cs:45-53` | WPF 解析 |
| `net20` / `net48` / `netstandard2.0` 分散在十余个 csproj | — | 轴 A |

---

## 7. 待验证 / 未决项

1. **Runtime Async 方法的元数据标记方式**(§5.3 #1)——决定 STJ 分析器的改造方案,最优先。
2. **C# 14 extension members 的 `<Extension>$` 类型对改名的影响**(§5.3 #8)。
3. **dnlib 4.5.0 对 net11 程序集的读写**(§5.3 #5)。
4. **AntiTamper 在 net11 + ReadyToRun / 单文件下的实际失败形态**——目前是静态推断"会崩",未实测。
5. **`compat=true` 作为现代 .NET 默认值**是否与既有测试(CompressorWithResx、IncorrectRedirectToGac)全部相容,需跑一遍。
6. §4.2 的检查点目前依赖人工过一遍,建议逐步自动化(§5.4 阶段三)。

---

## 附录 A:测试工程 props 导入静默失效(已在 `aa329fb` 修复)

`Tests/Confuser.UnitTest/Confuser.UnitTest.csproj:3` 写的是:

```xml
<Import Project="..\ConfuserEx.Common.props" Condition="Exists('..\ConfuserEx.Common.props')" />
```

该文件实际在**仓库根**,而从 `Tests/Confuser.UnitTest/` 往上一级是 `Tests/`,`Exists()` 判定为假 → **导入静默失效**,`ConfuserEx.Common.props:5` 的 `LangVersion=latest` 从未生效,net462 腿回落到 SDK 默认的 C# 7.3。

`TestBase.cs:214` 引入 C# 8 的 `using var`(提交 `705aebe`)后,`Confuser.UnitTest` 的 net462 腿编译失败(CS8370),而该工程在 `Confuser2.sln` 内,CI 的构建步骤 `msbuild Confuser2.sln` 随之失败。

修法:在该工程显式声明 `<LangVersion>latest</LangVersion>`(未改导入路径,以免给测试工程引入 SourceLink / 分析器等额外依赖),并加注释说明这个坑。

**推广结论**:`Tests/` 下所有工程对 `ConfuserEx.Common.props` / `.targets` 的一级相对导入都是无效的。若某个测试工程需要 `Common.props` 里的设置(语言版本、程序集信息等),必须**显式声明**,或把路径改为两级 `..\..\`。这也解释了为什么多个 net≤48 样本工程自己写了 `<LangVersion>7.3</LangVersion>`。