# MCP 工具契约 v1

> [架构入口](architecture.md)。本文是计划实现的公开 API 唯一依据，不表示工具已经注册或运行。
> 所有例子都是 `tools/call.arguments` 对象；`/Game/Example/...`、行名和任务 ID 是合成测试夹具，不是声称黑神话存在这些资产。真实调用先用搜索/列表返回的路径。

## 1. 公共协议

### 1.1 MCP 约定

- 注册下文 24 个工具，名称使用 snake_case，参数和输出字段使用 camelCase。
- `inputSchema` 的顶层是 object，必填项明确列出，`additionalProperties=false`；patch 和 where 也有受限 schema。不可把参数统统暴露成无描述字符串。
- 工具描述说明成本、前置条件、副作用和截断行为。工具列表稳定，不因缺 AES 动态消失。
- 纯查询：`readOnlyHint=true`、`openWorldHint=false`。配置、索引、取消、导出：`readOnlyHint=false`；配置/索引会替换本地状态，标注 `destructiveHint=true`；导出不覆盖已有内容，标注 `destructiveHint=false`。不要把注解当授权边界。
- 查询可以标注 `idempotentHint=true`；配置切换、remount、创建任务/导出标为 false。cancel_task 对已结束任务不改变状态，可标为 true。
- 正常结果同时放入 `structuredContent` 和一个包含同一 JSON 的 TextContent，兼容不读取结构化结果的客户端；检查最终两份合计大小。
- 使用 SDK 的显式 CallToolResult 映射，不能只返回 DTO 然后假设错误会自动设置 isError。
- 无效工具/损坏的 JSON-RPC 由 SDK 处理；领域参数错误、未配置、解析失败通过工具结果 `isError=true` 表达。工具错误也使用下述 envelope。
- `outputSchema` 覆盖成功和失败两种 envelope，`data` 可空；局部结果字段按各工具 DTO 定义。
- v1 不使用 MCP 实验性 Tasks 协议，也不依赖客户端支持 progress token；任务就是普通工具返回的 taskId。

### 1.2 结果包络

```json
{
  "ok": true,
  "sessionId": "session-example",
  "data": {},
  "page": null,
  "coverage": null,
  "warnings": [],
  "error": null
}
```

- `sessionId`：未创建会话时为 null；旧数据只允许 get_status/get_task 明确以 stale/interrupted 形式提供。
- `data`：工具专属对象，不在这里混入未声明的纯文本日志。
- `page`：列表使用 `{limit, returned, nextCursor}`；nextCursor 为 null 表示当前声明范围的最后一页，不表示游戏中不存在其他结果。
- `coverage`：索引查询/扫描使用 `{snapshotId, scope, totalUnits, succeededUnits, failedUnits, skippedUnits, complete, catalogComplete, sourceFingerprint}`。单位在各工具说明中固定；未知计数是 null，不是 0。
- `catalogComplete`：已发现容器是否全部受支持且挂载，且没有未决有效路径冲突。未发现的新包不在本次快照内。
- `complete`：本声明范围的所需工作全部完成。正常不相关 export 被成功分类后过滤，不计 skipped；无法处理的必需信息才影响完整性。
- `warnings`：`{code, message, subject?}`，不得包含 AES、原始配置或堆栈；大批失败通过任务错误分页读取，不把几千条塞入 warnings。
- `error`：`{code, message, subject?, retryable, suggestedAction, details?}`；只有 ok=false 时非空。details 是有界结构化诊断，不是原始异常对象。
- 公共可用性词：`availability=available|partial|unavailable|unknown`，`metadataState=resolved|unresolved`，`typeState=known|unknown|conflict`。get_object 的 `parseStatus="parsed"` 只表示严格解析器完成本次读取，不声称恢复编辑器/Native 全部语义；失败通过 error 表达。
- 索引覆盖率按所用阶段计算；列表查询中的未知类型/未解析引用不能被外层 ok=true 抹掉。没有索引的目录/对象列表可使用 snapshotId=null 的 coverage，单位分别为有效文件/包内 export。

例：

```json
{
  "ok": false,
  "sessionId": "session-example",
  "data": null,
  "page": null,
  "coverage": null,
  "warnings": [],
  "error": {
    "code": "INDEX_NOT_READY",
    "message": "当前会话没有已发布的文本索引",
    "retryable": true,
    "suggestedAction": "调用 build_text_index，等待任务完成后重新检索"
  }
}
```

### 1.3 路径、选择器与分页

**文件路径**：使用 Provider 的虚拟路径，如 `b1/Content/Example/DT_Skill.uasset`；绝不当作 Windows 磁盘路径。显示保留原大小写，键比较用 OrdinalIgnoreCase。

**包路径**：可输入文件路径或 `/Game/Example/DT_Skill`。解析器返回 canonical packagePath 及 sourceFilePath。无扩展名时同时检查 uasset/umap，二者都存在时报 `AMBIGUOUS_PACKAGE`，不默认错选。

**对象路径**：例如 `/Game/Example/BP_Skill.BP_Skill_C`、嵌套对象使用实际 Outer 链产生的 `:Function` 等路径。实现必须用 export 表及 Outer 链匹配，不能把冒号后的名字简单传入只按 Name 搜索的 GetExport。

**共享选择规则**：读取工具有 `path:string`，及可选 `exportIndex:int`（零起，仅配包路径使用）。提供对象路径与 exportIndex 同时报 INVALID_ARGUMENT。普通 get_object 包内恰好一个 export 才可隐式选择；专用工具在对应类型中恰好一个才可隐式选择。多候选报 AMBIGUOUS_OBJECT 并建议 list_objects，不偷偷挑第一个。

**scope/dir**：省略表示当前有效目录的根；可用 `/Game/Example` 或 Provider 目录前缀；按目录段匹配，`Foo` 不匹配 `Foobar`。scope 包含子目录。目录参数不可包含对象部分、glob、`..`、磁盘前缀。无效目录报 DIRECTORY_NOT_FOUND。

**字段**：fields 是 JSON Pointer 字符串数组（RFC 6901），例如 `/Properties/Damage`；转义 `~0`、`~1`，数组下标为十进制。选中父节点就选其全部子树，结果保持原嵌套形状。不存在的显式字段报 FIELD_NOT_FOUND，不忽略拼写错误。空数组拒绝；省略表示全部字段。

**排序**：路径/名称使用规范化键 Ordinal 排序，之后原始路径 Ordinal，最后 exportIndex/条目 ID。不得依赖文件枚举、Dictionary 或当前区域文化的顺序。

**游标**：Base64URL 的版本化载荷，绑定 sessionId、工具名、规范化参数哈希、catalogRevision、相关 snapshotId、最后排序键；服务端校验格式和所有绑定，不把游标当可信 SQL。没有认证需求，不需要设计跨机器签名基础设施。

- 下一页必须重复相同过滤、字段、limit 等参数并加 cursor；修改任一项报 CURSOR_QUERY_MISMATCH。
- remount/sessionId 改变或索引快照切换报 STALE_CURSOR；正在构建的未发布快照不影响现有游标。
- limit 默认和上限见架构预算。数据库做 keyset pagination，不在每页把全部行拉到内存再 Skip。
- 行本身超过输出预算时整个调用报 OUTPUT_BUDGET_EXCEEDED；不生成无法前进的空页。建议缩 fields 或 export_json。

### 1.4 错误码目录

| 分组 | 错误码 | 处理原则 |
|---|---|---|
| 参数/选择 | INVALID_ARGUMENT, CONFIG_INVALID, PATH_NOT_ALLOWED, DIRECTORY_NOT_FOUND, FILE_NOT_FOUND, OBJECT_NOT_FOUND, FIELD_NOT_FOUND, ROW_NOT_FOUND, KEY_NOT_FOUND, TYPE_MISMATCH, AMBIGUOUS_PACKAGE, AMBIGUOUS_OBJECT | 修正输入；缺失与解析失败分开 |
| 会话 | NOT_CONFIGURED, SESSION_BUSY, SESSION_NOT_READY, SOURCE_CHANGED, WORKER_FAILED, WORKER_MEMORY_LIMIT, OPERATION_TIMEOUT, SERVER_BUSY | 先 get_status；故障后由用户显式 remount，不隐式重试 |
| 挂载/解析 | AES_KEY_REQUIRED, AES_KEY_REJECTED, MOUNT_FAILED, MAPPINGS_REQUIRED, MAPPINGS_LOAD_FAILED, PACKAGE_PARSE_FAILED, SCRIPT_PARSE_FAILED, DECOMPRESSION_FAILED, NATIVE_DEPENDENCY_MISSING, UNSUPPORTED_CONTAINER, AMBIGUOUS_OVERRIDE, PAYLOAD_VERSION_MISMATCH | 有证据才用具体原因；usmap 存在也可能 PACKAGE_PARSE_FAILED，不能擅自断言一定映射错误 |
| 索引/任务 | INDEX_NOT_READY, INDEX_SCOPE_MISMATCH, TASK_BUSY, TASK_NOT_FOUND, INDEX_BUILD_FAILED | 声明索引范围、当前任务和下一步 |
| 预算/分页 | OUTPUT_BUDGET_EXCEEDED, PACKAGE_BUDGET_EXCEEDED, REGEX_TIMEOUT, INVALID_CURSOR, CURSOR_QUERY_MISMATCH, STALE_CURSOR | 不截断后伪装全量；建议缩小请求或调整预算 |
| 存储/导出 | CACHE_LOCKED, CACHE_IO_FAILED, CONFIG_PERSIST_FAILED, EXPORT_FAILED, EXPORT_BUDGET_EXCEEDED, EXPORT_DESTINATION_EXISTS | 保留原错误类型与安全的路径信息；清理状态明确返回 |

实现新错误码时同步本表，不得将上述可识别错误统一变成 INTERNAL_ERROR。真正未分类缺陷使用 INTERNAL_ERROR 并给日志关联 ID，不向客户端泄漏堆栈。

## 2. 配置与诊断（4 个）

### 2.1 get_status

- 输入：无。示例 `{}`。
- 前置：无，始终可调用；只读，不触发自动挂载/全盘扫描。
- data：`hostState, sessionId, workerPid, reportTimeUtc, stale, mountState, gameRoot, archiveDir, ueVersion, effectiveEngineVersion, language, sourceFingerprint, catalogRevision`。
- 计数：`archives{discovered,mounted,unmounted,unsupported}`, `files{physicalEntries,effectiveFiles,packages,conflictedPaths}`。容器总条目与去重后路径分开。
- 密钥：`keys{configuredGuids,requiredGuids,rejectedGuids}`，不返回 key。
- 映射：`mappings{configured,loaded,path,sha256,requirement}`；requirement 为 `unknown | requiredObserved | notRequiredForObservedSamples`，不是全游戏布尔断言。
- Native：每个组件 `{name,available,path,version?,errorCode?}`。
- 索引：每类 `{kind,state,snapshotId,scope,coverage,builtAtUtc}`；任务汇总 activeTaskId；最近错误摘要。
- 失败：Host 本身读取状态异常才是工具错误；Worker 挂载失败仍可成功返回诊断。

### 2.2 set_config

- 输入：`patch:object` 必填；`persist:bool=false`。字段、null 和生效顺序以架构第 5 节为准。
- 示例 `{"patch":{"ueVersion":"GAME_BlackMythWukong","language":"zh-Hans"},"persist":false}`。
- data：`applied, persisted, previousSessionId, sessionId, mountState, configSummary`，configSummary 必须脱敏。
- 副作用：重建 Worker、使旧游标失效；persist=true 写当前 --config 文件，不允许另传任意配置输出路径。
- 错误：CONFIG_INVALID / CONFIG_PERSIST_FAILED / SESSION_BUSY / 挂载与 Native 类错误。applied=true 不代表挂载完整；错误结果可携带该 data。

### 2.3 remount

- 输入：无。示例 `{}`。
- 使用 Host 当前内存配置重启 Worker、重扫 pak 目录，发现新 MOD 包；不从配置文件偷偷覆盖运行时设置。
- data：`previousSessionId, sessionId, mountState, sourceFingerprint, changed`。
- 副作用/错误：与 set_config 一致，但不持久化配置。

### 2.4 list_archives

- 输入：`limit?, cursor?`。示例 `{"limit":50}`。
- data.items：`archiveId, relativePath, format, bytes, encryptionKeyGuid, indexEncrypted, entryEncryptionState, mounted, fileCount, readOrder, mountPoint, mountError`。
- entryEncryptionState 为 `none | some | all | unknown`；索引是否加密不能代替每个数据条目是否加密。
- fileCount/readOrder 未知时 null。archiveId 用规范化相对路径和源身份生成，不用可能重复的 AES GUID。
- utoc/ucas：诊断 format 和 UNSUPPORTED_CONTAINER；ucas 是数据伴随文件，不把每个 ucas 当成可独立挂载容器。
- 失败：NOT_CONFIGURED / SESSION_NOT_READY；部分挂载仍可返回列表。

## 3. 定位与符号（6 个）

### 3.1 search_files

- 输入：`pattern:string` 必填；`mode:"glob"|"regex"="glob"`, `extension?:string`, `assetType?:string`, `scope?, limit?, cursor?`。
- 示例 `{"pattern":"**/*Skill*","mode":"glob","extension":"uasset","scope":"/Game","limit":50}`。
- glob 对 Provider 相对路径匹配：`*` 不跨 `/`，`**` 可跨，`?` 单个非分隔字符；不支持 shell brace 展开。默认忽略大小写。
- regex 是 .NET Regex，不自动锚定，CultureInvariant + IgnoreCase + 明确超时；错误或超时直接失败，不改用 glob。
- extension 可带或不带点，规范化后精确匹配；assetType 是已解析 export 的类型名，精确匹配，不是扩展名猜测。
- data.items：`filePath, packagePath?, extension, size, archiveId, readOrder, assetTypes?, typeState, shadowedEntryCount`。
- 不指定 assetType 不需要索引；指定时要求已发布 asset 索引覆盖 scope，否则 INDEX_NOT_READY/INDEX_SCOPE_MISMATCH。一个包多个 export 类型可匹配任一类型，但文件只返回一次。
- 平级覆盖冲突路径以 `typeState/conflict` 明确返回，不给可解析赢家；读取它会 AMBIGUOUS_OVERRIDE。

### 3.2 list_directory

- 输入：`path:string="/Game"`, `limit?, cursor?`。示例 `{"path":"/Game","limit":50}`。
- 只列直接子项，目录排前；data.items：目录 `{kind:"directory",path,directChildCount}`，文件 `{kind:"file",path,extension,size,archiveId?,conflict}`。
- 不返回整个子树，不加载 UObject，不依赖 asset 索引。根本不存在的目录不是空目录。

### 3.3 list_objects

- 输入：`path:string` 包路径，`type?:string`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/BP_Skill","limit":50}`。
- 仅建立包头和 export 元数据；避免 GetExports().ToArray()。
- data：`packagePath, items`；每项 `exportIndex, objectPath, objectName, classPath?, className?, outerPath?, objectFlags, serialSize, metadataState`。
- class 无法解析时保持 unknown，type 过滤的覆盖率必须说明这些未知项；不反序列化未知 export 来猜类型。
- 错误：包选择/预算/解析类错误。

### 3.4 build_asset_index

- 输入：`scope?:string`, `includeSymbols:bool=true`。
- 示例 `{"scope":"/Game/Example","includeSymbols":true}`。
- 后台任务，返回 TaskReceipt：`taskId, kind:"asset", state:"queued", sessionId, scope`。
- 对声明 scope 内全部有效包建立 export 类型；includeSymbols=true 额外读取可用 UClass/UFunction/属性元数据，不扫描 Native 二进制符号。
- 副作用：创建新的 asset 快照，完成后替换该种类的当前 head；较小 scope 会替换旧大 scope，不做隐式多范围合并。
- 单位：包。包类型成功而符号失败时分别记录 coverage 和 capability coverage；查询 search_symbols 使用符号覆盖率。
- 错误：TASK_BUSY、目录错误；任务内解析失败通过 get_task，而不是启动时虚假返回完整成功。

### 3.5 list_asset_types

- 输入：`dir?:string`, `limit?, cursor?`。示例 `{"dir":"/Game/Example","limit":50}`。
- 前置：asset 索引覆盖 dir。
- data：`countUnit:"export", items[{assetType,exportCount,packageCount}], unknownExportCount`。
- 一个包可贡献多个类型；因此 packageCount 不可简单跨类型求和。父类包含关系不自动展开。
- 按 exportCount 降序、类型名升序，游标保存计数与名称；返回对应快照 coverage。

### 3.6 search_symbols

- 输入：`pattern:string`, `kind:"any"|"class"|"function"|"property"="any"`, `scope?, limit?, cursor?`。
- 示例 `{"pattern":"*Damage*","kind":"function","scope":"/Game/Example","limit":50}`。
- pattern 使用名称 glob（`*`/`?`，忽略大小写），不支持 regex；要求 includeSymbols=true 的 asset 快照。
- data.items：`symbolId, kind, name, ownerObjectPath, objectPath?, propertyPointer?, declaredType?, flags, declaringClassPath?, availability`。
- 函数返回可直接交给 get_function 的对象路径；属性没有 UObject 路径时返回所有者与属性指针，不造一个路径。
- 不返回“已经确认可 Hook”标志。`/Script` 仅有引用或 mappings 信息时来源写明，不宣称恢复全部原生函数。

## 4. 读取（6 个）

### 4.1 get_object

- 输入：共享 `path, exportIndex?`；`depth:int=4`, `fields?:string[]`。
- 示例 `{"path":"/Game/Example/DT_Skill.DT_Skill","depth":4,"fields":["/Rows"]}`。
- data：`object{objectPath,exportIndex,classPath,sourceFilePath,archiveId}, value, projection{depth,fields,omissions}, parseStatus`。
- value 使用 CUE4Parse JSON 的 UE 内容形状；外层 DTO 由本程序定义。JSON Pointer 相对于 value 根。
- depth 是输出 JSON 容器深度：value 根容器深度 0；超过 depth 的容器用 null 占位并在 omissions 记录 pointer/reason，明确这不是资产本身的 null。
- 对象引用默认是路径/索引信息，不递归展开目标 UObject。fields/depth 不承诺减少库读取整个包的成本。
- 总字节/token 超预算时工具失败，不交付损坏的 JSON。需要大表用 get_datatable，大文件用 export_json。

### 4.2 get_datatable

- 输入：共享选择器；`mode:"rows"|"rowNames"|"schema"="rows"`, `rowName?:string`, `where?:Filter[]`, `fields?:string[]`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/DT_Skill","mode":"rows","where":[{"field":"/Damage","op":"gte","value":100}],"fields":["/Damage"],"limit":50}`。
- where 按 AND 组合；字段指针相对于**每行的值对象**，不是完整包的 /Rows。先过滤后投影，再分页。
- Filter：`{field,op,value?}`；op 为 `eq,ne,contains,gt,gte,lt,lte,exists`。eq/ne 对相同 JSON 标量类型比较；数值不转字符串；contains 只接受字符串、OrdinalIgnoreCase；exists 无 value。禁用任意 SQL/JS 表达式。
- 行缺字段：exists=false；其他比较均不匹配，包括 ne。显式 null 仅可参与 eq/ne null。where 中某字段在 schema 和所有行都不存在时 FIELD_NOT_FOUND，避免拼错字段静默空集。
- rowName 精确 Ordinal 匹配；可与 where 组合，存在但未满足过滤时返回 0 项；不存在是 ROW_NOT_FOUND。schema 模式禁止 rowName/where/fields。
- rows：`rowStruct,items[{rowName,value}],projection`；rowNames：`rowStruct,items[{rowName}]`；schema：`rowStruct,items[{fieldPointer,type,source,nullable?,availability}]`，来源可为 usmap/reflection/observed，不根据单行猜完整结构。
- RowMap 未初始化是解析失败，不是零行。库可能一次加载整表，分页只是结果分页。

### 4.3 get_class_info

- 输入：共享选择器；`section:"summary"|"parents"|"properties"|"functions"="summary"`, `includeInherited:bool=false`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/BP_Skill.BP_Skill_C","section":"properties","includeInherited":true,"limit":50}`。
- summary：`classPath,superPath,classFlags,interfaces,cdoPath,ownPropertyCount,ownFunctionCount,availability`，不附海量成员；limit/cursor 仅列表 section 有效。
- parents：从直接父类到可解析最上层，返回 `{classPath,source,resolved,stopReason?}`；遇到循环、缺包或 Native 边界明确停止。
- properties：`name,declaredType,arrayDim,flags,declaringClassPath,defaultValue?,defaultState,defaultSource?,source`。defaultState 为 `serialized | inheritedSerialized | notSerialized | unavailable`。缺失默认值不是 0/null。
- functions：`name,objectPath?,flags,declaringClassPath,availability`，参数和字节码由 get_function 读取。
- includeInherited 对 properties/functions 生效；重名覆盖保留实际声明来源，不把父类成员静默拼成子类声明。源数据不足的部分报告 warnings/availability。
- 不调用 DecompileBlueprintToPseudo 作为真实逻辑输出。

### 4.4 get_function

- 输入：共享选择器；`section:"summary"|"parameters"|"bytecode"="summary"`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/BP_Skill.BP_Skill_C:ApplyDamage","section":"bytecode","limit":50}`。
- summary：`functionPath,ownerClassPath,functionFlags,eventGraphFunction,eventGraphCallOffset,scriptStatus,serializedScriptBytes,parsedScriptBytes,expressionCount,availability`。
- parameters：`items[{name,type,flags,direction,arrayDim}]`，direction 为 in/out/inout/return，按反射声明顺序；排除没有 CPF_Parm 的局部变量。
- bytecode：`items[{expressionOrdinal,statementIndex,token,expression}],scriptStatus,parseDiagnostic`。按顶层表达式分页，嵌套表达式保持结构；statementIndex 是库的脚本逻辑索引，不标为磁盘偏移。
- scriptStatus：`notRead | empty | complete | partial | failed`；若 FUNC_Native 且无脚本，另标 `implementation:"native"`，不是反编译成功。
- bytecode partial/failed 返回 `ok=false`、SCRIPT_PARSE_FAILED，同时 data 保留已解析前缀和失败位置；允许前缀分页，但每页都必须保持错误和完整性状态。summary/parameters 可以成功报告该状态。
- 必须先完成解析文档定义的上游结构化状态补丁，不能从 ScriptBytecode.Length>0 推断 complete。

### 4.5 get_string_table

- 输入：`path:string`, `exportIndex?:int`（仅 StringTable 包）；`key?:string`, `namespace?:string`, `limit?, cursor?`。
- 示例 `{"path":"b1/Content/Localization/Example/zh-Hans/Game.locres","namespace":"Skills","key":"Skill_Name"}`。
- 根据实际文件/对象类型处理 StringTable 或 locres；`.locres` 必须通过文件 reader，不能 LoadPackage。
- data：`sourceKind,language,items[{namespace,key,sourceText?,localizedText?,resolvedText,resolution,metadata?}]`。locres 已明确携带语言时不因配置语言不同替换内容。
- locres 以 namespace+key 标识；只传 key 时返回所有 namespace 的同键项，不擅自选一个。StringTable 的 namespace 使用自身 TableNamespace。
- key 是精确 Ordinal 比较；无对应 key 报 KEY_NOT_FOUND；不做通配模糊匹配。

### 4.6 get_curve

- 输入：共享选择器；`curveName?:string`, `mode:"names"|"keys"="keys"`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/CT_Damage","curveName":"DamageByLevel","mode":"keys","limit":50}`。
- 支持 UCurveTable 与 UCurveFloat。names 返回曲线行名；单个 UCurveFloat 规范曲线名是 FloatCurve。
- 多曲线且 keys 模式缺 curveName 报 AMBIGUOUS_OBJECT，建议 names；曲线不存在报 KEY_NOT_FOUND。
- data：`curveName,curveKind,defaultValue?,preInfinityExtrap?,postInfinityExtrap?,items[{keyIndex,time,value,interpMode?,tangentMode?,tangentWeightMode?,arriveTangent?,leaveTangent?,arriveTangentWeight?,leaveTangentWeight?}]`。
- 保留原始 keyIndex，不改写重复时间点；按 time、keyIndex 排序。未存储的属性是 null/缺失状态，不填零。Vector/LinearColor 曲线不在 v1 专用契约内，可 get_object。

## 5. 文本与包依赖（4 个）

### 5.1 build_text_index

- 输入：`scope?:string`。示例 `{"scope":"/Game/Example"}`。
- 使用当前配置 language，返回 kind=text 的 TaskReceipt。
- 只索引 StringTable、locres 和 DataTable 中字符串/FText 值，保留 namespace/key/tableId/rowName/fieldPointer。语言资源解析可能在 scope 外建立临时字典，但最终索引文档只属于 scope 内来源。
- 不要求先建 asset 索引；本任务自行读取范围内包头发现目标类型。已发布 asset 索引可提供候选线索，仍以当前包验证为准。
- 单位：范围内需要分类的包加 locres 文件；无目标文本的包成功分类后计 succeeded，而非被当作解析失败。
- 副作用/错误：与 build_asset_index 同类。

### 5.2 search_text

- 输入：`keyword:string` 非空；`scope?, sourceKind?:"datatable"|"stringTable"|"locres"`, `limit?, cursor?`。
- 示例 `{"keyword":"定身","scope":"/Game","limit":50}`。
- 前置：当前语言的文本快照覆盖请求 scope。不自动触发扫描。
- 语义：Unicode FormKC + ToUpperInvariant 后的子串匹配；不是分词相关性搜索，不执行关键词中的 SQL/FTS 运算符。
- data.items：`textId,sourceKind,sourcePath,objectPath?,rowName?,fieldPointer?,namespace?,key?,tableId?,text,sourceText?,language,resolution,identityLinks[]`。
- identityLinks 只包含可验证的 namespace/key 或 tableId/key 等关联，附目标来源；不用相同中文字符串直接证明同一业务 ID。
- 排序：sourcePath、对象、rowName、fieldPointer、textId；同文本不同出处保留，不去重成一条。
- 使用当前索引 coverage；短词的查询成本规则见解析文档。

### 5.3 build_reference_index

- 输入：`scope?:string`。示例 `{"scope":"/Game/Example"}`。
- 返回 kind=reference 的 TaskReceipt。
- 对 scope 内有效包读取 import/export 表和 Outer 链，建立 `packageImport` 包依赖边；目标可以在 scope 外或为 `/Script/...`。
- 不需要反序列化所有 export，不读取对象字符串猜引用，不声称包含全部 SoftObjectPath 或运行时调用。
- 单位：包；未解析目标路径记录 unresolved，不悄悄删边。任务输出声明 `granularity:"package"` 和 `edgeKind:"packageImport"`。

### 5.4 find_references

- 输入：`path:string`, `direction:"incoming"|"outgoing"`, `limit?, cursor?`。
- 示例 `{"path":"/Game/Example/DT_Skill","direction":"incoming","limit":50}`。
- 若传对象路径，解析为包后查询，data 明确 `requestedPath,queryPackagePath,granularity:"package"`，不能冒充精确到该对象。
- data.items：`sourcePackage,targetPackage?,targetRawName?,edgeKind,evidenceCount,resolution,exampleImportIndex`。
- outgoing 要求该源包在快照范围内；incoming 查所有已索引源包的入边，查询目标可在范围外或是 `/Script/...`，不要求目标可本地反序列化。
- 返回 coverage 与 `completeFor:"indexedScope"`。只有 scope 为完整已发现有效目录、catalogComplete=true、所有源包成功时，才可声明“对当前目录快照的包 import 入边完整”。
- 没有已发布快照是 INDEX_NOT_READY；范围不足的 outgoing 是 INDEX_SCOPE_MISMATCH。0 条边不是“游戏里绝对无人引用”。

## 6. 导出与任务（4 个）

### 6.1 export_raw

- 输入：`path:string` 包路径；`outDir:string`，outputRoot 下相对目录。
- 示例 `{"path":"/Game/Example/BP_Skill","outDir":"skill-research"}`。
- 返回 kind=exportRaw 的 TaskReceipt，不把原始字节传回 MCP。
- 范围：当前有效主包及同一来源版本所需的 uexp/ubulk/uptnl 等实际伴随文件；导出是解密/解压后的逻辑文件，不是 pak 内压缩块。
- 任务成功 result：`exportId,outputDirectory,manifestPath,format:"legacyPackage",compatibility:"notVerified",files[{relativePath,bytes,sha256,sourceArchiveId}]`。manifest 与完成目录一起提交。
- 不保证 UAssetGUI/kismet-analyzer 一定支持该版本。不存在的可选伴随文件不伪造；必要文件缺失或跨版本冲突明确失败。

### 6.2 export_json

- 输入：共享 `path, exportIndex?`；`outDir:string`。
- 示例 `{"path":"/Game/Example/DT_Skill","outDir":"skill-research"}`。
- 包路径且不指定 exportIndex：导出该包全部 export；对象路径或 exportIndex：只导出选定对象。这是与 get_object 的明确差别。
- 流式写 JSON：外层 `{formatVersion,source,exports:[...]}`，exports 内使用 CUE4Parse JSON；没有 fields/depth 在线投影，不截断。UObject 引用保持引用而不是无限展开。
- 任何选定对象解析失败或 ScriptBytecode 被标为 partial/failed，整次导出失败；希望研究残缺字节码时用 get_function 的显式前缀，不把完整 JSON 导出降级。
- 成功 result：`exportId,outputDirectory,manifestPath,format:"cue4parseJson",files[{relativePath,bytes,sha256}],objectCount,parserBaseline`。
- 原子提交、预算、路径规则与原始导出共用。

### 6.3 get_task

- 输入：`taskId:string`, `includeErrors:bool=false`, `limit?, cursor?`（分页仅用于 errors）。
- 示例 `{"taskId":"task-example","includeErrors":true,"limit":50}`。
- data：`taskId,kind,state,sessionId,stale,scope?,startedAtUtc,finishedAtUtc?,phase,currentSubject?,totalUnits?,processedUnits,succeededUnits,failedUnits,skippedUnits,cancelRequested,result?,errors?`。stale=true 表示上个会话的历史任务或 Worker 失联后的最后报告；历史 completed 不自动改成失败，但不表示它的索引仍是当前 head。
- state：`queued | running | cancelling | completed | completedWithErrors | cancelled | failed | interrupted`。
- result 是已发布快照摘要或导出 manifest；任务未完成时 null。completedWithErrors 仅用于可发布部分覆盖的索引，导出不存在该成功状态。
- errors 每项 `{subject,stage,code,message}`，按固定序号分页。Worker 死亡时 Host 用最后状态返回 interrupted/stale，明确错误清单可能尚未收到。
- 活动任务的错误列表仍会增长，因此 includeErrors=true 的游标绑定错误列表版本；变化后 STALE_CURSOR，建议任务结束后分页取完整失败清单。
- 任务 ID 不存在报 TASK_NOT_FOUND。进程重启后可从同一个 cacheRoot 的任务表恢复终态；Host 的旧内存状态只保证本次连接期间可查。

### 6.4 cancel_task

- 输入：`taskId:string`。示例 `{"taskId":"task-example"}`。
- data：`taskId,previousState,state,cancelRequested,canInterruptCurrentUnit`。
- queued 立即 cancelled；running 设置 cancelling，在当前包/写入安全点检查；不得立即虚报 cancelled。
- 已终态返回原状态、cancelRequested=false，不破坏已发布结果。
- 若当前同步操作一直不返回，由架构超时规则终止 Worker；状态 interrupted 而非优雅 cancelled。
- 副作用：丢弃未发布索引快照或未提交导出目录，保留上一次已发布索引。失败清理位置可在任务诊断查询。
