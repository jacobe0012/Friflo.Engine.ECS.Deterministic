# 将 Klotho 0.7.1（除 Godot 外）集成到 Friflo.Engine.ECS.Deterministic

> 状态：**已实施**（分支 `feat/klotho-integration`）
> 落档日期：2026-08-04，第六至九轮补充于 2026-08-05，第十、十一轮（对抗性复核）2026-08-05，
> 第十二轮（房间生命周期 / Release 构建）、第十三轮（组件哈希按字读）、第十四轮（收益复测）2026-08-05
> 来源：Klotho 0.7.1 @ `H:\Test\Git\Klotho-0.7.1`

> **Unity-layout 后续修复（2026-10-19）**：当前分支的 FullState 为 **v5**，不与 v4 混用；
> NativeAOT 嵌套值类型需调用 `RegisterDeterministicType<T>()`；SD 客户端先完成权威配置握手再构造引擎。
> 下文保留历史阶段记录；当前使用合同见 [UNITY-LAYOUT.md](../UNITY-LAYOUT.md#deterministic-state-and-nativeaot-registration)。

---

## 实施结果（2026-08-04；第六至九轮 2026-08-05）

**验收全部通过**：`dotnet build Engine.sln`（Debug + Release）0 错误
（**第十二轮更正**：Release 这半句在第十二轮之前**不成立**——测试项目读了 `#if DEBUG` 内的
`KlothoEngine.Stats`，Release 下 4 个错误，故此前所有"全绿"都只是 Debug 的结论）；现有 `Tests` 656/656、
`Tests-internal` 133/133 全绿；新增 `Klotho.Tests` **86/86** 通过（第十轮 +3，第十一轮 +12，第十二轮 +4，第十三轮 +4）；
NativeAOT 真机二进制 12/12 通过（第三、五轮，见下）；
**P2P 真实联机双引擎逐 verified-tick 哈希一致**（第四轮，见下）；
**ServerDriven 服务端 + 双客户端逐 verified-tick 哈希一致**（第五轮，见下）；
**丢包/乱序/重复链路下仍一致**（第六轮）；**late-join / reconnect / spectator 三条流程**（第七轮）；
**真 UDP 跨进程 2/3 人局**（第八轮）；**单 socket 多房间**（第九轮）。
Klotho 源码经 `diff -r` 校验与上游**逐字节一致，改动 0 行**。

### 实施中推翻的三个方案假设

调研阶段的三条结论在实施时被实测证伪，方案已据此调整：

| # | 原方案假设 | 实测结果 | 采取的方案 |
|---|---|---|---|
| 1 | 对 Friflo 改动 **1 行**（`InternalsVisibleTo`）即可 | `EntityStore.intern`（含 `sequenceId`/`recycleIds`）与 `EntityStoreBase.archsCount` 是 **private**，`InternalsVisibleTo` 够不着 | 两者均为 `partial`，改用**新增文件** `DeterministicAccess.cs` 内部补充访问器。对既有文件的改动降到 **6 行 `partial` 关键字 + 1 行 `InternalsVisibleTo` + 4 处 `ClearIndex`/1 处 `RebuildLinkLookup` 新增方法**，共 43 行新增、6 行修改（**第十一轮追加**：`IdArrayPool.Clear()` 与 `IdArrayHeap.Clear()` 两个新增方法，纯新增，既有路径不调用）|
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
- 新增 `src/Klotho/AotSmoke`（`PublishAot=true`，故 `IsDynamicCodeSupported` 实为 false），覆盖：关系回滚、Script 回滚、关系哈希按身份而非地址、全量状态往返。
- 测试 `InterpretedHash_MatchesCompiledHash` 断言两条路径哈希一致——否则 AOT 客户端会与 JIT 客户端互相 desync。

### 第三轮：AOT 真机二进制实测（2026-08-04 复核）

上一轮记的「8 项检查全通过」是在 JIT 下跑 `dotnet test` 得出的，与同段落自述的
「未在真机原生二进制上跑过」自相矛盾。本轮补跑真机二进制，**推翻了该结论**。

先解决链接：本机 VS 18 + MSVC 14.50 工具链齐备，`vswhere.exe` 也在
`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`。ILC 以**无路径**方式调用
`vswhere.exe`，它不在 PATH 上 → 其报错文本被拼进链接器路径 → `MSB3073 exit 123`，
看起来像缺工具链，实为缺 PATH 条目。补上 PATH 后成功产出
`Klotho.AotSmoke.exe`（7.4 MB）。

首次真机运行 **8 项里 2 项 FAIL + 1 处未捕获异常**（进程 exit 127）：

| 现象 | 根因 | 修复 |
|---|---|---|
| `[FAIL] script field restored by rollback`、`[FAIL] entity reference in script restored` | Script 字段经**运行期类型**发现（`ScriptLayout` → `DeterministicPlan.Get(script.GetType())`），无任何静态引用为 ILC 锚定，字段元数据被裁剪 → `GetFields` 返回空 → 计划 0 个 leaf → capture/restore **静默变成 no-op** | `NativeAOT.RegisterScript<T>` 的泛型参数加 `[DynamicallyAccessedMembers(PublicFields \| NonPublicFields)]`，在必经注册点锚定字段 |
| `InvalidOperationException: Native AOT requires registration of IIndexedComponent` | `AotSmoke` 自身注册顺序 bug：`Register*` 均由 `if (typeSet.Add(typeof(T)))` 把关，**重复注册整段跳过**。先调 `RegisterComponent<Team>()` 使随后的 `RegisterIndexedComponentStruct<Team,int>()` 静默失效，索引工厂从未安装 | 删掉多余的 `RegisterComponent<Team>()`（专用方法本就注册组件类型），并注释说明该去重语义 |

**关于「ILC 0 条本代码告警」**：该说法当时成立但具误导性——
`DeterministicComponentLayout.cs` 上的 `[UnconditionalSuppressMessage("TrimAnalysis", "IL2070")]`
压掉的正是会报告此事的告警，且其 justification（「类型已由 entity schema 显式注册」）
本身就是错的：**注册类型 ≠ 保留其字段元数据**。该 justification 已订正为如实描述真实前提。

**防止静默复发**：`ScriptLayout` 现在对空计划直接抛错。每个 `Script` 都继承基类
`entity` 字段，故 0 leaf 不可能合法；若锚定再次丢失，会当场报错而非悄悄丢状态、
在很久之后表现为无法解释的 desync。

修复后真机二进制 **8/8 通过，退出码 0**：

```
IsDynamicCodeSupported = False
  [ok] relation removed / restored / target preserved
  [ok] script field restored by rollback
  [ok] entity reference in script restored
  [ok] script instance identity kept
  [ok] relation hash is identity-based, not address-based
  [ok] full state round-trips with relations
AOT smoke: PASS
```

复核方式（`vswhere` 必须在 PATH 上，否则链接失败）：

```bash
PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer" \
  dotnet publish src/Klotho/AotSmoke/Klotho.AotSmoke.csproj -r win-x64 -c Release
./src/Klotho/AotSmoke/bin/Release/net8.0/win-x64/publish/Klotho.AotSmoke.exe
echo $?   # 0 = 全部通过；非 0 即失败，可直接作构建门禁
```

回归：`dotnet build Engine.sln` 0 错误且告警数与改动前逐项一致（本次新增 0 条）
（**第十轮更正**：这条当时不成立，`DeterministicAccess.cs` 有 4 条 XML 文档告警未计入，见第十轮）；
`Tests` 656/656、`Tests-internal` 133/133（1 skipped）、`Klotho.Tests` 50/50 全绿。
对 Friflo 既有文件的改动增加 1 处（`RegisterScript` 的注解，纯新增 attribute，
不改变任何运行时行为，JIT 路径不受影响）。

### 第四轮：真实联机（Phase 2，P2P）

风险 3「网络层与桥接层的耦合面未验证」此前一直成立：`FrifloSimulation` 只在单进程直接调用下
验证过，从未经过 `KlothoEngine`。本轮补齐。

**结论：Klotho 源码仍 0 改动。** 关键在于 `RegisterSystems`（唯一形参为具体 `EcsSimulation`
的回调）**引擎从不调用** —— 只有 `KlothoSession.Create` 和 `RoomManager` 调它。绕过前者、
自写 session 组装，该耦合就根本不进入执行路径。另外 `_simulationCallbacks == null` 时引擎
回退到无关类型的 `OnPreTick` 事件，故连 `ISimulationCallbacks` 都不必实现。

新增 4 个文件（全部新增，未改动任何既有文件）：

| 文件 | 作用 |
|---|---|
| `Bridge/FrifloKlothoSession.cs` | 替代 `KlothoSession.Create` 组装 CommandFactory + KlothoNetworkService + KlothoEngine + FrifloSimulation |
| `Bridge/IFrifloSimulationCallbacks.cs` | Friflo 侧平行回调接口（`RegisterSystems(SystemRoot,EntityStore)` / `OnInitializeWorld` / `OnPollInput`），与 Klotho 的 `ISimulationCallbacks` 无继承关系 |
| `Bridge/LoopbackTransport.cs` | 进程内 `INetworkTransport` 对，确定性投递，可注入按轮询计数的延迟 |
| `Tests/Test_NetworkedDeterminism.cs` | 双引擎真实联机验证 |

**实施中踩到并修掉的三个问题**（都是本次新代码的问题，非 Klotho 缺陷）：

1. **`OnDataReceived` 的 peerId 语义**。它必须是**发送方在接收方视角下的 id**，且要与
   `OnPeerConnected` 给出的 id 一致 —— host 用它在 `_pendingPeers` 里查握手状态
   （`KlothoNetworkService.HandleDataReceived`）。最初写成了「接收方自己的 id」，
   于是 host 收到 `PlayerJoinMessage` 却静默丢弃，握手永远停在 `Syncing`。
2. **guest 不能在 `InitializeFromConnection` 之后再调 `JoinRoom`**。前者已把 phase 置为
   `Synchronized`（接管已完成的握手），`JoinRoom` 会把它退回 `Lobby`，而 `SetReady` 在
   非 `Synchronized` 下直接 return —— 比赛永远开不了。
3. **世界初始化必须订阅 service 的 `OnGameStart` 而非 engine 的**。engine 的那个在
   `Start()` 返回后才触发，那时 tick 0 已快照、首帧即将执行。订阅 service 的事件并抢在
   `engine.Initialize` 之前，才等价于 Klotho 放 `OnInitializeWorld` 的位置。

**验证做到什么程度**（刻意避免"绿了但什么都没验"）：

- 走**真实握手**：`KlothoConnection.Connect` → PlayerJoin → 5 次 SyncRequest/Reply →
  SyncComplete → host 下发权威 `SimulationConfig`。没有走任何测试专用捷径。
- 比赛开始后给链路注入延迟，使一端的输入**赶不上**需要它的那一帧，从而真正触发
  预测 → 判错 → 回滚重算。实测双方各 **10 次回滚、最大深度 5、0 次失败**。
- **比对基准是 verified tick 而非 CurrentTick**。同一 `CurrentTick` 下两端状态本就可能
  不同（一端还带着尚未纠正的错误预测），拿它比对会把正常预测误报成 desync。改为各自
  记录「已确认且不会再改」的 tick 的哈希再交叉比对 —— 这也正是引擎自己发送 sync hash
  的条件（`CurrentTick == _lastVerifiedTick + 1 && !_hasPendingRollback`）。
  107 个共同 verified tick 全部一致。
- 断言里显式钉死非空跑：比赛必须推进、必须有共同 verified tick、**双方回滚次数必须 > 0**。

> **一处诚实修正**：`RollbackToTickZero_RestoresTheSeededWorld` 最初被描述为「能抓住
> 世界初始化顺序写错」的回归测试。实测把该 bug 重新注入后**测试依然通过** —— 因为
> `ExecuteTick` 在入口会用当前 tick 重新快照，首帧执行前就把 tick 0 的快照覆盖成了含
> 世界的状态。故该测试实际覆盖的是「最深回滚（tick 0，跨引擎启动序列的那个快照）能正确
> 还原」，不是顺序守卫。顺序仍按正确写法实现（不依赖这个巧合），注释已如实说明。

### 第五轮：ServerDriven（并修掉全量状态的两处缺陷）

风险「ServerDriven 未验证」此前一直成立。本轮补齐，**Klotho 源码仍 0 改动**。

**先修阻塞项：全量状态序列化是坏的。** 实施前的探针实测证明
`FrifloStateSerializer` 有两处缺陷，且**只在 SD 下致命**：

| 缺陷 | 实测现象 | 为何 P2P 没暴露 |
|---|---|---|
| **Script 被整个丢弃**：只写 archetypes / relations / nodes，无 script 段 | 源 store `scripts=1`，还原后 `scripts=0`，哈希不一致 | P2P 双方各自跑 `OnInitializeWorld` 建世界，全量状态只在 resync 时才用 |
| **层级还原后损坏**：`TreeNode.childIds` 是 `IdArray`（`(start,count)` 池句柄），按原始字节写出后，还原端 `childHeap` 里并无对应分配 | 访问 `ChildEntities[i]` 直接 `NullReferenceException`（`IdArrayPool.GetIds`） | 同上 |

而 **SD 客户端的世界完全靠全量状态引导**：`KlothoEngine.Start()` 对 SD client 跳过
`OnInitializeWorld`，改由 `HandleInitialFullStateReceived` → `ApplyFullState` →
`RestoreFromFullState` 建世界。所以这不是「resync 降级」，而是**客户端从来就没拿到过完整世界**。

修法与快照路径同构（`FrifloStoreSnapshot` 早已这么做）：层级按**内容**导出子 id + `parentMap`，
还原时 `SetParentMap` + `RebuildChildHeap` 重建池；Script 新增一段，类型用
`ScriptType.ScriptIndex` 上线，还原时**新建实例**（与快照路径刻意不同 —— 快照保留原实例是
为了不让游戏持有的引用失效，全量还原则是换一个世界，目标 store 里本就可能没有该脚本）。
格式版本 `1 → 2`：旧格式的读者会静默建出一个没有脚本、子列表损坏的世界。
`DeterministicAccess.cs` 新增 2 个 internal 访问器（纯新增）。

回归守卫：`Test_FullStateTransfer.cs` 两个用例。**已验证其有效性** —— 把修复
revert 掉后二者分别以那个 NRE 和哈希不一致失败。

**SD 组装**（新增 2 个文件，未改动任何既有文件）：

| 文件 | 作用 |
|---|---|
| `Bridge/FrifloKlothoServer.cs` | 替代 `RoomManager.CreateRoomAt` 的硬构造：CommandFactory + ServerNetworkService + KlothoEngine + FrifloSimulation |
| `Bridge/FrifloKlothoClient.cs` | SD 客户端：ServerDrivenClientService + engine + simulation |

关键点与第四轮同理：用**不带 `ISimulationCallbacks`** 的 `Initialize` 重载，
`RegisterSystems(EcsSimulation)` 那条硬耦合就不进执行路径。刻意**不复用**
`RoomManager`/`Room`/`ServerLoop` —— 它们是多房间调度与线程池编排，且 `Room` 的构造函数
额外要求一个 `ISimulationCallbacks`。单房间无需 `RoomScopedTransport`/`RoomRouter`。
`LoopbackTransport` 扩展为 `LoopbackHub`（一服务端 + N 客户端），`CreatePair` 语义不变，
P2P 测试不受影响。

**实施中踩到并修掉的三点**（均为本次新代码/新测试的问题）：

1. **客户端 tick 0 不可观测**。实测客户端在收到世界的**同一帧内**从 tick 0 直接跳到
   tick 10（预测提前量），故「在 tick 0 比哈希」根本没有那个时刻。改为按实体逐项比对
   结构（子实体、脚本字段、Entity 引用重绑），不依赖任何瞬时时刻。
2. **verified 状态只在事件内可观测**。客户端每收到一个 verified tick 就立刻重算回预测
   提前量，故 `Update` 返回后 store 永远停在**预测** tick 上。改为在
   `FrifloKlothoClient.OnTickVerified` 事件内采样 —— 那是 verified 状态唯一存在的瞬间。
3. **`Engine.Stats.TotalRollbacks` 在 SD 下恒为 0**。它只在 P2P 路径且 `#if DEBUG` 下累加，
   而 SD 客户端直接调 `_simulation.Rollback`。改为在 `FrifloSimulation.RollbackCount` 计数。

**验证做到什么程度**：

- 走**真实引导全流程**：握手 → ready → `StartGame` → 权威 initial FullState →
  各客户端 `SendBootstrapReady` → `CompleteBootstrap` → `Running`。无任何测试专用捷径。
- 种子世界刻意**同时包含组件、父子层级与 Script**，覆盖三种存储方式；断言客户端收到的
  子实体、脚本字段值、脚本内 Entity 引用（且已重绑到**客户端自己的** store）逐项正确。
- 注入链路延迟触发预测 → 判错 → 回滚重算；断言 `RollbackCount > 0`。
- 逐 verified tick 交叉比对哈希，断言比对数 > 10×客户端数（避免「只比了两三个 tick」的空跑）。
- **断言 `ResyncCount == 0`**。这条是必需的：引擎发现哈希不一致时会用服务端全量状态
  重建客户端，**恢复之后哈希又一致了** —— 只比哈希会让「反复 desync 又反复被救」看起来全绿。
  实测把一个客户端的系统改成发散后，正是这条断言抓住了它。

回归：`dotnet build Engine.sln` 0 错误，告警指纹与改动前逐项完全一致（新增 0 条）
（**第十轮更正**：同上，该说法当时不成立，已在第十轮修掉那 4 条告警后才为真）；
`Klotho.Tests` 56/56、`Tests` 656/656、`Tests-internal` 133/133（1 skipped）全绿；
AOT 真机二进制 **12/12 通过，退出码 0**（新增 4 项，覆盖本轮新增的
「按运行期类型发现 Script 字段」的全量状态路径 —— 正是第三轮踩过的 ILC 裁剪坑）。

### 第六轮：劣化链路（丢包 / 乱序 / 重复 / MTU）

此前所有联机验证都跑在理想链路上：不丢、不乱序、不限包长。这是**刻意**的默认——它让哈希不
一致只可能来自模拟——但代价是 Klotho 的恢复机制从未执行过。输入丢失、输入重复、输入迟到
都是真网络的日常，而桥接层可能正依赖着 UDP 并不提供的保证。

**只劣化 unreliable 流量。** 真实传输会对 reliable 通道重传与重排序，把它们也丢掉是在模拟
一个**坏掉的传输层**而非坏网络，还会破坏 Klotho 有权依赖的顺序保证（late-join 依赖
roster `Send` 先于消费它的 join `Broadcast`）。协议真正必须扛住的，是它自己用 unreliable
发的那些：客户端输入、input ack、ping/pong、sync hash。

乱序意味着轮询循环不能再「遇到第一个未到期的就停」——那正是此前维持顺序的机制。现在它会
跳过被扣住的包，但**把该发送方 `ReliableOrdered` 流的后续一并扣住**，于是契约仍然成立，而
unreliable 流量真正实现了超车。

MTU 单列一个用例，建模 LiteNetLib 的**不对称性**：超长 reliable 包会被分片重组，超长
unreliable 包则直接抛 `TooBigPacketException`——没有重传，丢了的分片永远补不回来。内存传输
没有长度限制，所以缺了这条，一个对真 socket 而言过大的命令只会在真 UDP 上才暴露。

概率带种子，失败可复现；断言链路**确实劣化过**而不是相信配置——220 帧内实测 231 次丢包、
79 次乱序、45 次重复。同样断言 `ResyncCount == 0`：只靠丢包后被救的客户端不能算通过。实测
注入一个发散的系统，正是这条断言失败。

### 第七轮：late-join / reconnect / spectator

三条流程此前都记为「未验证」。实测**其中两条根本没有实现**，不只是没测。

**late-join 是另一套构造顺序，不是一个开关。** 服务端的应答里带着 roster、session seed 和
**当前 tick 的整个世界**，引擎必须拿这些去构造，而不是先空转再纠正。所以握手要先在裸
transport 上跑完（`KlothoConnection`，与 ECS 无关），再由 `JoinRunningMatch` 从结果里建出
客户端。reconnect 走同一条路，只是握手带的载荷不同。

直接拿 `Connect` 去连一个已开赛的服务端：服务端侧是对的（它自己会把这个 peer 路由到
late-join 握手），客户端则**永久卡死**——那条路径上不会触发桥接层用来判定「世界已到」的任何
事件，于是它停在 `Syncing`，而服务端已经把它算作一名玩家。

`HasWorld` 也答不了 joiner 真正要问的问题：它的世界从构造起就存在，但停在全量状态被取走的
那个 tick 上，而比赛已经跑在前面；引擎随后要补完这段差距才开始预测。新增
`IsCaughtUp` / `OnCaughtUp` 标记这个时刻，取自引擎的 `OnCatchupComplete`。

**spectator 同样要新建。** 它不是「关掉输入的客户端」：不占玩家位、不发任何东西、从不预测
——只回放服务端已确认的内容，因此**落后**于实时比赛而非跑在前面。它的引擎也**不能提前构造**：
tick 率、回滚窗口和 session 参数都由服务端在接纳它时告知，本地猜一个 tick 率会让回放速度就
是错的。于是 `Watch()` 只发起连接，引擎与系统等配置到达后才建——与
`KlothoSession.CreateSpectator` 推迟建 `EcsSimulation` 是同一个理由。

服务端此前会拒绝所有 spectator：`MaxSpectatorsPerRoom` 是 `ServerNetworkService` 上的一个
属性，而不是它从 `SessionConfig` 读的字段，默认 0，除非有人赋值——而负责赋值的正是本方案
绕开的 `RoomManager`。

**一处必须记下的测试自身缺陷**：late-join 测试最初给每个 avatar **一个**子实体，把子实体池
重建整个删掉后**它依然通过**。原因是 `IdArray` 在 `count == 1` 时把 id 直接存在 `start`
字段里，**根本不碰池分配器**。改成两个子实体后，`start` 才是池偏移，同样的删除会抛出当初那个
`NullReferenceException`。第五轮的测试恰好用了两个子实体才抓住这个缺陷——这是运气，不是设计。

顺带修掉的传输层错误假设：向未连接的 peer 发送此前**抛异常**，理由是「地址一个未知 peer 是
调用方的 bug」。并不是——Klotho 在自己的断开处理里向 `_peerToPlayer` 全体广播离开通知，而
刚离开的那个 peer 还在这张表里。`LiteNetLibTransport.Send` 在 peer map 查不到时直接返回，
所以那个抛出是在**发明一个真实传输层不会有的故障**。

### 第八轮：真 UDP，逐进程

此前每个联机检查都跑在内存 `LoopbackHub` 上：投递是同步的、进程内的、按需有序的、不限长度。
这对确定性测试是对的默认，但意味着**整整一层从未执行**：真 socket、跨进程边界的序列化、MTU，
以及测试无法编排的**两个独立时钟**。

`Klotho.UdpMatch` 是一个二进制、三种角色。`server` 与 `client` 各自跑一个 peer，把每个自己
认定已定型的 tick 的哈希写进文件；`match` 把它们作为**子进程**拉起，等它们退出后比对文件。
事后比对而非走旁路通道，是为了让进程之间**在比赛进行时只有 UDP**。

首次运行客户端立刻断线：`ServerNetworkService.Update` **不轮询自己的 transport**（客户端
服务会轮询自己的），于是服务端一个包也收不到。内存测试因为自己会 pump 每条链路而掩盖了这点。

实测：2 人局 798 个 verified tick 一致、每客户端约 400 次回滚、0 次 resync；3 人局 1194 个
一致——**也是本项目第一次跑超过两个 peer**。给客户端加一个服务端没有的系统会让整个运行以
退出码 1 失败。

### 第九轮：单 socket 多房间

Klotho 的多房间栈有四块，其中**三块可以直接用**：`RoomRouter`、`RoomScopedTransport` 和
`Room` 都是按 `ISimulation` 写的。不能用的是 `RoomManager` 与 `ServerLoop`——
`CreateRoomAt` 直接 `new EcsSimulation` 且没有替换的缝，`ServerLoop` 又要具体的
`RoomManager`。所以本轮重写的正是**房间工厂与更新循环**这两块，其余全部是 Klotho 的。

`Room` 还要一个 `ISimulationCallbacks`（其 `RegisterSystems` 形参是 `EcsSimulation`，无法
实现）。它**只存不用**——Klotho 里没有任何地方读 `Room.Callbacks`——所以给它一个成员全部抛错
的桩：万一将来某个版本真的开始调用，失败信息应当说清原因，而不是看起来像游戏忘了初始化世界。

**房间按顺序更新，不用 `ServerLoop` 的线程池派发。** 它的理由在纸面上成立（每个房间各有一个
`EntityStore`），但本桥接层存在的意义就是让 tick 可复现，而一旦有任何游戏代码跨房间读写，
并发就是拿这个换吞吐。`UpdateRooms` 是将来要重新权衡时的缝。

踩到的第二个坑：**世界初始化的订阅必须早于 `engine.Initialize`**，后者会把引擎自己的处理器挂到
同一个事件上——而那个处理器负责 `Start()`、快照 tick 0 并把它作为权威世界广播出去。挂晚了就
晚了一个处理器：客户端收到的是空世界，然后**全部被 resync**。

客户端此前也无从指定房间（`Connect` 写死 `JoinRoom(null)`），现在接受 `roomId`，`-1` 保持
单房间行为——单房间服务端前面没有 router，会把房间握手前缀当作畸形首包拒绝。

两个房间**刻意种不同的世界**（实体数不同、带房间 tag、起始位置偏移）：两个一样的房间会因构造
而一致，那样一个把两边包搞混的路由 bug 会让**两个房间看起来都是对的**。断言两个房间在任何共同
tick 上状态**都不相同**，且每个客户端拿到的世界带着自己房间的 tag。把所有客户端都指向房间 0
会失败——房间 0 以满员拒绝它们。

### 第十轮：对抗性复核（位宽越界）

> **第十一轮更正**：本轮把边界判定为 64 位，**不成立**。真实边界是 **32**——`EntityNode.isOwner`
> 是 `int`，`indexBit`/`relationBit` 是 `(int)bitSet.l0`。本轮把桥接层拓到 256 位这件事本身是对的
> （且是必要的），但「拓到 256 位就没有越界问题了」是错的。详见第十一轮。

本轮不新增功能，只做一件事：**复核前九轮的声称是否属实**。逐项复跑全部验收命令，结论是
前九轮记录的数字**全部复现**（构建 0 错误、`Tests` 656/656、`Tests-internal` 133/133、
`Klotho.Tests` 66/66、AOT 真机 12/12、真 UDP 2 人局与 3 人局退出码 0）。

但**查出两处此前所有测试都够不到的缺陷**，同属一类：`BitSet` 是 **256 位**（`l0..l3`
四个字），而桥接层三处只读写了 `l0`。

| # | 位置 | 后果 | 为何九轮都没抓到 |
|---|---|---|---|
| 1 | `Archetype.GetTagBits()` → 状态哈希 + 全量状态 | tag 序号 ≥ 64 时：两端 tag 不同而**哈希相同**（desync 永不上报）；SD 客户端从全量状态重建的世界**丢掉该 tag**，从此匹配到不同的 query | 全部测试加起来的 tag 数远不到 64 |
| 2 | `RebuildIndexes()` 的 `componentTypes & indexTypes` 交集 | 索引组件序号 ≥ 64 时，回滚**跳过**它的索引重建：组件值正确还原、**哈希也一致**，只有按值查询返回被回滚掉的实体 | schema 把索引组件排在最前，要 64 个索引类型才够到边界 |

第 2 条尤其安静：**状态哈希察觉不到它**。数据是对的，错的是查找表，于是分歧通过系统走到
不同分支体现出来，而不是通过哈希。这正是「哈希一致」不等于「状态一致」的一个实例。

修法是把三处都改成读写四个字。`GetTagBits` 改为 `out` 四个 long；`GetArchetypeByContent`
同样收四个；全量状态**格式版本 2 → 3**（archetype 头部的 tag 字段从 1 个 long 变成 4 个，
混用会从该字段起整段错位）。

两条回归都**按本项目标准实测过有效性**——把缺陷重新注入后分别以
「state hash ignores tag 'Tag62' (index 64)」和「index lookup ... missed the entity」失败：

| 新增测试 | 钉住的东西 |
|---|---|
| `Test_TagBits.StateHash_SeesTagsBeyondTheFirstBitSetWord` | 高位 tag 必须改变哈希 |
| `Test_TagBits.FullState_CarriesTagsBeyondTheFirstBitSetWord` | 高位 tag 必须随全量状态过去 |
| `Test_IndexRebuildWidth.Rollback_RebuildsIndexesForHighStructIndexComponents` | 高位索引组件回滚后按值查询必须正确 |

两个测试都**先断言自己确实越过了边界**（`structIndex >= 64`），否则填充类型数量一变，
测试会退化成在低位上做一件必然成立的事而依旧全绿。

**顺带订正一处不实记载**：前九轮反复声称「告警指纹与改动前逐项完全一致（新增 0 条）」。
实测**不成立**——`DeterministicAccess.cs` 有 4 条 XML 文档告警（CS1574 ×2、CS1734 ×2），
其中一条是一个 `<summary>` 落到了错误的成员上。已修掉，现在该说法才为真。当前与改动前的
差异只剩下**新增项目自身**的告警（`Klotho.Tests` 3 条 CS0618/CS0649、
`DeterministicComponentLayout.cs` 3 条 IL2026），既有项目**逐项一致**。

### 第十一轮：对抗性复核（查找表的生命周期与越界的真实边界）

本轮同样不新增功能，只复核。**前十轮的全部数字再次复现**（构建 0 错误、`Tests` 656/656、
`Tests-internal` 133/133、AOT 真机 12/12、真 UDP 2 人局 799 tick / 3 人局 1200 tick 均退出码 0）。
但查出**两处新缺陷**，并**推翻了第十轮对越界边界的判断**。

#### 缺陷 1：回滚会永久泄漏池分配器（每次回滚一次，直到比赛结束）

第 4 层「清空字典再重建」的写法漏掉了一件事：字典里存的 `IdArray` 是**池分配器的句柄**，而
`IdArrayPool` 只有拿着 `start` 偏移显式 `DeleteArray` 才会回收。`Clear()` 一次性丢掉了所有句柄，
于是**没有任何东西可以还给池子**——每次重建都从池子里取一段全新区域。四处都有：子实体池、
索引 `idHeap`、关系 `idHeap`、链接关系 `linkIdsHeap`。全量状态还原走同一批例程，所以**每次 resync
也泄漏一次**。

实测（4 个子实体的 store）：10000 次回滚后池子从 96 字节涨到 262176 字节，`Count` 从 1 涨到 10001。
约合**每千次回滚每个池子 26 KB**，且只增不减。

**为什么前十轮一次都没碰到**：池偏移是分配历史，**被刻意排除在状态哈希之外**
（正是 `StateHash_IsIndependentOfChildAllocatorHistory` 钉住的那条），所以两端始终一致；
泄漏的内存**可达**，不是 GC 泄漏；而唯一的零分配断言覆盖的是 `SaveSnapshot`，泄漏发生在
`Rollback`。它只表现为「客户端内存随预测失败次数上涨」——而预测失败正是网络越差越多。

修法：给 `IdArrayPool` / `IdArrayHeap` 加 `Clear()`（保留 `ids` 缓冲，只重置 `count`/`freeStart`/
`freeStarts`，所以重建路径仍然零分配），在三处重建入口调用。

#### 缺陷 2：全量状态不清理载荷里没有的关系类型

`ReadArchetypes` 早就有「本地有、载荷没有」的清空回合，但**关系原型不在 `store.archs[]` 里**
（第一轮就记过：`AbstractEntityRelations` 绕过 `AddArchetype`），那个循环够不着它们，而
`ReadRelations` 自己没有对应的回合。后果：SD 客户端 resync 时**保留自己旧世界的关系**——既进状态
哈希，也进每一次关系查询。这是「用来终结分歧的机制本身制造了永久分歧」。

> **一处必须记下的测试自身缺陷**：该回归测试最初的前置断言写成
> `sender.GetRelations<Targeting>().Length == 0`。**光是这样问一句就会在发送端建出关系原型**，
> 它随后以 0 实体随载荷过去，被 `ReadArchetype` 清掉——于是把修复 revert 掉**测试依然通过**。
> 改为通过只读的 `GetRelationArchetypes` 检查发送端后，才真的失败。与第七轮的单子实体是同一类
> 错误：断言用的动作本身改变了被测状态。

#### 推翻第十轮：真实边界是 **32**，不是 64

第十轮把桥接层从 64 位修到 256 位，并据此认为边界在 64。**实测证伪**：实体用
`EntityNode.isOwner`（一个 **`int`**）记录自己拥有哪些索引组件与关系，而
`indexBit`/`relationBit` 都是 `(int)ComponentTypes.bitSet.l0` —— 256 位集合被窄化成 32 位。
逐类型实测（本仓库的 schema 已经越过这条线）：

| structIndex | `DeleteEntity` 行为 |
|---|---|
| ≤ 30 | 正常清理索引 |
| 31 | **抛 `NullReferenceException`**（符号位；`int`→`long` 符号扩展后高 32 位全 1，于是去遍历并不存在的组件类型） |
| ≥ 32 | **静默留下陈旧索引项**（窄化成 0，实体被记成「什么都不拥有」，删除路径整段跳过） |

**这是 Friflo 自身的限制，不是桥接层的**：不涉及任何模拟即可复现，修它要把 `isOwner`/`isLinked`
在删除/索引/关系三条路径上全部拓宽成 `BitSet`，远超「不改引擎」的前提。桥接层能做的是**把边界
测准并钉住**。实际含义：**一次确定性模拟中索引组件与关系类型合计不得超过 32 个**（含所有程序集、
含测试夹具）。越过之后实体删除会破坏查找表，**而且状态哈希察觉不到**——又一个「哈希一致 ≠ 状态
一致」的实例。

顺带发现：`CreateEntity(签名)` 用的也是 `l0` 单字掩码，所以**结构序号 ≥ 64 的索引组件经签名创建时
根本不进索引**，而回滚重建（已修成 256 位）会把它加进去——回滚**凭空造出**创建时不存在的索引项。
本轮新测试因此一律走 `AddComponent`（它按 `StructInfo<T>.HasIndex` 逐类型判断，不受掩码影响）。

| 新增测试 | 钉住的东西 | 已实测有效性 |
|---|---|---|
| `Test_LookupPoolReuse`（5 条增长守卫） | 子实体池 / 索引 / 关系 / 链接关系 / 全量状态路径的池必须复用 | 注入缺陷后 5 条全部失败 |
| `Test_LookupPoolReuse.Rollback_KeepsLookupsIntactWhileReusingPools` | 复用池子不得损坏任何查找结果（哈希查不出来，只能读回列表） | 修复的正确性护栏 |
| `Test_FullStateTransfer.FullState_DropsRelationsAbsentFromThePayload` | 载荷没有的关系类型必须被清掉 | 注入缺陷后失败 |
| `Test_IndexedTypeCountLimit`（2 条） | 把 32 这条边界连同其算术根因钉成绊线：Friflo 一旦拓宽字段，测试失败，届时应重新测量 | 刻意断言**当前的错误行为** |

回归：`dotnet build Engine.sln -t:Rebuild` 0 错误，**告警指纹与改动前逐项完全一致**
（CS0618 32、IL2071 20、IL3050 12、IL2090 12、IL2026 6、CS0649 2，新增 0 条）；
`Klotho.Tests` **78/78**（第十轮 66 + 本轮 12）；`Tests` 656/656、`Tests-internal` 133/133（1 skipped）；
AOT 真机 12/12 退出码 0；真 UDP 2 人局与 3 人局退出码 0。

### 第十二轮：房间生命周期与 Release 构建（面向"多房间并行 + 最大容载"）

本轮的驱动问题不是正确性而是**容量**：权威服务器要并行开多个房间、要能长时间跑。前十一轮把
回滚正确性做扎实了，但那个目标另有三块短板，本轮做掉两块（另两块只落档，见下）。

#### 缺陷 1：房间只能创建，不能回收——容量随运行时长单调下降

`FrifloKlothoRoomServer` 只有 `CreateRoom` 与 `Stop`（全部关掉），**Klotho 房间生命周期的后半段
整套没接**。`Room.Update` 会在peer散尽时**自己**把 State 置为 `Draining`，但随后负责收尾的两个阶段
（`RoomManager.TransitionDrainingRooms` / `CleanupDisposingRooms`）长在 `RoomManager` 上——正是本方案
重写掉的那一块。于是 `UpdateRooms` 只遍历 `Active`，**排空的房间被静默跳过，槽位永不释放**，其
`EntityStore`/引擎/网络服务在进程存活期间一直可达。

**不抛异常、不串包、不 desync**——服务器只是跑得越久能开的房间越少。这也是为什么本轮的断言钉在
**槽位复用**上而不是任何错误表面：这个缺陷没有错误表面。

修法照搬 `ServerLoop` 的形状（三阶段：回收 → 轮询 → 更新）：
- 新增 `ReclaimFinishedRooms()`，在 `Update` **开头**执行（对应 `ServerLoop` 的 stage 0）。顺序有向：
  房间若在本帧数据已扇出之后才释放，那些包会滞留在再也不会被更新的房间队列里。
- `Draining → Disposing` 与 `Disposing → 释放` **保持为两个阶段**，不合并：前者停引擎，后者才交还
  peer 与槽位。合并会在房间还在回应最后几个包的同一帧里把它拆掉。
- `UpdateRooms` 现在也更新 `Draining` 的房间（与 `ServerLoop.GetReadyRooms` 一致）——排空中的房间仍有
  入站队列要消费，跳过它还会让队列里的缓冲区永不归还。
- `Stop()` 改走同一条 `ReleaseRoom`，避免两处清理逻辑漂移。

#### 缺陷 2：Release 构建是坏的——而服务器正是要发 Release

`dotnet build Engine.sln -c Release` 在**干净树上就 4 个错误**：`Test_NetworkedDeterminism` 读
`KlothoEngine.Stats`，而该属性在 `#if DEBUG` 内。含义比"编译不过"更重：前十一轮所有"全绿"
**只在 Debug 下成立**，Release 这条路径从未编译过、更未测过。而计划自述的"Debug + Release 0 错误"
在被写下时就不成立。

修法与第五轮 `RollbackCount` 同理：改用桥接层自己的计数。`FrifloSimulation.RollbackCount` 顶替
`TotalRollbacks`；新增 `FrifloKlothoSession.FailedRollbackCount`，取自引擎的 `OnRollbackFailed`
事件（该事件在所有配置下都有）。**本轮首次在 Release 下跑通全部测试。**

#### 实测数据（本轮新增，用于容量估算）

| 实体数 | save+tick | GetStateHash | rollback+resim |
|---|---|---|---|
| 100 | 3.9 us | 7.8 us | 4.8 us |
| 500 | 15.1 us | 35.9 us | 20.8 us |
| 2000 | 66.3 us | **162.4 us** | 84.6 us |

**哈希比 tick 本身贵约 2.4 倍**，而 `ExecuteServerTick` 每 tick 无条件调它、无采样开关。按 2000 实体
房间每 tick ≈ 230us 计，20ms 预算下顺序更新约 **85 间**触顶，其中约三分之二开销在哈希而非模拟。

内存侧复核了第十一轮的池泄漏修复：500 实体 store 连续 **30000 次回滚**，堆稳定在 3094.2 KB，
增量 0.0 KB。单房间常驻内存 100/500/2000 实体分别约 0.15/0.5/2.1 MB（ring=52）。

#### 一处必须记下的测试自身缺陷（第三次同类）

`AReusedPeerIdIsRoutedByItsOwnHandshake` 最初是**作为 `RemoveRoomPeers` 的回归**写的，注释也这么
声称。**注入验证证伪**：把 `router.RemoveRoomPeers(roomId)` 删掉，该测试连同其余三条**依然全绿**。
根因是 `RoomRouter.OnPeerDisconnected` 自己就会 `_peerToRoom.Remove(peerId)`，所以**正常离开的
客户端根本不留残留**。`RemoveRoomPeers` 真正针对的是房间释放时**仍处于连接状态**的 peer，即强制
关闭路径，而非一局正常打完。注释已订正为如实描述，该路径列入遗留。

与第七轮（单子实体不碰池）、第十一轮（`GetRelations<T>()` 前置断言反建原型）是同一类错误：
**测试自认为在守的东西，与它实际能守的东西不是一回事。** 本项目"每条回归都要注入验证"的规矩，
第三次抓到自己。

顺带发现两件与内存链路有关的事：
- `LoopbackHub` **从不复用 peer id**（每次 `CreateClient` 都发新 id），而真实传输层会重发已释放的
  槽位。这类似第八轮"内存测试自己 pump 每条链路"掩盖了服务端不轮询 transport。新增
  `LoopbackHub.ReuseClient(peerId)` 显式建模，`CreateClient` 语义不变。
- `SessionConfig.ReconnectTimeoutMs` 默认 **60 秒**：离开的玩家座位为 reconnect 保留，房间在此之前
  不算空。这是正常服务器行为，不是缺陷——测试把它调短到 50ms 而非绕开，因为绕开就不是在测真实路径了。

新增测试（4 条，`Test_RoomLifecycle.cs`）：

| 测试 | 钉住的东西 | 注入验证 |
|---|---|---|
| `AnEmptiedRoomIsReleasedAndItsSlotReused` | 排空的房间必须交还槽位，且该槽位能再开一局全新世界 | 删 `ReclaimFinishedRooms` 后失败 |
| `CapacitySurvivesManyMatches` | 2 房间 × 4 局后容量不衰减，`Room` 对象不残留 | 同上 |
| `APeerThatNeverNamesARoomIsSweptUp` | 只连接不握手的 peer 必须被扫掉（任何能开 socket 的人都可增长该表） | 删 `CleanupUnroutedPeers` 后失败 |
| `AReusedPeerIdIsRoutedByItsOwnHandshake` | 复用的 peer id 必须按新连接自己的握手路由 | **不守 `RemoveRoomPeers`**（见上） |

回归：`dotnet build Engine.sln -t:Rebuild` 0 错误，**告警指纹与第十一轮逐项完全一致**
（CS0618 32、IL2071 20、IL3050 12、IL2090 12、IL2026 6、CS0649 2，新增 0 条）；
`Klotho.Tests` **82/82（Debug 与 Release 均通过）**；`Tests` 656/656、`Tests-internal` 133/133（1 skipped）；
AOT 真机退出码 0；真 UDP 2 人局 796 tick、3 人局 1191 tick 均退出码 0。Klotho 源码改动仍为 **0 行**。

### 第十三轮：组件哈希按字读（待办 A）

第十二轮把待办 A 记为「加采样间隔」。**调研直接推翻了这个方向**，实施的是另一件事。

#### 推翻第十二轮：`SyncCheckInterval` 只作用于 P2P

第十二轮称「客户端只在 `SyncCheckInterval` 上核对」——**那是 P2P 的行为**（`KlothoEngine.cs`
的三处 `SendSyncHash`）。SD 客户端走完全不同的路径：`ProcessVerifiedBatch` 对**每一个** verified
tick 都无条件比对，`resimHash != entry.StateHash` 即判定 determinism failure 并**请求 FullState
重建**。所以服务端单方面少算哈希不是「省一点」，而是让客户端把缺失当成 desync——代价远大于收益。

采样间隔因此需要**协议语义变更**（哨兵值标记「本 tick 不比对」）外加 desync 发现延迟 N 帧。
实测显示不必付这个代价。

#### 实测：瓶颈是逐字节循环本身

| 组件宽度 | 每实体 | 每字节 |
|---|---|---|
| 16 B | 10.1 ns | 0.63 ns |
| 48 B | 72.3 ns | 1.51 ns |
| 96 B | 110.3 ns | 1.15 ns |

宽度 6 倍 → 每实体成本 11 倍，而原型遍历开销恒定（8000 实体下仍是 10.5 ns/实体，完全线性）。
即**开销在 `HashComponent` 的逐字节 FNV-1a 循环里**，不在虚调用或遍历。哈希占单 tick 总开销
**81–83%**（第十二轮估的 2/3 偏低）。

结论：优化循环本身即可，**不必碰协议、不降低 desync 检测密度、Klotho 仍 0 改动**。

#### 实施：8 字节字 + 小端

`StructHeap<T>.HashComponent` 的原始字节路径改为每次消费 8 字节（含引用的组件走 leaf plan，不动）。
三个必须守住的前提：

1. **仍拷贝到局部变量**。组件在 `T[]` 中背靠背存放，直接对数组元素取 span 会让尾部的字读跨进
   下一个组件。局部拷贝同时给尾部**零填充**，使部分字无需单独的字节路径——尾部与主体因此由同一
   套算术混合，不可能互相矛盾。
2. **显式小端**（`BinaryPrimitives.ReadUInt64LittleEndian`）。被替换的字节循环按地址序消费，
   **天然与字节序无关**；字读不是，它必须指定字节序，否则大端 peer 与小端 peer 对同一状态算出
   不同哈希、互相判定 desync。这是本轮**新引入**的风险，与 Klotho 自己的线上序列化取同一约定。
3. 哈希值本身改变是安全的：它只在**同版本对等端之间比较**，不持久化、不是线上格式
   （Klotho 用 `GetLocalStaticFingerprint` 保证同版本）。

#### 一处必须记下的测试自身缺陷（第四次同类）

`TheTailIsReadInTheSameByteOrderAsTheBody` 第一版全部写成「两个值必须哈希不同」的断言。
**注入验证证伪**：把尾部字节序反装后**它依然通过**——因为反装仍是**双射**，每个值仍映射到唯一
哈希，「能区分不同值」这类断言一条都不会失败。

真正的不变式是**同一份字节在主体与尾部必须产生相同贡献**，而这在状态哈希层面**不可观测**
（两个值必然属于不同组件类型，类型也进哈希）。改为借 `InternalsVisibleTo` 直接对
`Archetype.HashHeapComponent` 断言 FNV 的精确代数结果后，注入才失败。

与第七轮（单子实体不碰池）、第十一轮（`GetRelations<T>()` 反建原型）、第十二轮
（`RemoveRoomPeers` 其实由别处清理）是同一类错误。这次的形式是新的：**断言的问题类型不对**
——问「能否区分」永远抓不到保序变换。

新增测试（4 条，`Test_ComponentHashWidth.cs`）：

| 测试 | 钉住的东西 | 注入验证 |
|---|---|---|
| `EveryByteOfAComponentReachesTheHash` | 各宽度类（1/3/4/8/12/20 B）每个字节都必须进哈希 | 丢尾部后失败 |
| `EqualValuesHashEqualRegardlessOfWhereTheyLive` | 落在回收 slot 上的相同值必须同哈希（padding 不得泄漏） | — |
| `AComponentHashDoesNotDependOnItsNeighbours` | 读不得跨进相邻组件 | 改读 2 个元素后失败 |
| `TheTailIsReadInTheSameByteOrderAsTheBody` | 主体与尾部同为小端 | 反装尾部后失败（第一版**不失败**，见上） |

#### 收益：形状改善确凿，倍数当时未能测出（第十四轮已补测）

宽度依赖基本消除。**但本轮的精确加速比未能测准**：多次探针互相矛盾（同一进程内 8 B 测出 65.4ns
而 16 B 仅 10.4ns 的顺序效应；`WIDTH` 段与 `TOTAL` 段对同一操作差 30 倍，取决于此前是否跑过
tick）。测量被 JIT 预热与顺序效应主导，当时**不作为已验证结论**。见第十四轮。

回归：`dotnet build Engine.sln -t:Rebuild` 0 错误，**告警指纹逐项一致**（新增 0 条——期间
`Test_ComponentHashWidth` 一度引入 2 条 CS0649，已修）；`Klotho.Tests` **86/86（Debug + Release）**；
`Tests` 656/656、`Tests-internal` 133/133（1 skipped）；AOT 真机退出码 0；
真 UDP 2 人局 800 tick、3 人局 1199 tick 均退出码 0。

### 第十四轮：用 BenchmarkDotNet 复测哈希收益（待办 C）

第十三轮的加速比测不准，本轮补测。**不新增任何仓库依赖**：基准跑在临时目录的独立项目里，
两个循环从 `StructHeap<T>.HashComponent` 逐字抄出（含局部拷贝与 `MemoryMarshal` 取 span，
所以量的是整段实现而非孤立的循环体）。仓库未被触碰，`Engine.sln` 的告警指纹不受影响。

结果（net8.0 / Release，误差列为 99.9% 置信区间的一半）：

| 组件宽度 | 逐字节 | 按字 | 加速 |
|---|---|---|---|
| 8 B（一个整字，无尾部） | 2.81 ns | 0.71 ns | **约 4×** |
| 12 B（一字 + 4B 尾部） | 7.67 ns | 1.32 ns | **约 5.8×** |
| 16 B（两个整字） | 5.90 ns | 1.32 ns | **约 4.5×** |
| 20 B（两字 + 4B 尾部） | 10.81 ns | 1.42 ns | **约 7.6×** |
| 48 B | 29.3 ns | 1.9–14.1 ns | **2×–15×**（见下） |
| 96 B | 70.0 ns | 6.5–20.4 ns | **3.4×–11×**（见下） |

**≤ 20 B 的结果稳定可信**：三次独立运行的偏差均在 3% 以内，且字节路径每次都稳定复现。
这是最有意义的区间——本仓库测试与示例里的组件几乎都落在这里（`Position` 24 B、`Avatar` 20 B）。

**48 B 与 96 B 三次运行不稳定**：`Word048` 依次为 1.88 / 14.08 / 1.99 ns，`Word096` 为
12.68 / 20.45 / 6.52 ns，而同一批次的字节路径每次都在 ±3% 内。差异只出现在字路径的宽结构上，
特征符合 JIT 对该循环是否展开/向量化的代码生成抖动。**故宽结构只给区间，不给单值**；即便取
每次最差的一次，仍是 2× 以上的改善。

结论：加速在**所有宽度上都成立**，窄组件（≤20 B，即本项目的典型形状）约 **4–7.6×**，
宽组件至少 **2×**。这也修正了第十三轮「窄组件没有收益」的印象——那来自不可信的手写计时。

**未做外推。** 单房间 tick 成本与容量上限需要在真实工作负载下重测，本轮只测了哈希这一段。
第十二轮那个「约 85 间」的估算仍然失效（见已知遗留）。

方法学备忘：`dotnet test` 里的手写计时对这个量级（个位数纳秒）不可用——第十三轮同一进程内
同一操作因先后次序差出 30 倍。BenchmarkDotNet 的隔离进程、预热与离群值剔除是必需的，不是讲究。

### 面向容量的待办（落档，未实施）

按依赖顺序排在第十二轮之后。两项都**不是缺陷**，是明确的取舍，落档以免被当成遗漏：

> **待办 A（状态哈希采样间隔）已于第十三轮处理，但走的不是这条路。** 此处原记「客户端只在
> `SyncCheckInterval` 上核对」**不成立**——那是 P2P 的行为，SD 客户端逐 tick 无条件比对。
> 采样间隔因此需要协议语义变更，而实测表明瓶颈是逐字节循环本身，优化它即可，不必碰协议。
> 详见第十三轮。**采样间隔方案就此放弃**：它换来的边际收益要付出协议变更 + desync 发现延迟
> N 帧的代价。

**待办 B：房间并行更新。** 第九轮把顺序更新记为刻意取舍（理由：跨房间读写会拿确定性换吞吐），
第十二轮维持该取舍。建议先把单房间成本降下来再评估——第十三轮已消除哈希的宽度依赖，所需线程数
会相应减少，跨房间并发那一类问题也就不必现在引入。`UpdateRooms` 仍是那个缝。

> **待办 C（复测哈希收益）已于第十四轮完成。** 窄组件（≤20 B）约 4–7.6×，宽组件至少 2×。
> 详见第十四轮。仍未做的是**单房间 tick 成本与容量上限的重测**——本轮只测了哈希这一段。

### 已知遗留

- 单次 `Tests` 运行中曾出现 1 例失败，此后**累计 34 次连续运行**（28 次 Debug + 6 次
  Release）均 656/656，**仍未复现、仍未定位到具体用例**。本轮做的是把范围收窄而非结案：
  - 曾怀疑 `Test_SystemGroup` 里的 `Perf.LastMs > 0`——它要求那个 1000 次空循环至少耗满
    一个 Stopwatch tick。**已实测证伪**：本机 `Stopwatch.Frequency` 为 10 MHz，20 万次测量中
    **0 次**测出 0 tick（该循环约合 10 tick）。
  - 剩下的合理嫌疑是 `Tests` 里的 **82 处零分配断言**（`Mem.AssertNoAlloc`）：只要有一次
    JIT 或延迟初始化的分配落进被测窗口就会失败，而这与运行顺序、是否首次执行有关。**这是
    嫌疑而非结论**，未能复现即未能证实。
- ~~NativeAOT 未完成 native 链接产物验证~~ **已完成**：真机二进制已产出并 12/12 通过（见「第三轮」「第五轮」）。仅在 `win-x64` 上验证过，其它 RID 未测。
- ~~第二阶段（真实网络）尚未验证，风险 3 仍然成立~~ **P2P 与 ServerDriven 均已验证**（见「第四轮」「第五轮」）。
  - ~~**ServerDriven 模式未验证**~~ **已验证**（第五轮）。
  - ~~**仅内存传输**~~ **已解除**：劣化链路见第六轮，真 UDP 跨进程见第八轮。
  - ~~仅两名玩家、单房间；late-join / reconnect / spectator 未验证~~ **已验证**：三条流程见
    第七轮，多房间见第九轮，3 人局见第八轮。
  - ~~`RoomManager` / `ServerLoop` 的多房间线程池调度未纳入~~ **部分解除**：多房间已跑通
    （第九轮），但**房间是顺序更新的**，`ServerLoop` 的线程池并发派发**仍未采用**——理由见
    第九轮，这是一个刻意的取舍而非遗漏。
- **真 UDP 只在本机回环上跑过**。跨物理主机、NAT、真实丢包与抖动均未验证；第六轮的劣化是
  内存链路上的**模型**，不是真网络。
- 第七轮的三条流程都只在**内存链路**上验证过，未与第六轮的劣化链路或第八轮的真 UDP 组合。
- spectator 只验证了单个观战者；`MaxSpectators` 上限与多观战者未测。
- **索引组件与关系类型合计上限为 32**（第十一轮实测）。这是 Friflo `EntityNode.isOwner`（`int`）
  的宽度所致，桥接层无法补救；越过后实体删除会破坏查找表且**状态哈希察觉不到**。
  `Test_IndexedTypeCountLimit` 把这条边界钉成绊线。要突破需先把 Friflo 的
  `isOwner`/`isLinked` 拓宽成 `BitSet`。
- **`CreateEntity(签名)` 对结构序号 ≥ 64 的索引组件不建索引**（第十一轮实测，`l0` 单字掩码）。
  同属 Friflo 自身问题。绕法是改用 `AddComponent`。**未修**——修它同样要动引擎。
- **`BitSet` 的 256 位上限本身未测**。超过 256 个组件类型或 256 个 tag 时 Friflo 自身即溢出
  （`BitSet` 注释写明「currently limited to 256 bits」）。不过在够到 256 之前，上面那条 32
  的限制早就先撞上了。
- **越界一类问题仍未穷尽排查**。第十轮查了位宽，第十一轮查了其中一处的真实边界与查找表生命周期；
  其余可能位置（各种以 `int` 为上限的计数、`short revision` 的回绕）**仍未系统排查**——这两轮都是
  针对性复核，不是穷尽审计。
- **池分配器的复用只在回滚/全量还原路径上验证过**。第十一轮修的是重建入口；`IdArrayPool` 在
  正常增删路径上的回收行为是 Friflo 既有逻辑，未在本项目中单独复核。
- **`RemoveRoomPeers` 所针对的路径未被覆盖**（第十二轮实测）。房间释放时仍处于**连接状态**的 peer
  才会留下 `_peerToRoom` 残留；正常离开的客户端由 `RoomRouter.OnPeerDisconnected` 自行清理。该调用
  已按 Klotho 的顺序接上，但**没有回归守住它**——写来守它的那条测试经注入验证证明守不住（见第十二轮）。
  要覆盖需构造强制关闭/过载踢房场景。
- **房间生命周期只在内存链路上验证过**（第十二轮）。未与第六轮的劣化链路或第八轮的真 UDP 组合；
  真 UDP 下的 peer id 复用行为亦未实测，`LoopbackHub.ReuseClient` 是**模型**而非真网络。
- **房间释放只覆盖"peer 散尽"这一条触发路径**。`Room` 的另外两条——`RequestEnd` 的比赛结束宽限
  （`EndReason.MatchEnded`/`MatchAborted`）与 `ServerLoop` 的 straggler 过载强关——均未验证。前者需要
  游戏侧发起结束，后者在顺序更新下不存在（无 straggler 概念）。
- **顺序更新下没有 straggler 处理**。`ServerLoop` 的超时栅栏、`MarkStraggler`、`RecoverCompletedStragglers`
  与过载强关整套未移植——顺序更新中一个卡住的房间会直接拖住整个循环，没有隔离机制。这是第九轮
  取舍的直接后果，在待办 B 之前不会改变。
- **单机房间容量上限**（第十二轮按 2000 实体/房间、20ms tick 推算约 85 间）**现已失效且未重测**：
  第十三轮改变了哈希成本结构。该数字本就来自 Debug 构建的单房间外推、**未在多房间满载下实测**，
  重测前不应引用。
- **组件哈希的小端假设未在大端机器上验证**（第十三轮新增）。按字读必须指定字节序，实现取小端
  （与 Klotho 线上序列化一致），`TheTailIsReadInTheSameByteOrderAsTheBody` 以精确代数值钉住了
  这一点——但那只证明**本机**按小端读，无法证明大端机器上同一状态得到同一哈希。被替换的逐字节
  循环没有这个假设。.NET 支持的大端目标极少（s390x/ppc64be），实际风险低，但这是**本轮新引入**
  的约束，不是既有的。
- ~~**哈希改动的收益倍数未经可信测量**（第十三轮）~~ **已测**（第十四轮，BenchmarkDotNet）：
  窄组件 4–7.6×，宽组件至少 2×。但 **48 B / 96 B 的字路径三次运行不稳定**（`Word048` 1.88–14.08 ns，
  同批次字节路径稳定在 ±3%），疑为 JIT 展开/向量化抖动，**未定位**；宽结构因此只有区间没有单值。
- **单房间 tick 成本与容量上限在哈希改动后未重测**（第十四轮）。第十四轮只测了哈希这一段，
  未做端到端外推。要给容量数字需在真实工作负载下重测。
- **基准测试不在仓库内**（第十四轮）。BenchmarkDotNet 项目建在临时目录、用后即删，以免为一次性
  测量引入包依赖并改变告警指纹。代价是**这个测量不可复跑**——要复现需按第十四轮的描述重建。

## Context

本仓库是 Friflo ECS 的一个副本（net10.0 / C# 14），名字表明它本就是为确定性改造准备的。Friflo 是高性能 archetype ECS，但**没有任何联机/确定性能力**：没有回滚、没有帧同步、没有定点数学、没有状态哈希（`Engine.Hub` 只是 Fliox 数据库同步，与游戏网络无关）。

Klotho 0.7.1 恰好提供这些：CSP/回滚/帧同步/Server-Driven/回放，纯 C# 引擎无关核心，且已有现成的 net8.0 构建（`Server~/xpTURN.Klotho.Runtime.csproj`）。

**目标形态**：Friflo 为主——游戏代码用 Friflo 的 `EntityStore`/`SystemRoot`/`Query`，Klotho 提供它没有的联机与确定性设施。为此需要一个桥接层，把 Friflo 的 `EntityStore` 适配成 Klotho 的 `ISimulation`。

**核心约束**：尽可能不改动现有源码，增量扩展。本方案对 Friflo 的改动为 **1 行**（`InternalsVisibleTo`），对 Klotho 的改动为 **0 行**。

---

## 前置阻塞项（已解除）

> **已解除（2026-08-04 复核）**：本机现有 SDK `10.0.302`，`dotnet build Engine.sln` 通过。
> 以下为当初的记录，保留作背景。

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
│       ├── AotSmoke/                 NativeAOT 真机自检（第三轮新增）
│       ├── UdpMatch/                 真 UDP 跨进程联机（第八轮新增）
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
| `Klotho.AotSmoke` | net8.0 | `PublishAot=true`。**必须跑真机 exe**——`dotnet test` 恒有 JIT，覆盖不到反射回退路径（第三轮新增） |
| `Klotho.UdpMatch` | net8.0 | 一个二进制三种角色（server / client / match），真 UDP 逐进程。同样覆盖不到于 `dotnet test`：内存链路没有真 socket、跨进程序列化与独立时钟（第八轮新增） |

`Friflo.Engine.ECS` 多目标含 net8.0，桥接层以 net8.0 引用即可，无需改其 TFM。

**桥接层最终提供的入口**（均在 `src/Klotho/Bridge/`）：

| 类型 | 用途 |
|---|---|
| `FrifloSimulation` | `ISimulation` 实现，桥接层核心 |
| `FrifloKlothoSession` | P2P 会话（第四轮） |
| `FrifloKlothoServer` / `FrifloKlothoClient` | ServerDriven 单房间服务端 / 客户端（第五轮）。客户端另有 `JoinRunningMatch`（late-join / reconnect，第七轮） |
| `FrifloKlothoSpectator` | 观战（第七轮） |
| `FrifloKlothoRoomServer` | 单 socket 多房间服务端（第九轮） |
| `LoopbackHub` / `LoopbackTransport` / `LinkConditions` | 进程内传输，可配置丢包 / 乱序 / 重复 / MTU（第六轮扩展） |

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

### 风险 2：验收标准依赖 SDK 安装 ✅ 已解除

未装 .NET 10 SDK 前，无法编译、无法跑验证、无法达成验收标准。
> 本机已装 `10.0.302`，风险解除。AOT 验收另需 VS C++ 工具链且 `vswhere` 在 PATH 上（见「第三轮」）。

### 风险 3：Klotho 网络层与桥接层的耦合面未完全验证 ✅ 已解除（P2P + ServerDriven）

`KlothoEngine` 有约 15 处 `_simulation is EcsSimulation` 类型判断（诊断/内存采样/错误注入等）。这些分支对非 `EcsSimulation` 会静默跳过（不抛异常），预期不阻塞第一阶段（单进程双实例验证不走网络路径）。但**第二阶段接真实网络时可能暴露更多硬耦合点**——届时若需修改 Klotho 源码，会先告知再动。

> **实测结论**（第四至九轮）：P2P、ServerDriven、late-join / reconnect / spectator、真 UDP
> 跨进程、单 socket 多房间——全部跑通，**Klotho 源码改动仍为 0 行**。
> 上述 15 处类型判断确实全部静默跳过，未阻塞任何路径。真正的硬耦合只有一处形状：
> `KlothoSession.Create` 与 `RoomManager.CreateRoomAt` 直接 `new EcsSimulation(...)`，
> 且 `ISimulationCallbacks.RegisterSystems` 的形参是具体的 `EcsSimulation`。绕法是同一个：
> 自写 session/server 组装，并使用**不带 `ISimulationCallbacks`** 的 `Initialize` 重载，
> 该回调接口便不进执行路径。
>
> 多房间（第九轮）给这个结论加了一条**边界**：`RoomRouter` / `RoomScopedTransport` / `Room`
> 三者都是按 `ISimulation` 写的，可以**原样复用**；`RoomManager` 与 `ServerLoop` 不行，只能
> 重写房间工厂与更新循环。`Room` 构造函数还要一个 `ISimulationCallbacks`——但它**只存不用**，
> 所以桩即可满足。这是「无法实现」与「实现了也没人调用」之间的区别。

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

# 4. NativeAOT 真机二进制（vswhere 必须在 PATH 上）
PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer" \
  dotnet publish src/Klotho/AotSmoke/Klotho.AotSmoke.csproj -r win-x64 -c Release
./src/Klotho/AotSmoke/bin/Release/net8.0/win-x64/publish/Klotho.AotSmoke.exe
echo $?   # 必须为 0

# 5. 真 UDP 跨进程联机（第八轮）。自己拉起服务端与各客户端子进程，退出后比对各自记录的哈希。
#    这一项 dotnet test 覆盖不到：内存链路没有真 socket、没有跨进程序列化、没有独立时钟。
dotnet build src/Klotho/UdpMatch/Klotho.UdpMatch.csproj
./src/Klotho/UdpMatch/bin/Debug/net8.0/Klotho.UdpMatch.exe match --players 2
echo $?   # 必须为 0；--players 3 亦须通过
```

**通过标准**：
- 现有测试全绿（证明"不改动现有项目"成立）
- `FrifloSyncTestRunner` 双实例跑 N 帧，每帧强制回滚重算，**逐帧状态哈希完全一致**
- 覆盖四条路径：组件数据变化、实体创建、实体销毁、**索引/关系回滚后一致**
- 索引/关系专项断言：回滚后按索引查询的结果集与回滚前同 tick 完全一致
- 稳态快照零 GC 分配（`GC.GetAllocatedBytesForCurrentThread()` 断言，**仅针对第 1–3 层**；第 4 层重建只在回滚时触发，不计入每帧稳态）
- **AOT 原生二进制** 打印 `IsDynamicCodeSupported = False` 且 `AOT smoke: PASS`，退出码 0。注意：`dotnet test` 恒有 JIT，跑不到反射路径，故此项**必须跑真机 exe**，不能以 JIT 测试代替
- **真实联机**（第四、五轮追加）：P2P 双引擎、ServerDriven 服务端+双客户端，各自逐 verified tick
  哈希一致。三条「防空跑」断言缺一不可：比赛必须推进、必须有足量共同 verified tick、
  必须真的发生过回滚。SD 另需断言 `ResyncCount == 0` —— 引擎会用服务端全量状态修复 desync，
  修完哈希又一致，只比哈希会让「反复 desync 又反复被救」看起来全绿
- **劣化链路**（第六轮追加）：丢包/乱序/重复下仍逐 verified tick 一致，且必须断言链路**确实
  劣化过**（丢/乱序/重复计数均 > 0）——否则这只是换了个名字的理想链路测试。另需
  `ResyncCount == 0`：只是「丢了包又被救回来」不算通过
- **真 UDP 跨进程**（第八轮追加）：2 人与 3 人局均以退出码 0 通过，且每客户端与服务端的共同
  verified tick 数须 > 20（防止「只比了两三个 tick」的空跑）
- **多房间隔离**（第九轮追加）：各房间刻意种**不同**的世界，断言任意共同 tick 上两房间状态
  **不相同**，且每个客户端拿到的是自己房间的世界。房间一样会因构造而一致，串包 bug 便隐形
- **回归测试的有效性必须实测**：每条新增回归都要把它声称能抓的缺陷**重新注入一次**，确认它
  真的失败。本轮据此发现 late-join 测试用单个子实体时，把子实体池重建删掉**依然通过**
  （`IdArray` 在 `count == 1` 时不碰池分配器）
- **告警指纹不变**：`dotnet build Engine.sln` 除 0 错误外，按告警码分类计数须与改动前逐项一致
  （既有项目新增 0 条）。只看「0 错误」会放过新引入的告警。**必须 `-t:Rebuild` 后比较**：
  增量构建只重编改动过的 TFM，计数会随上次构建的残留而漂移，看起来像凭空多出或少掉告警
- **位宽越界**（第十轮追加）：凡是把 `BitSet`（tag / 组件类型，各 **256 位**）搬进哈希、
  全量状态或索引重建的地方，都必须取满四个字。测试须**先断言自己确实越过了 64 位边界**
  （`structIndex >= 64` / `TagIndex >= 64`），否则填充类型一变就退化成在低位上做一件必然
  成立的事而依旧全绿。注意第 2 类（索引重建）**状态哈希察觉不到**——数据是对的，错的是
  查找表，只能靠按值查询的断言抓
- **查找表的生命周期**（第十一轮追加）：凡是「清空字典再重建」的地方，都必须连同其
  `IdArrayHeap` 一起重置——字典里存的是池句柄，清掉字典就再也还不回去了。断言方式是
  **反复回滚后池子大小不变**（对比第一次重建后的基线，而非回滚前），单看一两次回滚看不出趋势。
  另需一条正确性护栏读回子实体/索引/关系列表：**状态哈希对此无感**，它刻意不看池偏移
- **测试的前置断言不得改变被测状态**（第十一轮追加）：`GetRelations<T>()` 这类「只是问一句」的
  调用会**建出关系原型**，足以让一条回归测试在缺陷注入后依旧通过。前置检查要走只读入口
- **越界边界必须实测而非推断**（第十一轮追加）：第十轮从「`BitSet` 是 256 位」推出边界在 64，
  实测是 32（`isOwner` 是 `int`）。凡声称某个上限，都要有逐值扫描的实测支撑
- **验收必须在 Release 下也跑一遍**（第十二轮追加）：服务器发的是 Release，而 `dotnet test` 默认
  Debug。此前十一轮全部只在 Debug 下验证，Release 甚至编译不过而无人发现。**测试代码不得依赖
  `#if DEBUG` 内的 API**（如 `KlothoEngine.Stats`）——需要什么就在桥接层自己计数
- **资源回收类的断言要钉在"能否复用"上，不是"计数是否归零"**（第十二轮追加）：房间槽位、peer 表
  这类资源，计数归零可以由多种实现满足，其中包括仍然持有对象的那些。断言应当**再用一次**那个
  资源（再开一局、再连一个 peer），因为那正是长期运行的服务器实际要做的事
- **注入验证要针对被守的那一行，不是整组测试**（第十二轮追加）：第十二轮一条测试自称守着
  `RemoveRoomPeers`，删掉该调用后**全组依旧全绿**——因为残留其实由 Klotho 别处清掉了。逐行注入
  才能分清「这条测试守住了它」与「这件事恰好由别处保证」
- **「能否区分」类断言抓不到保序变换**（第十三轮追加）：哈希的字节序若整体反装，仍是**双射**，
  于是每条「两个值必须哈希不同」的断言都照常通过。凡涉及**编码顺序**（字节序、字段序、
  序列化布局）的性质，都要断言**具体取值或跨形态的相等关系**，而不是「能区分」。第十三轮那条
  测试第一版正因如此漏掉了反装的尾部
- **性能声称需要专用工具**（第十三轮追加）：`dotnet test` 内的手写计时会被 JIT 预热与测量顺序
  主导——第十三轮同一进程内同一操作因先后次序差出 30 倍。可以用它判断**趋势**（如宽度依赖是否
  消失），但**不得据此给出加速倍数**

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
