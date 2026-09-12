# 开发交接、实施顺序与验收

> [架构入口](architecture.md) · [工具契约](tool-contracts.md) · [解析与索引](parser-and-indexes.md)
> 读者结果：接手开发者能逐阶段完成可验证实现，而不是根据目录图一次生成一堆无法运行的代码。
> 状态：实现已落地并完成当前工作树的锁定 restore、Release 构建、自动化测试、win-x64 发布、官方 MCP 子进程验证和真实 Black Myth: Wukong 探针。验收矩阵仍是持续回归清单；未通过或环境不可得的能力必须按第 11 节明确列出。

## 1. 接手任务书

请把这份任务书和整个 `docs/` 一起交给开发模型，而不是只复制架构总览。

```text
目标：按 docs/architecture.md 及其三个专题文档，实现本机 Windows 的 FModel MCP。
用户主要编写黑神话悟空逻辑 MOD；只读游戏、导出原始包与 JSON、不做运行时注入。

执行：
1. 阅读四份文档，先核对 P0 的本地依赖和真实游戏可读性。
2. 逐阶段实现，每阶段先拿到真实失败/可观察结果，再扩展下一阶段。
3. 所有公开工具遵守 tool-contracts.md，不能随意改名、省掉分页、把错误转成空数组。
4. 挂载、Kismet 和短解压错误的可观察性是前置门禁，不以日志猜测代替结果字段。
5. 任何底层不支持的目标先给出复现、影响和可选方案；不得自行发明成功数据。
6. 用真实黑神话样本闭环，同时保留不依赖商业游戏数据的自动化测试。
7. 完成后给出可运行的 stdio MCP、本地配置方法、测试结果、已知限制和依赖锁定证据。

停止条件：P0 无法解密/解析关键真实样本，或必要 Native 依赖/上游补丁无法验证时，
先报告根因，不继续生成所有工具并声称系统完成。
```

没有要求开发者顺带完成 MOD 本身、安装 UE4SS 或把派生游戏资源提交到 Git。

## 2. 已核实的环境与源码基线

### 2.1 本轮观测

| 项目 | 观测结果 |
|---|---|
| 目标仓库 | `E:/mod/fmodel-mcp`，任务开始只有 `.git`，尚无 HEAD 提交、源码、配置或既有文档 |
| CUE4Parse | `E:/mod/CUE4Parse`，有源码但没有 Git 元数据，不能取得上游 commit |
| FModel | `E:/mod/FModel`，有源码但没有 Git 元数据，只作参考 |
| codegraph | 三个目录都没有 `.codegraph`，本轮直接按文件/符号查证 |
| 目标游戏 | `D:/SteamLibrary/steamapps/common/BlackMythWukong` 存在 |
| Pak 观测 | `b1/Content/Paks` 顶层发现 21 个 `.pak`，0 个 `.utoc`，0 个 `.ucas`；不是递归总量/成功挂载数 |
| usmap | `b1/Binaries/Win64/Mappings.usmap` 存在，1,237,211 字节；未验证版本适配 |
| SDK | 6.0.428 / 8.0.418 / 9.0.316；本轮未安装 .NET 10 |
| CUE4Parse 目标 | 根 `Directory.Build.props` 为 net10.0 |
| 许可证 | CUE4Parse：Apache-2.0；FModel：GPL-3.0 |
| 真实运行 | 未编译/挂载/解密/导出，不得把目录检查当成集成测试 |

用户已提供 AES；开发时从用户会话/本地配置取得，不在提交的示例中写入。不要从 Git 历史或网络另找密钥。

### 2.2 可复算的源码快照指纹

本轮没有可用 Git commit，使用文件树指纹作为证据：

| 源码根 | 文件数 | snapshot SHA256 |
|---|---:|---|
| `E:/mod/CUE4Parse` | 2240 | `79e452e0eda564ae07f5cff17fedc56ca83df5aead609be6c96e10d087a42ddb` |
| `E:/mod/FModel` | 408 | `c7fda608535b5cf16f8d3c37ca7fc75f1bc663ceed953cb23ba57fb527ba6658` |

算法：递归全部文件，排除目录名 `.git,.codegraph,bin,obj,.vs,.idea`；相对路径转 `/`，使用 JavaScript 默认字符串排序；每行 `文件SHA256 + 两空格 + 相对路径 + LF`；对整个 UTF-8 manifest 再做 SHA256。不包含文件时间，包含源码内容和路径。

复算脚本（后续需要保存时放仓库 `.tmp/verify-snapshot.cjs`，先确保 `.tmp/` 被 Git 忽略；运行 `node .tmp/verify-snapshot.cjs E:/mod/CUE4Parse`）：

```javascript
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(process.argv[2]);
const excluded = new Set(['.git', '.codegraph', 'bin', 'obj', '.vs', '.idea']);
function walk(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    if (excluded.has(entry.name)) return [];
    const full = path.join(dir, entry.name);
    if (entry.isSymbolicLink()) throw new Error('Unexpected symbolic link: ' + full);
    return entry.isDirectory() ? walk(full) : [full];
  });
}
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const files = walk(root).map(file => path.relative(root, file).replaceAll('\\', '/')).sort();
const manifest = files.map(file => hash(fs.readFileSync(path.join(root, file))) + '  ' + file + '\n').join('');
console.log(JSON.stringify({ root, files: files.length, sha256: hash(Buffer.from(manifest, 'utf8')) }));
```

这是本轮设计核实指纹，不代表上游版本标签；后续打补丁后指纹应变化，并单独记录基线与补丁，不能重算后偷偷覆盖本轮证据。

### 2.3 源码锚点

下列 `CUE4Parse/...` 相对 `E:/mod/CUE4Parse`；`FModel/...` 相对 `E:/mod/FModel`。以符号定位，不依赖容易漂移的行号。

| ID | 文件与符号 | 支持的事实 |
|---|---|---|
| S01 | `Directory.Build.props` / TargetFramework | 需要 net10.0 |
| S02 | `CUE4Parse/UE4/Versions/EGame.cs` / GAME_BlackMythWukong | 存在黑神话专用预设 |
| S03 | `CUE4Parse/FileProvider/DefaultFileProvider.cs` / 构造函数、Initialize、IterateFiles | 显式 StringComparer、发现/注册 pak 和 utoc |
| S04 | `CUE4Parse/FileProvider/Vfs/AbstractVfsFileProvider.cs` / Mount、SubmitKeysAsync、TryMountReader | 挂载流程、RequiredKeys、吞异常诊断缺口 |
| S05 | `CUE4Parse/UE4/VirtualFileSystem/AbstractVfsReader.cs` / ReadOrder、VerifyReadOrder | `_P` 与版本补丁优先级来自实际 ReadOrder |
| S06 | `CUE4Parse/FileProvider/Vfs/FileProviderDictionary.cs` / TryGetValue、TryGetValues、GetEnumerator、FindPayloads | 按 ReadOrder 搜索、多来源条目和 payload 查找 |
| S07 | `CUE4Parse/FileProvider/AbstractFileProvider.cs` / FixPath、LoadPackage、SavePackage、LoadPackageObject | 包/对象入口、原始导出、路径转换；默认工具不能照搬宽松选择规则 |
| S08 | `CUE4Parse/UE4/Assets/IPackage.cs` / ExportsLazy、GetExports、ResolvePackageIndex | 惰性 export 与元数据解析 |
| S09 | `CUE4Parse/UE4/Assets/Package.cs` / 构造函数、ImportMap、ExportMap | 传统包表与指定 FArchive 构造 |
| S10 | `CUE4Parse/UE4/Assets/AbstractUePackage.cs` / DeserializeObject、ResolvedObject.GetPathName | 对象错误开关、Outer 路径和冒号语义 |
| S11 | `CUE4Parse/Globals.cs` / FatalObjectSerializationErrors | 默认 false，需显式开启 |
| S12 | `CUE4Parse/MappingsProvider/Usmap/FileUsmapTypeMappingsProvider.cs` / 构造函数、Reload | 本地 usmap provider |
| S13 | `CUE4Parse/JsonConverters.cs` / UObjectConverter；`CUE4Parse/UE4/Assets/Exports/UObject.cs` / Properties、WriteJson | UE JSON 转换与属性读取 |
| S14 | `CUE4Parse/UE4/Assets/Exports/Engine/UDataTable.cs` / RowMap、RowStructName、Deserialize | 表格读取；找不到 RowStruct 可能提前返回 |
| S15 | `CUE4Parse/UE4/Assets/Exports/Internationalization/UStringTable.cs` / _cache、TryGet、StringTable | 进程级 tableId 缓存，不按 Provider 隔离 |
| S16 | `CUE4Parse/UE4/Localization/FTextLocalizationResource.cs` / Entries、构造函数；`CUE4Parse/UE4/Objects/Core/i18N/FText.cs` | locres / FText 类型入口；具体 history 提取需 P0/P2 核实 |
| S17 | `CUE4Parse/UE4/Objects/UObject/UClass.cs` / FuncMap、ClassDefaultObject、Interfaces、DecompileBlueprintToPseudo | 蓝图类和默认对象；伪代码方法不作为本版结果 |
| S18 | `CUE4Parse/UE4/Objects/UObject/UStruct.cs` / SuperStruct、Children、ChildProperties、ScriptBytecode、Deserialize | 字节码内层 catch，必须增加完整性状态 |
| S19 | `CUE4Parse/UE4/Objects/UObject/UFunction.cs` / FunctionFlags、EventGraphFunction、Deserialize | 函数元数据 |
| S20 | `CUE4Parse/UE4/Assets/Readers/FKismetArchive.cs` / ReadExpression、Index；`CUE4Parse/UE4/Kismet/KismetExpression.cs` / Token、StatementIndex | 表达式树及两种位置维度 |
| S21 | `CUE4Parse/UE4/Assets/Exports/Engine/UCurveTable.cs` / RowMap、CurveTableMode；`CUE4Parse/UE4/Objects/Engine/Curves/UCurveFloat.cs` | 曲线入口；UCurveFloat 本身是薄类型，FloatCurve 需从 UObject 属性读取 |
| S22 | `CUE4Parse/UE4/AssetRegistry/FAssetRegistryState.cs` / PreallocatedAssetDataBuffers；`CUE4Parse/UE4/AssetRegistry/Objects/FAssetData.cs` / ObjectPath、AssetClass | Registry 可提供线索，不保证覆盖新 MOD |
| S23 | `CUE4Parse/Compression/OodleHelper.cs` / Initialize、DownloadOodleDllAsync、Decompress | 可能自动下载、短解压仅 warning 的缺口 |
| S24 | `CUE4Parse/CUE4Parse.csproj` / Build-Natives、PackageReference | Native 构建警告与依赖 |
| S25 | `FModel/ViewModels/CUE4ParseViewModel.cs` / Provider.ReadScriptData、JsonConvert.SerializeObject | FModel 的 JSON/脚本选项参考，不复制桌面实现 |

公开协议参考（已查阅，URL 对应页面不是本项目锁定的依赖版本）：

- [官方 MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)：官方包与 API 入口。
- [官方 stdio 示例](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/QuickstartWeatherServer/Program.cs)：AddMcpServer、WithStdioServerTransport、WithTools，以及 ILogger stderr 配置。
- [MCP 2025-11-25 Tools 规范](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)：structuredContent、TextContent 兼容、outputSchema、isError。实际握手版本由固定 SDK 与客户端协商，不把 HTTP 规范页版本写死到自制协议实现中。

## 3. P0：依赖与真实解析探针（阻塞所有后续业务）

### 输入

本地源码、用户游戏目录/AES/usmap、四份设计文档。不得以这份文档的合成 Example 资产路径替代真实样本。

### 实施

1. 安装官方 .NET 10 x64 SDK；运行 `dotnet --list-sdks`、`dotnet --info`，确定一个实际可用 patch，用 global.json 固定。此处是后续操作，本轮没有安装。
2. 推荐将已核实的 CUE4Parse 快照纳入 `third_party/CUE4Parse`，记录原始指纹、许可证、来源及日期。这样不依赖用户机器的 `E:/mod/CUE4Parse` 可变目录来构建。
3. 若能证明本地快照对应某个上游 commit，可直接固定该 commit；不能证明就保留“源码快照 + 项目 Git 提交”的可复现身份，不能猜一个相近的标签。
4. 选择并锁定 ModelContextProtocol、Microsoft.Data.Sqlite、Hosting、测试框架等实际兼容版本；启用 NuGet lock file。避免重复引用与上游冲突的 Newtonsoft.Json 版本。
5. 构建 CUE4Parse 和实际需要的 Native 依赖，明确缺失的 DLL。运行时不联网；开发准备阶段若需下载，来源/许可/完整性确认后由维护者显式取得。
6. 在正式测试项目建立 Integration 类别的最小探针，不生成未来业务依赖的临时示例程序。可丢弃的原始输出放 `.tmp/`，先用 Git ignore 规则验证不会跟踪。
7. 使用 GAME_BlackMythWukong、提供的 AES 和 usmap，完成 pak 发现、挂载结果记录、有效目录统计。
8. 找到并记录真实的一张 DataTable、一个 UClass/CDO、一个 UFunction、一个 locres 或 StringTable、一个曲线样本。允许通过包头/Registry 定位，但名字必须来自实际查询。
9. 打出上述对象的类型、关键计数、解析完整性和来源；不是输出几 MB JSON。查证 FText history 提取、函数参数 flags、Curve FloatCurve 属性的真实结构。
10. 实现并测试上游补丁 A/B，验证所选解压路径是否需要 C。用 strict 对象解析开关保留真实失败。
11. 测试当前 Microsoft.Data.Sqlite 的 SQLite：`CREATE VIRTUAL TABLE ... tokenize='trigram case_sensitive 1'`，及中文/短词算法；缺失能力就先修依赖。
12. 最小 MCP 握手验证 structuredContent、TextContent、isError 和 stderr；固定 SDK 的实际注解/schema API 后再扩展工具。

### 完成标准

- 有固定 SDK/依赖与 Native 能力证据，干净 checkout 可以复建。
- 至少一个真实 DataTable 与蓝图函数能读出可解释数据；脚本不支持也必须能精确区分 partial/failed，而不是默认成功。
- 能证明 AES 挂载状态及 usmap 读取状态；错误密钥/缺映射的失败可复现。
- FTS5 trigram、MCP 成功/错误 schema 已用测试验证。
- 若找不到某类真实资源，写明搜索范围与结果，并建立合法合成样本；“暂未发现”不是“功能已通过真实游戏验证”。

## 4. P1：Host、Worker、配置与目录闭环

### 实施

- 建立同 exe 的 Host/Worker 两种模式、IPC 版本/大小限制、stderr 日志和进程监督。
- 先实现 get_status、set_config、remount、list_archives。
- 实现 pak-only 注册、诊断补丁对接、ReadOrder 冲突与 effective file 目录。
- 实现 search_files（无 assetType）、list_directory、list_objects。
- 创建全局单解析 Scheduler 和独立控制面；配置切换按 SESSION_BUSY 契约。
- 原生崩溃/硬超时/内存越界后 Host 保持可响应，Worker 必须退出，不允许并存旧新 Worker。

### 完成标准

客户端可以走 `get_status → 补配置 → list_directory → search_files → list_objects`；切换前后 sessionId 不同，旧游标失效；缺 key、错误 key、路径冲突都有明确结果。日志不污染任何协议 stdout。

## 5. P2：逻辑 MOD 的核心读取

### 实施

- 共享包/对象选择器与来源一致性验证，解决重复 export 名和冒号 Outer 路径。
- get_object 的有界 writer；get_datatable 的 schema、精确行名、结构化 where 和输出分页。
- get_class_info 的 parents/properties/functions，CDO 与继承默认值证据。
- get_function 的参数、flags、eventGraph 和可分页 Kismet；不实现“猜出来的反编译源码”。
- get_string_table 区分 locres、StringTable、FText 身份；get_curve 处理 Rich/Simple/FloatCurve。

### 完成标准

能从真实资源读到行、字段、类、函数、参数/表达式；调用者能分辨字段不存在、值为 null、未序列化默认值、Native 实现和残缺脚本。巨型输出会明确拒绝或按请求分页，不把完整包先转巨型字符串。

## 6. P3：任务、类型与符号索引

### 实施

- SQLite schema、独占缓存锁、指纹、不可变快照、任务状态及取消。
- build_asset_index、list_asset_types、search_symbols；补齐 search_files.assetType。
- 验证 metadata 与 symbols 两阶段覆盖率；Registry 缺失/过时不阻止包表索引。
- 让查询继续读取旧发布快照，任务完成后原子切换；任务取消不破坏旧 head。

### 完成标准

通过名称定位函数/属性，得到的 objectPath 可直接用于 get_function/get_class_info；构建过程中状态可查、取消有效、前台请求不永久饿死。重启相同指纹复用索引，配置/来源变化后拒绝旧索引。

## 7. P4：中文文本与包依赖

### 实施

- build_text_index/search_text，按身份关联当前语言；包含明确短词扫描算法。
- build_reference_index/find_references，包 import 图、Native 目标、未知目标及 incoming 覆盖率。
- 所有任务逐包事务、失败清单、阶段覆盖率、源变更复核、发布与取消清理。

### 完成标准

至少完成一条真实研究链：`中文关键词 → 文本出处 → DataTable 行/字段或已验证身份关联 → 相关包/类`。如果数据中没有可直接关联的业务 ID，输出证据缺口，不推测 ID 补齐演示。

选一组已知 import 关系包，与人工读取表项逐条对照。全库入边完整性只能对已发现并成功扫描的有效包快照成立。

## 8. P5：导出与交付

### 实施

- export_raw/export_json 共用任务、预算、路径守卫、manifest、同卷原子目录提交。
- 处理主包/payload 来源冲突、磁盘不足、取消、崩溃后文件系统/任务恢复。
- 生成无密钥 config 示例和发布说明；本地配置不跟踪。
- Windows x64 apphost 发布；runtime/native 依赖显式携带或说明，不在首次运行时隐式下载。

### 完成标准

- 原始导出 SHA256 与解析器读出的对应逻辑文件字节一致，必要 companion 完整且同版本。
- JSON 可被独立 JSON parser 读取，选定 export 数量一致，没有深度裁剪和静默脚本缺失。
- 通过路径攻击、失败清理、原子提交和恢复测试。
- 最小 stdio 客户端可以从干净启动到完成查询/导出；24 个工具都按契约注册。

## 9. 自动化验收矩阵

`Unit` 使用合成 DTO/包表/流替身，`Integration` 使用合法的小型 UE 样本或本机游戏，`Protocol` 使用真实 MCP 客户端子进程。不得将商业游戏原始资源或导出数据放入可跟踪测试目录。

| ID | 层级 | 场景 | 必须观察到的结果 |
|---|---|---|---|
| T01 | Unit | AES 长度、非法字符、重复零 GUID、null 字段 | 精确 CONFIG_INVALID，活动会话未改变 |
| T02 | Protocol | 空/坏配置启动 | MCP 握手与 get_status 成功，数据工具 NOT_CONFIGURED |
| T03 | Integration | 正确/错误/缺失 key 混合容器 | 每包独立状态，错误 key 与 required GUID 区分，无明文泄漏 |
| T04 | Integration | usmap 缺失、损坏、可读但不匹配 | 区分加载错误、需要映射、真实包解析异常，不空表成功 |
| T05 | Unit | `_P` 高优先级、同级冲突、大小写重复 | 实际 ReadOrder 决定有效文件；同级冲突拒绝读取 |
| T06 | Unit | 主包高优先级、payload 只有旧版本 | PAYLOAD_VERSION_MISMATCH 或必要 payload 缺失诊断，无拼包 |
| T07 | Unit | `/Game`、Provider 路径、uasset/umap 同名、嵌套对象同名 | 规范身份正确；歧义必须显式选择 exportIndex |
| T08 | Unit | 目录 Foo/Foobar、glob * 与 **、regex 灾难回溯 | scope 段正确、regex 超时明确、不换算法 |
| T09 | Protocol | 连续分页、修改过滤条件、索引发布、remount | 无重复/漏页，错误游标和陈旧游标准确区分 |
| T10 | Unit | 巨型对象、fields 转义、深度、单项超预算 | 合法受控 JSON 或预算错误，绝无半个 JSON/假完整 |
| T11 | Unit | DataTable 缺字段、null、大 uint64、rowName 大小写 | 过滤契约正确，无 double 精度损失、无猜测列名 |
| T12 | Unit | CDO 字段缺失/继承覆盖/Native 父类/循环 | 默认值来源和 stopReason 正确，不把缺值填零 |
| T13 | Unit | 函数 Parm/Out/Return/局部变量 | 参数顺序、方向正确，局部变量不冒充参数 |
| T14 | Unit | Kismet 完整/空/中途未知 token/截断嵌套 | typed 完整性正确；partial 前缀每页仍是 SCRIPT_PARSE_FAILED |
| T15 | Unit | Oodle 返回短长度 | DECOMPRESSION_FAILED，不向对象解析传残缺缓冲 |
| T16 | Unit | namespace/key 重复、同中文不同 ID、StringTableEntry | 身份不混淆；相同显示文本不自动 join |
| T17 | Unit | 中文 1/2/3 字、英文大小写、标点引号、emoji | 与规范化子串契约一致，FTS/SQL 运算符不执行 |
| T18 | Unit | Rich/Simple/FloatCurve、重复时间、切线权重 | 原始键序号/模式/切线保留，无默认填零 |
| T19 | Integration | 任务扫描中查询和取消 | 状态/取消及时应答；包间让出；旧 head 保持可用 |
| T20 | Integration | 包级失败与全局失败混合 | 每项错误可查，partial snapshot 不伪装全量 |
| T21 | Integration | Worker 被杀/超时/内存阈值 | Host 不死、不自动重试，任务 interrupted，旧快照不被半成品替换 |
| T22 | Integration | 相同配置重启、修改 key/usmap/pak、相同 tableId 新内容 | 指纹复用/失效正确；进程级 StringTable 旧缓存不会残留 |
| T23 | Unit | 源在 scope 内/目标在外/Native/未知 import | 边方向、去重和 unresolved 正确；incoming 声明真实覆盖 |
| T24 | Integration | FTS 插入中断、head 发布中断、旧快照清理失败 | 事务一致；无孤立可查询 FTS 行；已发布 head 不被回滚 |
| T25 | Integration | 两个 MCP 指向同 cacheRoot/gameId | 第二个 CACHE_LOCKED，无删除他人数据库/锁文件 |
| T26 | Integration | outDir 越界、ADS、UNC、设备名、reparse point | PATH_NOT_ALLOWED，不写游戏目录/任意外部目录 |
| T27 | Integration | 导出取消、磁盘满、进程在目录改名两侧崩溃 | 未提交可清理；已提交用 manifest 恢复，不删除成功成果 |
| T28 | Integration | raw/json 正常导出 | 字节/对象数量/哈希/格式正确；无密钥、无 JSON 截断 |
| T29 | Protocol | stdout 注入普通日志、畸形 IPC、超大消息 | 可诊断协议失败，不吞垃圾继续误解析 |
| T30 | Protocol | 24 工具 schema、isError、输出双份预算 | tools/list 与本契约一致；成功/失败通过 schema 验证 |
| T31 | Integration | 扫描中游戏包被改、新 pak 在会话后加入 | 源变更不发布混合索引；新包经 remount 才进入快照 |
| T32 | Integration | utoc/ucas 与 pak 混合 | unsupported 明确，pak 能力不冒充 IoStore 支持 |

**性能记录而非虚假 SLA**：真实样本记录启动时间、有效文件数、每种索引单位数/耗时、数据库大小、单表读取耗时、峰值 Worker 私有内存、最大工具响应字节数。先测，再考虑优化。不要先为个人工具实现并行解析/多级缓存。

## 10. 最终发布接口与验证命令

以下命令是当前项目的锁定验收命令。开发者若改变项目路径，必须同步此处，不保留失效示例。

```powershell
dotnet --info
dotnet restore src/FModelMcp/FModelMcp.csproj --locked-mode
dotnet test tests/FModelMcp.Tests/FModelMcp.Tests.csproj -c Release --filter "Category!=Integration"
dotnet test tests/FModelMcp.Tests/FModelMcp.Tests.csproj -c Release --filter "Category=Integration"
dotnet publish src/FModelMcp/FModelMcp.csproj -c Release -r win-x64 --self-contained false -o .tmp/publish
```

- Integration 使用 `FMODEL_TEST_CONFIG` 指向被忽略的本地配置；缺配置应标 skipped 并在报告里说明，不能算通过。
- 当前建议 framework-dependent apphost，机器需要 .NET 10 Runtime；若选择 self-contained 是明确打包变更，不改变核心架构。
- 发布产物与测试报告放 `.tmp/` 或本地发布目录，不把游戏导出当构建依赖。
- 官方 MCP SDK 客户端测试以子进程连接发布 exe，完成 initialize、tools/list 和真实工具调用；不要用手写 stdin 一行 JSON 代替完整协议握手。

通用 MCP 客户端配置示意（不同客户端外层键可能不同，后续接入时验证）：

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "E:\\mod\\fmodel-mcp\\.tmp\\publish\\FModelMcp.exe",
      "args": ["--config", "E:\\mod\\fmodel-mcp\\config\\blackmyth.local.json"]
    }
  }
}
```

所有路径绝对化，工作目录变化不影响配置/native/cache/output 定位。`--worker` 不出现在用户 MCP 配置中。

## 11. 交付时必须给出的证据

1. 固定 SDK、NuGet lock file、CUE4Parse 基线、补丁及其测试；Native 组件实际可用性。
2. tools/list 含全部 24 工具；成功、失败、分页、取消、部分覆盖等代表性协议测试。
3. 黑神话真实样本的脱敏路径与结果摘要；未通过的功能明确列出，不用空返回顶替。
4. 完整的中文检索/表格/蓝图研究链以及 raw/json 导出清单校验。
5. 本地配置与启动说明，密钥不进入 Git/日志，游戏目录写入测试为零。
6. 已知限制和成本数据；特别说明 Native/运行时 Hook、IoStore、CDO 默认值和包级引用图的边界。

## 12. 当前实现核验范围

当前工作树已完成以下可复核证据：

- `global.json` 固定 .NET SDK 10.0.400；应用、测试和本地 CUE4Parse 依赖启用 lock file，locked restore 成功。
- Release 构建无警告/错误；官方 ModelContextProtocol `StdioClientTransport` 子进程测试通过，`tools/list` 暴露 24 个工具，schema 闭合，`get_status` 和 `list_archives` 可调用。
- 自动化测试全量通过（14 个，无失败/跳过），包含 structuredContent/TextContent 一致性、失败包络、结果预算、严格数字校验、FTS5 trigram、规范化 SQLite v1 schema、快照/任务恢复、配置路径和 patch 校验、ToolEnvelope outputSchema、真实游标失配/非法游标、异步导出路径拒绝，以及第二会话 `CACHE_LOCKED`。
- framework-dependent `win-x64` 发布成功，发布目录包含 `FModelMcp.exe` 和 `CUE4Parse-Natives.dll`；工具绑定使用单一 Host runtime，不创建重复 DI 容器。
- 在此前被忽略的 `.tmp/real-key.json` 与游戏目录 `b1/Binaries/Win64/Mappings.usmap` 的完整真实样本运行中，22 个 pak 全部挂载，得到 502,986 个物理条目、438,669 个有效文件、161,027 个包、16,025 个冲突；DataTable 5 行、UCurveFloat 2 个曲线键、UClass 7 个 CDO/reflection 属性、UFunction 3 个完整 Kismet 表达式、zh-Hans locres 与中文 `显卡` 搜索均有结果。当前本地游戏目录本轮复查仅剩 1 个 pak，因此本轮新增协议测试改为动态发现 `uasset`，不把历史 22-pak 数字冒充为本轮新鲜探针结果。
- 真实 asset/text/reference 索引分别完成 19/19、14/14、19/19 和 18/18 单元；引用边人工核对了 `ABP_rebirthpoint → SK_empty` 的 resolved packageImport。`显卡` 中文检索返回 locres 的 namespace/key/text/identityLinks；探针同时确认该样本没有可验证的业务 ID 把该 locres key 直接关联到 DataTable 行，因此不臆造关联，证据缺口被保留。raw/JSON 导出均完成，manifest 的 exportId 目录、字节数、SHA-256 和 sourceArchiveId 已由文件重算核对；`../outside` 明确返回 `PATH_NOT_ALLOWED`。
- Worker 畸形 JSON 探针返回 `INVALID_ARGUMENT`，stdout 没有日志污染；游戏目录未写入测试产物，真实 key、缓存、发布物和导出物均位于 `.tmp/` 忽略目录。

仍需明确保留的环境限制：当前机器没有可用于构建/验证 CUE4Parse-Natives Oodle feature 的 Oodle SDK/core 源码；即使显式指定本地 Oodle runtime DLL，发布程序仍正确报告 `NATIVE_DEPENDENCY_MISSING`、`mountState=partial`，不能把 sidecar 或独立 runtime DLL 存在当作 Oodle 已验证。v1 仍只支持 pak，IoStore 只报告 unsupported。T01–T32 中未被上述自动化或真实探针覆盖的故障注入/极限预算场景仍应在后续回归中逐项补充，不能用本节证据替代。
