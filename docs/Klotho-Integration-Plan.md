# 将 Klotho 0.7.1（除 Godot 外）集成到 Friflo.Engine.ECS.Deterministic

> 状态：**已实施**（分支 `feat/klotho-integration`）
> 落档日期：2026-08-04，第六至九轮补充于 2026-08-05
> 来源：Klotho 0.7.1 @ `H:\Test\Git\Klotho-0.7.1`

---

## 实施结果（2026-08-04；第六至九轮 2026-08-05）

**验收全部通过**：`dotnet build Engine.sln`（Debug + Release）0 错误；现有 `Tests` 656/656、
`Tests-internal` 133/133 全绿；新增 `Klotho.Tests` **66/66** 通过；
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

回归：`dotnet build Engine.sln` 0 错误且**告警数与改动前逐项一致**（本次新增 0 条）；
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

回归：`dotnet build Engine.sln` 0 错误，**告警指纹与改动前逐项完全一致**（新增 0 条）；
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
  （新增 0 条）。只看「0 错误」会放过新引入的告警

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
