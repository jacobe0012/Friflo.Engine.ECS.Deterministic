# Friflo ECS 确定性桥接兼容性、回滚与 SD 多房间容量审计

> 审计日期：2026-08-06  
> 审计对象：`feat/klotho-integration`，基线提交 `7c98e30172a9390e48251fae5e633429e12b387f` 加当前工作树  
> 结论状态：**有条件通过（Conditional Pass）**  
> 容量口径：**单进程** `FrifloKlothoRoomServer`，不是整台机器跨进程总量

## 1. 执行摘要

当前移植可以让 Friflo ECS 的确定性子集运行在 Klotho 的 P2P 预测回滚、Server-Driven
权威验证、FullState 重同步、late join、reconnect、spectator 与单 socket 多房间链路上。

它不是、也不能被描述成“任意 Friflo ECS 程序无需约束即可确定性联机”。Friflo 官方 API
允许浮点数、并行 query、系统/闭包外部状态、随机 PID、任意引用字段和同步事件副作用；这些能力中
有些天然不能由只保存 `EntityStore` 的回滚层复现。生产代码必须遵守第 4 节的确定性合同。

本轮对抗审计实际证实并修复了 3 类静默错误：

1. 状态 hash 漏掉 ID 回收栈、`sequenceId`、Entity revision 等未来状态。两个世界当下 hash
   相同，但下一次 `CreateEntity()` 会分配不同 ID。
2. `EntityStore.StoreRoot` 与 `RecycleIds` 没进快照、FullState 和 hash。回滚或重同步后公开 ECS
   API 的行为会与权威端不同。
3. `EventRecorder` 会保留废弃预测分支的结构事件。现在其语义固定为“每 tick 事件流”：tick
   开始前清理；命令产生的事件仍可被同 tick 的 systems 读取；Rollback/FullState 也清理旧时间线。

修复后，Klotho 专项测试由 86 增至 **93 条**，Debug/Release 全绿；Friflo 原测试 **656/656**、
内部测试 **133/133（1 skipped）**；NativeAOT 真机 **12/12**；Release 全解决方案 **0 error**。

### 容量结论

在本机 i5-13400F、Windows 11、.NET 8 Runtime、Release、50 Hz、2 玩家/房、2000 个 32-byte
运动实体/房、每 tick 输入 + 模拟 + 权威 hash、52 个回滚槽的合成负载下：

- **生产建议上限：96 房/进程。** 400 帧复测 p95 `12.12 ms`，p99 `18.92 ms`；满足
  20 ms deadline，并在 p95 上保留约 39% 余量。
- **更保守配置：64 房/进程。** p95 `8.25 ms`，p99 `11.84 ms`。适合与日志、持久化、
  真实 UDP、反作弊或其他服务共进程。
- **128 房不通过本次生产门槛。** 两次运行的 p95 分别约 `10.00 ms` 与 `15.77 ms`；后一轮
  p99 `26.49 ms`，证明它对机器抖动/GC/热状态不稳，不能用最好的一次认证。
- 压力 sweep 中 224 房已出现 p99 `24.04 ms`，256 房均值 `26.67 ms`，确认该工作负载的
  过载区间；这不是可部署上限。

每个纯权威 simulation 的稳态托管内存实测约 **7.03 MB/房**，96 房约 **675 MB**，所以该档
首先受单线程 tick 预算限制，不受 32 GB 物理内存限制。端到端基准进程还同时承载 2 个预测客户端/房，
它的工作集不可当成服务端 RAM。

“96”不是框架常数。实体数、组件宽度、systems、玩家数、tick rate、回滚窗口、网络包量任一变化都
需要重跑第 7 节工具。对实际游戏唯一负责的答案是：**用实际 world 与 systems 替换合成负载后再认证。**

## 2. 审计依据

本审计对照了 Friflo 官方文档当前描述的能力：

- [Start / ECS 能力总览](https://friflo.gitbook.io/friflo.engine.ecs)
- [Entity、EntityStore、组件、Script、Hierarchy](https://friflo.gitbook.io/friflo.engine.ecs/documentation/entity)
- [Query、结构变更与 CommandBuffer](https://friflo.gitbook.io/friflo.engine.ecs/documentation/query)
- [Events 与 EventRecorder](https://friflo.gitbook.io/friflo.engine.ecs/documentation/events)
- [Component Index](https://friflo.gitbook.io/friflo.engine.ecs/documentation/component-index)
- [Release Notes / NativeAOT 与行为变更](https://friflo.gitbook.io/friflo.engine.ecs/project/package/release-notes)

官方文档说明 Friflo 是 archetype ECS，组件连续存储；结构变更不能在 query 枚举中直接进行；
EventRecorder 会记录组件/tag 变化；关系、索引、Script、层级与 NativeAOT 都是公开能力。本桥接层必须
同时覆盖“主数据”和这些能力依赖的旁路表，不能只复制 archetype component arrays。

## 3. 架构与状态边界

```text
Klotho Engine
  ├─ P2P prediction / rollback
  ├─ Server-Driven verified state / FullState resync
  └─ RoomRouter + Room + ServerNetworkService
                │ ISimulation
                ▼
FrifloSimulation
  ├─ Tick: commands → Friflo SystemRoot.Update
  ├─ FrifloStoreSnapshot: per-tick rollback ring
  ├─ FrifloStateHasher: verified state hash
  └─ FrifloStateSerializer v4: late join / reconnect / resync
                │ internal deterministic access
                ▼
Friflo EntityStore
  ├─ archetypes / component arrays / tags
  ├─ nodes / sequenceId / recycle stack / revision / StoreRoot
  ├─ indexes / relations (restore 后重建)
  ├─ hierarchy parentMap / child pool
  └─ scripts / EventRecorder
```

快照是 tick 前状态：`key K = 执行 tick K 之前的状态`。Rollback 恢复 K 后，Klotho 用修正后的
输入重演 K..N。FullState v4 是另一条独立路径，用于 SD 初始世界、late join、reconnect 和 desync
修复；它不能依赖接收端已经创建过相同 archetype。

## 4. 兼容性矩阵与确定性合同

| Friflo 能力 | 状态 | 生产合同 |
|---|---|---|
| Entity 创建/删除/ID 复用/revision | ✅ | 已快照并纳入 hash；`RecycleIds` 策略也已覆盖 |
| struct components、tags、archetypes | ✅/受限 | 只允许可复现字段；见浮点、引用和平台限制 |
| Query / SystemRoot | ✅/受限 | system 顺序固定；结构变更用 CommandBuffer 或枚举后执行 |
| Component Index | ⚠️ | 回滚后重建；本 fork 的 `isOwner` 位宽使 specialized 类型总量存在 32 上限 |
| Relations / LinkRelations | ⚠️ | 数据与反向表可恢复；与 indexed components 共用上述 32-bit 风险边界 |
| Hierarchy / `StoreRoot` | ✅ | parent/children、child pool、StoreRoot 均覆盖；子顺序有语义 |
| Script | ⚠️ | 字段必须是值类型或 Entity；rollback 保持实例，FullState 会创建新实例 |
| EventRecorder / EventFilter | ✅/语义收窄 | 固定为 per-tick；框架自动在 tick 开始清理 |
| 同步 ECS event handlers | ⚠️ | 会在预测和重演中重复触发；不得直接播放音效/发外部消息/改非快照状态 |
| CommandBuffer | ✅/受限 | 必须在同一确定性 tick 内 Playback；不得跨 tick 存在 system 字段里 |
| Parallel query / parallel jobs | ❌ | 禁止。完成顺序与跨线程写入顺序不可复现 |
| `PidType.RandomPids` | ❌ | 禁止。simulation store 使用默认 `UsePidAsId` |
| `float` / `double` 跨 tick 状态 | ❌ | 使用 FP64/定点；桥接当前只文档约束，尚未 fail-fast |
| 任意 class/string/array 引用字段 | ❌ | component/script 仅允许值类型；唯一特例为 Entity identity |
| system 实例字段中的模拟状态 | ❌ | 必须移入 EntityStore component；桥接无 Klotho snapshot participant 等价物 |
| NativeAOT/IL2CPP 风格路径 | ✅/注册制 | specialized component、relation、script 必须按 Friflo NativeAOT API 注册 |
| 跨大端/不同运行时内存布局 | ❌/未认证 | raw unmanaged component wire data 假定同 build、little-endian、兼容布局 |
| Klotho `IsMatchEndedState` backstop | ⚠️ | FrifloSimulation 目前使用接口默认 false；房间结束需游戏/Room 显式驱动 |
| `ISimulation.Reset()` 清空 world | ⚠️ | 当前只清 tick/历史，不清 EntityStore；Klotho 现有联网路径未调用，但接口语义未完整 |

因此兼容口径应写成：

> “兼容 Friflo ECS 的确定性 simulation profile”，而不是“兼容 Friflo 的所有合法程序”。

建议把本表做成项目编码规范，并在游戏程序集增加 analyzer/source generator，至少对 float/double、
managed reference、RandomPids、parallel execution 和带状态的 system 字段 fail-fast。

## 5. 对抗性发现

### 已修复

| ID | 严重度 | 旧行为 | 证伪方式 | 修复 |
|---|---:|---|---|---|
| F-01 | Critical | recycle order/revision 不进 hash；未来 ID 分配分歧被权威验证漏报 | 两 store 当前实体相同、回收顺序相反；旧 hash 相同，下一实体 ID 不同 | hash 加入 sequence、ordered recycle stack、revision、live/owner/link/parent metadata |
| F-02 | High | StoreRoot 与 RecycleIds 在 rollback/FullState 后漂移 | 3 个测试在旧实现全部失败 | snapshot、FullState v4、hash 三路覆盖 |
| F-03 | High | EventRecorder 保留废弃预测分支事件 | snapshot 后制造 event，rollback + corrected tick 后旧事件仍在 | tick 前、Rollback、FullState 清理；命令事件仍可供同 tick system 读取 |
| F-04 | Test | 4 个“layout independence”测试实际比较了不同 revision/recycle 元数据 | 元数据 hash 修复后测试反而失败 | 改夹具：用组件进出 archetype、对称 churn 隔离真正的 slot/pool 变量 |

### 仍需接受或排期

| ID | 严重度 | 风险 | 建议 |
|---|---:|---|---|
| R-01 | High | entity-level event handler 在实体删除时由 Friflo 清除，rollback 复活实体不会复活 delegate；同步 handler 还会在重演中重复副作用 | simulation 逻辑用 EventRecorder；表现事件走 Klotho confirmed/verified event buffer |
| R-02 | High | system/闭包/static 中的可变模拟状态不被快照 | 强制所有跨 tick 状态组件化；后续可增加 `IFrifloSnapshotParticipant` |
| R-03 | High | specialized index/relation owner bits 当前真实边界约 32，不是官方 components/tags 的 256 | 启动时计数并 fail-fast；要解除需把 node 位图扩宽 |
| R-04 | Medium | unmanaged component 按原始内存 bytes 传输；padding、ABI、端序未做异构认证 | 生产只混合同 build 的 x64/ARM64 little-endian；异构前改成字段级 canonical encoding |
| R-05 | High | rooms 顺序更新；一个卡死/超时 room 会阻塞整进程，没有 Klotho ServerLoop straggler isolation | 生产先用进程分片隔离；并行房间前禁止跨房间状态并加 watchdog |
| R-06 | Medium | 强制关房、MatchEnded/Aborted grace、真实 UDP 下生命周期与 peer-id 复用未组成一条端到端回归 | 补真 UDP 生命周期 soak |
| R-07 | Medium | 容量工具使用 Loopback；未测 NIC、内核 UDP buffer、带宽、包加密或真实丢包下 CPU | 上线前在目标 Linux/云机用真实 transport 复跑 |
| R-08 | Medium | FullState 已升级 v4，v3/v4 混跑会明确拒绝而非兼容 | 滚动发布需 protocol/build gate；不要跨版本复用房间 |
| R-09 | Medium | `Reset()` 与 match-end state 两个 ISimulation 可选/边缘语义仍不完整 | replay/房间自动结束若要依赖它们，应先补接口实现和测试 |

## 6. 回滚审计结论

在第 4 节合同内，未发现仍可复现的 rollback 数据错误。已验证的范围包括：

- component 值、tags、archetype 迁移、实体创建/删除、ID stack、revision；
- component index 及高位类型重建、relations/incoming links；
- parent/child hierarchy、child allocator pool、StoreRoot；
- scripts 字段、Entity 引用重绑定、rollback 实例 identity；
- EventRecorder 的 corrected-tick 边界；
- FullState 到 fresh/non-empty store、late join、reconnect、spectator；
- prediction 被晚到输入推翻后的逐 verified-tick server/client hash；
- 丢包、重复、乱序、MTU、真 UDP 2/3 人局；
- 多房间路由隔离、房间释放与槽位复用；
- NativeAOT 无动态代码路径。

“hash 一致”没有被当成唯一证据。索引/关系/池/事件等派生或旁路状态使用 API 读回断言；联机测试还
断言 rollback 必须实际发生、比较 tick 数足够、`ResyncCount == 0`，防止“持续 desync 又被 FullState
救回”伪装成通过。

## 7. SD 多房间性能方法与结果

### 环境

| 项 | 值 |
|---|---|
| CPU | Intel Core i5-13400F，10 cores / 16 logical processors |
| RAM | 31.84 GB |
| OS | Windows 11 Pro build 26200, win-x64 |
| Runtime | .NET 8.0.25；SDK 10.0.302 构建 net8.0 项目 |
| Build | Release，Server GC |
| Tick | 20 ms / 50 Hz |
| Room | 2 predictive clients，2000 Body + 2 control entities |
| Body workload | 4 × FP64（32 B），逐实体积分/边界分支，每玩家每 tick 1 command |
| Rollback | MaxRollbackTicks=30；实际 snapshot slots=52 |
| Sampling | warmup 100 ticks；64/96 房各测 400 server updates；客户端 update 不计入计时 |

工具真实创建 `FrifloKlothoRoomServer`、`RoomRouter`、`ServerNetworkService`、两个
`FrifloKlothoClient`/房，完成 handshake、initial FullState、ready、Running，再逐帧计时 server
`Update()`。所有房间最少推进 500 tick，避免空跑。

### 最终资格点

| 房间 | mean | p50 | p95 | p99 | max | 判定 |
|---:|---:|---:|---:|---:|---:|---|
| 64 | 6.22 ms | 5.91 ms | 8.25 ms | 11.84 ms | 21.97 ms | 通过，保守档 |
| 96 | 9.08 ms | 8.48 ms | 12.12 ms | 18.92 ms | 40.83 ms | 通过，本机建议上限 |
| 128（复测） | 12.12 ms | 11.22 ms | 15.77 ms | 26.49 ms | 88.91 ms | 不通过 70% p95 与 20 ms p99 门槛 |

Windows 非实时调度下单次 max 仍可越过 20 ms，所以“通过”不是硬实时保证。若业务要求零 deadline miss，
应使用独占/固定 CPU、关闭同机干扰、在目标 Linux server 上复测，并降低到 64 或增加分片。

### 压力定位（非生产资格）

| 房间 | p95 | p99 | 说明 |
|---:|---:|---:|---|
| 160 | 14.15 ms | 17.94 ms | 已无 30% p95 余量 |
| 192 | 14.28 ms | 16.38 ms | 单次 sweep 仍推进，但不作为认证值 |
| 224 | 17.32 ms | 24.04 ms | p99 deadline miss |
| 256 | 36.07 ms | 66.14 ms | 均值 26.67 ms，明确过载 |

该 sweep 只用于找到拐点。多次运行显示热状态、GC 与同机负载会显著改变尾延迟，容量决策取重复运行的
较差结果。因此最终建议是 96，不是 192。

500 实体/房的探索性结果在 384 房时 p95 约 `10.13 ms`，说明世界大小对容量影响很大；该档没有继续
压到失败点，不能声称 384 是它的上限。

### 内存

纯权威 simulation（不含两个同进程客户端）在 2000 Body、52 slots、80 tick 预热后的托管增量：

| rooms | managed delta | per room |
|---:|---:|---:|
| 1 | 7.09 MB | 7.09 MB |
| 8 | 56.24 MB | 7.03 MB |
| 32 | 224.99 MB | 7.03 MB |

线性外推到 96 房约 675 MB、128 房约 900 MB。生产仍需另加 Klotho 网络 buffers、日志、游戏资源、
socket buffers 与 GC headroom；建议进程内存预算至少按测得 simulation 值的 2 倍配置，再以实际峰值修正。

### 复现命令

```powershell
dotnet build src/Klotho/Tools/RoomCapacityBenchmark/RoomCapacityBenchmark.csproj -c Release

dotnet run --project src/Klotho/Tools/RoomCapacityBenchmark/RoomCapacityBenchmark.csproj `
  -c Release --no-build -- `
  --rooms 64,96,128 --entities 2000 --tick-ms 20 --warmup 100 --frames 400

dotnet run --project src/Klotho/Tools/RoomCapacityBenchmark/RoomCapacityBenchmark.csproj `
  -c Release --no-build -- `
  --memory-only --rooms 32 --entities 2000 --warmup 80 --frames 10
```

容量门槛建议固定为：

```text
p95(server update) <= tickBudget × 0.70
p99(server update) <  tickBudget
serverTicksMin     >= warmup + measuredFrames
GC / working set   无持续单调增长
```

## 8. 验证记录

```text
dotnet build Engine.sln -c Release --no-restore
  PASS, 0 errors, 25 existing trim/AOT warnings

dotnet test src/Klotho/Tests/Klotho.Tests.csproj -c Debug
  PASS, 93/93

dotnet test src/Klotho/Tests/Klotho.Tests.csproj -c Release
  PASS, 93/93

dotnet test src/Tests/Tests.csproj -c Release
  PASS, 656/656

dotnet test src/Tests-internal/Tests-internal.csproj -c Release
  PASS, 133 passed, 1 existing skipped

NativeAOT publish + native executable
  IsDynamicCodeSupported=False, 12/12 checks, exit code 0
```

## 9. 上线决策清单

满足以下全部条件才可把本报告的 Conditional Pass 转成某个游戏版本的 Go：

1. 游戏组件通过确定性静态检查：无 float/double、无任意引用字段、无 RandomPids。
2. 所有跨 tick 逻辑状态都在 store；system/static/closure 不持有会影响分支的可变状态。
3. 无 parallel query；结构变更不发生在 query 枚举中。
4. specialized index + relation 类型数量不越过当前 32-bit 边界。
5. 表现副作用不直接订阅预测态同步事件；只消费 confirmed/verified 事件。
6. 在目标服务器、真实 UDP、真实组件与 systems 上重跑容量工具。
7. 单进程配置不高于该目标机复测得到的 p95/p99 门槛；本机合成负载默认采用 96。
8. 做至少 24 小时 room create/run/end/release soak，监控 RSS、GC、tick p99、resync、rollback failure。
9. v4 FullState 与应用版本绑定，不允许 v3/v4 客户端滚动混连同一房间。

## 10. 最终结论

当前框架已经具备可用的 Friflo ECS + Klotho 确定性联机基础，核心 rollback、权威 hash、FullState、
预测纠错和多房间路径在所声明的子集内有较强证据。它仍需要游戏侧遵守严格 deterministic profile，
并在目标工作负载上重新做容量认证。

本机 2000 实体合成负载的落档配置为：**96 房/进程建议上限，64 房保守档，128 房不认证。**
