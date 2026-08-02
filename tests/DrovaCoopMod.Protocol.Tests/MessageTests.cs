using System.Reflection;
using Drova_Modding_API.Systems.Networking;
using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// What the wire contract has to hold to: fields come back in the order they went out, and a name from
    /// a stranger is bounded no matter what the sender claims.
    /// </summary>
    public class MessageTests
    {
        [Fact]
        public void Handshake_RoundTripsVersionAndName()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 7,
                PlayerName = "Nemeton",
                ModSetHash = 0xDEADBEEF,
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(7, received.ProtocolVersion);
            Assert.Equal("Nemeton", received.PlayerName);
            Assert.Equal(0xDEADBEEF, received.ModSetHash);
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

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HandshakeResult_RoundTrips(bool accepted)
        {
            HandshakeResultMessage sent = new()
            {
                Accepted = accepted,
                HostProtocolVersion = 42,
                AssignedPlayerId = accepted ? new PlayerId(3) : PlayerId.None,
                Refusal = accepted ? HandshakeRefusal.None : HandshakeRefusal.SessionFull,
            };

            HandshakeResultMessage received = RoundTrip(sent);

            Assert.Equal(accepted, received.Accepted);
            Assert.Equal(42, received.HostProtocolVersion);
            Assert.Equal(sent.AssignedPlayerId, received.AssignedPlayerId);
            Assert.Equal(sent.Refusal, received.Refusal);
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
                FriendlyFire = friendlyFire,
            });

            Assert.Equal(modSetMatches, received.ModSetMatches);
            Assert.Equal(friendlyFire, received.FriendlyFire);
        }

        /// <summary>
        /// The default has to be the safe one. A peer whose message never set the field - an older build,
        /// a truncated read, a default-constructed message - must not have friendly fire switched on by
        /// omission, because that failure is silent until somebody is killed by a friend.
        /// </summary>
        [Fact]
        public void HandshakeResult_DefaultsFriendlyFireOff()
        {
            Assert.False(default(HandshakeResultMessage).FriendlyFire);
            Assert.False(RoundTrip(new HandshakeResultMessage { Accepted = true }).FriendlyFire);
        }

        /// <summary>
        /// The mid-session rule change carries the same answer the handshake does, in both directions.
        /// Two messages state this rule, and they are read by different code paths on different machines;
        /// a session where the door and the update disagree is one where a player can be killed by
        /// somebody they were told could not hurt them.
        /// </summary>
        [Theory]
        [InlineData(true, 5)]
        [InlineData(false, 100)]
        public void SessionRules_RoundTripsEveryRule(bool friendlyFire, byte recovery)
        {
            SessionRulesMessage received = RoundTrip(new SessionRulesMessage
            {
                FriendlyFire = friendlyFire,
                RecoveryHealthPercent = recovery,
            });

            Assert.Equal(friendlyFire, received.FriendlyFire);
            Assert.Equal(recovery, received.RecoveryHealthPercent);
        }

        /// <summary>
        /// The two messages that state the session's rules have to state them identically. One is read at
        /// the door and the other whenever they change, by different code on different machines, so a
        /// field carried by one and dropped by the other is a session where joining and staying disagree.
        /// </summary>
        [Fact]
        public void HandshakeResult_CarriesTheSameRulesAsARuleChange()
        {
            HandshakeResultMessage received = RoundTrip(new HandshakeResultMessage
            {
                Accepted = true,
                FriendlyFire = true,
                RecoveryHealthPercent = 5,
            });

            Assert.True(received.FriendlyFire);
            Assert.Equal(5, received.RecoveryHealthPercent);
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

        [Fact]
        public void DamageReport_RoundTripsPoiseAndTheWound()
        {
            DamageReportMessage sent = new()
            {
                Attacker = new PlayerId(1),
                Victim = new PlayerId(2),
                Amount = 40,
                PoiseDamage = 12.5f,
                AppliesBleed = 1,
            };

            DamageReportMessage received = RoundTrip(sent);

            Assert.Equal(sent.PoiseDamage, received.PoiseDamage);
            Assert.Equal(sent.AppliesBleed, received.AppliesBleed);
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

        [Theory]
        [InlineData(KnowledgeMessage.KindRecipe, "b1946ac92492d2347c6235b4d2611184")]
        [InlineData(KnowledgeMessage.KindWriting, "letter_forge_notes")]
        public void Knowledge_RoundTripsWhatWasLearned(byte kind, string id)
        {
            KnowledgeMessage sent = new()
            {
                Learner = new PlayerId(2),
                Kind = kind,
                Id = id,
            };

            KnowledgeMessage received = RoundTrip(sent);

            Assert.Equal(sent.Learner, received.Learner);
            Assert.Equal(kind, received.Kind);
            Assert.Equal(id, received.Id);
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

        [Fact]
        public void ItemUse_RoundTripsTheUserAndTheItem()
        {
            ItemUseMessage sent = new()
            {
                User = new PlayerId(2),
                ItemId = "Flow_Fireball",
                Sequence = 7,
                Released = 1,
            };

            ItemUseMessage received = RoundTrip(sent);

            Assert.Equal(sent.User, received.User);
            Assert.Equal(sent.ItemId, received.ItemId);
            Assert.Equal(sent.Sequence, received.Sequence);
            Assert.Equal(sent.Released, received.Released);
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
                DirectionX = -0.7071f,
                DirectionY = 0.7071f,
                LookX = 1f,
                LookY = -0.0001f,
                MoveMode = 2,
                InteractionMode = 1,
                Action = 3,
                ActionSequence = 200,
                ActionReleased = 1,

                // Two adjacent fields of one type, so they are given different values: equal ones survive
                // a Read that returns them the wrong way round, and a health bar reading as a fraction of
                // the wrong maximum is a player who looks fine while dying.
                Health = 37,
                MaxHealth = 240,

                // Four ushorts in a row now, all distinct: swapping health with stamina would show up as
                // a guard that breaks at the wrong moment rather than as anything obviously broken.
                Stamina = 12,
                MaxStamina = 95,
                Downed = 1,
            };

            PlayerStateMessage received = RoundTrip(sent);

            Assert.Equal(sent.PlayerId, received.PlayerId);
            Assert.Equal(sent.X, received.X);
            Assert.Equal(sent.Y, received.Y);
            Assert.Equal(sent.DirectionX, received.DirectionX);
            Assert.Equal(sent.DirectionY, received.DirectionY);
            Assert.Equal(sent.LookX, received.LookX);
            Assert.Equal(sent.LookY, received.LookY);
            Assert.Equal(sent.MoveMode, received.MoveMode);
            Assert.Equal(sent.InteractionMode, received.InteractionMode);
            Assert.Equal(sent.Action, received.Action);
            Assert.Equal(sent.ActionSequence, received.ActionSequence);
            Assert.Equal(sent.ActionReleased, received.ActionReleased);
            Assert.Equal(sent.Health, received.Health);
            Assert.Equal(sent.MaxHealth, received.MaxHealth);
            Assert.Equal(sent.Stamina, received.Stamina);
            Assert.Equal(sent.MaxStamina, received.MaxStamina);
            Assert.Equal(sent.Downed, received.Downed);
        }

        /// <summary>
        /// On its feet is what a message that never said otherwise has to mean. The flag is the only thing
        /// keeping a body upright, so a default or truncated read that came out non-zero would drop a
        /// player who is fine to the floor and leave them there - every subsequent packet says the same.
        /// </summary>
        [Fact]
        public void PlayerState_DefaultsToStanding()
        {
            Assert.Equal(0, default(PlayerStateMessage).Downed);
            Assert.Equal(0, RoundTrip(default(PlayerStateMessage)).Downed);
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
                DirectionX = -1f,
                DirectionY = 1f,
                LookX = 0.7071f,
                LookY = -0.7071f,
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
            Assert.False(new PlayerStateMessage { DirectionX = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { DirectionY = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookX = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookY = bad }.IsPlausible());
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
            Assert.False(new PlayerStateMessage { DirectionX = PlayerStateMessage.MaxAxis * 2f }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookY = -PlayerStateMessage.MaxAxis * 2f }.IsPlausible());

            // The limits themselves are accepted; a bound that refuses its own edge is a bound nobody can
            // reason about.
            Assert.True(new PlayerStateMessage { X = PlayerStateMessage.MaxCoordinate }.IsPlausible());
            Assert.True(new PlayerStateMessage { LookX = -PlayerStateMessage.MaxAxis }.IsPlausible());
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

        [Fact]
        public void EnemyRelease_RoundTripsHandleAndReason()
        {
            EnemyReleaseMessage received = RoundTrip(new EnemyReleaseMessage
            {
                NetId = new EnemyNetId(9),
                Reason = EnemyReleaseMessage.ReasonHostLostBody,
            });

            Assert.Equal(new EnemyNetId(9), received.NetId);
            Assert.Equal(EnemyReleaseMessage.ReasonHostLostBody, received.Reason);
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
        public void EnemyHealth_RoundTripsHandleAndFigures()
        {
            EnemyHealthMessage received = RoundTrip(new EnemyHealthMessage
            {
                NetId = new EnemyNetId(77),
                Health = 5,
                MaxHealth = 250,
            });

            Assert.Equal(new EnemyNetId(77), received.NetId);
            Assert.Equal(5, received.Health);
            Assert.Equal(250, received.MaxHealth);
        }

        [Fact]
        public void EnemyDamage_RoundTripsEverythingTheHostNeeds()
        {
            EnemyDamageMessage received = RoundTrip(new EnemyDamageMessage
            {
                Attacker = new PlayerId(2),
                NetId = new EnemyNetId(11),
                Sequence = 900_000,
                Amount = 64,
                PoiseDamage = 12.5f,
                AppliesBleed = 1,
                DamageType = DamageReportMessage.TypeMagical,
            });

            Assert.Equal(new PlayerId(2), received.Attacker);
            Assert.Equal(new EnemyNetId(11), received.NetId);
            Assert.Equal(900_000u, received.Sequence);
            Assert.Equal(64, received.Amount);
            Assert.Equal(12.5f, received.PoiseDamage);
            Assert.Equal(1, received.AppliesBleed);
            Assert.Equal(DamageReportMessage.TypeMagical, received.DamageType);
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

        [Fact]
        public void EnemyGraveyard_RoundTripsGuidsAndTheMoreFlag()
        {
            EnemyGraveyardMessage received = RoundTrip(new EnemyGraveyardMessage
            {
                Guids = ["a", "b", "c"],
                More = 1,
            });

            Assert.Equal(3, received.Guids.Length);
            Assert.Equal("b", received.Guids[1]);
            Assert.Equal(1, received.More);
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

        [Fact]
        public void EnemyAlive_RoundTripsGuids()
        {
            EnemyAliveMessage received = RoundTrip(new EnemyAliveMessage
            {
                Guids = ["a", "b", "c"],
            });

            Assert.Equal(3, received.Guids.Length);
            Assert.Equal("b", received.Guids[1]);
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

        [Fact]
        public void EnemySnapshot_RoundTripsOriginAndRecords()
        {
            EnemySnapshotMessage sent = new()
            {
                OriginX = 1234.5f,
                OriginY = -678.25f,
                Records =
                [
                    new EnemySnapshotMessage.Record
                    {
                        NetId = new EnemyNetId(3),
                        OffsetX = -400,
                        OffsetY = 900,
                        LookAngle = 64,
                        MoveAngle = 192,
                        Flags = EnemySnapshotMessage.FlagMoving | EnemySnapshotMessage.FlagInCombat,
                        ModeAndTarget = EnemySnapshotMessage.NoValue,
                        Action = 2,
                        ActionSequence = 200,
                        ActionHash = 0xDEADBEEF,
                    },
                ],
                Count = 1,
            };

            EnemySnapshotMessage received = RoundTrip(sent);

            Assert.Equal(1234.5f, received.OriginX);
            Assert.Equal(-678.25f, received.OriginY);
            Assert.Equal(1, received.Count);
            Assert.Equal(new EnemyNetId(3), received.Records[0].NetId);
            Assert.Equal(-400, received.Records[0].OffsetX);
            Assert.Equal(900, received.Records[0].OffsetY);
            Assert.Equal(64, received.Records[0].LookAngle);
            Assert.Equal(2, received.Records[0].Action);
            Assert.Equal(200, received.Records[0].ActionSequence);
            Assert.Equal(0xDEADBEEF, received.Records[0].ActionHash);
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

        private static T RoundTrip<T>(in T message) where T : struct, INetMessage
        {
            using MemoryStream stream = new();
            message.Write(new BufferWriter(stream));

            stream.Position = 0;
            T received = default;
            received.Read(new BufferReader(stream));

            return received;
        }
    }
}
