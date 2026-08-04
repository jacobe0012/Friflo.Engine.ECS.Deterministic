using System;
using System.Collections.Generic;

namespace xpTURN.Klotho.Network
{
    /// <summary>
    /// Network transport layer abstraction interface.
    /// </summary>
    public interface INetworkTransport
    {
        /// <summary>
        /// Connection state
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Local peer ID
        /// </summary>
        int LocalPeerId { get; }

        /// <summary>
        /// Start listening as server/host.
        /// Determines IPv6 vs IPv4 usage based on the host IP.
        /// Returns true on socket bind/start success, false on immediate failure.
        /// </summary>
        bool Listen(string address, int port, int maxConnections);

        /// <summary>
        /// Connect to a server as a client/guest.
        /// Returns true on socket start success (actual connection establishment is reported via OnConnected/OnDisconnected),
        /// false on immediate failure.
        /// </summary>
        bool Connect(string address, int port);

        /// <summary>
        /// Disconnect
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Disconnect a specific peer
        /// </summary>
        void DisconnectPeer(int peerId);

        /// <summary>
        /// Disconnect a specific peer, attaching a small payload (e.g. a reject reason byte) to the
        /// disconnect packet. The remote receives it via <see cref="LastDisconnectPayload"/> alongside
        /// its OnDisconnected (RemoteDisconnect) notification.
        /// </summary>
        void DisconnectPeer(int peerId, byte[] data);

        /// <summary>
        /// Enumerate transport-level connected peer IDs. Used by zombie cleanup to find
        /// peers that exist at the transport layer but are not tracked in service-level
        /// state. Implementations may return an empty sequence if not supported.
        /// </summary>
        IEnumerable<int> GetConnectedPeerIds();

        /// <summary>
        /// Send data to a specific peer.
        /// Implementation requirement: for the same <see cref="DeliveryMethod"/>, Send and Broadcast
        /// MUST share a single per-peer ordered stream — a Send followed by a Broadcast (or vice versa)
        /// with <see cref="DeliveryMethod.ReliableOrdered"/> must be delivered to each peer in call
        /// order. Late-join relies on this: the roster notification (Send) must reach existing peers
        /// before the join command (Broadcast) that consumes the state it carries.
        /// </summary>
        void Send(int peerId, byte[] data, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Send data to a specific peer (length specified, for pooled buffers).
        /// Same cross-call ordering requirement as <see cref="Send(int, byte[], DeliveryMethod)"/>.
        /// </summary>
        void Send(int peerId, byte[] data, int length, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Broadcast data to all peers.
        /// Implementation requirement: must share the per-peer ordered stream with Send for the same
        /// <see cref="DeliveryMethod"/> (see <see cref="Send(int, byte[], DeliveryMethod)"/>) — do NOT
        /// route broadcasts through a separate channel or queue.
        /// </summary>
        void Broadcast(byte[] data, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Broadcast data to all peers (length specified, for pooled buffers).
        /// Same cross-call ordering requirement as <see cref="Broadcast(byte[], DeliveryMethod)"/>.
        /// </summary>
        void Broadcast(byte[] data, int length, DeliveryMethod deliveryMethod);

        /// <summary>
        /// Process received packets (called per frame)
        /// </summary>
        void PollEvents();

        /// <summary>
        /// Flush queued outbound packets without processing inbound messages
        /// </summary>
        void FlushSendQueue();

        /// <summary>
        /// Data received event
        /// </summary>
        event Action<int, byte[], int> OnDataReceived; // peerId, data, length

        /// <summary>
        /// Peer connected event
        /// </summary>
        event Action<int> OnPeerConnected;

        /// <summary>
        /// Peer disconnected event
        /// </summary>
        event Action<int> OnPeerDisconnected;

        /// <summary>
        /// Connection established event
        /// </summary>
        event Action OnConnected;

        /// <summary>
        /// Disconnected event. The argument carries the categorized disconnect reason.
        /// </summary>
        event Action<DisconnectReason> OnDisconnected;

        /// <summary>
        /// Payload byte attached by the remote to the most recent disconnect (see
        /// <see cref="DisconnectPeer(int, byte[])"/>), or -1 if the disconnect carried no payload.
        /// Valid only synchronously within an OnDisconnected handler.
        /// </summary>
        int LastDisconnectPayload { get; }

        /// <summary>
        /// Address of the last connected remote host. Retained after disconnection.
        /// </summary>
        string RemoteAddress { get; }

        /// <summary>
        /// Port of the last connected remote host. Retained after disconnection.
        /// </summary>
        int RemotePort { get; }
    }

    /// <summary>
    /// Data delivery method
    /// </summary>
    public enum DeliveryMethod
    {
        /// <summary>
        /// Unreliable transport (UDP)
        /// </summary>
        Unreliable,

        /// <summary>
        /// Reliable transport
        /// </summary>
        Reliable,

        /// <summary>
        /// Reliable and ordered transport. Ordering is per peer across ALL messages sent with this
        /// method — including across Send/Broadcast calls (single stream per peer, see
        /// <see cref="INetworkTransport.Send(int, byte[], DeliveryMethod)"/>).
        /// </summary>
        ReliableOrdered,

        /// <summary>
        /// Ordered only
        /// </summary>
        Sequenced
    }
}
