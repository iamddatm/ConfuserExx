# AGENTS.md

本文件是本仓库跨工具共享的 AI 指令单一来源(Claude Code 通过 `CLAUDE.md` 里的 `@AGENTS.md` 引用读取)。
ConfuserExx 是 .NET 混淆器(ConfuserEx 的维护分支):用 dnlib 读写目标程序集 IL,按规则注入保护代码。仓库根有 `.codegraph/` 索引,定位/理解代码优先用 codegraph(优先 MCP `codegraph_explore`;Git Bash 里 CLI 须写 `codegraph.cmd`)。

## 常用命令

```bash
# 构建(仅 .NET 工程,不需要 VS C++ 工作负载;会跳过 C++/CLI 测试工程)
dotnet build Confuser2.sln -c Release

# 完整解决方案(需 VS 2025+ / MSBuild 18 + C++ 工作负载,只为 Tests/244_ClrProtection)
msbuild Confuser2.sln -p:Configuration=Release

# 全部测试 / 单个测试工程 / 单个用例
dotnet test Confuser2.sln -c Release
dotnet test Tests/Confuser.CLI.Test/Confuser.CLI.Test.csproj -c Release
dotnet test Tests/Confuser.CLI.Test/Confuser.CLI.Test.csproj --filter "FullyQualifiedName~MyTest"

# 本地 CI(镜像 GitHub Actions:lint → build → test+coverage → package)
./scripts/local-ci.sh                        # 全部;也可只跑 lint|build|test|package
RUN_GUI_TESTS=1 ./scripts/local-ci.sh test   # 含 GUI 冒烟测试(默认跳过,它会弹真窗口)

# 格式 / 静态检查(CI 用同一组命令)
dotnet format whitespace Confuser2.sln --verify-no-changes
dotnet format style      Confuser2.sln --verify-no-changes --severity warn
dotnet format analyzers  Confuser2.sln --verify-no-changes --severity warn

./scripts/clean-build-artifacts.sh   # 清理构建产物
```

反例:不要给 `dotnet test` 传 `--collect:"XPlat Code Coverage"`——它让 Coverlet 就地改写已签名的 `Confuser.*`,破坏强名称,net4x 测试会全部加载失败。覆盖率由 `Tests/Directory.Build.targets` 按 TFM 自动开启(只对 net10 测试工程生效)。

## 架构

- **核心引擎在 `Confuser.Core`。** 入口 `ConfuserEngine.Run(ConfuserParameters)`:解析依赖(Probe)→ 标记(Mark,按规则 pattern + preset + `[Obfuscation]` 决定每个成员应用哪些保护)→ 执行保护管线 → 写回模块。CLI(`Confuser.CLI`)与 WPF GUI(`ConfuserEx`)只是这个引擎的两个前端。
- **保护管线 `ProtectionPipeline`**:按 `PipelineStage` 分阶段,每阶段顺序为「所有 pre-phase → 内置动作(如写模块)→ 所有 post-phase」。`ProtectionPhase` 声明自己的 `Targets` / `ProcessAll`。所有保护(含 Core 自带的 WatermarkingProtection)都通过重写 `ConfuserComponent.PopulatePipeline` 注册 phase。
- **组件发现靠反射**:`PluginDiscovery` 从 `Confuser.Protections`、`Confuser.Renamer`、`Confuser.DynCipher` 三个程序集(以及 `-plugin` 指定的外部 DLL)扫描 `Protection` / `Packer` / `ConfuserComponent` 子类并实例化;要求 **public 无参构造**,新增保护类即自动生效。
- **保护间通信**:`context.Annotations`(往 IDnlibDef 上挂任意数据的注解存储)+ `context.Registry`(服务注册表,见 `CoreComponent.Initialize`:`IRandomService` / `IMarkerService` / `ITraceService` / `IRuntimeService` / `ICompressionService` / `IAPIStore`)。每个 target 的保护参数存于 Annotations,用 `ProtectionParameters.GetParameter` / `SetParameters` 读写;`GetParameters(...) != null` 即「已标记」。
- **注入运行时**:`Confuser.Runtime` 是要注入目标程序集的运行时桩,**只构建 net20 一个版本**,由 `RuntimeService` 从磁盘加载后按类型全名注入(不是 Roslyn 现场编译);因此该 DLL 必须与混淆器同目录,由各 csproj 的 Copy 目标与 `Tests/Directory.Build.props` 保证(改 Runtime TFM 时三处同改)。
- **框架适配全在 Core 层**:corlib 重映射(`Helpers/InjectHelper.cs`)、.NET Core 运行时路径探测(`DotNetCorePathResolver.cs` 枚举 `dotnet/shared/**`)、netstandard 门面扁平化(`ConfuserAssemblyResolver.cs`)。被混淆目标从 .NET Framework 2.0 到 .NET 10+ 都由这一层处理,不必为每个框架改注入实现。
- **加新目标框架版本的固定动作与风险点**:读 `docs/framework-upgrade-analysis.md`(中文,含加 TFM 的机械清单、风险表、硬编码清单)。

## 目标框架

| 项目 | TFM |
|---|---|
| Core / Protections / Renamer / DynCipher | `net48;netstandard2.0` |
| GUI `ConfuserEx` | `net10.0-windows`(WPF,仅 Windows) |
| CLI `Confuser.CLI` | `net10.0` |
| Runtime(注入用) | `net20` |
| Analyzers / MSBuild Tasks | `netstandard2.0` |

SDK 由 `global.json` 钉在 `10.0.201`(`rollForward: latestMinor`,不跨 major);版本号由 Nerdbank.GitVersioning 从 `version.json` 生成。

## 测试基建

- 单元测试:`.Test` 后缀的工程,`dotnet test` 直接跑(测试宿主 / xunit / coverlet 由 `Tests/Directory.Build.props` 统一引入)。
- 集成测试 = 两个工程一对:`Tests/XXX/`(被测样本,约定打印 `START` … 业务输出 … `END`,退出码 **42**)+ `Tests/XXX.Test/`(引用 `Tests/Confuser.UnitTest` 的 `TestBase`,调 `Run(...)`)。`Run` 会真跑混淆、执行产物、逐行校验 stdout 与退出码。
- UI 应用产物用 `--selftest`(离屏显示窗口并自校验,退出 42)/ `--selftest-crash`(触发异常处理)驱动,宿主桩在 `Tests/Shared/*SelfTestHost.cs`。
- 跨框架样本在 `Tests/CrossFramework/{Console,WinForms,WPF,Library}/NetN/`,由 `Tests/CrossFramework/Test/CrossFramework.Test.csproj` 引用,沿用 `*SelfTestHost.cs`。样本含 net20/net35/net40 等旧框架——跑这些用例会真的启动对应产物,需要机器装有该版本的 .NET Framework 运行时(net20 还依赖 2.0 CLR,随 3.5 一起装)。未安装这些旧运行时的环境用 Trait `TFM` 过滤跳过,例如
  `dotnet test Tests/CrossFramework/Test/CrossFramework.Test.csproj --filter "TFM!=net20&TFM!=net35"`(多个条件用 `&` 连接)。
- 命名约定:`方法名_条件_预期结果`。

## 关键约定

- `.editorconfig`:C# 用 **tab** 缩进、LF;`.csproj/.props/.yml` 用 2 空格;`.xaml` 用 4 空格。
- 构建会跑 NetAnalyzers + Roslynator + 自研 `Confuser.Analyzers`(**CX001** 误从宿主运行时导入程序集成员 / **CX002** 未判空的 `ResolveTypeDef` / `ResolveMethodDef` / **CX003** 使用 `...Throw` 解析助手 / **CX004** `GetTypes()` 未处理 `ReflectionTypeLoadException`)。新代码不要用 `ResolveThrow` 系列,改用 `Resolve` + null 判断。
- `Confuser.Runtime` 需保持 net20 能编译的语法(不能随便用新语言特性)。
- 分支模型:功能 PR → `develop`;`develop → main` 是发布闸。CI 只在「PR into `main`」自动运行,`develop` PR 用 `./scripts/local-ci.sh` 验证(或加 `run-ci` 标签触发云 CI)。发布由 `release.yml` 手动 / 每月触发。提交信息格式见 `CONTRIBUTING.md`(`<type>: <描述>`)。

## 文档

面向用户的英文文档:`README.md`、`docs/getting-started.md`、`docs/protections.md`(全部保护及参数)、`docs/cli-reference.md`、`docs/ProjectFormat.md`(`.crproj` 模式表达式与规则继承)、`docs/declarative-obfuscation.md`(`[Obfuscation]` 特性语法)、`docs/building.md`;另有一份中文的 `docs/framework-upgrade-analysis.md`。

已知文档漂移(改到相关代码时顺手修):`build.sh` 仍校验 `net461` 输出路径(现状是 `net10.0`);`docs/protections.md` 的 AntiTamper 仍写 "via a JIT hook"(`jit` 模式已删除,现只有 `normal`/`anti`),并称 System.Text.Json "not detected automatically"(实际已有 `SystemTextJsonAnalyzer`,但有覆盖边界)。