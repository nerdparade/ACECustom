using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Bank lock (2026-11-20): /banklock freezes one character's bank until /bankunlock (BankAdminCommands.cs). While
    /// PropertyBool.BankLocked is set on the character:
    /// <list type="number">
    /// <item>No banked currency changes. Every write of one goes through WorldObject or OfflinePlayer SetProperty /
    /// RemoveProperty (where the pyreal and economy ledgers hook in too), and those refuse it - so deposits, withdrawals,
    /// transfers either way, kill / quest / event income, vendor sales and purchases, fees, refunds, Powerball, the casino
    /// and emotes are all stopped, and so is any path added later. Staff commands that write inside <see cref="AdminWrite"/>
    /// (/setproperty, /bankwipe) still get through.</item>
    /// <item>The bank reads as empty to the game. The Player balance properties (Player_Bank.cs) and the InqInt64Stat emote
    /// return 0 for it, so every "can you pay" test that counts the bank sees nothing there, and nothing is handed over
    /// against a debit that would then be refused.</item>
    /// <item>The ways in that a player uses take nothing and say why: /bank and /b, /clap, using a lum token, selling to a
    /// vendor, handing an NPC an item it pays for only through the bank (<see cref="UsesBank"/>), the Paragon Collector, an
    /// enlightenment gate fee the pack's coins can't cover, and a transfer TO a locked character (refused before anything
    /// leaves the sender's bank; the sender is not told why).</item>
    /// </list>
    /// Income nobody hands anything over for (kill pyreals, Prestige Coins and luminance, quest luminance, Powerball
    /// winnings, refunds - which then say they were not paid) is simply not paid while the bank is locked. Staff reads
    /// (/getproperty, /propertydump, /bankwipe, /banklock) and the ledgers read the stored balances.
    /// </summary>
    public static class BankLock
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The eight banked currencies, in /b order, with the names /b gives them.</summary>
        public static readonly IReadOnlyList<(PropertyInt64 Property, string Name)> Currencies = new (PropertyInt64 Property, string Name)[]
        {
            (PropertyInt64.BankedPyreals, "Pyreals"),
            (PropertyInt64.BankedLuminance, "Luminance"),
            (PropertyInt64.BankedLegendaryKeys, "Legendary Keys"),
            (PropertyInt64.BankedMythicalKeys, "Mythical Keys"),
            (PropertyInt64.BankedEnlightenedCoins, "Enlightened Coins"),
            (PropertyInt64.BankedWeaklyEnlightenedCoins, "Weakly Enlightened Coins"),
            (PropertyInt64.BankedPrestigeCoins, "Prestige Coins"),
            (PropertyInt64.BankedEnlightenmentTokens, "Enlightenment Tokens"),
        };

        /// <summary>True for the eight banked currencies.</summary>
        public static bool IsBankProperty(PropertyInt64 property)
        {
            switch (property)
            {
                case PropertyInt64.BankedPyreals:
                case PropertyInt64.BankedLuminance:
                case PropertyInt64.BankedLegendaryKeys:
                case PropertyInt64.BankedMythicalKeys:
                case PropertyInt64.BankedEnlightenedCoins:
                case PropertyInt64.BankedWeaklyEnlightenedCoins:
                case PropertyInt64.BankedPrestigeCoins:
                case PropertyInt64.BankedEnlightenmentTokens:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True when staff locked this character's bank. Online or offline.</summary>
        public static bool IsLocked(IPlayer character) => character != null && (character.GetProperty(PropertyBool.BankLocked) ?? false);

        // ---- staff writes ---------------------------------------------------------------------------------------------

        // Chat commands run on the world thread, and a staff write is made in the command itself, so a count per thread is
        // enough: a write on any other thread meanwhile is still refused.
        [ThreadStatic]
        private static int adminWriteDepth;

        /// <summary>
        /// Opens a staff write: until it is disposed, writes on this thread reach a locked bank. /setproperty and /bankwipe
        /// write inside one, so staff can still set or clear a locked balance. Use it in a using statement.
        /// </summary>
        public static IDisposable AdminWrite()
        {
            adminWriteDepth++;
            return new AdminWriteScope();
        }

        /// <summary>True while a staff write is open on this thread.</summary>
        internal static bool AdminWriteOpen => adminWriteDepth > 0;

        private sealed class AdminWriteScope : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                    return;

                disposed = true;
                adminWriteDepth--;
            }
        }

        // ---- the freeze -----------------------------------------------------------------------------------------------

        /// <summary>
        /// True when a write of <paramref name="property"/> on <paramref name="character"/> must not happen: it is a banked
        /// currency, that bank is locked, and no staff write is open on this thread. Called first in the property writers,
        /// so anything that is not a banked currency returns at the first test.
        /// </summary>
        public static bool RefusesWrite(IPlayer character, PropertyInt64 property)
        {
            if (!IsBankProperty(property) || adminWriteDepth > 0 || !IsLocked(character))
                return false;

            NoteRefused(character, property);
            return true;
        }

        private static readonly ConcurrentDictionary<uint, DateTime> lastRefusalLogged = new ConcurrentDictionary<uint, DateTime>();

        /// <summary>One log line per character a minute, so a hunt on a locked character does not flood the log.</summary>
        private static void NoteRefused(IPlayer character, PropertyInt64 property)
        {
            var id = character.Guid.Full;
            var now = DateTime.UtcNow;

            if (lastRefusalLogged.TryGetValue(id, out var last) && now - last < TimeSpan.FromMinutes(1))
                return;

            lastRefusalLogged[id] = now;
            log.Info($"[BankLock] {character.Name} (0x{id:X8}): a change to {property} was refused - the bank is locked. Further refusals for this character are not logged for a minute.");
        }

        /// <summary>
        /// True when a locked player should keep an item rather than hand it to an NPC with this Give emote set, because the
        /// set pays for it through the bank: an Int64 stat step on a banked currency (the lum token NPCs), or a luminance award
        /// (every luminance award is banked, Player_Luminance.AddLuminance) that is the only reward. With an item, XP, skill,
        /// credit, spell, title, treasure or contract as well - or a branch (Goto, an Inq test) to steps not looked at here -
        /// the hand-in goes ahead, and only its luminance is not paid. Quest stamps are bookkeeping, not a reward.
        /// </summary>
        public static bool UsesBank(PropertiesEmote emoteSet)
        {
            if (emoteSet?.PropertiesEmoteAction == null)
                return false;

            var awardsLuminance = false;
            var otherReward = false;

            foreach (var action in emoteSet.PropertiesEmoteAction)
            {
                var type = (EmoteType)action.Type;

                switch (type)
                {
                    case EmoteType.IncrementInt64Stat:
                    case EmoteType.DecrementInt64Stat:
                    case EmoteType.SetInt64Stat:
                        if (action.Stat.HasValue && IsBankProperty((PropertyInt64)action.Stat.Value))
                            return true;
                        break;

                    case EmoteType.AwardLuminance:
                        awardsLuminance = true;
                        break;

                    default:
                        if (IsOtherReward(type))
                            otherReward = true;
                        break;
                }
            }

            return awardsLuminance && !otherReward;
        }

        /// <summary>
        /// A step that makes a hand-in worth doing without its luminance: an item, XP, skill, credit, spell, title, treasure or
        /// contract - or a branch to steps UsesBank does not follow (Goto, and every Inq test).
        /// </summary>
        private static bool IsOtherReward(EmoteType type)
        {
            switch (type)
            {
                case EmoteType.Give:
                case EmoteType.AwardXP:
                case EmoteType.AwardNoShareXP:
                case EmoteType.AwardLevelProportionalXP:
                case EmoteType.AwardLevelProportionalSkillXP:
                case EmoteType.AwardSkillXP:
                case EmoteType.AwardSkillPoints:
                case EmoteType.AwardTrainingCredits:
                case EmoteType.TeachSpell:
                case EmoteType.AddCharacterTitle:
                case EmoteType.CreateTreasure:
                case EmoteType.AddContract:
                case EmoteType.Goto:
                    return true;

                default:
                    return type.ToString().StartsWith("Inq", StringComparison.Ordinal);
            }
        }

        // ---- staff commands -------------------------------------------------------------------------------------------

        /// <summary>The stored balance of each banked currency, in /b order: what is really there, locked or not.</summary>
        public static List<(string Name, PropertyInt64 Property, long Amount)> ReadStored(IPlayer character)
        {
            var balances = new List<(string Name, PropertyInt64 Property, long Amount)>(Currencies.Count);

            foreach (var (property, name) in Currencies)
                balances.Add((name, property, character.GetProperty(property) ?? 0));

            return balances;
        }

        /// <summary>"Pyreals 1,234; Luminance 0; ..." for staff output and the audit channel.</summary>
        public static string Describe(IEnumerable<(string Name, PropertyInt64 Property, long Amount)> balances)
        {
            return string.Join("; ", balances.Select(b => $"{b.Name} {b.Amount:N0}"));
        }

        /// <summary>Locks or unlocks a bank and saves the character. An online player's coin total is redrawn, as it counts the bank.</summary>
        public static void SetLocked(IPlayer character, bool locked)
        {
            if (locked)
                character.SetProperty(PropertyBool.BankLocked, true);
            else
                character.RemoveProperty(PropertyBool.BankLocked);

            if (character is Player online)
                online.RefreshCoinValueAfterBankChange();

            SaveNow(character);
        }

        /// <summary>
        /// Sets every banked currency of <paramref name="character"/> to 0, locked bank or not, clears the part of a Prestige
        /// Coin in progress, and saves. Returns the balances as they were. Run it on the world thread (a chat command).
        /// </summary>
        public static List<(string Name, PropertyInt64 Property, long Amount)> Wipe(IPlayer character, out double coinFraction)
        {
            var before = ReadStored(character);
            coinFraction = character.GetProperty(PropertyFloat.PrestigeCoinFraction) ?? 0;

            using (AdminWrite())
            {
                foreach (var (_, property, amount) in before)
                {
                    if (amount != 0)
                        character.SetProperty(property, 0);
                }
            }

            if (coinFraction != 0)
                character.RemoveProperty(PropertyFloat.PrestigeCoinFraction);

            if (character is Player online)
            {
                foreach (var (_, property, amount) in before)
                {
                    if (amount != 0)
                        online.Session?.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(online, property, 0));
                }

                online.RefreshCoinValueAfterBankChange();
            }

            SaveNow(character);

            return before;
        }

        /// <summary>Saves the character now: an online player through its own save, an offline one by its stored record.</summary>
        public static void SaveNow(IPlayer character)
        {
            if (character is Player online)
                online.SavePlayerToDatabase();
            else if (character is OfflinePlayer offline)
                offline.SaveBiotaToDatabase();
        }
    }
}
