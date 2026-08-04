using System;
using System.Collections.Generic;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// An in-process network of <see cref="LoopbackTransport"/> endpoints, so a server and its clients
/// can run the real Klotho protocol inside one test.
/// </summary>
/// <remarks>
/// Delivery is deterministic on purpose. Nothing is dispatched at <c>Send</c> time: payloads queue
/// on the receiver and are handed over only inside its <see cref="LoopbackTransport.PollEvents"/>,
/// on the thread that calls it. A real socket would deliver whenever the OS felt like it, which is
/// fine for a game and useless for a test that has to attribute a hash mismatch to the simulation
/// rather than to scheduling.
/// <para>
/// By default this models an ideal link: no loss, no reordering, no MTU limit. Set
/// <see cref="LoopbackTransport.Conditions"/> to degrade it — see <see cref="LinkConditions"/>,
/// which drives loss and reordering from a seeded generator so a failure stays reproducible.
/// </para>
/// </remarks>
public sealed class LoopbackHub
{
    /// <summary>Peer id of the server, and of a P2P host. Mirrors LiteNetLib, where a listener is peer 0.</summary>
    internal const int ServerPeerId = 0;

    /// <summary>Endpoints by peer id. Index 0 is the server once created.</summary>
    private readonly List<LoopbackTransport> endpoints = new();

    /// <summary>
    /// Create the endpoint every client connects to. One per hub.
    /// </summary>
    /// <remarks>
    /// Clients address it as peer 0 without being told: Klotho's client services send to a
    /// hardcoded peer 0 (see <c>ServerDrivenClientService.SendClientInput</c>).
    /// </remarks>
    public LoopbackTransport CreateServer()
    {
        if (endpoints.Count != 0) {
            throw new InvalidOperationException("this hub already has a server - create it first, then the clients");
        }
        var server = new LoopbackTransport(this, ServerPeerId);
        endpoints.Add(server);
        return server;
    }

    /// <summary>Create a client endpoint with a fresh peer id. Not connected until <see cref="Connect"/>.</summary>
    public LoopbackTransport CreateClient()
    {
        if (endpoints.Count == 0) {
            throw new InvalidOperationException("create the server before any client - clients connect to it");
        }
        var client = new LoopbackTransport(this, endpoints.Count);
        endpoints.Add(client);
        return client;
    }

    /// <summary>
    /// Bring <paramref name="client"/> up and raise the connection events in the order a real
    /// transport would: the client learns it is connected, then each side learns about the other.
    /// </summary>
    /// <remarks>
    /// Call this after both sides have subscribed their handlers. Doing it inside
    /// <see cref="CreateClient"/> would fire the events before anyone is listening.
    /// </remarks>
    public void Connect(LoopbackTransport client)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (!ReferenceEquals(client.Hub, this)) {
            throw new ArgumentException("that endpoint belongs to a different hub", nameof(client));
        }
        if (client.LocalPeerId == ServerPeerId) {
            throw new ArgumentException("the server does not connect to itself", nameof(client));
        }
        var server = endpoints[ServerPeerId];
        server.Link(client.LocalPeerId);
        client.Link(ServerPeerId);

        // The client's own "I am connected" notification precedes the per-peer ones, matching the
        // LiteNetLib client path where OnConnected is raised before OnPeerConnected.
        client.RaiseConnected();
        server.RaisePeerConnected(client.LocalPeerId);
        client.RaisePeerConnected(ServerPeerId);
    }

    /// <summary>The endpoint with the passed id, or null if there is none.</summary>
    internal LoopbackTransport Find(int peerId) =>
        (uint)peerId < (uint)endpoints.Count ? endpoints[peerId] : null;
}

/// <summary>
/// How badly a link misbehaves: packet loss, reordering, duplication, and a size limit.
/// </summary>
/// <remarks>
/// <b>Only unreliable traffic is degraded.</b> A real transport retransmits and re-sequences
/// <see cref="DeliveryMethod.Reliable"/> and <see cref="DeliveryMethod.ReliableOrdered"/>, so
/// dropping those here would model a broken transport rather than a bad network, and would break
/// guarantees Klotho is entitled to rely on — the roster/join ordering that late join needs, for
/// one. What the protocol actually has to survive is losing the traffic that is <i>sent</i>
/// unreliably: client inputs, ping/pong, sync hashes, input acks. Those are the ones dropped.
/// <para>
/// <b>Randomness is seeded.</b> A run that fails must fail the same way when re-run, so the
/// sequence comes from an explicit seed rather than a shared clock-seeded generator. Each endpoint
/// draws from its own stream, so changing one link's conditions does not shift another's draws.
/// </para>
/// </remarks>
public sealed class LinkConditions
{
    /// <summary>
    /// LiteNetLib's initial MTU less its UDP header allowance
    /// (<c>NetConstants.PossibleMtu[0]</c> = 1232 - 68), the smallest a real peer starts at before
    /// MTU discovery raises it. Payloads above it are what force fragmentation.
    /// </summary>
    public const int LiteNetLibInitialMtu = 1164;

    /// <summary>Fraction of unreliable payloads dropped, 0 to 1.</summary>
    public double LossRate { get; set; }

    /// <summary>
    /// Fraction of unreliable payloads held back a few polls, arriving after ones sent later.
    /// </summary>
    /// <remarks>
    /// Reordering only, not extra loss: the payload is still delivered, just out of sequence. It is
    /// the case a receiver that assumes arrival order matches send order gets wrong.
    /// </remarks>
    public double ReorderRate { get; set; }

    /// <summary>How many extra polls a reordered payload waits.</summary>
    public int ReorderDelayPolls { get; set; } = 3;

    /// <summary>Fraction of unreliable payloads delivered twice.</summary>
    /// <remarks>
    /// A real UDP link duplicates packets, and an unreliable channel has no dedup to hide it. The
    /// receiver has to be idempotent; a command applied twice would double that player's move.
    /// </remarks>
    public double DuplicateRate { get; set; }

    /// <summary>
    /// Largest payload a single unreliable packet may carry, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// Models the asymmetry in <c>LiteNetPeer.SendInternal</c>: an oversized reliable payload is
    /// fragmented and reassembled, but an oversized <i>unreliable</i> one throws
    /// <c>TooBigPacketException</c> — it cannot be fragmented, because there is no retransmission to
    /// recover a lost fragment. Set this to <see cref="LiteNetLibInitialMtu"/> to find out whether
    /// anything sent unreliably would have thrown on a real socket. Reliable payloads are exempt,
    /// as they are on the real one.
    /// </remarks>
    public int MaxUnreliablePayload { get; set; }

    /// <summary>Seed for this link's random stream. Runs with the same seed behave identically.</summary>
    public int Seed { get; set; } = 12345;

    /// <summary>Unreliable payloads dropped so far.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Unreliable payloads held back out of order so far.</summary>
    public int ReorderedCount { get; private set; }

    /// <summary>Unreliable payloads duplicated so far.</summary>
    public int DuplicatedCount { get; private set; }

    /// <summary>Lazily created so that changing <see cref="Seed"/> before use takes effect.</summary>
    private Random random;

    private Random Random => random ??= new Random(Seed);

    /// <summary>What the link decided to do with one payload.</summary>
    internal enum Verdict { Deliver, Drop, Delay, Duplicate }

    /// <summary>
    /// Decide the fate of a payload. Reliable traffic is always delivered as sent.
    /// </summary>
    /// <remarks>
    /// The draws are taken in a fixed order regardless of the configured rates, so that a rate of
    /// zero still consumes its draw. Without that, enabling duplication would shift every later
    /// loss decision and a test tuned against one setting would behave differently under another.
    /// </remarks>
    internal Verdict Decide(DeliveryMethod deliveryMethod)
    {
        if (deliveryMethod != DeliveryMethod.Unreliable) {
            return Verdict.Deliver;
        }
        double lossDraw      = Random.NextDouble();
        double reorderDraw   = Random.NextDouble();
        double duplicateDraw = Random.NextDouble();

        if (lossDraw < LossRate) {
            DroppedCount++;
            return Verdict.Drop;
        }
        if (reorderDraw < ReorderRate) {
            ReorderedCount++;
            return Verdict.Delay;
        }
        if (duplicateDraw < DuplicateRate) {
            DuplicatedCount++;
            return Verdict.Duplicate;
        }
        return Verdict.Deliver;
    }
}

/// <summary>
/// One endpoint of a <see cref="LoopbackHub"/>: an <see cref="INetworkTransport"/> that delivers in
/// process.
/// </summary>
/// <remarks>
/// Ordering follows the contract on <see cref="INetworkTransport.Send(int, byte[], DeliveryMethod)"/>:
/// for a given delivery method, <c>Send</c> and <c>Broadcast</c> share <b>one</b> ordered stream per
/// peer. They are queued together here rather than into separate lists — late join depends on a
/// roster <c>Send</c> arriving before the join <c>Broadcast</c> that consumes it, and splitting the
/// queues would reorder exactly that pair. A receiver's single inbox also preserves each sender's
/// own order, since a FIFO preserves the relative order of any subsequence.
/// </remarks>
public sealed class LoopbackTransport : INetworkTransport
{
    private readonly LoopbackHub     hub;
    private readonly int             localPeerId;
    private readonly Queue<Datagram> inbox = new();

    /// <summary>Peers this endpoint currently has a link to, in ascending id order.</summary>
    private readonly List<int> peers = new();

    /// <summary>Counts calls to <see cref="PollEvents"/>; the clock that <see cref="LatencyPolls"/> is measured against.</summary>
    private long pollCount;

    /// <summary>
    /// How many of this endpoint's polls an <i>inbound</i> payload waits before it is delivered.
    /// 0 delivers on the next poll.
    /// </summary>
    /// <remarks>
    /// Latency is counted in polls rather than milliseconds on purpose: a wall-clock delay would
    /// make delivery depend on how fast the machine runs the test. What this buys is a peer whose
    /// input has not arrived when its tick executes, so the engine predicts, is later proven wrong,
    /// and rolls back — the path that a zero-latency link never reaches.
    /// <para>It delays what this endpoint <i>receives</i>, so delaying one client's view of the
    /// server is independent of delaying the server's view of that client.</para>
    /// </remarks>
    public int LatencyPolls { get; set; }

    /// <summary>The hub this endpoint belongs to.</summary>
    internal LoopbackHub Hub => hub;

    /// <summary>
    /// Loss / reordering / duplication applied to what this endpoint <i>receives</i>, or null for a
    /// perfect link.
    /// </summary>
    /// <remarks>
    /// On the receiving side, like <see cref="LatencyPolls"/>, so one direction can be degraded
    /// without touching the other — a client that cannot get its input to the server is a different
    /// scenario from one that cannot hear the server's replies.
    /// </remarks>
    public LinkConditions Conditions { get; set; }

    /// <summary>A queued payload. The bytes are copied on send, so the caller may reuse its buffer.</summary>
    private readonly struct Datagram
    {
        /// <summary>Sender's id. Raised as the peer id of <see cref="OnDataReceived"/>.</summary>
        internal readonly int    senderId;
        internal readonly byte[] data;
        internal readonly int    length;
        /// <summary>Poll count of the receiving endpoint at which this may be handed over.</summary>
        internal readonly long   deliverAtPoll;
        /// <summary>How it was sent. Decides whether it may overtake an earlier payload.</summary>
        internal readonly DeliveryMethod deliveryMethod;

        internal Datagram(int senderId, byte[] data, int length, long deliverAtPoll, DeliveryMethod deliveryMethod)
        {
            this.senderId       = senderId;
            this.data           = data;
            this.length         = length;
            this.deliverAtPoll  = deliverAtPoll;
            this.deliveryMethod = deliveryMethod;
        }
    }

    internal LoopbackTransport(LoopbackHub hub, int localPeerId)
    {
        this.hub         = hub;
        this.localPeerId = localPeerId;
    }

    /// <summary>
    /// Create a linked host/guest pair for a two-peer P2P session.
    /// </summary>
    /// <remarks>
    /// A hub with exactly one client. The host is peer 0 and the guest peer 1, as over UDP.
    /// </remarks>
    public static (LoopbackTransport host, LoopbackTransport guest) CreatePair()
    {
        var hub   = new LoopbackHub();
        var host  = hub.CreateServer();
        var guest = hub.CreateClient();
        return (host, guest);
    }

    /// <summary>
    /// Bring both ends of a <see cref="CreatePair"/> link up, raising the connection events.
    /// </summary>
    /// <remarks>Call this after both sides have subscribed their handlers.</remarks>
    public static void Connect(LoopbackTransport host, LoopbackTransport guest)
    {
        if (host == null)  throw new ArgumentNullException(nameof(host));
        if (guest == null) throw new ArgumentNullException(nameof(guest));
        if (!ReferenceEquals(host.hub, guest.hub)) {
            throw new ArgumentException("host and guest are not a linked pair - use CreatePair()");
        }
        host.hub.Connect(guest);
    }

    /// <inheritdoc />
    public bool IsConnected => peers.Count > 0;

    /// <inheritdoc />
    public int LocalPeerId => localPeerId;

    /// <inheritdoc />
    public string RemoteAddress { get; private set; } = "loopback";

    /// <inheritdoc />
    public int RemotePort { get; private set; }

    /// <inheritdoc />
    /// <remarks>Nothing here disconnects with a payload, so this is always -1.</remarks>
    public int LastDisconnectPayload => -1;

    /// <inheritdoc />
    public event Action<int, byte[], int> OnDataReceived;
    /// <inheritdoc />
    public event Action<int>              OnPeerConnected;
    /// <inheritdoc />
    public event Action<int>              OnPeerDisconnected;
    /// <inheritdoc />
    public event Action                   OnConnected;
    /// <inheritdoc />
    public event Action<DisconnectReason> OnDisconnected;

    /// <summary>Record a link to <paramref name="peerId"/>, keeping <see cref="peers"/> ordered.</summary>
    internal void Link(int peerId)
    {
        if (peers.Contains(peerId)) {
            return;
        }
        peers.Add(peerId);
        peers.Sort();   // stable broadcast order, so delivery does not depend on connect order
    }

    internal void RaiseConnected()              => OnConnected?.Invoke();
    internal void RaisePeerConnected(int peerId) => OnPeerConnected?.Invoke(peerId);

    /// <inheritdoc />
    /// <remarks>The endpoints are already linked; the address and port are recorded for reporting only.</remarks>
    public bool Listen(string address, int port, int maxConnections)
    {
        RemoteAddress = address;
        RemotePort    = port;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Does not itself complete the connection — call <see cref="LoopbackHub.Connect"/> once both
    /// ends have their handlers attached, so no event fires into a half-wired session.
    /// </remarks>
    public bool Connect(string address, int port)
    {
        RemoteAddress = address;
        RemotePort    = port;
        return true;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        // Copied: Drop mutates `peers`.
        var current = peers.ToArray();
        foreach (int peerId in current) {
            Drop(peerId, localDisconnect: true);
        }
        inbox.Clear();
    }

    /// <inheritdoc />
    /// <remarks>Disconnecting an id that is not a peer is ignored, as it would be on a real transport.</remarks>
    public void DisconnectPeer(int peerId)
    {
        if (peers.Contains(peerId)) {
            Drop(peerId, localDisconnect: true);
        }
    }

    /// <inheritdoc />
    /// <remarks>The payload is dropped: nothing here reads <see cref="LastDisconnectPayload"/>.</remarks>
    public void DisconnectPeer(int peerId, byte[] data) => DisconnectPeer(peerId);

    /// <summary>
    /// Tear down the link to <paramref name="peerId"/> and notify both ends.
    /// </summary>
    /// <remarks>
    /// A client that loses the server also gets <see cref="OnDisconnected"/>: it has no other link,
    /// so losing that peer is losing the connection. The server keeps running with its remaining
    /// clients, which is why it only ever sees the per-peer event.
    /// </remarks>
    private void Drop(int peerId, bool localDisconnect)
    {
        if (!peers.Remove(peerId)) {
            return;
        }
        var other = hub.Find(peerId);
        other?.Drop(localPeerId, localDisconnect: false);

        OnPeerDisconnected?.Invoke(peerId);
        if (localPeerId != LoopbackHub.ServerPeerId && peerId == LoopbackHub.ServerPeerId) {
            OnDisconnected?.Invoke(localDisconnect
                ? DisconnectReason.LocalDisconnect
                : DisconnectReason.RemoteDisconnect);
        }
    }

    /// <inheritdoc />
    public IEnumerable<int> GetConnectedPeerIds() => peers.ToArray();

    /// <inheritdoc />
    public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod) =>
        Send(peerId, data, data?.Length ?? 0, deliveryMethod);

    /// <inheritdoc />
    /// <remarks>
    /// Addressing an unconnected peer is a bug in the caller — dropping it silently, as a real
    /// transport would for an unknown peer, would surface much later as an unexplained protocol
    /// stall.
    /// </remarks>
    public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
    {
        if (!peers.Contains(peerId)) {
            throw new ArgumentOutOfRangeException(
                nameof(peerId), peerId, $"not a connected peer of endpoint {localPeerId}");
        }
        Enqueue(peerId, data, length, deliveryMethod);
    }

    /// <inheritdoc />
    public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) =>
        Broadcast(data, data?.Length ?? 0, deliveryMethod);

    /// <inheritdoc />
    /// <remarks>
    /// Reaches the same per-peer queues as <c>Send</c> and so keeps its order relative to them, as
    /// the contract requires.
    /// </remarks>
    public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod)
    {
        for (int i = 0; i < peers.Count; i++) {
            Enqueue(peers[i], data, length, deliveryMethod);
        }
    }

    /// <summary>Queue a payload on <paramref name="peerId"/>'s inbox, subject to its link conditions.</summary>
    private void Enqueue(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
    {
        var target = hub.Find(peerId);
        if (target == null || data == null || length <= 0) {
            return;
        }
        var conditions = target.Conditions;

        // Size limit before anything else: a real peer throws at the send call, so it fails on the
        // sender regardless of what the link would have done with the packet.
        if (conditions != null && conditions.MaxUnreliablePayload > 0
            && deliveryMethod == DeliveryMethod.Unreliable
            && length > conditions.MaxUnreliablePayload) {
            throw new InvalidOperationException(
                $"unreliable payload of {length} bytes exceeds the {conditions.MaxUnreliablePayload}-byte limit; " +
                "LiteNetLib throws TooBigPacketException here - an unreliable packet cannot be fragmented, " +
                "since there is no retransmission to recover a lost fragment");
        }

        var verdict = conditions?.Decide(deliveryMethod) ?? LinkConditions.Verdict.Deliver;
        if (verdict == LinkConditions.Verdict.Drop) {
            return;
        }
        // Copy: callers send from pooled buffers and reuse them before the receiver polls.
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);

        // The deadline is on the receiver's clock, since it is the one that will release this.
        long deliverAt = target.pollCount + target.LatencyPolls;
        if (verdict == LinkConditions.Verdict.Delay) {
            deliverAt += conditions.ReorderDelayPolls;
        }
        target.inbox.Enqueue(new Datagram(localPeerId, copy, length, deliverAt, deliveryMethod));

        if (verdict == LinkConditions.Verdict.Duplicate) {
            // The same bytes again, on the same schedule: a duplicate is a second copy of one
            // packet, not a resend of a lost one.
            target.inbox.Enqueue(new Datagram(localPeerId, copy, length, deliverAt, deliveryMethod));
        }
    }

    /// <summary>
    /// Senders whose ordered stream is blocked for the rest of the current <see cref="PollEvents"/>,
    /// because an earlier <see cref="DeliveryMethod.ReliableOrdered"/> payload from them is not due.
    /// </summary>
    /// <remarks>Reused across polls rather than allocated per call; cleared at the top of each.</remarks>
    private readonly HashSet<int> blockedOrderedSenders = new();

    /// <inheritdoc />
    /// <remarks>
    /// Drains what was queued when this call started. A handler that sends while being dispatched
    /// (a reply to a request) queues onto the <i>other</i> inbox anyway, but bounding the loop up
    /// front also keeps a self-directed send from spinning here forever.
    /// <para>
    /// On a perfect link every payload is due in send order and this is a straight drain. Once
    /// <see cref="Conditions"/> holds one back, the payloads behind it must still go — that is what
    /// reordering <i>is</i>, and a real link carries unreliable traffic on no ordered channel at
    /// all. But the contract on <see cref="INetworkTransport.Send(int, byte[], DeliveryMethod)"/>
    /// still has to hold: <see cref="DeliveryMethod.ReliableOrdered"/> is one stream per peer, and
    /// late join depends on it (a roster <c>Send</c> must precede the join <c>Broadcast</c> that
    /// consumes it). So a sender whose ordered payload is not yet due has the rest of its ordered
    /// stream held with it, while everything else keeps flowing.
    /// </para>
    /// </remarks>
    public void PollEvents()
    {
        pollCount++;
        blockedOrderedSenders.Clear();
        int pending = inbox.Count;
        for (int i = 0; i < pending && inbox.Count > 0; i++) {
            var datagram = inbox.Dequeue();
            bool ordered  = datagram.deliveryMethod == DeliveryMethod.ReliableOrdered;
            bool withheld = datagram.deliverAtPoll > pollCount
                            || (ordered && blockedOrderedSenders.Contains(datagram.senderId));
            if (withheld) {
                if (ordered) {
                    // Everything ordered behind this one, from this sender, waits with it.
                    blockedOrderedSenders.Add(datagram.senderId);
                }
                // Re-queue behind the rest of this batch: that is what lets a payload sent later
                // arrive first.
                inbox.Enqueue(datagram);
                continue;
            }
            // The sender's id, as seen from here: the host looks a peer's handshake state up by it
            // (KlothoNetworkService.HandlePeerConnected records the peer in _pendingPeers under the
            // id it was given, and HandleDataReceived requires the first message to arrive under
            // the same one, else the join is silently dropped).
            OnDataReceived?.Invoke(datagram.senderId, datagram.data, datagram.length);
        }
    }

    /// <inheritdoc />
    /// <remarks>Sends are queued on the receiver as they are made, so there is nothing to flush.</remarks>
    public void FlushSendQueue() { }
}
