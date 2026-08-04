# 将 Klotho 0.7.1（除 Godot 外）集成到 Friflo.Engine.ECS.Deterministic

> 状态：**已实施**（分支 `feat/klotho-integration`）
> 落档日期：2026-08-04
> 来源：Klotho 0.7.1 @ `H:\Test\Git\Klotho-0.7.1`

---

## 实施结果（2026-08-04）

**验收全部通过**：`dotnet build Engine.sln`（Debug + Release）0 错误；现有 `Tests` 656/656、
`Tests-internal` 133/133 全绿；新增 `Klotho.Tests` 50/50 通过。
Klotho 源码经 `diff -r` 校验与上游**逐字节一致，改动 0 行**。

### 实施中推翻的三个方案假设

调研阶段的三条结论在实施时被实测证伪，方案已据此调整：

| # | 原方案假设 | 实测结果 | 采取的方案 |
|---|---|---|---|
| 1 | 对 Friflo 改动 **1 行**（`InternalsVisibleTo`）即可 | `EntityStore.intern`（含 `sequenceId`/`recycleIds`）与 `EntityStoreBase.archsCount` 是 **private**，`InternalsVisibleTo` 够不着 | 两者均为 `partial`，改用**新增文件** `DeterministicAccess.cs` 内部补充访问器。对既有文件的改动降到 **6 行 `partial` 关键字 + 1 行 `InternalsVisibleTo` + 4 处 `ClearIndex`/1 处 `RebuildLinkLookup` 新增方法**，共 43 行新增、6 行修改 |
| 2 | 关系数据"已被原型快照覆盖" | **证伪**。`AbstractEntityRelations` 用 `new Archetype(config, heap)` 直接构造，**绕过 `AddArchetype`**，关系原型不在 `store.archs[]` 中（实测 `ArchetypeCount` 加关系后仍为 2，其 `archIndex` 越界） | 快照/哈希/序列化均**单独遍历** `extension.relationsMap[i].archetype` |
| 3 | 组件"含引用字段会破坏快照"，故契约禁止 | 过严。`ILinkRelation` **必须**含 `Entity` 字段（它就是关系键），一刀切会让关系功能不可用。`Entity` 含 store 引用（进程相关），但其身份 `(Id, Revision)` 是确定的 | 新增 `DeterministicComponentLayout.cs`：按组件类型编译一次"叶子计划"，`Entity` 只贡献 `(Id, Revision)`、跳过 store 引用，还原时重新绑定到本地 store；含其它引用类型才报错 |

### 另外两处偏离

- **状态哈希不按 archIndex+entityId 排序**，改为**每实体独立哈希后求和**（可交换累加）。原因：实体在原型内的槽位会因删除时"末位补空"而变动，按存储顺序哈希会把正常的布局变化误报成 desync。已有测试 `StateHash_IsIndependentOfArchetypeSlotOrder` 专门守住这一点。
- **`Tick` 不使用 `UpdateTick` 传时间**：其字段是 `float`，做帧计数超过 2^24 会丢精度。系统应从 `CurrentTick` 取帧号、用定点常量做步长。

### 验证示例的一个真实教训

示例最初把帧计数器放在 `SpawnSystem` 的字段里，`SyncTest` 立刻在 tick 5 报 desync——**存储之外的模拟状态回滚时不会被还原**。已改为 `SimClock` 组件存入 store。这正是该验证要抓的问题，保留在 `SampleSimulation.cs` 的注释中作为约束说明。

### 第二轮补充（层级 / Script / NativeAOT）

原方案把父子层级与 `Script` 列为"契约禁用"、NativeAOT 列为未验证。这三项已全部补齐：

| 项 | 原状态 | 现状态 |
|---|---|---|
| 父子层级（`AddChild`） | ❌ 契约禁用 | ✅ 快照 `parentMap`，并从捕获的子 id 重建 `childHeap` |
| `Script` | ❌ 契约禁用 | ✅ 保留实例身份，按类型计划存取其字段值 |
| NativeAOT | ⚠️ 未验证 | ✅ 已实测（见下） |

**层级的关键陷阱**：`TreeNode` 存的是 `IdArray`，即 `(start, count)` —— `start` 是**池分配器偏移**，属于分配历史而非状态。两端持有完全相同的树，偏移也可能不同。若照常按字节哈希，会报出并不存在的 desync。因此 `TreeNode` 改为**按子 id 内容哈希**，还原时重建整个池。已有测试 `StateHash_IsIndependentOfChildAllocatorHistory` 专门制造两端偏移不同的场景来钉死这点。

**Script 的取舍**：Script 是 class，回滚时**保留原实例、只写回字段值**——若换成新对象，游戏代码持有的引用会全部失效。

**NativeAOT 实测结论**：
- 布局计划改为双路径：能生成代码时编译表达式树，`RuntimeFeature.IsDynamicCodeSupported == false` 时改用反射解释。原实现无条件用表达式树，AOT 下必然在第一次哈希时抛异常。
- 新增 `src/Klotho/AotSmoke`（`PublishAot=true`，故 `IsDynamicCodeSupported` 实为 false），8 项检查全通过：关系回滚、Script 回滚、关系哈希按身份而非地址、全量状态往返。
- ILC 分析：**19 条警告全部来自 `Friflo.Json.Fliox`（既有依赖），本次新增代码 0 条**。
- 测试 `InterpretedHash_MatchesCompiledHash` 断言两条路径哈希一致——否则 AOT 客户端会与 JIT 客户端互相 desync。

> **诚实边界**：本机 native 链接因缺 `vswhere.exe`（VS 工具链问题）未能产出 exe，故**未在真机原生二进制上跑过**。ILC 全量分析已完成且无本代码告警，反射路径也在 `IsDynamicCodeSupported=false` 下实测通过。

### 已知遗留

- 单次 `Tests` 运行中曾出现 1 例失败，其后 15 次连续运行均 656/656 通过，**未能复现、未能定位到具体用例**，故如实记录而非判定为无害。
- NativeAOT 未完成 native 链接产物验证（原因如上，属本机环境问题）。
- 第二阶段（真实网络）尚未验证，风险 3 仍然成立。

## Context

本仓库是 Friflo ECS 的一个副本（net10.0 / C# 14），名字表明它本就是为确定性改造准备的。Friflo 是高性能 archetype ECS，但**没有任何联机/确定性能力**：没有回滚、没有帧同步、没有定点数学、没有状态哈希（`Engine.Hub` 只是 Fliox 数据库同步，与游戏网络无关）。

Klotho 0.7.1 恰好提供这些：CSP/回滚/帧同步/Server-Driven/回放，纯 C# 引擎无关核心，且已有现成的 net8.0 构建（`Server~/xpTURN.Klotho.Runtime.csproj`）。

**目标形态**：Friflo 为主——游戏代码用 Friflo 的 `EntityStore`/`SystemRoot`/`Query`，Klotho 提供它没有的联机与确定性设施。为此需要一个桥接层，把 Friflo 的 `EntityStore` 适配成 Klotho 的 `ISimulation`。

**核心约束**：尽可能不改动现有源码，增量扩展。本方案对 Friflo 的改动为 **1 行**（`InternalsVisibleTo`），对 Klotho 的改动为 **0 行**。

---

## 前置阻塞项（必须先解决）

**本机无法构建本项目**。实测：

```
error NETSDK1045: 当前 .NET SDK 不支持面向 .NET 10.0
```

本机仅有 SDK 6.0.421 / 8.0.419，本项目面向 `net10.0`。需先安装 .NET 10 SDK：

```
winget install Microsoft.DotNet.SDK.Preview
```

或从 https://aka.ms/dotnet/download 下载。装完 `dotnet --list-sdks` 应见 10.x。**在此之前无法达成验收标准。**

---

## 已确认的关键事实（调研结论，非假设）

| 事实 | 位置 | 对方案的意义 |
|---|---|---|
| `StructHeap<T>.components` 是连续 `T[]` | `src/ECS/Archetype/StructHeap.generic.cs:34` | 快照可用 `Array.Copy` 批量拷贝，零分配 |
| 类注释明示"不应包含其它字段" | 同上 `:32` | 每堆状态边界清晰 = `components` + `componentStash` |
| **原型永不销毁**（只有 `archsCount++`，无移除路径） | `src/ECS/Archetype/EntityStore.Archetype.cs:188` | 快照存 `archIndex` 整数即可，绝不会悬垂 |
| `Archetype` 结构性字段全 `readonly` | `src/ECS/Archetype/Archetype.cs:77-88` | 只需快照 `entityIds`/`entityCount`/`structHeaps[].components` |
| `NewId()` 走 `++sequenceId` + LIFO 回收栈 | `src/ECS/Entity/Store/NodeTree.cs:485` | 实体 ID 分配确定，回滚可复现 |
| `new EntityStore()` 默认 `PidType.UsePidAsId` | `src/ECS/EntityStore.cs:199` | 默认无随机 PID，确定性成立 |
| `EntityNode` 含 `archetype`(引用) + `compIndex` + `revision` | `src/ECS/Entity/EntityNode.cs:59-63` | 快照需存 `archIndex` 而非引用；`revision` 必须一并还原 |
| `revision` 在删除时 `++` | `src/ECS/Entity/Store/NodeTree.cs:535` | 回滚必须还原 revision，否则旧 `Entity` 句柄失效判断错乱 |
| **`StoreExtension` 有 5 处独立旁路状态** | `src/ECS/Entity/Store/Extension.cs:33-61` | parentMap/childHeap/scripts/indexMap/relationsMap —— 见下方风险 |
| **关系的数据存在普通 Archetype+StructHeap 里** | `src/ECS/Relations/Internal/AbstractEntityRelations.cs:27,30` | 关系数据**已被原型快照覆盖**，只有查找表是旁路状态 |
| 索引表是组件值的纯派生数据 | `src/ECS/Index/Indexes/ValueStructIndex.cs:19` | `Dictionary<TValue,IdArray>` 可从组件值完全重建 |
| 关系查找表 = `positionMap`/`linkEntityMap`/`idHeap` | `AbstractEntityRelations.cs:33,41,43` | 同样可从关系原型数据重建 |
| 组件约束仅 `where T : struct`（非 `unmanaged`） | `src/ECS/Archetype/StructHeap.generic.cs:22` | 组件含引用字段会破坏快照语义，需契约约束 |
| Klotho `SyncTestRunner` 硬绑 `EcsSimulation` | Klotho `Core/Sync/SyncTestRunner.cs:51` | **不可复用**，必须自写验证器 |
| Klotho `ICommand` 是纯手写接口 | Klotho `Core/ICommand.cs` | 生成器非硬依赖，可手写序列化 |
| Klotho `KUnsafe` 在 net8+ 走 BCL `Unsafe` | Klotho `LowLevel/KUnsafe.cs` | unsafe 仅局部，且 `AllowUnsafeBlocks` 是 per-project |
| FP64 数学/物理与 Klotho ECS **零耦合** | Klotho `Deterministic/` | 白捡，可直接用于 Friflo |
| Klotho `Gameplay/` 4 个 System **全绑** Klotho ECS | Klotho `Gameplay/Systems/*.cs` | 对 Friflo 无用，仅作参考 |

---

## 方案

### 目录结构

```
<repo root>/
├── Engine.sln                        ← 仅新增项目条目
├── src/
│   ├── ECS/                          ← 仅 CompilerServices.cs +1 行
│   │   └── Base/CompilerServices.cs
│   ├── Hub/ Tests/ apps/ ...         ← 完全不动
│   └── Klotho/                       ← 全新
│       ├── Runtime/                  Klotho 核心源码副本（~510 文件）
│       ├── Server/                   KlothoServer + LiteNetLib
│       ├── Bridge/                   桥接层（新写，本方案核心）
│       ├── Tests/                    确定性验证（新写）
│       ├── Analyzers/                KlothoGenerator.dll
│       ├── Tools/                    DeterminismVerification / PhysicsDeterminismProbe
│       └── Docs/                     Klotho 文档（去 *.Godot.md）
```

**不引入**：Klotho `Unity/` 适配层与 Unity 示例（依赖 UnityEngine + UniTask，此纯 .NET 解决方案编译不了，且其视图层是照 Klotho 自带 ECS 写的，对 Friflo 参考价值低）。

### 唯一的 Friflo 改动

`src/ECS/Base/CompilerServices.cs` 追加 1 行：

```csharp
[assembly: InternalsVisibleTo("Tests-internal")]
[assembly: InternalsVisibleTo("Fliox.Tests-internal")]
[assembly: InternalsVisibleTo("Klotho.Friflo.Bridge")]   // ← 新增
```

纯新增，不改变任何现有行为，不影响现有 11 个项目。

### 新增项目

| 项目 | TFM | 说明 |
|---|---|---|
| `Klotho.Runtime` | net8.0 | 源码链接 `src/Klotho/Runtime/**`，仿 Klotho 自带 `Server~` csproj 的 Exclude 规则（排除 `Unity/`、`**/Json/`）。`AllowUnsafeBlocks=true`（隔离在本程序集内，不影响 Friflo 的"无 unsafe"主张） |
| `Klotho.Friflo.Bridge` | net8.0 | 引用 `Klotho.Runtime` + `Friflo.Engine.ECS`。**本方案核心**，见下 |
| `Klotho.Server` | net8.0 | KlothoServer + LiteNetLib 传输（第二阶段真实联机用） |
| `Klotho.Tests` | net8.0 | NUnit 3（对齐 `src/Tests/Tests.csproj` 现有约定） |

`Friflo.Engine.ECS` 多目标含 net8.0，桥接层以 net8.0 引用即可，无需改其 TFM。

### 桥接层设计（`src/Klotho/Bridge/`）

**核心类型 `FrifloSimulation : ISimulation`** —— 实现 Klotho 的 12 个成员：

| ISimulation 成员 | 实现方式 |
|---|---|
| `Tick(List<ICommand>)` | 应用命令 → `SystemRoot.Update(tick)` |
| `SaveSnapshot()` | 环形缓冲存快照，键 = 执行 tick K 之前的状态（Klotho 契约） |
| `Rollback(int)` | 从环形缓冲还原 store |
| `GetNearestRollbackTick(int)` | 查环形缓冲最近可还原 tick |
| `GetStateHash()` | 遍历原型，对组件字节做 FNV/xxHash，**按 archIndex+entityId 排序**保证顺序确定 |
| `SerializeFullState` / `RestoreFromFullState` | 全量状态序列化（late-join / resync 用） |
| `Reset` / `OnPlayerJoined` / `EmitSyncEvents` 等 | 直接实现 |

**快照器 `FrifloStoreSnapshot`** —— 三层存储 + 一层重建，全量支持实体生灭、索引组件、关系：

```
[存] 第1层 组件数据：每原型 structHeaps[i].components → Array.Copy 到复用缓冲
                     （关系数据也在此层——关系存于普通 Archetype+StructHeap）
[存] 第2层 实体结构：每原型 entityIds[] + entityCount
[存] 第3层 store 状态：nodes[]（存 archIndex 而非 archetype 引用）+ compIndex
                       + revision + sequenceId + recycleIds 栈
[建] 第4层 查找表：  回滚后从组件值重建
                     - indexMap[]     : ValueStructIndex.entityMap 等
                     - relationsMap[] : positionMap / linkEntityMap / idHeap
```

**为何第 4 层重建而非深拷**：索引表 `Dictionary<TValue,IdArray>` 是组件值的纯派生数据；关系查找表同理（真实数据已在第 1 层）。这些是引用类型堆结构，无法 `Array.Copy`，每帧深拷会持续分配且极易出错。重建方案让"表与数据不一致"在结构上不可能发生，代价仅为**回滚时**一次 O(实体数) 扫描（非每帧）。

还原时按 `archIndex` 反查 `store.archs[]`（原型永不销毁，索引恒稳）。第 1–3 层缓冲复用，稳态零分配。

**确定性守卫（契约文档 + 少量断言）**：
- 模拟 store 与表现 store 分离（`simStore` 只在 `Tick` 内修改）
- 禁止 `JobExecution.Parallel` / `QueryJob`（破坏确定性）
- 禁止 `PidType.RandomPids`（默认已是 `UsePidAsId`）
- 组件必须是全值类型（`where T : struct` 允许引用字段，但引用字段会破坏快照）

**验证器 `FrifloSyncTestRunner`**（新写，~200-300 行）：
Klotho 的 `SyncTestRunner` 第 51 行 `simulation as EcsSimulation ?? throw` 硬绑其自带 ECS，无法复用。照搬其逻辑（哈希环 / 回滚重算 / 逐帧比对），但**只调用 `ISimulation` 接口**。Klotho 与 Friflo 源码均不改，且 Klotho 升级不冲突。

### 验证示例（`src/Klotho/Tests/`）

最小可信示例，覆盖快照全部四层：

```
MoveSystem   : pos += vel * dt        (FP64，压测第1层 组件数据)
BounceSystem : 碰壁反弹                (压测第1层)
SpawnSystem  : 每 N 帧生成 / 到期销毁   (压测第2、3层 实体结构 + nodes/revision)
TargetSystem : [IIndexedComponent] Team 按阵营查敌
               [IRelation] Targeting 锁定目标   (压测第4层 索引/关系重建)
```

---

## 风险与诚实边界

### 风险 1：`StoreExtension` 旁路状态

`src/ECS/Entity/Store/Extension.cs` 有 5 处独立于原型的状态。本方案的覆盖情况：

| 旁路状态 | 第一阶段处理 |
|---|---|
| `indexMap`（索引组件） | ✅ **回滚后重建**（第 4 层） |
| `relationsMap`（关系） | ✅ **数据在第 1 层，查找表回滚后重建** |
| `parentMap` + `childHeap`（父子层级） | ✅ **已覆盖**（第二轮补充，见上） |
| `entityScripts` / `scriptMap`（Script） | ✅ **已覆盖**（第二轮补充，见上） |
| `pid2Id` / `id2Pid` | ✅ 不适用（默认 `UsePidAsId`，无此映射） |

> ~~第一阶段契约：模拟 store 不得使用父子层级（`AddChild`）与 `Script`。~~
> **已作废**：层级与 Script 已在第二轮补齐。索引组件（`IIndexedComponent`）、关系
> （`IRelation`）、父子层级与 `Script` **均受支持**。

层级/Script 通常属于表现层而非确定性模拟层，故排除代价低。若后续模拟逻辑需要，第二阶段扩展快照器覆盖 `parentMap`/`childHeap`（`IdArrayHeap` 结构较复杂，工作量明显增加）。

**重建方案的待验证点**：索引/关系重建需调用 Friflo 内部 API 逐实体重新登记。若 Friflo 未暴露合适的批量重建入口，可能需要通过"先清空 index/relations、再逐实体重新 Add"实现——这会在实现阶段确认。若该路径不可行，会立即报告并给出替代方案（深拷或降级为契约禁用），不静默降级。

### 风险 2：验收标准依赖 SDK 安装

未装 .NET 10 SDK 前，无法编译、无法跑验证、无法达成验收标准。

### 风险 3：Klotho 网络层与桥接层的耦合面未完全验证

`KlothoEngine` 有约 15 处 `_simulation is EcsSimulation` 类型判断（诊断/内存采样/错误注入等）。这些分支对非 `EcsSimulation` 会静默跳过（不抛异常），预期不阻塞第一阶段（单进程双实例验证不走网络路径）。但**第二阶段接真实网络时可能暴露更多硬耦合点**——届时若需修改 Klotho 源码，会先告知再动。

---

## 执行步骤

1. `git checkout -b feat/klotho-integration`
2. 复制 Klotho 源码到 `src/Klotho/`（Runtime / Server / Analyzers / Tools / Docs，去 Godot 与 Unity）
3. 新建 4 个 csproj，挂进 `Engine.sln`
4. `src/ECS/Base/CompilerServices.cs` 加 1 行 `InternalsVisibleTo`
5. 实现 `FrifloStoreSnapshot` 第 1–3 层（组件数据 / 实体结构 / nodes）
6. 实现第 4 层：索引与关系查找表重建
7. 实现 `FrifloSimulation : ISimulation`
8. 实现 `FrifloSyncTestRunner`
9. 写验证示例（Move / Bounce / Spawn / Target 四个 System）
10. 跑验证，迭代至通过

---

## 验证方式（验收标准）

```bash
# 1. 现有项目未被破坏（回归）
dotnet build Engine.sln
dotnet test src/Tests/Tests.csproj          # 现有 122 个测试文件必须全绿

# 2. 新增项目构建
dotnet build src/Klotho/Bridge/Klotho.Friflo.Bridge.csproj

# 3. 确定性验证（核心验收）
dotnet test src/Klotho/Tests/Klotho.Tests.csproj
```

**通过标准**：
- 现有测试全绿（证明"不改动现有项目"成立）
- `FrifloSyncTestRunner` 双实例跑 N 帧，每帧强制回滚重算，**逐帧状态哈希完全一致**
- 覆盖四条路径：组件数据变化、实体创建、实体销毁、**索引/关系回滚后一致**
- 索引/关系专项断言：回滚后按索引查询的结果集与回滚前同 tick 完全一致
- 稳态快照零 GC 分配（`GC.GetAllocatedBytesForCurrentThread()` 断言，**仅针对第 1–3 层**；第 4 层重建只在回滚时触发，不计入每帧稳态）

---

## 决策记录（grill 问答结论）

| # | 决策 | 选择 | 被否方案及原因 |
|---|---|---|---|
| 1 | 谁持有模拟状态 | Friflo 为主 + 桥接层 | 不选"两套 ECS 并存"（只是co-location非集成）；不选"弃用 Klotho ECS"（需重改 Klotho 源码） |
| 2 | 模拟层数值类型 | 自定义 FP64 组件 | 不用 Friflo 内置 float 组件（跨平台 desync 风险） |
| 3 | 快照访问方式 | 加 1 行 `InternalsVisibleTo` | 不选纯公开 API（`CloneEntity` 每帧分配，60Hz 跑不动） |
| 4 | 验收标准 | 单进程双实例确定性验证 | 不选"能编译即可"（风险后置） |
| 5 | 验证器实现 | 自写只依赖 `ISimulation` | 不改 Klotho 的 `SyncTestRunner`（升级需重打补丁） |
| 6 | 状态隔离 | 模拟/表现双 store，约束靠文档 | 不加运行时守卫（额外开销） |
| 7 | 快照范围 | 全量（支持实体生灭） | 不选"实体集合固定"（限制游戏写法） |
| 8 | 落地方式 | 新分支 + 复制到 `src/Klotho/` | 不选源码链接（依赖外部绝对路径） |
| 9 | 集成范围 | 除 Godot、Unity 外全部 | Unity 适配层编译不了且参考价值低 |
| 10 | 索引/关系回滚 | 回滚后重建查找表 | 不选深拷字典（每帧分配、易不一致） |
