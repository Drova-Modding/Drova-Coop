using System.Reflection;
using Drova_Modding_API.Systems.Networking;
using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// What the wire contract has to hold to: fields come back in the order they went out, and a name from
    /// a stranger is bounded no matter what the sender claims.
    /// </summary>
    public class MessageTests
    {
        [Fact]
        public void Trade_AtItsLimitsFitsWhatARelayForwards()
        {
            // A relay forwards 1199 bytes and drops anything bigger - the first trade test lost the whole bag that way.
            LootStack[] side = Enumerable.Range(0, TradeMessage.MaxStacks)
                .Select(index => new LootStack { Id = new string('x', LootStack.MaxIdLength), Amount = LootStack.MaxAmount })
                .ToArray();

            using MemoryStream stream = new();
            new TradeMessage { Id = 1, Stage = TradeMessage.StageOffer, Give = side, Take = side }.Write(new BufferWriter(stream));

            // One more byte per string than this writer's length prefix, plus the id and the packet header.
            long onWire = stream.Length + (2 * TradeMessage.MaxStacks) + 16;
            Assert.True(onWire <= 1199, $"{onWire} bytes");
        }

        [Fact]
        public void DialogueLine_RoundTripsAndAtItsLimitsFitsWhatARelayForwards()
        {
            DialogueLineMessage sent = new()
            {
                Player = new PlayerId(2),
                SpeakerGuid = new string('g', 36),
                Text = new string('€', DialogueLineMessage.MaxTextLength + 50), // three UTF-8 bytes each
                Ambient = true,
                Voice = new string('v', DialogueLineMessage.MaxVoiceLength + 20),
                VoiceActor = "Cengiz",
            };

            DialogueLineMessage received = RoundTrip(sent);
            Assert.Equal(DialogueLineMessage.MaxVoiceLength, received.Voice.Length);
            Assert.Equal("Cengiz", received.VoiceActor);
            // A clip name in anything but ASCII is not budgeted for: no voice rather than a line too big to cross.
            Assert.Equal(string.Empty, RoundTrip(sent with { Voice = "Stimme_für_Cengiz" }).Voice);
            Assert.Equal(2, received.Player.Value);
            Assert.Equal(sent.SpeakerGuid, received.SpeakerGuid);
            Assert.Equal(DialogueLineMessage.MaxTextLength, received.Text.Length);
            Assert.True(received.Ambient);

            using MemoryStream stream = new();
            sent.Write(new BufferWriter(stream));
            long onWire = stream.Length + 2 + 16; // a byte more per string prefix than this writer, the id, the header
            Assert.True(onWire <= 1199, $"{onWire} bytes");
        }

        [Fact]
        public void Handshake_TruncatesOverlongNameOnWrite()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 1,
                PlayerName = new string('n', HandshakeMessage.MaxPlayerNameLength + 200),
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, received.PlayerName.Length);
        }

        /// <summary>
        /// The case that matters, because it is the one an attacker controls: a sender that ignores the cap
        /// and writes the field by hand. The receiver has to apply the bound itself rather than trusting
        /// that the other side already did.
        /// </summary>
        [Fact]
        public void Handshake_TruncatesOverlongNameOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put(CoopProtocol.Version);
            writer.Put(new string('n', 900));
            writer.Put(0u);

            stream.Position = 0;
            HandshakeMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, received.PlayerName.Length);
        }

        [Fact]
        public void Handshake_WritesNullNameAsEmpty()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 1,
                PlayerName = null!,
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(string.Empty, received.PlayerName);
        }

        /// <summary>
        /// The session rules the host hands down, each given the value the other does not have. They are
        /// adjacent booleans, so equal ones would survive a Read that swapped them - and the swap is not
        /// cosmetic here: it would turn "we run different mods" into "you may be killed by your friend".
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void HandshakeResult_RoundTripsSessionRulesIndependently(bool modSetMatches, bool friendlyFire)
        {
            HandshakeResultMessage received = RoundTrip(new HandshakeResultMessage
            {
                Accepted = true,
                ModSetMatches = modSetMatches,
                Rules = new SessionRulesMessage { FriendlyFire = friendlyFire },
            });

            Assert.Equal(modSetMatches, received.ModSetMatches);
            Assert.Equal(friendlyFire, received.Rules.FriendlyFire);
        }

        /// <summary>
        /// The default has to be the safe one. A peer whose message never set the field - an older build,
        /// a truncated read, a default-constructed message - must not have friendly fire switched on by
        /// omission, because that failure is silent until somebody is killed by a friend.
        /// </summary>
        [Fact]
        public void HandshakeResult_DefaultsFriendlyFireOff()
        {
            Assert.False(default(HandshakeResultMessage).Rules.FriendlyFire);
            Assert.False(RoundTrip(new HandshakeResultMessage { Accepted = true }).Rules.FriendlyFire);
        }

        /// <summary>
        /// The loot rule's default has to be the game as shipped. A peer that never set the field, or a
        /// build that sends a value this one does not know, must not empty anybody's chests.
        /// </summary>
        [Theory]
        [InlineData((byte)0, CoopProtocol.LootModeInstanced)]
        [InlineData((byte)1, CoopProtocol.LootModeShared)]
        [InlineData((byte)2, CoopProtocol.LootModeInstanced)]
        [InlineData((byte)255, CoopProtocol.LootModeInstanced)]
        public void LootMode_UnknownValuesReadAsInstanced(byte sent, byte expected)
        {
            Assert.Equal(expected, RoundTrip(new SessionRulesMessage { LootMode = sent }).LootMode);
            Assert.Equal(CoopProtocol.LootModeInstanced, default(SessionRulesMessage).LootMode);
        }

        /// <summary>
        /// A cache crosses as two arrays that have to stay the same length, because one is the item and
        /// the other is how many. A pair that disagrees names nothing anybody could act on.
        /// </summary>
        [Fact]
        public void CacheSpill_KeepsItsArraysInStep()
        {
            CacheSpillMessage spill = RoundTrip(new CacheSpillMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = CacheSpillMessage.KindOffer,
                Player = 1,
                Keys = ["b4542fdf4ba7c7342bb435cfffcd423b", "aa11"],
                Amounts = [3, 1],
            });

            Assert.Equal(CacheSpillMessage.KindOffer, spill.Kind);
            Assert.Equal(2, spill.Keys.Length);
            Assert.Equal(2, spill.Amounts.Length);
            Assert.Equal("b4542fdf4ba7c7342bb435cfffcd423b", spill.Keys[0]);
            Assert.Equal(3, spill.Amounts[0]);
            Assert.True(spill.IsPlausible());

            // An empty cache is a real answer: everything in it has been taken.
            CacheSpillMessage emptied = RoundTrip(new CacheSpillMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = CacheSpillMessage.KindRemainder,
                Keys = [],
                Amounts = [],
            });

            Assert.Empty(emptied.Keys);
            Assert.True(emptied.IsPlausible());

            // Arrays that disagree, and a key naming nothing, are both refused rather than guessed at.
            Assert.False(new CacheSpillMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Keys = ["a", "b"],
                Amounts = [1],
            }.IsPlausible());

            Assert.False(new CacheSpillMessage { RootKey = "Player#0", Keys = [], Amounts = [] }.IsPlausible());

            // A kind nobody defined reads as a remainder, which is the one that is never outranked.
            Assert.Equal(CacheSpillMessage.KindRemainder, RoundTrip(new CacheSpillMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = 200,
                Keys = [],
                Amounts = [],
            }).Kind);
        }

        /// <summary>
        /// An arena is a place, and a place a body cannot stand in is no arena. The radius is clamped
        /// rather than refused because a sender that means well and overshoots should still get a circle,
        /// while a centre that is not a number would put the edge of the world inside it.
        /// </summary>
        [Fact]
        public void ArenaState_ClampsItsRadiusAndRefusesNonsense()
        {
            ArenaStateMessage marked = RoundTrip(new ArenaStateMessage
            {
                Active = 1,
                CenterX = 12.5f,
                CenterY = -40f,
                Radius = 20f,
            });

            Assert.Equal(1, marked.Active);
            Assert.Equal(12.5f, marked.CenterX);
            Assert.Equal(-40f, marked.CenterY);
            Assert.Equal(20f, marked.Radius);
            Assert.True(marked.IsPlausible());

            Assert.Equal(ArenaStateMessage.MaxRadius, RoundTrip(new ArenaStateMessage
            {
                Active = 1,
                Radius = ArenaStateMessage.MaxRadius * 10f,
            }).Radius);

            Assert.Equal(ArenaStateMessage.MinRadius, RoundTrip(new ArenaStateMessage
            {
                Active = 1,
                Radius = 0f,
            }).Radius);

            // An arena nobody could stand in is refused rather than clamped into existence.
            Assert.False(new ArenaStateMessage
            {
                Active = 1,
                CenterX = float.NaN,
                Radius = 20f,
            }.IsPlausible());

            // Taken down, so the rest means nothing and does not have to make sense.
            Assert.True(new ArenaStateMessage { Active = 0, CenterX = float.NaN }.IsPlausible());
        }

        /// <summary>
        /// A round names its winner - or nobody, for a draw - and had at least two people in it. One player standing in
        /// a circle has not won anything, and the host is the only one who may say otherwise.
        /// </summary>
        [Fact]
        public void ArenaRound_NeedsAFightAndMayBeADraw()
        {
            ArenaRoundMessage round = RoundTrip(new ArenaRoundMessage
            {
                Round = 3,
                Winner = new PlayerId(2),
                Fighters = 4,
            });

            Assert.Equal(3, round.Round);
            Assert.Equal(new PlayerId(2), round.Winner);
            Assert.Equal(4, round.Fighters);
            Assert.True(round.IsPlausible());

            Assert.True(new ArenaRoundMessage { Round = 1, Winner = PlayerId.None, Fighters = 2 }.IsPlausible());
            Assert.False(new ArenaRoundMessage { Round = 0, Winner = new PlayerId(1), Fighters = 2 }.IsPlausible());
            Assert.False(new ArenaRoundMessage { Round = 1, Winner = new PlayerId(1), Fighters = 1 }.IsPlausible());
        }

        /// <summary>
        /// A verdict answers exactly one blow, and it names it with the pair the report already carries.
        /// A verdict nobody defined has to read as the harmless one, because the only verdict this mod
        /// acts on is a guard break and inventing one of those staggers a player for nothing.
        /// </summary>
        [Fact]
        public void BlockVerdict_RoundTripsAndFallsBackToHarmless()
        {
            BlockVerdictMessage broken = RoundTrip(new BlockVerdictMessage
            {
                Attacker = new PlayerId(1),
                Victim = new PlayerId(2),
                Sequence = 4242,
                Verdict = BlockVerdictMessage.VerdictBroken,
            });

            Assert.Equal(new PlayerId(1), broken.Attacker);
            Assert.Equal(new PlayerId(2), broken.Victim);
            Assert.Equal(4242u, broken.Sequence);
            Assert.Equal(BlockVerdictMessage.VerdictBroken, broken.Verdict);
            Assert.True(broken.IsPlausible());

            Assert.Equal(BlockVerdictMessage.VerdictNone, RoundTrip(new BlockVerdictMessage
            {
                Attacker = new PlayerId(1),
                Victim = new PlayerId(2),
                Verdict = 200,
            }).Verdict);

            // Nobody answers their own blow.
            Assert.False(new BlockVerdictMessage
            {
                Attacker = new PlayerId(3),
                Victim = new PlayerId(3),
                Verdict = BlockVerdictMessage.VerdictBlocked,
            }.IsPlausible());
        }

        /// <summary>
        /// A body's loot has no save root to hang off, so it is named by the creature that died. The two
        /// key forms have to stay tellable apart, because one is resolved against this world's save and
        /// the other against the session's creature handles.
        /// </summary>
        [Fact]
        public void LootTaken_NamesABodyByItsCreature()
        {
            string key = LootTakenMessage.CreatureKey(1234);

            Assert.True(LootTakenMessage.IsCreatureKey(key));
            Assert.False(ResourceWorkMessage.IsReplicableKey(key), "a creature key is not a save root key");

            LootTakenMessage body = RoundTrip(new LootTakenMessage
            {
                RootKey = key,
                Kind = LootTakenMessage.KindContainer,
                Remaining = [new LootStack { Id = "weapon_sword_short", Amount = 1 }],
            });

            Assert.Equal(key, body.RootKey);
            Assert.True(body.IsPlausible());

            // A save root key is still plausible, and is still not a creature.
            Assert.False(LootTakenMessage.IsCreatureKey("1f3320bf-b5bc-472c-af8a-06f331b9ccb0"));
            Assert.True(new LootTakenMessage { RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0" }.IsPlausible());

            // Neither form, so nothing a teammate could resolve.
            Assert.False(LootTakenMessage.IsCreatureKey("E"));
            Assert.False(LootTakenMessage.IsCreatureKey("Ewhat"));
            Assert.False(new LootTakenMessage { RootKey = "E" }.IsPlausible());
        }

        /// <summary>
        /// A world object crosses as a whole list, because an index into that list is the only name its
        /// entries have. The values are read back exactly: a bool travels as zero or one and a stage
        /// number as a small integer, and both have to survive the float they ride in.
        /// </summary>
        [Fact]
        public void WorldObject_RoundTripsWholeListAndRefusesWhatCannotBeAddressed()
        {
            WorldObjectMessage bools = RoundTrip(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = WorldObjectMessage.KindBool,
                Player = 3,
                Values = [1f, 0f, 1f],
            });

            Assert.Equal(WorldObjectMessage.KindBool, bools.Kind);
            Assert.Equal(3, bools.Player);
            Assert.Equal([1f, 0f, 1f], bools.Values);
            Assert.True(bools.IsReplicable());

            WorldObjectMessage stages = RoundTrip(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = WorldObjectMessage.KindFloat,
                Values = [2f],
            });

            Assert.Equal(2f, stages.Values[0]);
            Assert.True(stages.IsReplicable());

            // A missing list is empty rather than null, so nothing downstream has to guard for it.
            Assert.Empty(RoundTrip(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = WorldObjectMessage.KindActivation,
                Values = null!,
            }).Values);

            // The player's own root is per machine, and anything that is not a save root key names
            // nothing a teammate could find.
            Assert.False(RoundTrip(new WorldObjectMessage { RootKey = "Player#0", Values = [] }).IsReplicable());
            Assert.False(RoundTrip(new WorldObjectMessage { RootKey = "not a key", Values = [] }).IsReplicable());

            // A kind nobody defined is refused rather than guessed at: the three lists are applied to
            // different components, so guessing wrong writes onto the wrong objects.
            Assert.False(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = 9,
                Values = [],
            }.IsReplicable());
        }

        /// <summary>
        /// A list longer than the wire carries is refused whole, never cut. Cutting would leave the two
        /// worlds' indices out of step and write every entry past the cut onto the wrong object.
        /// </summary>
        [Fact]
        public void WorldObject_RefusesAListLongerThanTheWireCarries()
        {
            float[] tooMany = new float[WorldObjectMessage.MaxValues + 4];
            for (int index = 0; index < tooMany.Length; index++)
            {
                tooMany[index] = 1f;
            }

            Assert.False(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = WorldObjectMessage.KindBool,
                Values = tooMany,
            }.IsReplicable());

            // What does cross is capped, so a sender that ignores the rule cannot make the reader
            // allocate past it either.
            Assert.Equal(WorldObjectMessage.MaxValues, RoundTrip(new WorldObjectMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = WorldObjectMessage.KindBool,
                Values = tooMany,
            }).Values.Length);
        }

        /// <summary>
        /// A take crosses as the remainder, and a remainder can legitimately be nothing. The shape has to
        /// survive both, and an id longer than the wire allows is cut rather than refused.
        /// </summary>
        [Fact]
        public void LootTaken_RoundTripsRemainderAndEmptiness()
        {
            LootTakenMessage container = RoundTrip(new LootTakenMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = LootTakenMessage.KindContainer,
                Player = 2,
                Remaining =
                [
                    new LootStack { Id = "consumable_potion_heal", Amount = 3 },
                    new LootStack { Id = new string('x', LootStack.MaxIdLength + 10), Amount = LootStack.MaxAmount + 5 },
                ],
            });

            Assert.Equal(LootTakenMessage.KindContainer, container.Kind);
            Assert.Equal(2, container.Player);
            Assert.Equal(2, container.Remaining.Length);
            Assert.Equal("consumable_potion_heal", container.Remaining[0].Id);
            Assert.Equal(3, container.Remaining[0].Amount);
            Assert.Equal(LootStack.MaxIdLength, container.Remaining[1].Id.Length);
            Assert.Equal(LootStack.MaxAmount, container.Remaining[1].Amount);
            Assert.True(container.IsPlausible());

            LootTakenMessage pickup = RoundTrip(new LootTakenMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = LootTakenMessage.KindPickup,
                Remaining = null!,
            });

            Assert.Equal(LootTakenMessage.KindPickup, pickup.Kind);
            Assert.Empty(pickup.Remaining);

            Assert.False(RoundTrip(new LootTakenMessage { RootKey = "Player#0", Kind = LootTakenMessage.KindPickup }).IsPlausible());
            Assert.False(RoundTrip(new LootTakenMessage { RootKey = "not a key", Kind = LootTakenMessage.KindPickup }).IsPlausible());
        }

        /// <summary>
        /// An offer has to stay an offer across the wire, because the host treats it differently from a
        /// take: first one stands, and the host's own copy outranks it. A kind nobody defined reads as a
        /// take, the safe direction, since a take is never outranked.
        /// </summary>
        [Fact]
        public void LootTaken_KeepsProposalKindAndFoldsUnknownKindsToContainer()
        {
            LootTakenMessage proposal = RoundTrip(new LootTakenMessage
            {
                RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
                Kind = LootTakenMessage.KindProposal,
                Player = 1,
                Remaining = [new LootStack { Id = "weapon_sword", Amount = 1 }],
            });

            Assert.Equal(LootTakenMessage.KindProposal, proposal.Kind);
            Assert.Single(proposal.Remaining);

            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put("1f3320bf-b5bc-472c-af8a-06f331b9ccb0");
            writer.Put((byte)9);
            writer.Put((byte)1);
            writer.Put(false);
            writer.Put((byte)0);

            stream.Position = 0;
            LootTakenMessage unknown = default;
            unknown.Read(new BufferReader(stream));

            Assert.Equal(LootTakenMessage.KindContainer, unknown.Kind);
        }

        /// <summary>
        /// The enemy health rule is the only rule wider than a byte, and it sits last. A read that took
        /// it as a byte would parse cleanly and hand every session a creature toughness of zero, which
        /// the receiver reads as "unset" - so a host's 300 would silently become the default.
        /// </summary>
        [Fact]
        public void SessionRules_KeepsEnemyHealthAboveAByte()
        {
            SessionRulesMessage received = RoundTrip(new SessionRulesMessage
            {
                EnemyHealthPercent = CoopProtocol.MaxEnemyHealthPercent,
            });

            Assert.True(CoopProtocol.MaxEnemyHealthPercent > byte.MaxValue);
            Assert.Equal(CoopProtocol.MaxEnemyHealthPercent, received.EnemyHealthPercent);
        }

        /// <summary>
        /// Zero has to survive the wire as zero, because zero is the word for "the host never set
        /// this" and the receiver substitutes its default for it. A message layout that folded zero
        /// into something else would make a fresh host's session play with the wrong toughness.
        /// </summary>
        [Fact]
        public void SessionRules_DefaultsEnemyHealthToUnset()
        {
            Assert.Equal(0, default(SessionRulesMessage).EnemyHealthPercent);
            Assert.Equal(0, RoundTrip(new SessionRulesMessage { FriendlyFire = true }).EnemyHealthPercent);
        }

        /// <summary>
        /// Both ids and the figure, because a damage report is the one message where reading a field back
        /// wrong hurts the wrong person: the two player ids sit next to each other and are the same width,
        /// so swapping them serializes and deserializes perfectly and hits the attacker.
        /// </summary>
        [Fact]
        public void DamageReport_RoundTripsBothIdsAndTheBlow()
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage
            {
                Attacker = new PlayerId(1),
                Victim = new PlayerId(2),
                Amount = 4321,
                Sequence = 70_000u,
            });

            Assert.Equal(new PlayerId(1), received.Attacker);
            Assert.Equal(new PlayerId(2), received.Victim);
            Assert.Equal(4321, received.Amount);
            Assert.Equal(70_000u, received.Sequence);
        }

        /// <summary>
        /// The sequence is the only thing standing between a replayed packet and health taken twice, so it
        /// has to survive a value no smaller type would hold. A <c>ushort</c> here would wrap after about
        /// half an hour of fighting and start reading as "already applied".
        /// </summary>
        [Fact]
        public void DamageReport_CarriesASequenceWiderThanAShort()
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage { Sequence = uint.MaxValue });

            Assert.Equal(uint.MaxValue, received.Sequence);
        }

        /// <summary>
        /// Negative poise would restore the victim's stance rather than break it, which turns a blow into
        /// something that makes its target harder to stagger. The sender is the last thing that should
        /// decide that, so the figure is clamped on the way in rather than trusted.
        /// </summary>
        [Theory]
        [InlineData(-1f)]
        [InlineData(float.MinValue)]
        public void DamageReport_RefusesPoiseThatWouldRestoreStance(float poise)
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage { PoiseDamage = poise });

            Assert.Equal(0f, received.PoiseDamage);
        }

        /// <summary>
        /// The other end of the same field, and the one that matters more: a magnitude this large empties
        /// a pool that never refills, so its victim is staggered by every hit for the rest of the session.
        /// A sender does not get to decide that any more than it decides the sign.
        /// </summary>
        [Theory]
        [InlineData(float.MaxValue)]
        [InlineData(1e30f)]
        public void DamageReport_CapsPoiseThatWouldEmptyTheStanceForever(float poise)
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage { PoiseDamage = poise });

            Assert.Equal(DamageReportMessage.MaxPoiseDamage, received.PoiseDamage);
        }

        /// <summary>
        /// A damage type this build does not know has to read as physical - the answer it would have given
        /// before types travelled at all - rather than as whatever number happened to arrive.
        /// </summary>
        [Theory]
        [InlineData(DamageReportMessage.TypePhysical, DamageReportMessage.TypePhysical)]
        [InlineData(DamageReportMessage.TypeMagical, DamageReportMessage.TypeMagical)]
        [InlineData(DamageReportMessage.TypePure, DamageReportMessage.TypePure)]
        [InlineData((byte)200, DamageReportMessage.TypePhysical)]
        public void DamageReport_ReadsAnUnknownDamageTypeAsPhysical(byte sent, byte expected)
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage { DamageType = sent });

            Assert.Equal(expected, received.DamageType);
        }

        /// <summary>
        /// The origin decides whether the friendly-fire rule applies, so an unrecognized one has to read as
        /// the stricter of the two rather than letting an unclassifiable blow through a rule that is off.
        /// </summary>
        [Theory]
        [InlineData(DamageReportMessage.OriginPlayer, DamageReportMessage.OriginPlayer)]
        [InlineData(DamageReportMessage.OriginCreature, DamageReportMessage.OriginCreature)]
        [InlineData((byte)77, DamageReportMessage.OriginPlayer)]
        public void DamageReport_ReadsAnUnknownOriginAsAPlayersBlow(byte sent, byte expected)
        {
            DamageReportMessage received = RoundTrip(new DamageReportMessage { Origin = sent });

            Assert.Equal(expected, received.Origin);
        }

        /// <summary>The same rule on the message that carries a creature's poison.</summary>
        [Theory]
        [InlineData(DamageReportMessage.OriginPlayer, DamageReportMessage.OriginPlayer)]
        [InlineData(DamageReportMessage.OriginCreature, DamageReportMessage.OriginCreature)]
        [InlineData((byte)77, DamageReportMessage.OriginPlayer)]
        public void StatusEffect_ReadsAnUnknownOriginAsAPlayersDoing(byte sent, byte expected)
        {
            StatusEffectMessage received = RoundTrip(new StatusEffectMessage
            {
                EffectId = "poison",
                Origin = sent,
            });

            Assert.Equal(expected, received.Origin);
        }

        /// <summary>
        /// The case an attacker controls: a sender that ignores the cap and writes the field by hand. An
        /// unknown id is named in the log, so the length is what bounds the text that reaches it.
        /// </summary>
        [Fact]
        public void Knowledge_BoundsTheIdOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put(KnowledgeMessage.KindRecipe);
            writer.Put(new string('x', KnowledgeMessage.MaxIdLength + 500));

            stream.Position = 0;
            KnowledgeMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(KnowledgeMessage.MaxIdLength, received.Id.Length);
        }

        [Fact]
        public void Experience_RoundTripsTheHandleAndTheAmount()
        {
            ExperienceMessage sent = new()
            {
                Earner = new PlayerId(3),
                NetId = new EnemyNetId(4321),
                Amount = 37,
            };

            ExperienceMessage received = RoundTrip(sent);

            Assert.Equal(sent.Earner, received.Earner);
            Assert.Equal(sent.NetId, received.NetId);
            Assert.Equal(37, received.Amount);
            Assert.True(received.IsPlausible());
        }

        /// <summary>
        /// The case an attacker controls, and the one worth a test: this figure is added straight to a
        /// player's total, so an unclamped one is a single packet that ends somebody's progression.
        /// </summary>
        [Theory]
        [InlineData(ExperienceMessage.MaxAmount + 1, ExperienceMessage.MaxAmount)]
        [InlineData(int.MaxValue, ExperienceMessage.MaxAmount)]
        [InlineData(-5, 0)]
        [InlineData(int.MinValue, 0)]
        public void Experience_BoundsTheAmountOnRead(int sent, int expected)
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put((ushort)7);
            writer.Put(sent);

            stream.Position = 0;
            ExperienceMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(expected, received.Amount);
        }

        [Fact]
        public void Experience_IsNotPlausibleWithoutAHandleOrAnAmount()
        {
            Assert.False(new ExperienceMessage { NetId = EnemyNetId.None, Amount = 10 }.IsPlausible());
            Assert.False(new ExperienceMessage { NetId = new EnemyNetId(1), Amount = 0 }.IsPlausible());
        }

        [Fact]
        public void SummonBind_RoundTripsTheCastAndTheHandle()
        {
            SummonBindMessage sent = new()
            {
                Caster = new PlayerId(2),
                CastSequence = 200,
                Index = 3,
                NetId = new EnemyNetId(900),
            };

            SummonBindMessage received = RoundTrip(sent);

            Assert.Equal(sent.Caster, received.Caster);
            Assert.Equal(sent.CastSequence, received.CastSequence);
            Assert.Equal(sent.Index, received.Index);
            Assert.Equal(sent.NetId, received.NetId);
            Assert.True(received.IsPlausible());
        }

        /// <summary>
        /// The index is used to reach into a list, so a sender that ignores the cap must be refused rather
        /// than trusted to stay inside it.
        /// </summary>
        [Fact]
        public void SummonBind_IsNotPlausibleWithoutAHandleOrWithAWildIndex()
        {
            Assert.False(new SummonBindMessage { NetId = EnemyNetId.None, Index = 0 }.IsPlausible());
            Assert.False(new SummonBindMessage { NetId = new EnemyNetId(1), Index = SummonBindMessage.MaxIndex }.IsPlausible());
            Assert.False(new SummonBindMessage { NetId = new EnemyNetId(1), Index = 255 }.IsPlausible());
        }

        [Fact]
        public void StatusEffect_RoundTripsBothIdsAndTheEffect()
        {
            StatusEffectMessage sent = new()
            {
                Target = new PlayerId(2),
                Source = new PlayerId(1),
                EffectId = "b1946ac92492d2347c6235b4d2611184",
                Sequence = 9,
            };

            StatusEffectMessage received = RoundTrip(sent);

            // Both ids, because the two same-width ids sitting next to each other would swap silently and
            // poison the attacker instead of the victim.
            Assert.Equal(sent.Target, received.Target);
            Assert.Equal(sent.Source, received.Source);
            Assert.Equal(sent.EffectId, received.EffectId);
            Assert.Equal(sent.Sequence, received.Sequence);
        }

        /// <summary>
        /// The case an attacker controls: a sender that ignores the cap and writes the field by hand. An
        /// unknown effect id is named in the log, so the length is what bounds the text that gets there.
        /// </summary>
        [Fact]
        public void StatusEffect_BoundsTheEffectIdOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put((byte)2);
            writer.Put((byte)0);
            writer.Put(DamageReportMessage.OriginPlayer);
            writer.Put(new string('x', StatusEffectMessage.MaxEffectIdLength + 500));

            stream.Position = 0;
            StatusEffectMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(StatusEffectMessage.MaxEffectIdLength, received.EffectId.Length);
        }

        /// <summary>
        /// A null id is what an unset field serializes as, and it has to survive the trip as something the
        /// receiver can compare rather than as the null that would take out the body applying it.
        /// </summary>
        [Fact]
        public void ItemUse_ReadsAMissingItemAsEmpty()
        {
            ItemUseMessage received = RoundTrip(new ItemUseMessage { User = new PlayerId(1) });

            Assert.Equal(string.Empty, received.ItemId);
        }

        /// <summary>
        /// The case an attacker controls: a sender that ignores the cap and writes the field by hand. The
        /// length is what bounds the text that reaches this machine's log, which is where an unknown
        /// ability id ends up.
        /// </summary>
        [Fact]
        public void ItemUse_BoundsTheItemIdOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put((byte)0);
            writer.Put((byte)0);
            writer.Put(new string('x', ItemUseMessage.MaxItemUseIdLength + 500));

            stream.Position = 0;
            ItemUseMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(ItemUseMessage.MaxItemUseIdLength, received.ItemId.Length);
        }

        /// <summary>
        /// Same reasoning as the handshake's default: an unset rule has to read as off, because that
        /// failure is silent until somebody is killed by a friend.
        /// </summary>
        [Fact]
        public void SessionRules_DefaultsFriendlyFireOff()
        {
            Assert.False(default(SessionRulesMessage).FriendlyFire);
            Assert.False(RoundTrip(default(SessionRulesMessage)).FriendlyFire);
        }

        /// <summary>
        /// Exact equality rather than a tolerance on purpose: nothing in the path is allowed to round a
        /// coordinate, so a near-miss here is a serialization bug and not a floating point fact of life.
        /// </summary>
        [Fact]
        public void PlayerState_RoundTripsExactly()
        {
            PlayerStateMessage sent = new()
            {
                PlayerId = new PlayerId(2),
                X = -1234.5678f,
                Y = 0.1f,
                MoveAngle = 96,
                LookAngle = 200,
                MoveMode = 2,
                InteractionMode = 1,
                Action = 3,
                ActionSequence = 201,

                // Two adjacent fields of one type, so they are given different values: equal ones survive
                // a Read that returns them the wrong way round, and a guard that breaks at the wrong
                // moment is how that would show.
                Health = 37,
                Stamina = 12,
            };

            sent.Moving = true;
            sent.ActionReleased = true;
            sent.Downed = true;

            PlayerStateMessage received = RoundTrip(sent);

            Assert.Equal(sent.PlayerId, received.PlayerId);
            Assert.Equal(sent.X, received.X);
            Assert.Equal(sent.Y, received.Y);
            Assert.Equal(sent.MoveAngle, received.MoveAngle);
            Assert.Equal(sent.LookAngle, received.LookAngle);
            Assert.Equal(sent.MoveMode, received.MoveMode);
            Assert.Equal(sent.InteractionMode, received.InteractionMode);
            Assert.Equal(sent.Action, received.Action);
            Assert.Equal(sent.ActionSequence, received.ActionSequence);
            Assert.Equal(sent.Health, received.Health);
            Assert.Equal(sent.Stamina, received.Stamina);
            Assert.True(received.Moving);
            Assert.True(received.ActionReleased);
            Assert.True(received.Downed);
        }

        /// <summary>
        /// A player's talents arrive as sent, in order, and a guid longer than the limit is cut rather than
        /// allowed to carry an unbounded string.
        /// </summary>
        [Fact]
        public void PlayerTalents_RoundTripsAndBoundsGuids()
        {
            string longGuid = new('x', PlayerTalentsMessage.MaxGuidLength + 20);
            PlayerTalentsMessage received = RoundTrip(new PlayerTalentsMessage
            {
                PlayerId = new PlayerId(3),
                Guids = ["a1b2", "c3d4", longGuid],
            });

            Assert.Equal(new PlayerId(3), received.PlayerId);
            Assert.Equal(3, received.Guids.Length);
            Assert.Equal("a1b2", received.Guids[0]);
            Assert.Equal("c3d4", received.Guids[1]);
            Assert.Equal(PlayerTalentsMessage.MaxGuidLength, received.Guids[2].Length);

            Assert.Empty(RoundTrip(new PlayerTalentsMessage { PlayerId = new PlayerId(1) }).Guids);
        }

        /// <summary>
        /// An action no button started travels as its prefab's hash, only while there is one: the ordinary
        /// packet keeps its size, and the flag that says the hash follows never leaks into the flags read back.
        /// </summary>
        [Fact]
        public void PlayerState_CarriesAnUnboundActionOnlyWhenThereIsOne()
        {
            PlayerStateMessage working = RoundTrip(new PlayerStateMessage { ActionPrefab = 0xDEADBEEF, Health = 5, Stamina = 7, ActionReleased = true });
            Assert.Equal(0xDEADBEEFu, working.ActionPrefab);
            Assert.Equal(5, working.Health);
            Assert.Equal(7, working.Stamina);
            Assert.Equal(PlayerStateMessage.FlagActionReleased, working.Flags);

            PlayerStateMessage idle = RoundTrip(new PlayerStateMessage { Health = 5 });
            Assert.Equal(0u, idle.ActionPrefab);
            Assert.Equal(5, idle.Health);
        }

        /// <summary>
        /// The three flags share a byte. Each one set alone must read back as that one alone, or a body
        /// that started walking would also fall over.
        /// </summary>
        [Fact]
        public void PlayerState_FlagsAreIndependent()
        {
            PlayerStateMessage moving = RoundTrip(new PlayerStateMessage { Moving = true });
            Assert.True(moving.Moving);
            Assert.False(moving.ActionReleased);
            Assert.False(moving.Downed);

            PlayerStateMessage released = RoundTrip(new PlayerStateMessage { ActionReleased = true });
            Assert.False(released.Moving);
            Assert.True(released.ActionReleased);
            Assert.False(released.Downed);

            PlayerStateMessage downed = RoundTrip(new PlayerStateMessage { Downed = true });
            Assert.False(downed.Moving);
            Assert.False(downed.ActionReleased);
            Assert.True(downed.Downed);

            PlayerStateMessage combo = RoundTrip(new PlayerStateMessage { ActionCombo = true, ActionReleased = true });
            Assert.True(combo.ActionCombo);
            Assert.True(combo.ActionReleased);
            Assert.False(combo.Moving);
            Assert.False(combo.Downed);

            PlayerStateMessage cleared = downed;
            cleared.Downed = false;
            Assert.Equal(0, cleared.Flags);
        }

        /// <summary>
        /// A direction goes out as a 256th of a turn and comes back as a unit vector. Twenty-two
        /// bytes on a thirty-hertz packet were spent on four floats saying this; the byte has to say it
        /// to within a degree and a half, and the four compass points exactly.
        /// </summary>
        [Theory]
        [InlineData(1f, 0f, 0)]
        [InlineData(0f, 1f, 64)]
        [InlineData(-1f, 0f, 128)]
        [InlineData(0f, -1f, 192)]
        [InlineData(0.7071f, 0.7071f, 32)]
        [InlineData(-0.7071f, -0.7071f, 160)]
        [InlineData(5f, 0f, 0)]
        public void PlayerState_AnglesPackToTheCompass(float x, float y, byte expected)
        {
            Assert.True(PlayerStateMessage.TryPackAngle(x, y, out byte angle));
            Assert.Equal(expected, angle);

            PlayerStateMessage.UnpackAngle(angle, out float backX, out float backY);
            float length = MathF.Sqrt(x * x + y * y);
            Assert.Equal(x / length, backX, 2);
            Assert.Equal(y / length, backY, 2);
        }

        [Fact]
        public void PlayerState_EveryAngleRoundTripsWithinAStep()
        {
            for (int packed = 0; packed < 256; packed++)
            {
                PlayerStateMessage.UnpackAngle((byte)packed, out float x, out float y);
                Assert.True(PlayerStateMessage.TryPackAngle(x, y, out byte again));
                Assert.Equal(packed, again);
            }
        }

        /// <summary>
        /// Standing still has no direction, and NaN is not one either. Both come back as "no angle", so
        /// the sender carries them as the moving flag being off rather than as a zero that reads as +X.
        /// </summary>
        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.001f, -0.001f)]
        [InlineData(float.NaN, 1f)]
        [InlineData(1f, float.PositiveInfinity)]
        public void PlayerState_NoDirectionHasNoAngle(float x, float y)
        {
            Assert.False(PlayerStateMessage.TryPackAngle(x, y, out byte angle));
            Assert.Equal(0, angle);
        }

        /// <summary>
        /// On its feet is what a message that never said otherwise has to mean. The flag is the only thing
        /// keeping a body upright, so a default or truncated read that came out non-zero would drop a
        /// player who is fine to the floor and leave them there - every subsequent packet says the same.
        /// </summary>
        [Fact]
        public void PlayerState_DefaultsToStanding()
        {
            Assert.False(default(PlayerStateMessage).Downed);
            Assert.False(RoundTrip(default(PlayerStateMessage)).Downed);
        }

        /// <summary>
        /// An ordinary state has to survive the check, or the check is a way to stop coop working. The
        /// values here are deliberately unremarkable - the point is that nothing about a normal player
        /// looks implausible.
        /// </summary>
        [Fact]
        public void PlayerState_OrdinaryValuesArePlausible()
        {
            PlayerStateMessage state = new()
            {
                X = 1234.5f,
                Y = -987.25f,
                MoveAngle = 255,
                LookAngle = 128,
            };

            Assert.True(state.IsPlausible());
            Assert.True(default(PlayerStateMessage).IsPlausible());
        }

        /// <summary>
        /// NaN is the value that matters. It reaches the body's transform through the movement module and
        /// then feeds the next frame's interpolation as its own starting point, so a single one leaves the
        /// body broken for the rest of the session - it cannot recover, because what would repair it is
        /// consuming its own poisoned output.
        /// </summary>
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void PlayerState_RefusesNonFiniteCoordinates(float bad)
        {
            Assert.False(new PlayerStateMessage { X = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { Y = bad }.IsPlausible());
        }

        /// <summary>
        /// Finite and still nonsense. A coordinate this large is not somewhere a player can be followed
        /// to, and it is the other half of what the non-finite check misses.
        /// </summary>
        [Fact]
        public void PlayerState_RefusesAbsurdMagnitudes()
        {
            Assert.False(new PlayerStateMessage { X = PlayerStateMessage.MaxCoordinate * 2f }.IsPlausible());
            Assert.False(new PlayerStateMessage { Y = -PlayerStateMessage.MaxCoordinate * 2f }.IsPlausible());

            // The limits themselves are accepted; a bound that refuses its own edge is a bound nobody can
            // reason about.
            Assert.True(new PlayerStateMessage { X = PlayerStateMessage.MaxCoordinate }.IsPlausible());
            Assert.True(new PlayerStateMessage { Y = -PlayerStateMessage.MaxCoordinate }.IsPlausible());
        }

        /// <summary>
        /// The check has to survive the wire, not just the struct. A NaN written and read back is still a
        /// NaN, and this is the path it would actually arrive by.
        /// </summary>
        [Fact]
        public void PlayerState_NonFiniteSurvivesRoundTripAndIsStillRefused()
        {
            PlayerStateMessage received = RoundTrip(new PlayerStateMessage { X = float.NaN, Y = 5f });

            Assert.True(float.IsNaN(received.X));
            Assert.False(received.IsPlausible());
        }

        /// <summary>
        /// The build and the voice are asserted with values that differ from each other, and from the
        /// player id, on purpose. All three are bytes written next to each other, so a Read that returns
        /// them in the wrong order round-trips perfectly as long as the test sends the same number twice -
        /// and a body that comes out with somebody else's build is the exact bug this message exists to
        /// prevent.
        /// </summary>
        /// <summary>
        /// The ordinary case and the one the counter exists for. A repeat is the packet already acted on,
        /// and a step of one across the wrap is still a step of one - a comparison written as
        /// <c>arrived &gt; applied</c> passes every row above and fails that one, leaving a body that
        /// stops attacking every 256 swings for no reason anybody could reproduce on purpose.
        /// </summary>
        [Theory]
        [InlineData(1, 0, true)]
        [InlineData(0, 0, false)]
        [InlineData(200, 200, false)]
        [InlineData(0, 255, true)]
        [InlineData(3, 254, true)]
        public void ActionSequence_TreatsForwardStepsAsNewAcrossTheWrap(byte arrived, byte applied, bool expected)
        {
            Assert.Equal(expected, PlayerStateMessage.IsNewerAction(arrived, applied));
        }

        /// <summary>
        /// A packet overtaken by a newer one, which unreliable delivery produces routinely. Acting on it
        /// restarts an action its owner has already finished, and on screen that is the far player
        /// attacking twice - so the far half of the range has to read as the past, wrap included.
        /// </summary>
        [Theory]
        [InlineData(0, 1, false)]
        [InlineData(254, 3, false)]
        [InlineData(255, 0, false)]
        public void ActionSequence_TreatsLateArrivalsAsAlreadyApplied(byte arrived, byte applied, bool expected)
        {
            Assert.Equal(expected, PlayerStateMessage.IsNewerAction(arrived, applied));
        }

        /// <summary>
        /// The exact seam between the two halves, which no other test pins: at 127 ahead the packet is
        /// still the future, and one further is the past. Nothing in a session is expected to arrive this
        /// far out of order - the point is that the boundary cannot move unnoticed, because either side of
        /// it is a defensible rule and drifting between them silently is not.
        /// </summary>
        [Fact]
        public void ActionSequence_SplitsTheRangeInHalf()
        {
            Assert.True(PlayerStateMessage.IsNewerAction(127, 0));
            Assert.False(PlayerStateMessage.IsNewerAction(128, 0));
        }

        [Fact]
        public void Equipment_RoundTripsEveryItem()
        {
            PlayerEquipmentMessage sent = new()
            {
                PlayerId = new PlayerId(1),
                BodyType = 2,
                VoiceId = 3,
                ActiveSet = 1,
                ItemIds = ["Weapon_Spear", "Armor_Chest_Leather", "Amulet_Bone"],
                ItemSets = [0, 1, PlayerEquipmentMessage.SharedSet],
            };

            PlayerEquipmentMessage received = RoundTrip(sent);

            Assert.Equal(sent.PlayerId, received.PlayerId);
            Assert.Equal(sent.BodyType, received.BodyType);
            Assert.Equal(sent.VoiceId, received.VoiceId);
            Assert.Equal(sent.ItemIds, received.ItemIds);

            // Alongside the ids, and in step with them. An item arriving in the wrong set is a bow in the
            // hand that should hold a sword, and the two lists drifting apart by one is how that happens.
            Assert.Equal(sent.ActiveSet, received.ActiveSet);
            Assert.Equal(sent.ItemSets, received.ItemSets);
        }

        /// <summary>
        /// A sender that names fewer sets than items - an older peer, or a caller that built the arrays
        /// separately - must not have the items after the short point land in somebody else's set. Every
        /// item without a set of its own belongs to all of them.
        /// </summary>
        [Fact]
        public void Equipment_TreatsItemsWithNoSetAsShared()
        {
            PlayerEquipmentMessage sent = new()
            {
                ItemIds = ["Weapon_Bow", "Quiver_Simple"],
                ItemSets = [1],
            };

            PlayerEquipmentMessage received = RoundTrip(sent);

            Assert.Equal(2, received.ItemSets.Length);
            Assert.Equal(1, received.ItemSets[0]);
            Assert.Equal(PlayerEquipmentMessage.SharedSet, received.ItemSets[1]);
        }

        /// <summary>
        /// The cap itself is a legal set, not an overlong one. A bound written as the wrong comparison
        /// drops the last item of a full set, which is a weapon that silently does not arrive rather than
        /// anything that looks like a bug.
        /// </summary>
        [Fact]
        public void Equipment_KeepsExactlyTheMaximumIntact()
        {
            string[] full = Enumerable.Range(0, PlayerEquipmentMessage.MaxItems)
                .Select(index => $"Item_{index}")
                .ToArray();

            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = full });

            Assert.Equal(full, received.ItemIds);
        }

        /// <summary>
        /// The sender's own caps, which is the case a bug in this mod produces rather than one an attacker
        /// does: more items than the message allows and an id longer than it allows, both dropped to the
        /// bound instead of writing a message the far side reads as garbage.
        /// </summary>
        [Fact]
        public void Equipment_BoundsCountAndIdLengthOnWrite()
        {
            string[] tooMany = Enumerable.Range(0, PlayerEquipmentMessage.MaxItems + 9)
                .Select(_ => new string('x', PlayerEquipmentMessage.MaxItemIdLength + 50))
                .ToArray();

            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = tooMany });

            Assert.Equal(PlayerEquipmentMessage.MaxItems, received.ItemIds.Length);
            Assert.All(received.ItemIds, id => Assert.Equal(PlayerEquipmentMessage.MaxItemIdLength, id.Length));
        }

        /// <summary>
        /// An empty set is the ordinary state of a player who has just started, and it has to survive the
        /// trip as an empty array rather than as null - the receiver undresses a body from this list, and
        /// a null one would read as "no equipment message" instead of "no equipment".
        /// </summary>
        [Fact]
        public void Equipment_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = null! });

            Assert.NotNull(received.ItemIds);
            Assert.Empty(received.ItemIds);
        }

        /// <summary>
        /// The case an attacker controls: a sender that ignores both caps and writes the field by hand.
        /// The count bounds what the receiver allocates and the length bounds what reaches the log.
        ///
        /// Every field is written here, in the order <see cref="PlayerEquipmentMessage.Write"/> uses,
        /// because a hand-written stream that omits one does not fail - it slides the count into a
        /// neighboring byte and goes on reading strings out of the middle of other strings, which for a
        /// payload of one repeated character still satisfies every assertion below while testing nothing.
        /// </summary>
        [Fact]
        public void Equipment_BoundsCountAndIdLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put((byte)0);
            writer.Put((byte)0);
            writer.Put((byte)0);
            writer.Put(false);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put((byte)0);
                writer.Put(new string('x', PlayerEquipmentMessage.MaxItemIdLength + 50));
            }

            stream.Position = 0;
            PlayerEquipmentMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(PlayerEquipmentMessage.MaxItems, received.ItemIds.Length);
            Assert.All(received.ItemIds, id => Assert.Equal(PlayerEquipmentMessage.MaxItemIdLength, id.Length));
        }

        /// <summary>
        /// A list longer than one message carries crosses in parts, and the parts put back together are the
        /// list - every item, in order, the last part alone saying nothing more follows. Each receiver
        /// replaces what it knew with the list, so a part lost or a flag wrong is a whole outfit, talent set
        /// or chest gone rather than a few items.
        /// </summary>
        [Fact]
        public void Split_PartsComeBackAsTheWholeList()
        {
            string[] ids = Enumerable.Range(0, (2 * PlayerEquipmentMessage.MaxItems) + 3).Select(index => $"Item_{index}").ToArray();
            byte[] sets = ids.Select((_, index) => (byte)(index % 3)).ToArray();

            PlayerEquipmentMessage[] outfit = new PlayerEquipmentMessage { ItemIds = ids, ItemSets = sets, ActiveSet = 1 }
                .Split().Select(part => RoundTrip(part)).ToArray();

            Assert.Equal(3, outfit.Length);
            Assert.Equal(new[] { true, true, false }, outfit.Select(part => part.More));
            Assert.All(outfit, part => Assert.Equal(1, part.ActiveSet));
            Assert.Equal(ids, outfit.SelectMany(part => part.ItemIds));
            Assert.Equal(sets, outfit.SelectMany(part => part.ItemSets));

            string[] guids = Enumerable.Range(0, PlayerTalentsMessage.MaxTalents + 1).Select(index => $"guid_{index}").ToArray();
            PlayerTalentsMessage[] talents = new PlayerTalentsMessage { Guids = guids }.Split().Select(part => RoundTrip(part)).ToArray();

            Assert.Equal(new[] { true, false }, talents.Select(part => part.More));
            Assert.Equal(guids, talents.SelectMany(part => part.Guids));

            LootStack[] stacks = Enumerable.Range(0, LootTakenMessage.MaxStacks * 2).Select(index => new LootStack { Id = $"Item_{index}", Amount = index + 1 }).ToArray();
            LootTakenMessage[] chest = new LootTakenMessage { RootKey = "1f3320bf-b5bc-472c-af8a-06f331b9ccb0", Remaining = stacks }
                .Split().Select(part => RoundTrip(part)).ToArray();

            Assert.Equal(new[] { true, false }, chest.Select(part => part.More));
            Assert.All(chest, part => Assert.Equal("1f3320bf-b5bc-472c-af8a-06f331b9ccb0", part.RootKey));
            Assert.Equal(stacks, chest.SelectMany(part => part.Remaining));

            // Nothing to say is still said once: an emptied chest, a bare body.
            LootTakenMessage emptied = Assert.Single(new LootTakenMessage { Remaining = [] }.Split());
            Assert.False(emptied.More);
            Assert.Single(new PlayerEquipmentMessage().Split());
        }

        [Fact]
        public void EnemyDeath_RoundTripsGuidAndFlags()
        {
            EnemyDeathMessage sent = new()
            {
                ActorGuid = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c6d",
                Flags = EnemyDeathMessage.FlagReconcile,
            };

            EnemyDeathMessage received = RoundTrip(sent);

            Assert.Equal("1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c6d", received.ActorGuid);
            Assert.True(received.IsReconcile);
        }

        /// <summary>
        /// The default has to mean a real kill. A death is the one fact in this protocol that cannot be
        /// corrected afterwards, and a zeroed message that read as a save being reconciled would apply it
        /// silently - no animation, no experience, no loot - which looks like the creature was never
        /// killed at all.
        /// </summary>
        [Fact]
        public void EnemyDeath_DefaultIsARealKillRatherThanAReconcile()
        {
            EnemyDeathMessage received = RoundTrip(new EnemyDeathMessage { ActorGuid = "abc" });

            Assert.False(received.IsReconcile);
        }

        /// <summary>
        /// A null guid has to survive as an empty string rather than as null, because every consumer of
        /// this message uses it as a dictionary key.
        /// </summary>
        [Fact]
        public void EnemyDeath_RoundTripsNullGuidAsEmpty()
        {
            EnemyDeathMessage received = RoundTrip(new EnemyDeathMessage { ActorGuid = null! });

            Assert.NotNull(received.ActorGuid);
            Assert.Empty(received.ActorGuid);
        }

        /// <summary>
        /// The case an attacker controls: a hand-written stream from a sender that ignores the cap. The
        /// guid reaches a dictionary and a log line, so its length is bounded on read and not only on
        /// write.
        ///
        /// Both fields are written here, in the order <see cref="EnemyDeathMessage.Write"/> uses. A stream
        /// that omitted the flags byte would still satisfy the length assertion while testing nothing.
        /// </summary>
        [Fact]
        public void EnemyDeath_BoundsGuidLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put(new string('x', EnemyDeathMessage.MaxGuidLength + 500));
            writer.Put((byte)0);

            stream.Position = 0;
            EnemyDeathMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemyDeathMessage.MaxGuidLength, received.ActorGuid.Length);
        }

        /// <summary>
        /// Unknown flag bits are kept rather than refused, so a peer on a later version that sets one does
        /// not have the death itself discarded by this build.
        /// </summary>
        [Fact]
        public void EnemyDeath_IgnoresUnknownFlagsWithoutLosingTheDeath()
        {
            EnemyDeathMessage received = RoundTrip(new EnemyDeathMessage
            {
                ActorGuid = "abc",
                Flags = 0b1111_1110,
            });

            Assert.Equal("abc", received.ActorGuid);
            Assert.False(received.IsReconcile);
        }

        [Fact]
        public void EnemyBind_RoundTripsHandleGuidAndHealth()
        {
            EnemyBindMessage sent = new()
            {
                NetId = new EnemyNetId(4242),
                ActorGuid = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c6d",
                Health = 37,
                MaxHealth = 120,
                Flags = EnemyBindMessage.FlagDeadAtBind,
            };

            EnemyBindMessage received = RoundTrip(sent);

            Assert.Equal(new EnemyNetId(4242), received.NetId);
            Assert.Equal("1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c6d", received.ActorGuid);
            Assert.Equal(37, received.MaxHealth is 0 ? 0 : received.Health);
            Assert.Equal(120, received.MaxHealth);
            Assert.True(received.IsDeadAtBind);
            Assert.False(received.HostDrives);
        }

        /// <summary>
        /// A zeroed bind must read as a living creature the host only owns the health of. The two flags
        /// both make a receiver do something irreversible - lay a body out, or hand its movement to the
        /// wire - and neither should happen because a field was never set.
        /// </summary>
        [Fact]
        public void EnemyBind_DefaultsToAliveAndNotDriven()
        {
            EnemyBindMessage received = RoundTrip(new EnemyBindMessage { ActorGuid = "abc" });

            Assert.False(received.IsDeadAtBind);
            Assert.False(received.HostDrives);
        }

        [Fact]
        public void EnemyBind_BoundsGuidLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((ushort)1);
            writer.Put(new string('x', EnemyBindMessage.MaxGuidLength + 400));
            writer.Put((ushort)10);
            writer.Put((ushort)10);
            writer.Put((byte)0);

            stream.Position = 0;
            EnemyBindMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemyBindMessage.MaxGuidLength, received.ActorGuid.Length);
        }

        /// <summary>
        /// Both answers, because the default-heavy failure mode here is a message that always reads as
        /// its zero value - and for this one "false" quietly hands a creature to the wrong machine's AI.
        /// </summary>
        [Fact]
        public void EnemyDrive_RoundTripsBothAnswers()
        {
            EnemyDriveMessage driven = RoundTrip(new EnemyDriveMessage
            {
                NetId = new EnemyNetId(11),
                HostDrives = true,
            });

            Assert.Equal(new EnemyNetId(11), driven.NetId);
            Assert.True(driven.HostDrives);

            EnemyDriveMessage handedBack = RoundTrip(new EnemyDriveMessage
            {
                NetId = new EnemyNetId(12),
                HostDrives = false,
            });

            Assert.Equal(new EnemyNetId(12), handedBack.NetId);
            Assert.False(handedBack.HostDrives);
        }

        [Fact]
        public void EnemyEngage_RoundTripsPlayerTargetAndMode()
        {
            EnemyEngageMessage received = RoundTrip(new EnemyEngageMessage
            {
                Player = new PlayerId(3),
                NetId = new EnemyNetId(42),
                Mode = EnemyEngageMessage.ModeBrawl,
            });

            Assert.Equal(new PlayerId(3), received.Player);
            Assert.Equal(new EnemyNetId(42), received.NetId);
            Assert.Equal(EnemyEngageMessage.ModeBrawl, received.Mode);
            Assert.True(received.IsPlausible());
        }

        [Fact]
        public void EnemyEngage_RefusesAModeThisBuildDoesNotKnow()
        {
            EnemyEngageMessage received = RoundTrip(new EnemyEngageMessage
            {
                Player = new PlayerId(1),
                NetId = new EnemyNetId(7),
                Mode = 99,
            });

            Assert.False(received.IsPlausible());
        }

        /// <summary>
        /// The figure reaches a stance calculation on the host, so it is clamped at both ends: a negative
        /// would push stance the wrong way and an enormous one would stagger anything instantly.
        /// </summary>
        [Fact]
        public void EnemyDamage_ClampsPoiseAndFallsBackToPhysical()
        {
            EnemyDamageMessage negative = RoundTrip(new EnemyDamageMessage { PoiseDamage = -50f, DamageType = 200 });

            Assert.Equal(0f, negative.PoiseDamage);
            Assert.Equal(DamageReportMessage.TypePhysical, negative.DamageType);

            EnemyDamageMessage enormous = RoundTrip(new EnemyDamageMessage { PoiseDamage = float.MaxValue });

            Assert.Equal(DamageReportMessage.MaxPoiseDamage, enormous.PoiseDamage);
        }

        /// <summary>
        /// The count decides how much this machine allocates before reading a single guid, so it is
        /// bounded on read and not only on write.
        /// </summary>
        [Fact]
        public void EnemyGraveyard_BoundsCountAndGuidLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put(new string('x', EnemyGraveyardMessage.MaxGuidLength + 20));
            }

            writer.Put((byte)0);

            stream.Position = 0;
            EnemyGraveyardMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemyGraveyardMessage.MaxGuids, received.Guids.Length);
            Assert.All(received.Guids, guid => Assert.Equal(EnemyGraveyardMessage.MaxGuidLength, guid.Length));
        }

        /// <summary>
        /// An empty graveyard has to survive as an empty array rather than as null. The last chunk of a
        /// send is routinely empty - it exists to say the list has ended - and a receiver walking null
        /// would fault on the one message that is guaranteed to arrive.
        /// </summary>
        [Fact]
        public void EnemyGraveyard_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            EnemyGraveyardMessage received = RoundTrip(new EnemyGraveyardMessage { Guids = null! });

            Assert.NotNull(received.Guids);
            Assert.Empty(received.Guids);
        }

        /// <summary>
        /// The count decides how much this machine allocates before reading a single guid, so it is
        /// bounded on read and not only on write - the graveyard's rule, for the graveyard's reason.
        /// </summary>
        [Fact]
        public void EnemyAlive_BoundsCountAndGuidLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put(new string('x', EnemyAliveMessage.MaxGuidLength + 20));
            }

            stream.Position = 0;
            EnemyAliveMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemyAliveMessage.MaxGuids, received.Guids.Length);
            Assert.All(received.Guids, guid => Assert.Equal(EnemyAliveMessage.MaxGuidLength, guid.Length));
        }

        [Fact]
        public void EnemyAlive_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            EnemyAliveMessage received = RoundTrip(new EnemyAliveMessage { Guids = null! });

            Assert.NotNull(received.Guids);
            Assert.Empty(received.Guids);
        }

        /// <summary>
        /// The count decides how much this machine allocates before reading a single guid, so it is
        /// bounded on read and not only on write - the graveyard's rule, for the graveyard's reason.
        /// </summary>
        [Fact]
        public void EnemyRevive_BoundsCountAndGuidLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put(new string('x', EnemyReviveMessage.MaxGuidLength + 20));
            }

            stream.Position = 0;
            EnemyReviveMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemyReviveMessage.MaxGuids, received.Guids.Length);
            Assert.All(received.Guids, guid => Assert.Equal(EnemyReviveMessage.MaxGuidLength, guid.Length));
        }

        [Fact]
        public void EnemyRevive_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            EnemyReviveMessage received = RoundTrip(new EnemyReviveMessage { Guids = null! });

            Assert.NotNull(received.Guids);
            Assert.Empty(received.Guids);
        }

        [Fact]
        public void GVarChange_BoundsStringsOnRead()
        {
            GVarChangeMessage received = RoundTrip(new GVarChangeMessage
            {
                ListGuid = new string('x', GVarChangeMessage.MaxGuidLength + 20),
                VarId = new string('y', GVarChangeMessage.MaxIdLength + 20),
                Kind = GVarChangeMessage.KindBool,
                IntValue = 1,
            });

            Assert.Equal(GVarChangeMessage.MaxGuidLength, received.ListGuid.Length);
            Assert.Equal(GVarChangeMessage.MaxIdLength, received.VarId.Length);
        }

        /// <summary>
        /// The count decides how much this machine allocates before reading a single record, so it is
        /// bounded on read and not only on write - the graveyard's rule, for the graveyard's reason.
        /// </summary>
        [Fact]
        public void GVarSnapshot_BoundsCountAndStringsOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put(new string('g', GVarSnapshotMessage.MaxGuidLength + 20));
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put(new string('v', GVarSnapshotMessage.MaxIdLength + 20));
                writer.Put((byte)0);
                writer.Put(1);
                writer.Put(0f);
            }

            stream.Position = 0;
            GVarSnapshotMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(GVarSnapshotMessage.MaxGuidLength, received.ListGuid.Length);
            Assert.Equal(GVarSnapshotMessage.MaxRecords, received.Records.Length);
            Assert.All(received.Records, record => Assert.Equal(GVarSnapshotMessage.MaxIdLength, record.VarId.Length));
        }

        [Fact]
        public void GVarSnapshot_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            GVarSnapshotMessage received = RoundTrip(new GVarSnapshotMessage { ListGuid = "a", Records = null! });

            Assert.NotNull(received.Records);
            Assert.Empty(received.Records);
        }

        [Fact]
        public void Cutscene_BoundsNameAndRoundTripsNullAsEmpty()
        {
            CutsceneMessage received = RoundTrip(new CutsceneMessage
            {
                Name = new string('x', CutsceneMessage.MaxNameLength + 20),
                ActionId = 1,
            });

            Assert.Equal(CutsceneMessage.MaxNameLength, received.Name.Length);

            CutsceneMessage empty = RoundTrip(new CutsceneMessage { Name = null!, ActionId = 2 });

            Assert.NotNull(empty.Name);
            Assert.Equal(2, empty.ActionId);
        }

        [Fact]
        public void Weather_WritesNullAndShortParamsAsZeroes()
        {
            WeatherMessage fromNull = RoundTrip(new WeatherMessage { Params = null! });

            Assert.Equal(WeatherMessage.ParamCount, fromNull.Params.Length);
            Assert.All(fromNull.Params, value => Assert.Equal(0f, value));

            WeatherMessage fromShort = RoundTrip(new WeatherMessage { Params = [0.5f] });

            Assert.Equal(WeatherMessage.ParamCount, fromShort.Params.Length);
            Assert.Equal(0.5f, fromShort.Params[0]);
            Assert.Equal(0f, fromShort.Params[1]);
        }

        [Fact]
        public void JournalEntry_RoundTripsPaths()
        {
            JournalEntryMessage received = RoundTrip(new JournalEntryMessage
            {
                Paths = ["Journal/Entries/Entry_BanditMineBrutus_Entry_Main_01", "Journal/Entries/B"],
            });

            Assert.Equal(2, received.Paths.Length);
            Assert.Equal("Journal/Entries/Entry_BanditMineBrutus_Entry_Main_01", received.Paths[0]);

            JournalEntryMessage empty = RoundTrip(new JournalEntryMessage { Paths = null! });

            Assert.NotNull(empty.Paths);
            Assert.Empty(empty.Paths);
        }

        [Fact]
        public void JournalTopic_RoundTripsPathsAndNullAsEmpty()
        {
            JournalTopicMessage received = RoundTrip(new JournalTopicMessage
            {
                TopicPath = "Journal/Topics/Topic_BanditMineBrutus",
                CategoryPath = "Journal/Categories/Completed",
            });

            Assert.Equal("Journal/Topics/Topic_BanditMineBrutus", received.TopicPath);
            Assert.Equal("Journal/Categories/Completed", received.CategoryPath);

            JournalTopicMessage empty = RoundTrip(new JournalTopicMessage { TopicPath = null!, CategoryPath = null! });

            Assert.NotNull(empty.TopicPath);
            Assert.NotNull(empty.CategoryPath);
        }

        [Fact]
        public void MapUnlock_RoundTripsGuidAndNullAsEmpty()
        {
            MapUnlockMessage received = RoundTrip(new MapUnlockMessage
            {
                Guid = "49565bc58508b364092d97d936094afc",
            });

            Assert.Equal("49565bc58508b364092d97d936094afc", received.Guid);

            MapUnlockMessage empty = RoundTrip(new MapUnlockMessage { Guid = null! });

            Assert.NotNull(empty.Guid);
        }

        [Fact]
        public void DialogueExp_RoundTripsAndBoundsPlausibility()
        {
            DialogueExpMessage received = RoundTrip(new DialogueExpMessage { Amount = 150 });

            Assert.Equal(150, received.Amount);
            Assert.True(received.IsPlausible());
            Assert.False(new DialogueExpMessage { Amount = 0 }.IsPlausible());
            Assert.False(new DialogueExpMessage { Amount = DialogueExpMessage.MaxAmount + 1 }.IsPlausible());
        }

        [Fact]
        public void DialogueItems_RoundTripsIdentityAndStacks()
        {
            DialogueItemsMessage received = RoundTrip(new DialogueItemsMessage
            {
                Identity = "DT_MiningChef#42",
                Stacks =
                [
                    new LootStack { Id = "weapon_axe_bronzeTusk", Amount = 1 },
                    new LootStack { Id = "item_kiepe_rubble", Amount = 3 },
                ],
            });

            Assert.Equal("DT_MiningChef#42", received.Identity);
            Assert.Equal(2, received.Stacks.Length);
            Assert.Equal("weapon_axe_bronzeTusk", received.Stacks[0].Id);
            Assert.Equal(1, received.Stacks[0].Amount);
            Assert.Equal("item_kiepe_rubble", received.Stacks[1].Id);
            Assert.Equal(3, received.Stacks[1].Amount);

            DialogueItemsMessage empty = RoundTrip(new DialogueItemsMessage { Identity = null!, Stacks = null! });

            Assert.NotNull(empty.Identity);
            Assert.NotNull(empty.Stacks);
            Assert.Empty(empty.Stacks);
        }

        [Fact]
        public void GVarChange_RoundTripsNullStringsAsEmpty()
        {
            GVarChangeMessage received = RoundTrip(new GVarChangeMessage
            {
                ListGuid = null!,
                VarId = null!,
                Kind = GVarChangeMessage.KindInt,
            });

            Assert.NotNull(received.ListGuid);
            Assert.NotNull(received.VarId);
        }

        /// <summary>
        /// The count bounds what this machine allocates and how far it reads, so it is clamped on read as
        /// well as on write. A sender claiming more records than fit would otherwise walk off the end of
        /// the buffer.
        /// </summary>
        [Fact]
        public void EnemySnapshot_BoundsRecordCountOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put(0f);
            writer.Put(0f);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put((ushort)index);
                writer.Put((short)0);
                writer.Put((short)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((byte)0);
                writer.Put((uint)0);
            }

            stream.Position = 0;
            EnemySnapshotMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(EnemySnapshotMessage.MaxRecords, received.Count);
        }

        /// <summary>
        /// An implausible origin places every creature in the batch somewhere invented, so the whole
        /// message is refused rather than repaired. It is unreliable and replaced many times a second, so
        /// dropping one costs nothing.
        /// </summary>
        [Fact]
        public void EnemySnapshot_RefusesAnImplausibleOrigin()
        {
            Assert.False(new EnemySnapshotMessage { OriginX = float.NaN }.IsPlausible());
            Assert.False(new EnemySnapshotMessage { OriginY = float.PositiveInfinity }.IsPlausible());
            Assert.False(new EnemySnapshotMessage { OriginX = 5e9f }.IsPlausible());
            Assert.True(new EnemySnapshotMessage { OriginX = 100f, OriginY = -100f }.IsPlausible());
        }

        /// <summary>
        /// A zeroed record must read as a living creature standing still. The dead flag latches on the
        /// receiving side, so a stray one is not something a later message can take back.
        /// </summary>
        [Fact]
        public void EnemySnapshot_DefaultRecordIsAliveAndIdle()
        {
            EnemySnapshotMessage received = RoundTrip(new EnemySnapshotMessage
            {
                Records = [default],
                Count = 1,
            });

            Assert.Equal(0, received.Records[0].Flags & EnemySnapshotMessage.FlagDead);
            Assert.Equal(0, received.Records[0].Flags & EnemySnapshotMessage.FlagMoving);
        }

        /// <summary>
        /// Handles are never reused within a session, so the sentinel has to be a value the host will
        /// never hand out. If these ever met, an unbound handle would name a real creature.
        /// </summary>
        [Fact]
        public void EnemyNetId_SentinelIsAboveEveryAssignableHandle()
        {
            Assert.True(EnemyNetId.MaxAssignable < EnemyNetId.None.Value);
            Assert.False(EnemyNetId.None.IsSome);
            Assert.True(new EnemyNetId(EnemyNetId.MaxAssignable).IsSome);
        }

        /// <summary>
        /// The one rule <see cref="MessageIds"/> exists to enforce, checked by reflection so it keeps
        /// holding as ids are added rather than only for the three that exist today.
        /// </summary>
        [Fact]
        public void MessageIds_AreDistinct()
        {
            FieldInfo[] fields = typeof(MessageIds).GetFields(BindingFlags.Public | BindingFlags.Static);
            ushort[] ids = fields.Select(field => (ushort)field.GetRawConstantValue()!).ToArray();

            Assert.NotEmpty(ids);
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
    }
}
