using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.World;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    // The handshake, the join snapshot a new player is sent, and who is in the session.
    public static partial class CoopSession
    {
        private static void OnHandshake(INetPeer peer, HandshakeMessage message)
        {
            // Every message type is registered on both sides, so who may say what is checked in the handler.
            if (NetworkAccess.Role != NetRole.Host) return;

            // One handshake per peer, or a repeat would hand out a second id.
            if (PeerToPlayer.ContainsKey(peer.Id)) return;

            if (message.ProtocolVersion != ProtocolVersion)
            {
                Refuse(peer, HandshakeRefusal.ProtocolMismatch,
                    $"coop protocol {message.ProtocolVersion}, this host speaks {ProtocolVersion}");
                return;
            }

            if (!TryAssignPlayerId(out PlayerId assigned))
            {
                Refuse(peer, HandshakeRefusal.SessionFull, $"session already holds {CoopProtocol.MaxClients} clients");
                return;
            }

            string name = Sanitize(message.PlayerName, assigned);
            PeerToPlayer[peer.Id] = assigned;
            PlayerNames[assigned] = name;

            // The host stops being alone, which is what SinglePlayerOnly work is gated on.
            CoopProvider.NotifyChanged();

            // A zero hash means the other side computed none.
            bool modSetMatches = message.ModSetHash == 0u || message.ModSetHash == ModSet.Hash();
            if (!modSetMatches)
            {
                MelonLogger.Warning($"[Coop] {name} is running a different set of mods. This is allowed, but anything that changes " +
                    "items, stats or the world will only apply on one side.");
                ModSet.Report();
            }

            HandshakeResultMessage result = new()
            {
                Accepted = true,
                HostProtocolVersion = ProtocolVersion,
                AssignedPlayerId = assigned,
                Refusal = HandshakeRefusal.None,
                ModSetMatches = modSetMatches,

                // The latched values, not a fresh read, so a late joiner gets the same rules as everyone else.
                Rules = new SessionRulesMessage
                {
                    FriendlyFire = FriendlyFire,
                    RecoveryHealthPercent = RecoveryHealthPercent,
                    EnemyHealthPercent = EnemyHealthPercent,
                    LootMode = LootMode,
                    GameMode = HostSettings.LocalGameMode(),
                },
            };

            NetworkAccess.Send(peer, result);

            SendRoster(peer, exclude: assigned);
            HostSettings.SendAll(peer);

            PlayerJoinedMessage joined = new() { Id = assigned, Name = name };
            SendToClientsExcept(joined, peer.Id);

            // Nothing mentions an earlier death again, and the game reads a missing save record as alive.
            // The save's own corpses ride along for deaths from before any session.
            HashSet<string> graveyard = new(RemoteEnemyDeath.Dead, StringComparer.Ordinal);
            EnemyRegistry.CollectSaveDead(graveyard);
            HostWorldState.SendGraveyard(peer, graveyard);

            // Quest state set before the join is never mentioned again on its own.
            GVarSnapshot.SendTo(peer);

            if (WeatherSync.TryBuildCurrent(out WeatherMessage weather))
            {
                NetworkAccess.Send(peer, weather);
            }

            // The quest graphs that write the journal never cross, so it must be sent whole.
            JournalShare.SendTo(peer);

            if (DaytimeSync.TryBuildCurrent(out DaytimeMessage daytime))
            {
                NetworkAccess.Send(peer, daytime);
            }

            // A dialogue handed the maps to one player only.
            MapShare.SendTo(peer);
            ResourceWork.SendTo(peer);
            LootShare.SendTo(peer);
            WorldObjects.SendTo(peer);
            Arena.SendTo(peer);
            CacheShare.SendTo(peer);

            MelonLogger.Msg($"[Coop] {name} joined as {assigned} ({PlayerNames.Count} in session).");
        }

        private static void OnHandshakeResult(INetPeer peer, HandshakeResultMessage message)
        {
            // Only a client is owed a result. Otherwise any peer could send a refusal and tear down the host's session.
            if (NetworkAccess.Role != NetRole.Client) return;

            // Only while a join is in flight, so a second result cannot unseat a live session.
            if (State != CoopSessionState.Connecting) return;

            if (!message.Accepted)
            {
                Fail(Describe(message));
                return;
            }

            State = CoopSessionState.Joined;
            _connectingSeconds = 0f;

            LocalPlayerId = message.AssignedPlayerId;
            PlayerNames[LocalPlayerId] = Sanitize(PlayerIdentity.LocalName(), LocalPlayerId);

            // The host's rules, never the local settings.
            AdoptRules(message.Rules.FriendlyFire, message.Rules.RecoveryHealthPercent, message.Rules.EnemyHealthPercent, message.Rules.LootMode);
            HostSettings.HostGameMode = message.Rules.GameMode;

            MelonLogger.Msg($"[Coop] The host accepted the handshake; this client is {LocalPlayerId}.");

            if (!message.ModSetMatches)
            {
                MelonLogger.Warning("[Coop] The host is running a different set of mods. This is allowed, but anything that changes " +
                    "items, stats or the world will only apply on one side. Compare the list below with theirs.");
                ModSet.Report();
            }
        }

        private static void OnRosterSnapshot(INetPeer peer, RosterSnapshotMessage message)
        {
            if (!IsFromHost()) return;

            foreach (RosterEntry entry in message.Players ?? [])
            {
                if (entry.Id == LocalPlayerId) continue;

                PlayerNames[entry.Id] = Sanitize(entry.Name, entry.Id);
            }

            MelonLogger.Msg($"[Coop] Roster received: {PlayerNames.Count} in session.");
        }

        private static void OnPlayerJoined(INetPeer peer, PlayerJoinedMessage message)
        {
            if (!IsFromHost()) return;
            if (message.Id == LocalPlayerId) return;

            PlayerNames[message.Id] = Sanitize(message.Name, message.Id);

            CoopProvider.NotifyChanged();

            MelonLogger.Msg($"[Coop] {PlayerNames[message.Id]} joined as {message.Id} ({PlayerNames.Count} in session).");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            if (!IsFromHost()) return;

            ForgetPlayer(message.Id);
        }

        private static void ForgetPlayer(PlayerId player)
        {
            PlayerNames.Remove(player);

            // The host may be alone again, and session-stopped mods have no other way to hear it.
            CoopProvider.NotifyChanged();

            StateByPlayer.Remove(player);
            RoundTripByPlayer.Remove(player);

            foreach (ICoopSystem system in _systems) system.PlayerLeft(player);

            MelonLogger.Msg($"[Coop] {player} left ({PlayerNames.Count} in session).");
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            // A NaN lands in the transform and poisons every later interpolation, and a host must not relay it.
            // Counted, not logged, so a peer cannot fill the disk.
            if (!message.IsPlausible())
            {
                ImplausibleStates++;
                return;
            }

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who it is.
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                PlayerStateMessage stamped = message with { PlayerId = sender };
                StateByPlayer[sender] = stamped;

                // One broadcast instead of a copy per client: receivers skip their own state, and the relay fans it out.
                NetworkAccess.SendToAll(stamped, Delivery.Sequenced);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            StateByPlayer[message.PlayerId] = message;
        }

        // Lowest free id, so players coming and going never exhaust the ids.
        private static bool TryAssignPlayerId(out PlayerId assigned)
        {
            for (byte candidate = 1; candidate <= CoopProtocol.MaxClients; candidate++)
            {
                PlayerId id = new(candidate);
                if (PlayerNames.ContainsKey(id)) continue;

                assigned = id;
                return true;
            }

            assigned = PlayerId.None;
            return false;
        }

        private static void SendRoster(INetPeer peer, PlayerId exclude)
        {
            RosterEntry[] entries = PlayerNames.Where(p => p.Key != exclude).Select(p => new RosterEntry(p.Key, p.Value)).ToArray();

            foreach (RosterEntry[] chunk in entries.Chunk(RosterSnapshotMessage.MaxPlayers))
            {
                NetworkAccess.Send(peer, new RosterSnapshotMessage { Players = chunk });
            }
        }

        // A name is free text that lands in the log, and a newline would let the sender forge log lines.
        private static string Sanitize(string? name, PlayerId id)
        {
            if (string.IsNullOrWhiteSpace(name)) return id.ToString();

            char[] cleaned = name.ToCharArray();
            for (int i = 0; i < cleaned.Length; i++)
            {
                if (char.IsControl(cleaned[i]))
                {
                    cleaned[i] = ' ';
                }
            }

            string result = new string(cleaned).Trim();
            return result.Length == 0 ? id.ToString() : result;
        }

        private static void Refuse(INetPeer peer, HandshakeRefusal refusal, string why)
        {
            HandshakeResultMessage result = new()
            {
                Accepted = false,
                HostProtocolVersion = ProtocolVersion,
                AssignedPlayerId = PlayerId.None,
                Refusal = refusal,
            };

            NetworkAccess.Send(peer, result);
            MelonLogger.Warning($"[Coop] Refused peer {peer.Id}: {why}.");
            peer.Disconnect();
        }

        private static string Describe(HandshakeResultMessage message)
        {
            return message.Refusal switch
            {
                HandshakeRefusal.SessionFull => "That session is full.",
                HandshakeRefusal.ProtocolMismatch =>
                    $"The host runs coop protocol {message.HostProtocolVersion}, this mod runs {ProtocolVersion}. Same version on both sides is required.",
                _ => "The host refused the connection without saying why.",
            };
        }
    }
}
