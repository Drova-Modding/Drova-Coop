using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    public static partial class CoopSession
    {
        /// <summary>Every handler is registered here. The host hears only players who passed the handshake.</summary>
        /// <param name="id">The message's wire id, see <see cref="MessageIds"/>.</param>
        /// <param name="handler">Runs on the main thread with the sending peer.</param>
        /// <param name="worldOnly">The message changes the world, so it runs only while the players share one.</param>
        internal static void Register<T>(ushort id, Action<INetPeer, T> handler, bool worldOnly = false) where T : struct, INetMessage
        {
            NetworkAccess.RegisterWithId<T>(id, (peer, message) =>
            {
                // A bare connection could otherwise write quest state, join a checkpoint, or get past the player cap.
                if (NetworkAccess.Role == NetRole.Host && id != MessageIds.Handshake && !PeerToPlayer.ContainsKey(peer.Id)) return;

                if (worldOnly && !SharesWorld) return;

                handler(peer, message);
            });
            Unregisters.Add(NetworkAccess.Unregister<T>);
        }

        /// <summary>The host queues a client's message and relays it to everyone but the sender. A client queues only the host's.</summary>
        internal static void RelayAndQueue<T>(INetPeer peer, T message, Queue<T> queue, int cap, string what)
            where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting)
            {
                Enqueue(queue, message, cap, what);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            QueueFromHost(message, queue, cap, what);
        }

        /// <summary>A message only the host may send.</summary>
        internal static void QueueFromHost<T>(T message, Queue<T> queue, int cap, string what) where T : struct
        {
            if (!IsFromHost()) return;

            Enqueue(queue, message, cap, what);
        }

        /// <summary>Queues something nothing will repeat. Full, it refuses the newest.</summary>
        /// <returns>Whether the message was queued.</returns>
        internal static bool Enqueue<T>(Queue<T> queue, in T message, int cap, string? what = null) where T : struct
        {
            if (queue.Count >= cap)
            {
                // Once per filling, since a peer can keep a queue full with every message it sends.
                if (what != null && FullQueues.Add(queue)) MelonLogger.Warning($"[Coop] more than {cap} {what} are waiting; newer ones are refused until it drains.");

                return false;
            }

            if (queue.Count < cap / 2) FullQueues.Remove(queue);

            queue.Enqueue(message);
            return true;
        }

        /// <summary>Queues something the next message supersedes. Full, it drops the oldest.</summary>
        internal static void EnqueueLatest<T>(Queue<T> queue, in T message, int cap) where T : struct
        {
            if (queue.Count >= cap)
            {
                queue.Dequeue();
            }

            queue.Enqueue(message);
        }

        internal static void SendIfLive<T>(in T message) where T : struct, INetMessage
        {
            if (IsLive) NetworkAccess.SendToAll(message);
        }

        internal static void SendIfHost<T>(in T message) where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting) NetworkAccess.SendToAll(message);
        }

        /// <summary>Host only: who is behind a connection. The sender of a message is the connection, never what it says.</summary>
        internal static bool TryGetSender(INetPeer peer, out PlayerId player) => PeerToPlayer.TryGetValue(peer.Id, out player);

        /// <summary>Whether a message comes from the host, the only one a joined client hears.</summary>
        internal static bool IsFromHost()
        {
            return NetworkAccess.Role == NetRole.Client && State == CoopSessionState.Joined;
        }

        /// <summary>Host only. The peer-to-player table exists only on the host.</summary>
        internal static bool TrySendToPlayer<T>(T message, PlayerId player, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            int peerId = -1;
            foreach (KeyValuePair<int, PlayerId> mapping in PeerToPlayer)
            {
                if (mapping.Value != player) continue;

                peerId = mapping.Key;
                break;
            }

            if (peerId < 0) return false;

            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer candidate in PeerBuffer)
            {
                if (candidate.Id != peerId) continue;

                NetworkAccess.Send(candidate, message, delivery);
                return true;
            }

            return false;
        }

        /// <summary>Sends to every client but one. The API's broadcast has no exclusion list, so the host unicasts.</summary>
        internal static void SendToClientsExcept<T>(T message, int exceptPeerId, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer other in PeerBuffer)
            {
                if (other.Id == exceptPeerId) continue;

                NetworkAccess.Send(other, message, delivery);
            }
        }
    }
}
