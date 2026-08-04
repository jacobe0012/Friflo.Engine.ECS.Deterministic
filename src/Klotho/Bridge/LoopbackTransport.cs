using System;
using System.Collections.Generic;
using xpTURN.Klotho.Network;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo;

/// <summary>
/// An <see cref="INetworkTransport"/> pair that delivers in process, so a host and a guest can run
/// the real Klotho protocol inside one test.
/// </summary>
/// <remarks>
/// Delivery is deterministic on purpose. Nothing is dispatched at <c>Send</c> time: payloads queue
/// on the receiver and are handed over only inside its <see cref="PollEvents"/>, on the thread that
/// calls it. A real socket would deliver whenever the OS felt like it, which is fine for a game and
/// useless for a test that has to attribute a hash mismatch to the simulation rather than to
/// scheduling.
/// <para>
/// Ordering follows the contract on <see cref="INetworkTransport.Send(int, byte[], DeliveryMethod)"/>:
/// for a given delivery method, <c>Send</c> and <c>Broadcast</c> share <b>one</b> ordered stream per
/// peer. They are queued together here rather than into separate lists — late join depends on a
/// roster <c>Send</c> arriving before the join <c>Broadcast</c> that consumes it, and splitting the
/// queues would reorder exactly that pair.
/// </para>
/// <para>
/// This models an ideal link: no loss, no reordering, no MTU limit. It exercises protocol and
/// simulation logic, not the recovery paths a lossy network would trigger.
/// </para>
/// </remarks>
public sealed class LoopbackTransport : INetworkTransport
{
    /// <summary>Peer id the host is known by. Mirrors the LiteNetLib transport, where a listening host is peer 0.</summary>
    private const int HostPeerId  = 0;
    /// <summary>Peer id the guest is known by.</summary>
    private const int GuestPeerId = 1;

    private readonly bool             isHost;
    private readonly Queue<Datagram>  inbox = new();

    /// <summary>The transport on the other end. Set by <see cref="Connect(LoopbackTransport, LoopbackTransport)"/>.</summary>
    private LoopbackTransport peer;

    private bool connected;

    /// <summary>Counts calls to <see cref="PollEvents"/>; the clock that <see cref="LatencyPolls"/> is measured against.</summary>
    private long pollCount;

    /// <summary>
    /// How many of this transport's polls a payload waits before it is delivered. 0 delivers on the
    /// next poll.
    /// </summary>
    /// <remarks>
    /// Latency is counted in polls rather than milliseconds on purpose: a wall-clock delay would make
    /// delivery depend on how fast the machine runs the test. What this buys is a peer whose input
    /// has not arrived when its tick executes, so the engine predicts, is later proven wrong, and
    /// rolls back — the path that a zero-latency link never reaches.
    /// </remarks>
    public int LatencyPolls { get; set; }

    /// <summary>A queued payload. The bytes are copied on send, so the caller may reuse its buffer.</summary>
    private readonly struct Datagram
    {
        internal readonly byte[] data;
        internal readonly int    length;
        /// <summary>Poll count of the receiving transport at which this may be handed over.</summary>
        internal readonly long   deliverAtPoll;

        internal Datagram(byte[] data, int length, long deliverAtPoll)
        {
            this.data          = data;
            this.length        = length;
            this.deliverAtPoll = deliverAtPoll;
        }
    }

    private LoopbackTransport(bool isHost) => this.isHost = isHost;

    /// <summary>
    /// Create a linked host/guest pair. Both report as connected immediately: there is no
    /// negotiation to model, and the Klotho handshake runs on top of this as it would over UDP.
    /// </summary>
    public static (LoopbackTransport host, LoopbackTransport guest) CreatePair()
    {
        var host  = new LoopbackTransport(isHost: true);
        var guest = new LoopbackTransport(isHost: false);
        host.peer  = guest;
        guest.peer = host;
        return (host, guest);
    }

    /// <summary>
    /// The id of the transport at the other end, as seen from here.
    /// </summary>
    /// <remarks>
    /// This is the sender id to stamp on events raised on <i>this</i> side (<c>OnDataReceived</c>,
    /// <c>OnPeerDisconnected</c>): they describe the peer, not the local end. It must match the id
    /// this side was given by <c>OnPeerConnected</c>, because the host looks the peer's handshake
    /// state up by it — <c>KlothoNetworkService.HandlePeerConnected</c> records the peer in
    /// <c>_pendingPeers</c> under that id and <c>HandleDataReceived</c> requires the first message
    /// to arrive under the same one, else the join is silently dropped.
    /// </remarks>
    private int PeerId => isHost ? GuestPeerId : HostPeerId;

    /// <inheritdoc />
    public bool IsConnected => connected;

    /// <inheritdoc />
    public int LocalPeerId => isHost ? HostPeerId : GuestPeerId;

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

    /// <summary>
    /// Bring both ends up and raise the connection events in the order a real transport would:
    /// the guest learns it is connected, then each side learns about the other's peer.
    /// </summary>
    /// <remarks>
    /// Call this after both sides have subscribed their handlers. Doing it inside
    /// <see cref="CreatePair"/> would fire the events before anyone is listening.
    /// </remarks>
    public static void Connect(LoopbackTransport host, LoopbackTransport guest)
    {
        if (host == null)  throw new ArgumentNullException(nameof(host));
        if (guest == null) throw new ArgumentNullException(nameof(guest));
        if (!ReferenceEquals(host.peer, guest) || !ReferenceEquals(guest.peer, host)) {
            throw new ArgumentException("host and guest are not a linked pair - use CreatePair()");
        }
        host.connected  = true;
        guest.connected = true;

        // The guest's own "I am connected" notification precedes the per-peer ones, matching the
        // LiteNetLib client path where OnConnected is raised before OnPeerConnected.
        guest.OnConnected?.Invoke();
        host.OnPeerConnected?.Invoke(GuestPeerId);
        guest.OnPeerConnected?.Invoke(HostPeerId);
    }

    /// <inheritdoc />
    /// <remarks>The pair is already linked; the address and port are recorded for reporting only.</remarks>
    public bool Listen(string address, int port, int maxConnections)
    {
        RemoteAddress = address;
        RemotePort    = port;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Does not itself complete the connection — call <see cref="Connect(LoopbackTransport, LoopbackTransport)"/>
    /// once both ends have their handlers attached, so no event fires into a half-wired session.
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
        if (!connected) {
            return;
        }
        connected = false;
        inbox.Clear();

        var other = peer;
        if (other != null && other.connected) {
            other.connected = false;
            // Raised on the peer, so it carries this end's id — from over there, we are the peer.
            other.OnPeerDisconnected?.Invoke(LocalPeerId);
            if (!other.isHost) {
                other.OnDisconnected?.Invoke(DisconnectReason.RemoteDisconnect);
            }
        }
        if (!isHost) {
            OnDisconnected?.Invoke(DisconnectReason.LocalDisconnect);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// With a single peer, dropping it is the same as dropping the link. Disconnecting an id that is
    /// not the peer is ignored, as it would be on a real transport with no such peer.
    /// </remarks>
    public void DisconnectPeer(int peerId)
    {
        if (peerId == PeerId) {
            Disconnect();
        }
    }

    /// <inheritdoc />
    /// <remarks>The payload is dropped: nothing here reads <see cref="LastDisconnectPayload"/>.</remarks>
    public void DisconnectPeer(int peerId, byte[] data) => DisconnectPeer(peerId);

    /// <inheritdoc />
    public IEnumerable<int> GetConnectedPeerIds()
    {
        if (connected) {
            yield return isHost ? GuestPeerId : HostPeerId;
        }
    }

    /// <inheritdoc />
    public void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod) =>
        Send(peerId, data, data?.Length ?? 0, deliveryMethod);

    /// <inheritdoc />
    /// <remarks>
    /// A pair has exactly one reachable peer. Addressing anything else is a bug in the caller —
    /// dropping it silently, as a real transport would for an unknown peer, would surface much later
    /// as an unexplained protocol stall.
    /// </remarks>
    public void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod)
    {
        if (peerId != PeerId) {
            throw new ArgumentOutOfRangeException(
                nameof(peerId), peerId, $"the only peer of this transport is {PeerId}");
        }
        Enqueue(data, length);
    }

    /// <inheritdoc />
    public void Broadcast(byte[] data, DeliveryMethod deliveryMethod) =>
        Enqueue(data, data?.Length ?? 0);

    /// <inheritdoc />
    public void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod) =>
        Enqueue(data, length);

    /// <summary>
    /// Queue a payload on the peer's inbox. There is only one peer, so <c>Send</c> and
    /// <c>Broadcast</c> reach the same queue and keep their relative order, as the contract requires.
    /// </summary>
    private void Enqueue(byte[] data, int length)
    {
        var other = peer;
        if (!connected || other == null || data == null || length <= 0) {
            return;
        }
        // Copy: callers send from pooled buffers and reuse them before the receiver polls.
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        // The deadline is on the receiver's clock, since it is the one that will release this.
        other.inbox.Enqueue(new Datagram(copy, length, other.pollCount + other.LatencyPolls));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drains what was queued when this call started. A handler that sends while being dispatched
    /// (a reply to a request) queues onto the <i>other</i> inbox anyway, but bounding the loop up
    /// front also keeps a self-directed send from spinning here forever.
    /// </remarks>
    public void PollEvents()
    {
        pollCount++;
        int pending = inbox.Count;
        for (int i = 0; i < pending && inbox.Count > 0; i++) {
            // Peek: the queue is in send order, so once the head is not due yet, neither is
            // anything behind it. Stopping here is also what preserves ordering — releasing a later
            // payload while an earlier one waits would reorder the stream.
            if (inbox.Peek().deliverAtPoll > pollCount) {
                break;
            }
            var datagram = inbox.Dequeue();
            OnDataReceived?.Invoke(PeerId, datagram.data, datagram.length);
        }
    }

    /// <inheritdoc />
    /// <remarks>Sends are queued on the peer as they are made, so there is nothing to flush.</remarks>
    public void FlushSendQueue() { }
}
