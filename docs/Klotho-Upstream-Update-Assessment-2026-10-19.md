# Klotho 上游更新适用性评估（2026-10-19）

## 结论

**可以用上，但建议先移植少量确定有收益的修复，不要直接覆盖整个 `src/Klotho`。**

最值得优先采纳的是 **LiteNetLib transport 生命周期修复**，其次是 **固定随机种子兑现**及少量诊断开销修复。这些位于当前 Friflo 实际使用的 transport / network service / engine 路径。导航、物理、回放和新指纹能力也有价值，但其中很多依赖 Klotho 自有 ECS、`Frame`、系统注册和回调，不能因为新 Runtime 编译进来了就声称 Friflo 已获得那些功能。

上一轮评估阶段没有升级项目源码，只新增本文档。用户明确授权后，在临时副本验证了 transport-only 与整套 v0.14.1 候选：各 108/108 测试通过，整套候选 NativeAOT 30 项检查通过；但整套新版 Unity 验证未完成。第 6 节保留这些隔离实验记录。

## 已采纳的选择性修复（2026-10-19）

随后按用户要求，只将当前 Friflo 实际使用路径的修复移入项目。**当前仍是 v0.7.1 + selective backports，不是整套 v0.14.1。** 来源固定为 `e29c4892c88db097f3ac0d135874c82e9b6726d6` 的最终实现：

1. **Transport 生命周期**：仅更新 `Runtime/LiteNetLib/LiteNetLibTransport.cs`，处理重复 Listen、manager 发布/替换、并发关闭与 request callback 的 manager 捕获。
2. **固定 seed**：仅摘取 `KlothoNetworkService.cs`、`ServerNetworkService.cs` 的开局选择 hunk；非零 authored seed 生效，0 在开局自动选择；P2P 不把 effective seed 回写到 authored 配置。
3. **诊断开销**：仅摘取 ChainBreak 缺失玩家集合限流、suppressed 汇总和 SD Debug-disabled 时避免 buffer 扫描的 hunk。保留当前 `DEVELOPMENT_BUILD || UNITY_EDITOR` 编译边界。

新增回归为 `Test_TransportLifecycle`（4 项真实本机 UDP）、`Test_RandomSeed`（6 项 P2P/SD）及诊断构建专用 `Test_DiagnosticLogging`（1 项）。旧实现的重复 Listen、归档旧 DLL 的并发 Disconnect、固定 seed、自动 seed 配置复用和诊断洪泛均已见红；移植后转绿。SD 自动 seed 另以临时故障正控制验证，最终源码已恢复。

本批最终验证：

| 验证 | 实际结果 |
|---|---|
| Klotho Debug / Release 全套 | 各 118 passed / 0 failed / 0 skipped |
| 独立 DEVELOPMENT_BUILD 全套 | 119 passed / 0 failed / 0 skipped；普通构建不编入专用诊断测试，不是 NUnit 跳过 |
| 原 ECS / internal 测试 | 657 passed；133 passed + 1 个既有 skipped |
| 真 NativeAOT exe | `IsDynamicCodeSupported=False`，30 checks passed，exit 0 |
| 当前选择性修复的 Unity 编译 | Unity 2022.3.62f2c1 / Roslyn 4.3.1，ECS、Runtime、Bridge 编译成功；只有一条 Unsafe/netstandard 版本统一警告 |
| Unity Mono smoke | 29 checks passed，动态代码开启；不是 NativeAOT/IL2CPP |
| ECS netstandard2.1 | 构建成功 |

未更换 generator、消息/命令 wire、replay、物理/导航源码，也未改变 Friflo FullState v5；已有 Bridge/ECS 审核修复保持不变。完整 solution 的已知 ImageSharp/OpenGL 许可证阻塞未绕过；IL2CPP Player 与长期线程/socket soak 未验证。ChainBreak 原因集合改变时立即输出仅做上游 hunk 源码核对，现有诊断回归独立验证的是同一原因限流、suppressed 汇总与单次 dump；没有以计时断言冒充 buffer 扫描性能测试。

本批独立输出与红绿日志位于 `C:\Users\89256\AppData\Local\Temp\klotho-selective-backports-e1nrebwj`。

## 后续真实 UDP 验证（2026-10-19）

选择性补丁后，使用现有 `Klotho.UdpMatch` CLI 的 `match` 入口驱动真实服务端/客户端 OS 子进程，而非以 NUnit 或 Loopback 代替：

- 2 人局、3 人局各 6 次，共 **12 局、12 个服务端进程、30 个客户端进程**，所有退出码为 0。
- 累计 **11,947 次 verified tick hash 比对一致**；另用 Python 严格读取各进程的 `tick hash` 文件独立复核，与协调器统计一致。
- 所有 30 个客户端 **ResyncCount == 0**；每局都实际发生了回滚，未把服务端强制重同步后的相同状态当作确定性通过。
- 初始两局共用一个系统分配端口，后续连续十局共用另一个端口；每局所有进程退出后均能重新绑定该端口。
- 最后通过 Windows 进程查询确认本项目的 `Klotho.UdpMatch` 进程无残留。

另用同一进程内的同一个 transport 完成 20 次预热和 **200 次测量循环**，每轮两次 Listen 同一端口，再由两个线程同时 Disconnect，全部通过，最终端口可重绑。约 27 秒内按每 20 轮采样，线程数始终为 14；句柄由初始 274 上升至 281 后，从第 40 到第 200 轮保持 281；private bytes 在约 10.7–13.2 MB 间波动，没有观察到随循环线性增长。这里包含 JIT/序列化器/线程池的初期资源建立，不能把短期平台期当作长期无泄漏证明。

对局部分是约 101 秒的本机短轮次部署检查，不是互联网/NAT/跨机器测试，也不是长期内存、线程或 socket soak。基本对局与启动关闭覆盖已补充，不能把它等同于跨进程掉线重连覆盖。

运行方式：`dotnet build src/Klotho/UdpMatch~/Klotho.UdpMatch.csproj -c Release`，随后对构建的 `Klotho.UdpMatch.dll` 执行 `match --players 2` / `match --players 3`，显式传入测试端口。重复执行脚本、每局日志、独立复核结果、运行程序集 SHA256 与源文件 SHA256 快照保存在 `C:\Users\89256\AppData\Local\Temp\friflo-udp-validation-hznz7xhj`。

重连探针的接入注意：`client.Stop(keepReconnectCredentials: true)` 不等同于断开调用方拥有的 UDP transport。模拟掉线时须再调用旧 transport 的 `Disconnect()`，与 `Test_Reconnect` 的既有调用顺序一致；否则服务器仍会看到活连接。临时驱动首次遗漏该调用时等待断线超时，已保留日志并修正驱动，未为此改动产品所有权语义。

**重连接收交接缺口：已定位、修复，并于 2026-10-20 完成最终验证。** 无掉线控制为 839 次 hash 一致、双方 0 resync；actual-elapsed 与 CLI 两种节奏的延后交接均在返回端首个 verified tick 出现真正 StateDivergence，command digest 相同，随后收到纠正 FullState。公开 transport 观察明确记录 FullState tick 81 后，在握手完成至 session 创建的空窗里到达了 `VerifiedState:82`。仅改为回调内即时创建就能连续 3 次重连通过，证实是接收交接而非时钟口径问题；该调用方式只是诊断对照，不作为产品修复。

本地修复在移除握手接收器前安装有界原始消息暂存，交给 ConnectionResult，再由 SD/P2P service 的首次 Update 按序消费；不在初始化阶段提前重放、不提前反序列化 pooled commands。限制为 1024 包 / 4 MiB，超限或中途断开明确失败。结果可显式 Dispose 以放弃暂存，认领后由 service 持有；初始化和完成回调异常路径也清理监听。未改协议、generator、catch-up 算法或 Friflo FullState v5。

修复初版下，原始延后交接探针已连续 3 次通过（actual-elapsed、CLI 节奏均通过，所有返回者/留守者 0 resync），还重复通过了一组 actual-elapsed 三循环。每轮至少 80 次后续 verified hash 比对，playerId 保持 2；观察到 transport peer id 1→2→3→4，未声称 peer-id 复用覆盖。确定性 NUnit 对照中旧 DLL 的两项延后交接见红、两项即时交接通过；修复初版前 24 项交接防护通过，初始化回调所有权防护另 2 项见红后已修正。

新增 `Test_ConnectionHandoff.cs` 共 30 个参数化 case，覆盖 raw buffer 覆写、保序/恰好一次、SD/P2P、正常/late-join/reconnect、外部原 transport 轮询、结果认领/放弃、断开、队列上限、异常回调与清理。曾因执行工具的安全前置服务持续超时而暂停验收；2026-10-20 用户直接运行该 30 项测试全部通过，随后执行工具恢复，补齐了最终源代码的复测，而非沿用旧构建成绩。

| 最终修复验证（2026-10-20） | 实际结果 |
|---|---|
| Klotho Release / Debug 全套 | 各 148 passed / 0 failed / 0 skipped |
| DEVELOPMENT_BUILD 全套 | 149 passed / 0 failed / 0 skipped |
| ECS / internal 回归 | 657 passed；133 passed + 1 个既有 skipped |
| 原始延后交接重连，actual-elapsed | 连续 3 次通过，240 次重连后 verified hash 比对一致；返回者、留守者 0 resync |
| 原始延后交接重连，CLI 节奏 | 连续 3 次通过，242 次重连后 verified hash 比对一致；返回者、留守者 0 resync |
| 独立进程普通 UDP 对局 | 2/3 人局各一次，797 + 1,199 = 1,996 次 verified hash 一致，全部客户端 0 resync、端口释放 |
| 真 NativeAOT smoke | `IsDynamicCodeSupported=False`，30 checks passed，exit 0 |
| Unity 2022.3.62f2c1 / Roslyn 4.3.1 | ECS、Runtime、Bridge 编译成功；仅既有 Unsafe/netstandard 版本统一 warning |
| Unity Mono smoke | 29 checks passed，exit 0；不是 NativeAOT/IL2CPP |

本次是本地接收所有权修复，不是新的上游协议回迁。`KlothoConnection.Dispose()` 在握手成功后不销毁结果拥有的暂存；若不接入 service，调用方需 `ConnectionResult.Dispose()` 放弃结果；认领后结果 Dispose 不影响 service。暂存直到 service 初始化中所有可抛异常回调成功后才转移，避免初始化失败遗留接收监听。

最终日志和源文件校验快照位于 `C:\Users\89256\AppData\Local\Temp\friflo-handoff-final-wjlp4j0p`。重连仍是单进程、多真实 UDP 端点；独立进程验证是普通对局，未声称跨进程 cold-start reconnect、IL2CPP Player、跨机器/NAT 或长期 soak 通过。NativeAOT 发布有既有 Fliox trim/AOT 告警；完整 solution 的 ImageSharp/OpenGL 许可证阻塞没有绕过。

## 1. 评估时的实际版本与比较基线

| 对象 | 已确认事实 |
|---|---|
| 当前项目 vendored Runtime | 对应 **v0.7.1**，不是根据文档名称猜测；归一换行和目录改名后，402 个共有 `.cs` 中 401 个完全相同，唯一差异为本地 `KlothoConnection.SetDeviceIdProvider` 修复 |
| 当前本地额外 Runtime 源码 | `LowLevel/CompilerFeaturePolyfills.cs`；移入单一 Runtime 程序集的 MEL adapter。它们不是上游 Runtime 的同路径文件 |
| 当前 generator | Git blob `cfed6e84b0f8fa538e3cf09440fd825b413a44be`，严格匹配 v0.7.1 的预编译 DLL，104,448 bytes |
| v0.7.1 | `eea42a3526873f7039e762eda01c1ce680bae6b6`，2026-08-04 |
| 最新发布 v0.14.1 | `e29c4892c88db097f3ac0d135874c82e9b6726d6`，2026-09-17 |
| GitHub main | `9ebadca026147b0e58a2572c81462cbd34fd7493`，2026-09-20；比 v0.14.1 多一个 `release.yml` 提交，Runtime / generator 无差异 |
| `H:\Test\Git\Klotho-main` | `package.json` 为 **0.14.1**；Runtime 的 432 个相关文件（431 `.cs` + package.json）与 GitHub main 归一换行后完全相同，不需要再从网上下载才能取得这些源码 |

当前 vendored `.cs` 与 v0.14.1 对比：**320 相同、82 修改、29 上游新增、2 本地额外文件**。映射时还原了 `Json~`、`Plugins~` 和 `LiteNetLib.v2.1.4~` 的布局差异。不能把这些 Unity 包装改名误报成上游增删。

最新 generator 位于 `com.xpturn.klotho/Plugins/Analyzers/KlothoGenerator.dll`，108,544 bytes，SHA256 为 `63d711b82a709c8b0889773c7cbcd433951d1823bae2c6bdd14833df1208ae26`；与 H 盘快照相同，与当前 vendored DLL 不同。建议升级时钉住 v0.14.1 的 commit，而不是移动中的 main。[S1][S2][S3]

## 2. 第一批：可直接惠及当前 Friflo 的小范围修复

### 2.1 高优先：LiteNetLibTransport 重复启动与并发关闭

**建议第一项就做。** 当前 Bridge 的真实 UDP 使用 `LiteNetLibTransport`，不经过 Klotho `EcsSimulation`，所以收益不受 ECS 适配限制。

上游 v0.13.1 修复：

- 多次 `Listen()` 先停旧 manager、清理 peer map，再重新 bind，避免遗留 socket / 接收线程；
- `Disconnect()` 在锁内 capture-and-null，只有一个调用者获得 manager 并执行 `Stop()`，防止双重 Stop；
- `Connect()` 在同一锁内创建并启动新 manager，避免另一线程把尚未启动的对象取走、随后留下无人可关闭的线程和 socket；
- `OnConnectionRequest` 不再在不受保护的情况下反复解引用可变 manager 字段。

精确相关提交：`54e1d05`（并发 Disconnect）、`73c8e79`（重复 Listen）、`728d461`（其余 manager lifetime 窗口）。最小移植单位是一个文件 `Runtime/LiteNetLib/LiteNetLibTransport.cs`，所用 `LiteNetLibPeerMap.Clear` 等接口在当前基线已存在。**此批不需要改消息协议、Friflo 快照格式或 generator。** [S4]

验证重点应是真 UDP，而不仅是现有 Loopback 108 项：重复 host/listen 同一端口、连接失败后重试、多线程重复 Disconnect、server loop 与 UI 同时关闭、线程数/端口是否回落，以及断线通知是否只发一次。不能把静态锁结构正确等同于已经通过并发 soak。

### 2.2 高收益、较低移植成本：真正采用 authored RandomSeed

当前 v0.7.1 的 P2P host 和 SD server 都无条件使用 `Environment.TickCount`，忽略调用者提供的非零 `SessionConfig.RandomSeed`。上游 v0.11.0 在比赛开始时选择 `configured != 0 ? configured : Environment.TickCount`，使固定 seed 可复现，并不再把 effective seed 写回 authority 的 authored config。[S5]

这会直接到达 Friflo 的 `callbacks.OnInitializeWorld(store, networkService.RandomSeed)`，所以不是 Klotho ECS 专属改进。

建议摘取两个 network service 的小块修改，而不是整文件覆盖；整文件还含 ready fingerprint 等协议变动。另可独立引入 `SessionConfig.Clone()`，减少多房间共用可变配置。

需验证：P2P/SD 固定正负 seed 都兑现；0 仍自动生成；客户端和服务端收到相同 effective seed；同一配置复用不会把上一局自动 seed 固定下来；业务代码读 effective seed 使用 `Engine.RandomSeed` / network service，而不是假定 `SessionConfig.RandomSeed` 已被改写。[S5]

### 2.3 可随后摘取：诊断日志限流和关闭日志时的扫描开销

上游按缺失玩家集合而非变化中的 tick 来限流 ChainBreak 日志；SD 预测日志在计算 `GetBufferedTickRange()` 前先检查 Debug 日志级别。后者原本即使日志关闭仍遍历 input buffer。可只摘取这些诊断 hunk，避免整份 `KlothoEngine.ServerDrivenClient.cs` 把 error correction 等新增依赖一起带进来。[S6]

v0.14.1 还移除了 `DEVELOPMENT_BUILD` guards，适配 Unity 6000.6 的 analyzer；应作为独立编译兼容批次处理。不要仅设置替代的 `DefineConstants` 覆盖调用者原有 `DEBUG/TRACE`，也不要因此声称 Friflo 自己的全部 Unity 6000.6 warnings 已消除。[S3]

## 3. 有实际价值，但必须有适配/行为迁移的更新

| 更新 | 当前能否直接获得 | 需要处理的事项 |
|---|---|---|
| sphere/capsule/mesh 碰撞法线修复 | 若游戏直接使用 `FPPhysicsWorld`，可以受益；当前 Bridge 不会自动替 Friflo 注册 Klotho `PhysicsSystem` | `NarrowphaseDispatch` 与 `FPPhysicsWorld` 要配套；轨迹、state hash 和旧 replay 会变，所有参与端必须一致升级 |
| contact/trigger debug snapshot 有界复制 | 使用这些复制 API 的代码可直接受益 | 空 buffer 返回 0；超限截断而非越界，并有显式统计。验证大于旧 256/64 buffer 容量的场景 |
| 动态 NavMesh rebake、建筑 placement、分段寻路、partial path、调优 | 算法类可复用；`FPNavAgentSystem`、rebake driver、installer 的 `Frame`/系统集成不能直接给 Friflo 用 | 建筑/agent 状态放进 Friflo store，提供适配；rollback、FullState、replay 后重建派生 mesh，不能只在预测 tick 装一次；旧 `.bytes` 需重导出 |
| 环境/schema/ready fingerprints | 上游机制可借鉴，但**不是现成的 Friflo schema 保护** | Ready layout 读取 Klotho `ComponentStorageRegistry`；环境和 FullState fingerprint 路径按 `EcsSimulation` 查系统或直接返回 0。需为 Friflo 提供自己的 layout/content fingerprint seam |
| replay anchors、tick-0 reconstruction、late-join entitlement replay | replay codec/engine 有更新，但 Friflo 没有直接实现上游 `ISimulationCallbacks` 的 `EcsSimulation`/`Frame` 回调 | 补接 Friflo world initialization、join-time seeding、静态数据 fingerprints 与结果验证，再做实际录制/回放回归 |
| `NullTransport` / 单机 entry | `NullTransport` 自身是可用的 engine-agnostic transport | `StartLocal` 仍走 Klotho session/EcsSimulation 构造；若需要 Friflo 单机入口，要单独适配，不能直接用它替换现有 Listen/Connect（两者故意返回 false） |
| ServerLoop shutdown/straggler 修复 | 当前 Friflo 多房间 host 不走这个调度器，因此不会自动受益 | Bridge 的 RoomServer 顺序 update，Klotho RoomManager/ServerLoop 仍硬构造/依赖 Klotho ECS。需要迁移调度 seam 后才谈 parallel room、sliding straggler window 与 `ownsProcess:false` |

物理法线确有行为修正：sphere/capsule 与 mesh 等组合旧法线方向错误，会把物体推入而非推出；不会因为它确定地算错就产生 desync。修复后和旧端的轨迹不兼容。新增 `FPContactSnapshotStats` 主要是可观测性，而非模拟状态。[S7]

基础 FP64、既有 vector/quaternion/matrix 数学源文件没有变化；新增 `FPInt128`、`FPGeoPredicates` 服务于确定性几何/rebake。不要把本次导航更新说成“换掉了整个定点数学底层”。导航自身则有显著格式和行为变化：mesh format v4、triangle 120 → 88 bytes、array → span、per-agent masks、hierarchical path costs；上游性能数据只是特定样例的测量，不能直接套用本项目 96 rooms 容量结论。[S8]

## 4. 不应作为 Friflo 功能直接宣传的更新

- `[KlothoCleanup]`、component added/removed signals、`FilterMutationGuard`、`Frame.TryRead`、Klotho 自有 ECS storage 优化：作用于 `xpTURN.Klotho.ECS.Frame` / `SystemRunner`，不作用于 Friflo archetype/store/query。Friflo 已有自己的事件、结构修改规则；引入这些源码不会自动改变它们。[S9]
- snapshot views / Transform error correction：上游针对自己的 `EcsSimulation` 与 `FrameRef`/Transform 系统，当前 Friflo bridge 不因此获得表现层平滑。
- HFSM `.Named()` 是展示元数据，若当前使用 Klotho HFSM graph 可利用名称；但其运行上下文/agent/Frame 仍需适配。不能为了 editor window 的显示改进整包导入 Runtime.Unity。
- Unity view pool、Unity object id、Godot adapter、Brawler sample 更新：不是当前 engine-agnostic 三程序集的必要依赖；不建议为了“追最新”一起搬入。

## 5. 整包升级必须尊重的边界

### 5.1 接口稳定不等于功能自动接好

**`Core/ISimulation.cs` 从 v0.7.1 到当前 HEAD 没有变化。** 这使保留现有 FrifloSimulation 的大版本升级在接口上有良好基础，但不能据此断言已经编译、已经通过全部网络回归。

新增的 `ISimulationCallbacks.OnFullStateApplied` 虽有默认 no-op，签名仍使用 Klotho `ECS.Frame`；当前 Friflo callbacks 不实现它。`FullStateResync` 中的回调和 rebake driver 查找也受 `EcsSimulation` 条件控制。[S10]

新版 `GetLocalLayoutFingerprint()` 直接返回 Klotho `ComponentStorageRegistry.LayoutFingerprint`；这不是 Friflo 的 schema。`GetFingerprintBreakdown()` 对非 Klotho ECS 返回 `(KlothoLayout,0,0,0)`，`ComputeLocalStaticFingerprint()` 对非 Klotho ECS 直接返回 0。不能把“升级后消息里有 fingerprint”当成 Friflo 组件编号、Script 布局或静态资产已经受保护。[S11]

### 5.2 Wire / replay / generator 必须成套

- **`PlayerReadyMessage` 新增两个 long fingerprints**，旧 peer 的短 payload 与新 peer 不兼容。
- **`PlayerJoinCommand` 新增变长 `byte[] Entitlement`**，`GetSerializedSize`、codec 与 join command 的创建端都改变；pool 不清理用户字段，创建时必须连 `null` 也显式赋值。
- 当前项目的 Klotho replay metadata 为 **v1**，最新源码实际为 **v6**，loader 使用严格 `!=` 拒绝其他版本。不要只引用 v0.11 changelog 的中间版本 v5。
- **Klotho replay v6 与本地 Friflo FullState v5 是两个独立版本号**；其中一个升级不代表另一个兼容。
- `IReplaySystem.SetInitialStateSnapshot` 从两参变成 `(snapshot, hash, tick)`；custom recorder/caller 需改。
- Klotho cleanup 增加了 registry 元数据和生成注册参数；要配套最新 generator，不要用新的源码配旧二进制、或反过来。

这些不是本次已执行的 upgrade，而是静态确认的迁移约束。[S12][S13]

### 5.3 保留本地修复与 Unity 包装

不得直接用上游目录覆盖：

1. 保留本地 `KlothoConnection.SetDeviceIdProvider`，上游尚无此 setter；它用于延迟握手时保留设备绑定。
2. 保留 `CompilerFeaturePolyfills.cs`、三个当前 asmdef 的程序集边界、目录 `~` 隔离、`.obj/.bin` 排除与 LiteNetLib 由 Unity 消费工程提供的选择。
3. Friflo 专属 layout、snapshot、FullState v5、显式 NativeAOT 元数据注册、fresh-store index rebuild、late-join 通知及权威配置构造都在自己的 bridge/ECS 修改里；它们不会由上游更新替代。
4. 当前 MEL adapter 被并入单一 Runtime；上游的独立插件分包不能原样套当前 `.csproj` globs。
5. generator 仍是 netstandard2.0 / Roslyn 4.3.0，理论上延续现有 Unity 编译器线，但新版源码/生成输出仍须真实重编译，不能只看 TargetFramework 就判定成功。[S14]

## 6. 已执行的隔离验证与限制

所有候选代码、编译输出和 UDP 探针均位于 `C:\Users\89256\AppData\Local\Temp\klotho-upstream-assessment-iednnjv2`，没有在真实项目里更换 Runtime、generator、Bridge 或 ECS 源码。

| 验证对象 | 条件 | 实际结果 |
|---|---|---|
| 当前版本基线 | 当前未提交修复一起复制到临时目录 | Release 108 passed / 0 failed / 0 skipped |
| Transport-only | 只替换 `LiteNetLibTransport.cs`，仍用 v0.7.1 其余 Runtime 和原 generator；强制 Rebuild | Release 108 passed / 0 failed / 0 skipped；Runtime 构建 0 warning / 0 error |
| 整套 Runtime 候选 | v0.14.1 全部 431 个 Runtime `.cs` + 匹配新 generator；保留当前 csproj、Unity 隐藏目录、本地 polyfill/MEL 和 `SetDeviceIdProvider` | Runtime、当前 Friflo Bridge、测试均编译成功，Release 108 passed / 0 failed / 0 skipped |
| 整套候选的 NativeAOT | 同一临时副本中发布 win-x64 exe 并实际执行，不以 `dotnet test` 替代 | `IsDynamicCodeSupported=False`，30 checks passed，exit 0；负 revision signature 仍为 `-9097852460034388641` |
| 真实本机 UDP 生命周期 | 原版与 transport-only DLL 分别运行独立探针：同一 transport 重复 Listen 同一临时端口、Disconnect 后 Listen、两个线程同时 Disconnect | 原版 `True / False / False`、并发检查出现 1 个异常；更新版 `True / True / True`、并发检查 0 个异常，exit 0 |

UDP 验证仅使用本机 `127.0.0.1`，没有连接外部服务。它不是长期并发 soak、互联网丢包或容量认证。新 transport 的公开声明与旧版一致；同时核对探针加载的 DLL 具有 `_stopLock`，避免把复制文件保留旧时间戳导致的增量旧产物当成候选结果。

实验日志：`sandbox-baseline.log`、`transport-force-build.log`、`transport-update-tests.log`、`transport-lifecycle-baseline.log`、`transport-lifecycle-updated.log`、`full-update-tests.log`、`upstream-native-publish.log`、`upstream-native-run.log`。整套候选 .NET 构建仍有既有 obsolete / trim / SourceLink 告警；NativeAOT 发布也有 Fliox 依赖裁剪/AOT 告警，不能称零告警。

**尚未验证**：最新版 Unity 编译/Mono 探针在准备临时脚本时仍被执行权限拦截，已停止，没有换工具绕过；上一轮旧 Runtime 的 Unity Mono 29 项通过不能转用于新版。完整 Editor 导入、IL2CPP Player、整套更新的真实多进程 UDP 长测、跨版本混连拒绝、replay 迁移、物理 golden hash 和多房间容量亦未验证。固定 seed/诊断 hunk 的建议来自精确源码对照，未额外执行 seed 专项探针。

在评估结束前，对评估开始时所有未提交文件逐一比较 SHA256，确认**原有修复无文件内容变动**。

## 7. 推荐落地顺序与验证门槛

1. **先做 Transport 一个文件**，留在当前 v0.7.1 协议；加真实 UDP 生命周期测试，再跑现有 108 条、Unity compile、NativeAOT smoke。
2. **再做 seed / 日志小 hunk**，验证 P2P 与 SD 对配置 seed 的兑现及复用语义。不要顺带抄入新的 ready protocol。
3. **若游戏正在用物理，单独升级物理修复批次**，重新建立跨运行时 golden hash；明确旧 replay/旧客户端不能按原轨迹混用。
4. **若确实需要 RTS 建造/大规模导航或更完整 replay，再开 v0.14.1 整包适配任务**：先冻结当前已验证修复，再在隔离副本更换 Runtime + generator，保留本地补丁，处理接口/协议，补 Friflo fingerprint / rebuild / replay callbacks。重新测回滚、重同步、late-join/reconnect/spectator、多房间和容量，不能沿用旧版本的性能认证。

本次已完成版本确认、源码 diff、API/协议分析、两种候选的 .NET 回归、整套候选的真实 NativeAOT，以及 transport-only 的本机 UDP 生命周期比较。**这足以确认更新在当前架构上有可落地价值，不等于已在真实项目升级，也不等于新版 Unity/IL2CPP 或所有新增功能均已接好。**

## 主要一手来源

- [S1 — v0.7.1 源码固定点](https://github.com/xpTURN/Klotho/tree/eea42a3526873f7039e762eda01c1ce680bae6b6)；[main 最新提交（仅 release.yml）](https://github.com/xpTURN/Klotho/commit/9ebadca026147b0e58a2572c81462cbd34fd7493)。
- [S2 — v0.14.1 package.json](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/package.json)；[完整 v0.7.1…v0.14.1 对比](https://github.com/xpTURN/Klotho/compare/eea42a3526873f7039e762eda01c1ce680bae6b6...e29c4892c88db097f3ac0d135874c82e9b6726d6)。
- [S3 — Changelog：0.14.1 Unity 兼容与版本历史](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/CHANGELOG.md#0141---2026-09-17)。
- [S4 — LiteNetLibTransport 最新实现](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/LiteNetLib/LiteNetLibTransport.cs#L80-L207)；[并发关闭修复](https://github.com/xpTURN/Klotho/commit/54e1d05)；[重复 Listen 修复](https://github.com/xpTURN/Klotho/commit/73c8e79)；[其余生命周期修复](https://github.com/xpTURN/Klotho/commit/728d461)。
- [S5 — SD StartGame seed](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Network/Services/ServerNetworkService.cs#L888-L899)；[P2P authored seed](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Network/Services/KlothoNetworkService.cs#L995-L1024)；[SessionConfig Clone](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Engine/SessionConfig.cs)。
- [S6 — ChainBreak 限流](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FrameVerification.cs#L99-L173)；[SD 日志级别 gate](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ServerDrivenClient.cs#L322-L344)。
- [S7 — Narrowphase 法线](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Deterministic/Physics/NarrowphaseDispatch.cs)；[contact 有界复制](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Deterministic/Physics/FPPhysicsWorld.cs#L846-L914)。
- [S8 — 导航更新、格式与限制](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/CHANGELOG.md#0140---2026-09-09)；[运行时 rebake 文档](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/Docs/Navigation.Rebake.md)。
- [S9 — Klotho ECS Cleanup 实现](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/ECS/System/SystemRunner.cs)；[Filter guard](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/ECS/System/FilterMutationGuard.cs)。
- [S10 — 未变的 ISimulation](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/ISimulation.cs)；[Frame 类型的新增回调](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/ISimulationCallbacks.cs#L40-L59)。
- [S11 — Ready 指纹的真实数据源](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.ReadyFingerprint.cs#L36-L55)；[FullState/环境指纹的 EcsSimulation gate](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Engine/KlothoEngine.FullStateResync.cs#L592-L676)。
- [S12 — PlayerReady wire fields](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Network/Messages/PlayerReadyMessage.cs)；[PlayerJoin entitlement codec](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Core/Command/PlayerJoinCommand.cs)。
- [S13 — Replay v6 及严格版本检查](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Replay/Impl/ReplayData.cs#L15-L25)；[IReplaySystem 新接口](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/com.xpturn.klotho/Runtime/Replay/IReplaySystem.cs)。
- [S14 — Generator 项目依赖](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/Tools/KlothoGenerator/KlothoGenerator.csproj)；[cleanup 注册输出](https://github.com/xpTURN/Klotho/blob/e29c4892c88db097f3ac0d135874c82e9b6726d6/Tools/KlothoGenerator/src/Emitters/ComponentEmitter.cs#L187-L220)。
