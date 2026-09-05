# CUE4Parse 适配、索引与导出内部设计

> [架构入口](architecture.md) · [工具契约](tool-contracts.md)。本文件规定内部算法；公开参数和默认预算只在对应文件维护。
> `建议新增` 的类型与字段不是上游现成 API。标为“源码事实”的符号已在本地快照查证；真实游戏适用性仍由 P0/P1 验证。

## 1. 启动与挂载适配

### 1.1 Worker 初始化顺序

```text
接收并校验 init / ipcVersion / sessionId
  → 安装 stderr 日志与结构化诊断收集器
  → 获取 cacheRoot/gameId 的独占进程锁
  → 核对本地 Native 组件，禁止自动下载
  → 设置 CUE4Parse 全局解析策略
  → 创建 VersionContainer 和本程序的 PakOnlyFileProvider
  → 加载 usmap（若配置）
  → 扫描 archiveDir；检测不支持容器和目录联接
  → Initialize；Mount 未加密索引；按 GUID SubmitKeys
  → 汇总每个容器的挂载结果，不以返回数字代替诊断
  → 建立有效文件目录、覆盖来源与包身份
  → 计算 sourceFingerprint；验证/加载 SQLite 已发布快照
  → 发布 Worker 状态快照
```

建议初始化形状（是实现指导，不是可独立编译的完整程序；PakOnlyFileProvider 是本程序需要新增的薄封装，其余所用名称是上游入口）：

```csharp
Globals.FatalObjectSerializationErrors = true;
var versions = new VersionContainer(EGame.GAME_BlackMythWukong);
var provider = new PakOnlyFileProvider(
    archiveDirectory,
    versions,
    StringComparer.OrdinalIgnoreCase);
provider.UseLazyPackageSerialization = true;
provider.ReadScriptData = true;
provider.ReadShaderMaps = false;
provider.ReadNaniteData = false;
// 映射、Native、Mount 诊断及预算必须在实际实现中补齐。
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmapPath);
provider.Initialize();
provider.Mount();
await provider.SubmitKeyAsync(new FGuid(), new FAesKey(aesKey));
```

- 实际 EGame 来自校验后的配置，不在程序中永远写死示例值。
- usmap/key 为空时跳过对应配置动作，保留待补状态；并非构造不存在的默认 usmap/空 key。
- `PakOnlyFileProvider` 继承 AbstractVfsFileProvider；构造函数把 versions/comparer 交给基类，Initialize 按安全目录发现列表逐项 RegisterVfs(pakPath)。只实现这层发现/注册，不重写解压或包解析。其 Initialize 和上游 DefaultFileProvider 一样，不等于全部容器已挂载。
- `SubmitKeysAsync` 会按 GUID 选 Reader 并尝试挂载。错误密钥与缺失密钥不同，诊断依赖第 2 节补丁。
- 黑神话预设是 `GAME_BlackMythWukong`，本地 EGame 将其放在 UE5_0 家族并有游戏分支；不能根据游戏宣传引擎版本改成普通 UE5_2。
- v1 对 utoc 在交给 Provider 之前拒绝注册：PakOnlyFileProvider 的发现结果把 pak 与 unsupported 分开；utoc/ucas 只交给诊断目录，不调用 RegisterVfs。上游 DefaultFileProvider.Initialize 会自动注册 utoc，所以不直接使用它执行 v1 的发现。松散 uasset、uproject 与网络容器也不自动加入本版目录。
- archiveDir 含 pak 及 utoc 时：pak 可挂载，utoc 报 unsupported，catalogComplete=false；仅 IoStore 时 mountState=failed、UNSUPPORTED_CONTAINER。
- 对每个 pak 的 RegisterVfs/header 读取也逐项捕获并记录失败，不能只覆盖 Mount 的异常；根目录无法枚举属于整次初始化失败。拒绝的联接/无法读取的子目录需计入发现诊断和 catalogComplete，不静默跳过后宣称全目录完整。

### 1.2 Native 依赖与离线要求

源码 `OodleHelper.Initialize(path)` 在文件缺失时可能进入下载分支，不是天然离线 API。

- 首次开发阶段明确核对 CUE4Parse-Natives、Oodle、zlib 的实际运行依赖与架构（x64）。
- 运行时优先使用经过验证的本地库。调用 OodleHelper 前必须核对路径存在；更明确的初始化方式是构造 `OodleDotNet.Oodle` 的本地实例再交给 `OodleHelper.Initialize(Oodle)`。具体构造签名按已锁定依赖核验。
- 不直接调用无路径的下载型帮助方法。不在解析失败后偷偷换解压器或从网络下载。
- 本地 CUE4Parse 的构建目标可能在 Native 构建失败后只给 warning。`dotnet build` 退出码 0 不足以证明 Native 可用，必须做启动能力检测及真实压缩包读取。
- 没有 Oodle 也可能列出部分目录；状态必须区分“挂载索引可用”和“读取某压缩条目失败”。

### 1.3 映射需求的判断

- `FileUsmapTypeMappingsProvider` 加载失败：MAPPINGS_LOAD_FAILED。
- 包明确使用 unversioned properties 且映射不存在/所需类型缺失：有对应证据时 MAPPINGS_REQUIRED，并记录 package/object/type。
- 映射已加载但读数错位/未知属性/异常：保留 PACKAGE_PARSE_FAILED 和底层异常，不自动断言 AES 错或 usmap 错。
- get_status 的 requirement 根据已观察包累积，不把单个成功样本推成“整个游戏不需要 usmap”。

## 2. 必需的上游小补丁与异常策略

### 2.1 使用现成的严格对象解析开关

源码事实：`AbstractUePackage.DeserializeObject` 默认可能 catch 后仅记日志；`Globals.FatalObjectSerializationErrors` 默认 false。

Worker 必须设置为 true，让损坏对象变成可观察异常。适配器使用 Load/直接读取，不用会吞异常的 SafeLoad/TryLoad 作为正常读取路径。NotFound 先由目录/元数据确定，不能把任意异常转换成“对象不存在”。

### 2.2 补丁 A：结构化的 Kismet 解析状态（必须）

源码事实：`UStruct.Deserialize` 读取脚本后在内部捕获异常，将已经读出的 `tempCode` 存入 ScriptBytecode。外层 FatalObjectSerializationErrors 无法观察这次内部异常。

建议在固定的上游源码中做最小补丁：

- 给 UStruct 增加 `[JsonIgnore]` 的只读诊断属性（类型/名字可按仓库习惯实现）：`ScriptReadState, SerializedScriptSize, ParsedScriptSize, ScriptFailure`。
- state 为 `notRead | empty | complete | partial | failed`，不与工具任务状态混用。
- 开始 Deserialize 时重置状态；ReadScriptData=false 为 notRead；脚本大小 0 为 empty。
- 读取到脚本终点且没有异常才 complete，记录真实输入长度；每个顶层表达式成功后更新 lastSuccessfulPosition，ParsedScriptSize/公开 parsedScriptBytes 使用这个位置。
- catch 时保留当前前缀，若前缀有顶层表达式则 partial，否则 failed；另外记录失败时 FKismetArchive.Position、Index、异常类型、消息、函数/结构名。失败表达式消耗的字节不能计作已成功解析前缀。不泄漏密钥和完整对象数据。
- 外部读取用 typed diagnostic 判断，不能匹配日志英文句子。原来的日志可保留，但不是协议来源。
- 不改变 Kismet token、原有 StatementIndex、对象构造和默认解析分支。
- 字节码解析错误不必阻止读取该 UFunction 的参数/flags，但 get_function bytecode 和完整 export_json 按工具契约报告失败。

补丁单测：空脚本、关闭读取、正常脚本、第一条未知 token、中间未知 token、截断嵌套表达式。检查 partial 前缀及 failure offset，而不是仅检查数组非空。

### 2.3 补丁 B：挂载失败事件（必须）

源码事实：`AbstractVfsFileProvider.TryMountReader` 和 `SubmitKeysAsync` 会吞掉 InvalidAesKeyException，其他异常多为日志，调用者只有新增挂载数量。

- 在这两个 catch 分支发出**只读诊断事件**：`archivePath, encryptionKeyGuid, phase, exceptionType, message`。phase 区分 mount/submitKey。
- 事件不带 key、不执行重试、不改变上游继续挂载其他 Reader 的行为。
- Worker 按 archivePath 收集事件，与 MountedVfs/UnloadedVfs 交叉核对。
- InvalidAesKeyException → AES_KEY_REJECTED；没有提交对应 GUID 的 key → AES_KEY_REQUIRED；其他失败保留 MOUNT_FAILED 等具体异常。
- 没有事件且未挂载、又不能确定原因时标为 unknownMountFailure，不猜测 AES 错。后续通过真实失败补足诊断点。

补丁单测：一个合法 pak 与一个错误 key 的 pak 共存，合法包可读、另一个有明确失败；第二次改 key 成功后旧会话诊断不会泄漏到新 Worker。

### 2.4 补丁 C：Oodle 短解压结果（选定路径经过该 helper 时必须）

源码事实：`OodleHelper.Decompress` 对 decodedSize 小于期望值可能只记录 warning。

- 经 P0 确认使用该 helper 时，将输出长度不等于期望长度视为解压错误，不继续向包解析器交付不完整缓冲区。
- 若使用 Native 的另一条解压路径，验证它的等价长度校验，不添加无效补丁声称已经覆盖。
- 测试故意返回短长度的解压替身，必须得到 DECOMPRESSION_FAILED，不能读取到尾部零字节后生成看似有效 JSON。

### 2.5 补丁管理边界

只保存 A/B 及确实适用的 C，不借机重构上游。P0 用固定 fork commit 或可重复应用的 patch 文件管理；记录补丁 SHA256、受影响符号和单测。不能通过反射修改私有静态缓存，也不能用日志文本解析代替 typed diagnostics。

## 3. 目录、覆盖与包读取

### 3.1 文件目录构建

遍历每个已挂载 Reader 的条目一次，生成：

```text
ArchiveRecord(archiveId, relativePath, readOrder, mountPoint, diagnostics)
EntryRecord(archiveId, normalizedPath, displayPath, size, flags, sourceHandle)
EffectiveFile(pathKey, winningEntry?, shadowedEntries, conflict)
```

- pathKey 用正斜杠、OrdinalIgnoreCase 语义；显示用真实路径。pathKey 不直接作为磁盘相对路径使用。
- 构建过程中不反序列化 UObject。目录索引使用段前缀/直接子项表，避免每次 list_directory 遍历所有文件。
- `Files.Count` 和枚举可能覆盖多组目录，不能直接当去重后文件总数；本程序显式按规范路径去重后计数。
- 最高 `ReadOrder` 优先。库已经计算 `_P`、版本后缀等顺序，直接使用实际值，不另造“名称包含 _P 就 +1”。
- 同一有效路径存在多个**最高且相等** ReadOrder：标为 conflict；读取/索引该包失败 AMBIGUOUS_OVERRIDE。不能把 ConcurrentBag 的枚举顺序解释成确定覆盖规则。
- 所有索引默认只基于有效文件；物理重复只保留溯源和诊断，不把旧版本表格混进搜索结果。

复杂度：构建 O(E)，目录排序 O(F log F)，E 是物理条目数，F 是唯一有效路径数；内存存文件元数据，不存全部解压内容。无类型筛选的文件搜索最坏 O(F)；后续只有实测必要才建更复杂的路径索引。

### 3.2 路径解析与对象身份

内部键统一如下，避免同一目录在文件、文本、引用查询中落入不同坐标系：

- fileKey：规范化的 Provider 文件路径，保留扩展名；packageKey 对本地包就是其主文件 fileKey，因此显式指定的同名 uasset/umap 不会共用身份。
- scopeKey：同一 Provider 坐标系的目录前缀；根用空串，非根以 `/` 段边界匹配。数据库不直接用显示的 `/Game/...` 字符串过滤 Provider 文件。
- objectPath/packagePath：对外可读路径。解析器维护到 packageKey/exportIndex 的映射；无扩展名的显示包路径若歧义，要求调用者使用源文件路径。
- reference targetKey：本地可定位包用 packageKey；Native 用带标签的 `/Script` 键；未解析项用独立 unresolved 标签。查询时先解析身份，不把显示路径直接当数据库键。
- 数据库存储用于比较的规范键和用于展示的原值；文本额外保存 source_file_key 做 scope 过滤，sourcePath 可以是 locres 路径或对象来源展示。

- `/Game` 映射到 `b1/Content`，`/Engine` 到 Engine/Content；插件路径只有经过已观察 mount point/VirtualPaths 验证才可映射，未知插件根要求使用 Provider 原路径。
- 校验空字符串、纯 `/`、扩展名及对象部分之后才调用 FixPath；上游 FixPath 直接访问首尾字符，不承担所有公共参数校验。
- 先匹配明确文件路径；再解析包与对象部分。保留 `.uasset/.umap` 与对象分隔点的语法差别。
- 对象身份是 `(canonicalPackageKey, exportIndex)`。objectPath 是由该 export 的 Outer 链生成的可读路径；同名不同 Outer 的对象不混淆。
- 用 `IPackage.ResolvePackageIndex` 获取 metadata；访问 ResolvedObject 的 Name/Outer/Class 不访问 `.Object.Value`。
- Outer/父类链加 visited 集合，循环返回诊断。元数据丢失时返回 unresolved，而不是递归到栈溢出。
- 原生 `/Script/...` 可以是合法引用目标，但不是磁盘中一定有包可读的承诺。

### 3.3 包与 payload 一致性

内部建议入口：

```text
ResolvePackage(path) -> EffectivePackage
ReadMetadata(package) -> PackageMetadata
LoadExport(package, exportIndex) -> UObject（仅在 Worker 解析执行器内）
CollectPayloads(package) -> 带 archiveId 的实际伴随文件集合
```

- PackageMetadata 包含 imports/exports/Outer 链，不因 list_objects 就加载每个 Lazy export。
- 源码 `IPackage.ExportsLazy` 与 GetExports 支持惰性，但枚举 GetExports 会触发对应对象解析；不要为了计数 ToArray。
- `AbstractFileProvider.LoadPackage` 可用于初始实现，但必须确认伴随文件与主包来源一致。若 Provider 的全局 payload 查找选中了不同 archive 的文件，拒绝继续使用该结果。
- 黑神话 v1 规定有效主包与必须的伴随文件来自同一 archive；主包胜出后不能偷偷拼低优先级旧 uexp。确有跨容器合法布局的游戏不是本版扩展目标。
- 需要控制来源时，用同一 Reader 取得主包和 payload 的 FArchive，再通过已经存在的 `Package(..., provider, useLazySerialization:true)` 入口构造；参数以已锁定源码签名为准。不要复制 Pak 解压实现。
- 判断伴随文件是否必要结合包头/实际 reader 需求；有的包全部数据就在 uasset，不一律要求 uexp。
- 没有长期全局 UObject 缓存。一个读取单元结束释放包引用/归属本单元的 archive；Provider 持有的 Reader 生命周期由整个 Worker 管理，不能误释放共享 Reader。
- 允许单操作内复用父类/引用包；缓存必须按包身份和会话归属，不只按对象短名。

**成本限制**：仅访问包表避免的是 UObject 反序列化，不保证只从磁盘读几十字节。Provider 可能读取/解压完整 uasset/uexp，按输入体积和 Worker 内存预算约束。未测前不承诺全库扫描几秒完成。

## 4. 对象 JSON 与专用 Reader

### 4.1 JSON 分工

- CUE4Parse 的 JsonConverter/UObject.WriteJson 负责 UE 结构与游戏特殊类型。
- 本程序负责输出投影、预算、来源、完整性及 MCP 包络；System.Text.Json 不直接遍历 UObject。
- 参考 `JsonConverters.cs#UObjectConverter` 和 `FModel/ViewModels/CUE4ParseViewModel.cs` 中 JsonConvert.SerializeObject，但不复制 UI 代码。
- 保持 CUE4Parse 的 UObject 引用表示，不默认加载并展开引用目标。FText 的本地化处理见第 5 节；部分 converter 内部可能访问依赖，不能宣称 fields 能禁止所有依赖读取。

### 4.2 在线投影的可实施办法

实现一个 `ProjectionJsonWriter` 包装 Json.NET token 写入：

1. 跟踪当前 JSON Pointer、容器深度、数组索引与匹配 fields 的前缀树。
2. 只把被选择的 token 写入有界 UTF-8 缓冲区；未选中子树只维护状态，不构造 JObject。
3. depth 越界的容器写 null，同时记录 omission 的 pointer 和 reason=depth；省略由 fields 排除的字段不算未知数据。
4. 始终累计经过的 token 数，包括被字段过滤的 token；超预算抛出本程序异常，中止本次输出。
5. 写完且 JSON 合法后，将小的有界结果解析为 JsonElement/JToken 并组装 DTO。最后在 Host 再检查完整 MCP 结果预算。
6. 输入 fields 全部验证命中；任何未命中报 FIELD_NOT_FOUND。根空指针表示完整 value，但仍遵循预算。

注意：writer 过滤不阻止上游 converter 在写 token 前创建临时数组或读取对象，因此还有包预算和 Worker 隔离；这是有界输出，不是对上游所有分配的形式化限制。

对于 DataTable，先在 RowMap 做行过滤/字段选择，再逐个序列化选中行；不先序列化整张表到字符串再搜索。get_class_info/get_function 直接构造窄 DTO，不把整类 JSON 裁掉来冒充专用实现。

测试至少覆盖：嵌套数组、转义字段名、null、根标量、多个 fields 父子重叠、缺字段、超深度、一个字段超大字符串、token 超限。byte budget 不得输出半个 JSON 字符串。

### 4.3 表格、曲线、默认值

- DataTable：`UDataTable.RowMap` → 行名/FStructFallback。`RowStructName` 或 RowStruct 引用是 schema 线索。RowMap 为 null 时拒绝当空表。
- schema 来源优先明确的结构/映射定义，缺失时可报告 observed 字段及不完整状态；不自动把某个样本的数值类型升级成整表真实声明类型。
- 数字过滤使用 JSON 数值的实际数值语义，避免转 double 丢失大型整数。对超出 Decimal 精度的数使用 BigInteger/明确可比较表示；不能把 uint64 ID 经 double 四舍五入。
- UCurveTable 的 RowMap/CurveTableMode 区分 SimpleCurve/RichCurve；UCurveFloat 位于 `UE4/Objects/Engine/Curves`，从 FloatCurve 读取。
- 类信息来自 `UClass.SuperStruct/ClassDefaultObject/FuncMap/Interfaces/ClassFlags` 和 `UStruct.ChildProperties/Children`。
- 函数参数来自反射属性及 CPF_Parm/CPF_OutParm/CPF_ReturnParm/引用标志。声明顺序来自元数据，不按字母重排参数。
- CDO 的序列化属性是可观察默认值。继承合并按最派生类优先，保留 declaringClass 与值来源；无法解析 Native 父类时停止并标 unavailable。
- 未序列化字段不能默认填 0/false/null，也不能把 usmap 的类型定义当 CDO 的真实实例值。

### 4.4 Kismet 输出

开启 ReadScriptData=true 是固定的 Worker 解析策略，不在不同请求间随意切换，防止已缓存包在不同模式下混用。它会增加蓝图解析成本，需纳入样本性能记录。

`KismetExpression.Token/StatementIndex` 与派生 expression 字段用于 get_function bytecode；顶层 expression 按原顺序分页，嵌套表达式保持树形。`FKismetArchive.Index` 与序列化流 Position 是不同维度，诊断同时记录，不把其中一个伪装成另一个。

本地 UClass 虽有 DecompileBlueprintToPseudo，但它是辅助解释且使用映射/静态辅助状态；v1 不把结果当权威代码。逻辑 MOD 开发者使用静态符号/表达式定位，再用实际运行环境验证。

## 5. 文本身份与中文搜索

### 5.1 统一文本记录

```text
TextRecord
  sourceKind: datatable | stringTable | locres
  sourcePath / objectPath / rowName / fieldPointer
  namespace / key / tableId
  sourceText / text / language / resolution
  identityLinks
```

记录身份必须包含来源位置。两个资源都有“攻击力”，仍是两条记录。数值字段不转换成字符串参与全文搜索。

- locres：通过 `FTextLocalizationResource(FArchive)` 读取 Entries，按 namespace/key 枚举 FEntry；使用文件 reader，不 LoadPackage。
- StringTable：`UStringTable.StringTable` 的 namespace、KeysToEntries、可用 metadata。
- DataTable：遍历每行中 string/FName 的文字值及 FText，保留 JSON Pointer；数组下标和嵌套结构都进入位置。FName 作为字符串证据标 sourceType，不能因此当作业务 ID。
- FText 必须查看 history 的实际类型，提取 Base 的 namespace/key/source，或 StringTableEntry 的 tableId/key。不能只存 FText.Text/ToString() 然后丢身份。
- 格式化/复合 FText 若无法完整求值，保留身份和已知源文字，resolution=unresolved；不执行游戏逻辑模拟格式化参数。

### 5.2 语言与解析策略

- 配置 language 是 BCP-47 表达；v1 显式支持 zh-Hans/en。Worker 查找 localization 目录时使用项目中已观察的文化目录映射表（例如 zh-Hans 的大小写变体），不能直接猜枚举名。
- 建立当前语言的 namespace/key → 本地化文本字典；多个来源同键且文本不同则标 ambiguous，保留所有来源，不按扫描顺序覆盖。
- StringTableEntry 先通过完整 tableId/key 精确关联；同中文内容不构成关联证据。
- 解析顺序固定：有唯一匹配的当前语言文本 → resolvedLocalized；否则保留已存 sourceText → sourceOnly；两者都无 → unresolved；冲突 → ambiguous。
- sourceOnly 是明示的证据层，不是悄悄换到英文并声称中文命中。用户要求的语言没有 locres 时仍能搜索源文本，但结果必须明确 language/resolution，coverage 报本地化缺失。
- scope 限制最终文本记录来源；构建过程可以加载范围外被明确引用的 StringTable/语言字典，仅作解析依赖，不悄悄扩大被索引范围。
- 字典不能无限跨任务增长：归属当前构建或 Worker 会话；配置变更直接销毁 Worker。上游 UStringTable 静态缓存因此不会跨会话残留。

### 5.3 FTS5 trigram 检索

使用 SQLite FTS5 的 trigram tokenizer 解决中文无空格的子串查询。输入规范化为 `.Normalize(FormKC).ToUpperInvariant()`，保存原文与 normalizedText；不做简繁互转、同义词扩展或 ID 推理。

1. keyword 按 Unicode Rune 计数，不能为空。
2. 长度 >= 3：将整个 normalized keyword 作为经过引号转义的 FTS phrase 参数查询，按 snapshotId 限定；随后用 `instr(normalized_text,@keyword)>0` 精确复核，避免候选规则与契约不一致。
3. 长度 1–2：直接在该快照、scope、sourceKind 的 texts 子集执行 instr 扫描。这是明确的短词算法，不是 FTS 出错后的自动降级。
4. `%`、`_`、引号、AND、NEAR 都作为文本，SQL/FTS 参数全部绑定；不要拼接用户表达式。
5. 搜索按来源稳定排序，不使用不稳定的“AI 相关度”。返回 keyset 分页，不 COUNT 全结果作为每页必需步骤。

短词 O(T) 扫描可能慢，因此建议先 scope；超时必须报告。FTS 不可用时 P0 失败，不静默切换另一搜索实现。混合中文、英文、标点、emoji、三字以下关键词必须有契约测试。

## 6. SQLite 数据模型

### 6.1 单一所有者与版本

Worker 独占其 `cacheRoot/gameId`。用 FileStream 的 FileShare.None 持有锁直到退出，文件是否存在不代表锁还活着；第二个 Worker 返回 CACHE_LOCKED，不删除别人的锁文件。Host 不直接读写数据库。

建议数据库文件 `cacheRoot/gameId/index-v1.sqlite`。`gameId=SHA256(规范化 gameRoot)`，只用于本地隔离。连接启用 foreign_keys、WAL、合理 busy_timeout；同时仍只有一个写执行器，不依赖并发写提高性能。

以下 DDL 是可执行的逻辑 schema；实现可按 Microsoft.Data.Sqlite 参数化语句管理。迁移版本使用 PRAGMA user_version，不在每次查询中自动修复未知版本。数据库版本更新必须显式重建派生索引，任务/失败诊断保留迁移说明。

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE snapshots (
  id TEXT PRIMARY KEY,
  kind TEXT NOT NULL CHECK(kind IN ('asset','text','reference')),
  source_fingerprint TEXT NOT NULL,
  scope_key TEXT NOT NULL,
  language TEXT,
  include_symbols INTEGER NOT NULL DEFAULT 0,
  state TEXT NOT NULL CHECK(state IN ('building','published','discarded')),
  complete INTEGER NOT NULL DEFAULT 0,
  coverage_json TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  published_utc TEXT
);
CREATE TABLE index_heads (
  kind TEXT PRIMARY KEY CHECK(kind IN ('asset','text','reference')),
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id)
);
CREATE TABLE scan_units (
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  unit_key TEXT NOT NULL,
  phase TEXT NOT NULL,
  state TEXT NOT NULL CHECK(state IN ('succeeded','failed','skipped')),
  error_code TEXT,
  error_message TEXT,
  PRIMARY KEY(snapshot_id, unit_key, phase)
);
CREATE TABLE assets (
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  package_key TEXT NOT NULL,
  export_index INTEGER NOT NULL,
  object_path TEXT NOT NULL,
  object_path_key TEXT NOT NULL,
  object_name TEXT NOT NULL,
  class_name TEXT,
  class_path TEXT,
  outer_path TEXT,
  archive_id TEXT NOT NULL,
  flags TEXT NOT NULL,
  metadata_state TEXT NOT NULL,
  PRIMARY KEY(snapshot_id, package_key, export_index)
);
CREATE INDEX ix_assets_type ON assets(snapshot_id, class_name, package_key);
CREATE INDEX ix_assets_path ON assets(snapshot_id, object_path_key, export_index);
CREATE TABLE symbols (
  id INTEGER PRIMARY KEY,
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  package_key TEXT NOT NULL,
  owner_path TEXT NOT NULL,
  object_path TEXT,
  kind TEXT NOT NULL CHECK(kind IN ('class','function','property')),
  name TEXT NOT NULL,
  name_key TEXT NOT NULL,
  property_pointer TEXT,
  metadata_json TEXT NOT NULL,
  UNIQUE(snapshot_id, package_key, owner_path, kind, name, property_pointer)
);
CREATE INDEX ix_symbols_query ON symbols(snapshot_id, kind, name_key, id);
CREATE TABLE texts (
  id INTEGER PRIMARY KEY,
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  source_key TEXT NOT NULL,
  source_file_key TEXT NOT NULL,
  source_path TEXT NOT NULL,
  object_path TEXT,
  row_name TEXT,
  field_pointer TEXT,
  source_kind TEXT NOT NULL,
  namespace TEXT,
  text_key TEXT,
  table_id TEXT,
  text TEXT NOT NULL,
  normalized_text TEXT NOT NULL,
  source_text TEXT,
  language TEXT NOT NULL,
  resolution TEXT NOT NULL,
  identity_json TEXT NOT NULL,
  UNIQUE(snapshot_id, source_key)
);
CREATE INDEX ix_texts_scope ON texts(snapshot_id, source_file_key, id);
CREATE INDEX ix_texts_identity ON texts(snapshot_id, namespace, text_key);
CREATE VIRTUAL TABLE texts_fts USING fts5(
  normalized_text,
  snapshot_id UNINDEXED,
  text_id UNINDEXED,
  tokenize='trigram case_sensitive 1'
);
CREATE TABLE reference_edges (
  id INTEGER PRIMARY KEY,
  snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
  source_package TEXT NOT NULL,
  target_package TEXT,
  target_key TEXT NOT NULL,
  target_raw TEXT,
  resolution TEXT NOT NULL,
  edge_kind TEXT NOT NULL CHECK(edge_kind='packageImport'),
  evidence_count INTEGER NOT NULL,
  example_import_index INTEGER NOT NULL,
  UNIQUE(snapshot_id, source_package, target_key, edge_kind)
);
CREATE INDEX ix_refs_out ON reference_edges(snapshot_id, source_package, target_key);
CREATE INDEX ix_refs_in ON reference_edges(snapshot_id, target_key, source_package);
CREATE TABLE tasks (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  state TEXT NOT NULL,
  snapshot_id TEXT REFERENCES snapshots(id) ON DELETE SET NULL,
  request_json TEXT NOT NULL,
  progress_json TEXT NOT NULL,
  result_json TEXT,
  started_utc TEXT NOT NULL,
  finished_utc TEXT
);
CREATE TABLE task_errors (
  task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  sequence INTEGER NOT NULL,
  subject TEXT NOT NULL,
  stage TEXT NOT NULL,
  code TEXT NOT NULL,
  message TEXT NOT NULL,
  PRIMARY KEY(task_id, sequence)
);
PRAGMA user_version = 1;
```

实现细则：

- scope/path/name 的用于比较字段由 C# 统一规范化；SQLite NOCASE 不是 .NET OrdinalIgnoreCase 的通用替身。显示字段与比较字段不能混用。对显示字符串排序与 keyset 边界比较，注册基于 StringComparer.Ordinal 的 SQLite 自定义 collation（例如 UE_ORDINAL），使包含非 BMP 字符时也与工具排序契约一致；SQL smoke test 不代替该 .NET 集成测试。
- 对 symbols 的 nullable 唯一字段，写入前把“不适用”的 property_pointer 规范为 `""`，避免 SQLite NULL 唯一约束允许重复；公共 DTO 再还原 null。
- source_key 是规范化的来源位置元组的无歧义编码（JSON 数组或长度前缀），不是用 `|` 随便拼串。必须包含 sourceKind、对象、行名、字段指针、namespace/key 和语言；DataTable 行名保持原始精确大小写。
- texts_fts 是独立表：texts 与 FTS 插入/删除必须在同一个事务；删除 snapshot 前显式删除该 snapshot 的 FTS 行，外键不会自动清理虚表。
- FTS 查询后 JOIN texts 验证 snapshot_id；用 source_file_key 按目录段过滤 scope，用 source_path、object_path、row_name、field_pointer 和 id 按契约排序。文本引用身份可从 identity_json 返回，不能对 JSON 字符串做不可靠 contains join。
- reference target_key 对已解析包用规范包键，对 unresolved 使用带标签的原始索引身份，避免 unknown 与真实同名路径混合。
- tasks.request_json 存范围/参数，不存 AES 或整份配置。数据库只保存派生数据和诊断，不保存 UObject 大 JSON 或解压原始包。

### 6.2 指纹与失效

`sourceFingerprint=SHA256(canonicalManifest)`，manifest 至少包含：

- 规范 gameRoot/archiveDir；ueVersion；language；解析策略版本。
- 已固定 CUE4Parse 基线、补丁摘要、索引 schema 版本、投影/文本规范化版本。
- usmap 内容 SHA256，密钥集合的私有摘要（不保存明文 key）。
- 已发现容器相对路径、长度、LastWriteTimeUtc、实际 ReadOrder、挂载状态，以及有效路径冲突摘要。

为了个人大游戏启动成本，pak 默认只使用长度/时间元数据，不全量哈希几百 GB。状态标 `fingerprintStrength:"metadata"`：相同长度且时间被人为恢复的修改可能无法检测，不能宣称密码学级完整性。必要时可后续增加显式强校验，不悄悄每次重启扫完整 pak。

- 指纹不同：旧 head 不可供当前查询使用，返回 INDEX_NOT_READY 并建议重建；旧数据可保留待清理，不自动当新数据。
- 指纹相同：重启可重用已发布快照，但 sessionId 更新，旧游标仍失效。
- 普通读取前检查涉及源容器的长度/时间；扫描发布前重新核对全部源容器元数据。发生变更不发布混合结果，SOURCE_CHANGED。
- 新 pak 的发现只在 remount；这是目录快照契约，不是实时文件系统观察器。

### 6.3 构建与发布事务

每种 kind 只保留一个已发布 head。构建 scope 是该 head 的覆盖范围；构建小 scope 会替换同 kind 的旧大 scope。明确采用此简单模型，不悄悄合并重叠快照。

```text
创建 task + building snapshot
  → 枚举固定候选单位列表，记录总量
  → 每单位：读/解析/提取
       成功：同事务写派生行 + scan_units + 任务计数
       失败：同事务写失败记录，不写“空的成功数据”
  → 单位间检查取消；让出执行器给前台请求
  → 结束前复核源指纹
  → 同一事务：snapshot published + coverage + index_heads 切换 + task 终态
```

- asset includeSymbols=true 时 scan_units 分 `metadata` 与 `symbols`；metadata 成功、symbols 失败仍保留类型行，但 search_symbols 使用 symbols 的覆盖率。
- text 扫描内部分为分类和文本提取；没有目标类型属于成功分类，不是未支持。
- 逐包失败可继续扫描，任务 completedWithErrors 并发布 partial 覆盖快照；每个失败必须可从 get_task 分页读取。这是明示的批处理契约，不是隐藏异常。
- 结构化提取能返回可信局部结果（例如部分 export 类型未知、import 目标未解析）时，可以连同失败状态在同事务保留已知行，所需阶段 complete=false。真正抛异常而未形成结构化结果时回滚该阶段派生行。Native `/Script` 已知目标不算 unresolved；未知目标不得让 incoming 的覆盖率被标为完整。
- cancelled/failed/interrupted 不切换 head；building 数据标 discarded 并清理。第一次构建失败时仍为 INDEX_NOT_READY。
- Worker 重启发现 tasks 为 running/cancelling/queued，标 interrupted；不自动恢复到旧会话。用户重新 build，避免复杂断点兼容。
- 查询固定读取当前 head，构建期间读取旧 head；只在原子发布后游标变陈旧。
- 旧 head 清理在发布后安全点进行，先删 FTS 行，再按外键删派生记录；清理失败报告 CACHE_IO_FAILED，已发布新 head 不反转。
- 可丢弃的索引数据库不是资源备份；不承诺永久保存历史扫描。任务错误在当前数据库内保留到明确清理，v1 不新增“删除任意缓存路径”工具。

## 7. 三种索引的提取算法

### 7.1 asset / symbols

1. 对有效包读取 metadata，枚举 exportIndex、Name、Class、Outer、Flags。
2. 每个 export 写 assets。类型未解析写 metadata_state=unresolved，计入 unknown；不得按文件名 DT_/BP_ 当成真类型。
3. includeSymbols=true 时，仅加载实际为 UClass/UFunction 或相关反射对象的 export；记录类、函数、属性声明来源。
4. property 来源包括 ChildProperties 与必要旧式 Children 链；usmap 只补类型线索，source 明示。
5. FuncMap 关联函数 export 身份；同名不同类分开。不需要为全局符号索引展开所有 CDO 默认值。
6. AssetRegistry 可帮助定位候选、提供 tag，但当前有效包表是类型真值。旧 Registry 无法覆盖新 MOD 时不会让包从索引消失。

### 7.2 text

1. 枚举范围内包与 locres；包头先分类。
2. 对目标 StringTable/DataTable 加载必要 export，遍历第 5 节的文本类型。
3. 按已存身份解析当前语言，构造 TextRecord；不可解析的 FText 保留 available identity 和 unresolved 诊断。
4. 同单位事务写 texts 与 texts_fts。元数据、正文、FTS 三者不跨事务提交。
5. 统计源种类、resolved/sourceOnly/unresolved/ambiguous 数；覆盖率与结果数量是不同指标。

### 7.3 packageImport

1. 读取 Package.ImportMap/ExportMap，利用 OuterIndex 递归解析 import 的完整来源包。
2. import index 的符号规则使用 CUE4Parse 的 FPackageIndex 解析，不自行把所有负数当数组下标直接取。
3. 只读取 Name/Outer/Class 的元数据，不对 import 的 `.Object.Value` 求值，避免把一次建图变成加载整个世界。
4. 边从**当前源包**指向 import 所属的目标包，edgeKind=packageImport；去除源包到自身的包级自环（同包对象引用不属于本图）。
5. 多个 import 指向同一目标聚合 evidence_count 和 example_import_index，避免成千上万重复边。
6. 缺失 Outer/目标时记录 unresolved 边和诊断。有效 `/Script/模块` 是 Native package 目标，不当作缺失磁盘文件错误。
7. 扫描表中的 import 是静态依赖线索，可能包含未实际用到的 import。没有 import 不证明运行时不会动态加载。

入边查询只聚合已扫描源包；范围、失败和 catalogComplete 必须随每次查询返回，不做“零入边 = 无人使用”的推断。

## 8. 导出事务

### 8.1 共同流程

```text
验证 path 和 outDir
  → 解析有效包、检查源未变和 payload 来源
  → 分配随机 exportId
  → outputRoot/outDir/.<exportId>.partial（同卷临时目录）
  → 逐文件 CreateNew 写入，计数、哈希、Flush
  → 写 manifest.json 并 Flush
  → 复核源未变与父目录安全
  → Directory.Move 到 outputRoot/outDir/<exportId>
  → 原子记录 task completed/result
```

文件系统与 SQLite 没有跨资源分布式事务：

- 原子目录改名是导出成功的持久化边界。数据库记录在后；若进程在改名后、写 task 前崩溃，重启通过 manifest 中的 taskId/exportId 识别“已提交但任务未记录”，补记 completed 并标 recovered=true。
- 只扫描本程序记录过的待提交导出目标，不遍历用户整个输出盘。
- 未改名的 partial 目录可清理；改名成功后的完整目录不能在通用 catch 中删除。取消若晚于提交边界，返回已完成而不是删除成果。
- 在 tasks.progress_json 预先记录 partial/final 的受控相对路径和 exportId，避免崩溃后无法恢复。该路径不来自不可信 JSON 资产。

### 8.2 原始包

源码 `SavePackage/SavePackageAsync` 会收集主文件与 uexp/ubulk/uptnl，但其结果是 byte[] 字典，且 payload 查找使用全局文件视图。它能作为样本对照，不能直接承担大文件流式和版本一致性承诺。

- 复用 CollectPayloads 确定同来源文件；能取得流则分块复制和计算 SHA256，不能流式的 API 在预算内一次只持有一个文件 byte[]，写完释放。
- 文件相对路径保留 Provider 的安全包路径，使同目录 uasset/uexp 可被下游识别。
- 对 Windows 非法/保留名拒绝导出并解释，不静默改文件名破坏包关联。
- optional payload 不存在就不生成；required payload 缺失、来源冲突、读失败则全次导出失败。

### 8.3 JSON

- JsonTextWriter 直接写磁盘计数流，顶层 exports 数组逐 export 写入，避免 SerializeObject(allExports) 的大字符串。
- 无 fields/depth 裁剪；但引用表示仍是 CUE4Parse 定义的引用，不无限递归目标对象。
- 每个 export 写入前确认解析状态；Kismet partial/failed 拒绝“完整导出”。超预算或写盘失败删除未提交 partial 目录。
- manifest 记录本程序格式版本、上游基线、补丁摘要、配置的非敏感版本信息、源 archiveId、源指纹、对象数量、文件 SHA256 和兼容性未验证声明。
- 导出不包含真实 AES，不把 usmap 文件本身复制到输出目录。

## 9. 日志与可观察性

所有日志使用 stderr，正常 MCP stdout/Worker stdout 不含 Console.WriteLine 调试文本。设置 Microsoft ILogger 的 stderr 阈值，同时配置 CUE4Parse/Serilog 输出为 stderr；不能只改一套 logger。

关联字段：sessionId、requestId、taskId、packagePath、exportIndex、archiveId、stage、elapsedMs、bytesRead、exceptionType。保存完整异常仅在本机日志；API 给脱敏摘要和关联 ID。

所有日志 sink 与 API 错误映射共享 SecretRedactor：对已配置 key 的大小写/0x 变体做替换，再写格式化消息和 Exception.ToString；不记录 init/配置 IPC 原文，不将不合法消息整行回显。完整堆栈也必须经过脱敏，不能只过滤结构化 aesKey 属性。测试同时扫描 Host stderr、Worker stderr、工具结果和 manifest。

指标只服务本机诊断：启动耗时、各阶段包数、失败数、峰值 Worker 私有内存、数据库体积、导出字节数。没有遥测上传，也不新建监控服务。

## 10. 需要保留的真实限制

- Pak metadata 的时间/长度指纹不是全内容校验。
- usmap 对类型的描述不等于完整 Native 反射或当前运行实例。
- 字段/行分页不使上游反序列化变为随机访问。
- 局部包 import 图不是所有硬引用、软引用或蓝图调用图。
- 尽管使用 Worker 隔离，仍可能因系统级内存/磁盘耗尽影响机器；预算只降低风险。
- 静态导出能为 UAssetGUI/kismet-analyzer 提供输入，不等于已经验证这两个工具支持该包格式。
