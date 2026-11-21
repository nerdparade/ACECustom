using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bank lock (2026-11-20, /banklock): which properties are the bank, which NPC emote sets pay only through the bank (a
    /// locked player keeps the item instead of handing it over for nothing), the staff write that still reaches a locked
    /// bank, the property id, and /bankwipe confirm only wiping the character its staff member was just shown.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class BankLockTests
    {
        private static readonly PropertyInt64[] Balances =
        {
            PropertyInt64.BankedPyreals,
            PropertyInt64.BankedLuminance,
            PropertyInt64.BankedLegendaryKeys,
            PropertyInt64.BankedMythicalKeys,
            PropertyInt64.BankedEnlightenedCoins,
            PropertyInt64.BankedWeaklyEnlightenedCoins,
            PropertyInt64.BankedPrestigeCoins,
            PropertyInt64.BankedEnlightenmentTokens,
        };

        [TestMethod]
        public void IsBankProperty_TheEightBalances()
        {
            foreach (var balance in Balances)
                Assert.IsTrue(BankLock.IsBankProperty(balance), balance.ToString());
        }

        /// <summary>Nothing else is frozen: XP, available luminance and the saved XP overflow keep working on a locked character.</summary>
        [TestMethod]
        public void IsBankProperty_NothingElse()
        {
            var others = Enum.GetValues(typeof(PropertyInt64)).Cast<PropertyInt64>().Where(p => !Balances.Contains(p)).ToList();

            Assert.IsTrue(others.Contains(PropertyInt64.AvailableExperience));
            Assert.IsTrue(others.Contains(PropertyInt64.AvailableLuminance));
            Assert.IsTrue(others.Contains(PropertyInt64.OverflowExperience));

            foreach (var other in others)
                Assert.IsFalse(BankLock.IsBankProperty(other), other.ToString());
        }

        /// <summary>The list staff commands show and wipe: the eight balances, once each, in /b order.</summary>
        [TestMethod]
        public void Currencies_AreTheEightBalancesInBankOrder()
        {
            CollectionAssert.AreEqual(Balances, BankLock.Currencies.Select(c => c.Property).ToArray());
            Assert.AreEqual("Pyreals", BankLock.Currencies[0].Name);
            Assert.AreEqual("Enlightenment Tokens", BankLock.Currencies[7].Name);
        }

        /// <summary>Luminance is banked, so a hand-in whose only reward is luminance would go for nothing. A quest stamp is not a reward.</summary>
        [TestMethod]
        public void UsesBank_LuminanceAsTheOnlyReward()
        {
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.AwardLuminance))));
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.StampQuest), Step(EmoteType.AwardLuminance))));
        }

        /// <summary>With another reward, or a branch to steps not looked at, the hand-in goes ahead and only the luminance is not paid.</summary>
        [TestMethod]
        public void UsesBank_LuminanceBesideAnotherRewardOrABranch()
        {
            Assert.IsFalse(BankLock.UsesBank(Set(Step(EmoteType.AwardLuminance), Step(EmoteType.Give))));
            Assert.IsFalse(BankLock.UsesBank(Set(Step(EmoteType.AwardXP), Step(EmoteType.AwardLuminance))));
            Assert.IsFalse(BankLock.UsesBank(Set(Step(EmoteType.AwardLuminance), Step(EmoteType.InqQuest))));
            Assert.IsFalse(BankLock.UsesBank(Set(Step(EmoteType.AwardLuminance), Step(EmoteType.Goto))));
        }

        /// <summary>The lum token NPCs pay with IncrementInt64Stat on BankedLuminance; any Int64 stat step on a balance counts, whatever else the set gives.</summary>
        [TestMethod]
        public void UsesBank_Int64StatStepsOnABalance()
        {
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.IncrementInt64Stat, PropertyInt64.BankedLuminance))));
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.DecrementInt64Stat, PropertyInt64.BankedPrestigeCoins))));
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.SetInt64Stat, PropertyInt64.BankedPyreals))));
            Assert.IsTrue(BankLock.UsesBank(Set(Step(EmoteType.Give), Step(EmoteType.IncrementInt64Stat, PropertyInt64.BankedLuminance))));
        }

        [TestMethod]
        public void UsesBank_OtherStepsDoNot()
        {
            Assert.IsFalse(BankLock.UsesBank(Set(Step(EmoteType.Give), Step(EmoteType.IncrementInt64Stat, PropertyInt64.AvailableExperience))));
            Assert.IsFalse(BankLock.UsesBank(Set()));
            Assert.IsFalse(BankLock.UsesBank(null));
        }

        /// <summary>A staff write is open until its last scope is closed, and closing one twice does not close another.</summary>
        [TestMethod]
        public void AdminWrite_NestsAndUnwinds()
        {
            Assert.IsFalse(BankLock.AdminWriteOpen);

            var outer = BankLock.AdminWrite();
            var inner = BankLock.AdminWrite();
            Assert.IsTrue(BankLock.AdminWriteOpen);

            inner.Dispose();
            inner.Dispose();
            Assert.IsTrue(BankLock.AdminWriteOpen);

            outer.Dispose();
            Assert.IsFalse(BankLock.AdminWriteOpen);
        }

        /// <summary>PropertyBool.BankLocked is 56300, and 56300 is free in every other property enum.</summary>
        [TestMethod]
        public void BankLocked_IdIsFreeInEveryOtherPropertyEnum()
        {
            Assert.AreEqual(56300, (int)PropertyBool.BankLocked);

            foreach (var type in new[] { typeof(PropertyInt), typeof(PropertyInt64), typeof(PropertyFloat), typeof(PropertyString), typeof(PropertyDataId), typeof(PropertyInstanceId) })
            {
                var used = Enum.GetValues(type).Cast<object>().Any(v => Convert.ToInt64(v) == 56300);
                Assert.IsFalse(used, $"56300 is used in {type.Name}");
            }
        }

        /// <summary>/bankwipe confirm: only the character the same staff member was shown, once, and only while the preview is good.</summary>
        [TestMethod]
        public void BankWipePreviews_ConfirmOnlyThePreviewedCharacterOnce()
        {
            var now = new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);
            BankWipePreviews.Remember(0x50000011, 0x50000100, now);

            Assert.IsFalse(BankWipePreviews.TryConsume(0x50000011, 0x50000101, now));   // a mistyped id: the next character
            Assert.IsFalse(BankWipePreviews.TryConsume(0x50000012, 0x50000100, now));   // another staff member
            Assert.IsTrue(BankWipePreviews.TryConsume(0x50000011, 0x50000100, now.AddMinutes(1)));
            Assert.IsFalse(BankWipePreviews.TryConsume(0x50000011, 0x50000100, now.AddMinutes(1)));   // used up
        }

        [TestMethod]
        public void BankWipePreviews_Expire()
        {
            var now = new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);
            BankWipePreviews.Remember(0x50000021, 0x50000200, now);

            Assert.IsFalse(BankWipePreviews.TryConsume(0x50000021, 0x50000200, now + BankWipePreviews.Lifetime + TimeSpan.FromSeconds(1)));
        }

        /// <summary>A new preview replaces the last one: only the character shown most recently can be wiped.</summary>
        [TestMethod]
        public void BankWipePreviews_NewPreviewReplacesTheLast()
        {
            var now = new DateTime(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);
            BankWipePreviews.Remember(0x50000031, 0x50000300, now);
            BankWipePreviews.Remember(0x50000031, 0x50000301, now);

            Assert.IsFalse(BankWipePreviews.TryConsume(0x50000031, 0x50000300, now));
            Assert.IsTrue(BankWipePreviews.TryConsume(0x50000031, 0x50000301, now));
        }

        private static PropertiesEmote Set(params PropertiesEmoteAction[] steps)
        {
            var set = new PropertiesEmote { Category = EmoteCategory.Give, Probability = 1, Quest = "" };
            foreach (var step in steps)
                set.PropertiesEmoteAction.Add(step);
            return set;
        }

        private static PropertiesEmoteAction Step(EmoteType type, PropertyInt64? stat = null)
        {
            return new PropertiesEmoteAction
            {
                Type = (uint)type,
                Delay = 0,
                Extent = 0,
                Message = "",
                TestString = "",
                Stat = stat.HasValue ? (int?)(int)stat.Value : null,
                Amount64 = 1,
            };
        }
    }
}
