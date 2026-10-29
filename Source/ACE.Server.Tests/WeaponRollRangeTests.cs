using System.Collections.Generic;
using System.Globalization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers.ZoneControl;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>T11+ roll ranges on the weapon panel (2026-10-29): the "[lo-hi]" suffix on a card line. The band itself comes
    /// from ZoneStatResolver.WeaponResolveBand (live ladder), so these cases hand the formatters a band built here.</summary>
    [TestClass]
    public class WeaponRollRangeTests
    {
        private static Dictionary<int, (double Min, double Max)> Ranges(int key, double lo, double hi)
            => new Dictionary<int, (double Min, double Max)> { [key] = (lo, hi) };

        [TestMethod]
        public void Percent_UsesTheLinesOwnPrecision()
        {
            // T15 ladder rungs: Biting Strike 0.379-0.457 (line prints P0), Armor Rend 0.654-0.714 (line prints P1)
            Assert.AreEqual(" [38-46%]", AppraiseInfo.PercentRange(Ranges(ZoneStatResolver.WeaponBiteKey, 0.378571, 0.457143),
                ZoneStatResolver.WeaponBiteKey, "P0"));
            Assert.AreEqual(" [65.4-71.4%]", AppraiseInfo.PercentRange(Ranges(ZoneStatResolver.WeaponArmorRendKey, 0.654286, 0.714286),
                ZoneStatResolver.WeaponArmorRendKey, "P1"));
        }

        [TestMethod]
        public void Percent_MapsTheEndsThroughToShown()
        {
            // Rend Power: the line prints (1 + override) - 1 through a float, so the ends do too
            Assert.AreEqual(" [214-295%]", AppraiseInfo.PercentRange(Ranges(ZoneStatResolver.WeaponRendPowerKey, 2.142857, 2.95),
                ZoneStatResolver.WeaponRendPowerKey, "P0", v => (1.0f + (float)v) - 1.0));
        }

        [TestMethod]
        public void Multiplier_UsesTwoDecimals()
        {
            Assert.AreEqual(" [3.89-4.8x]", AppraiseInfo.MultiplierRange(Ranges(ZoneStatResolver.WeaponCrushKey, 3.892857, 4.8),
                ZoneStatResolver.WeaponCrushKey));
            Assert.AreEqual(" [1.75-2.1x]", AppraiseInfo.MultiplierRange(Ranges(ZoneStatResolver.WeaponSlayerKey, 1.75, 2.10),
                ZoneStatResolver.WeaponSlayerKey));
        }

        [TestMethod]
        public void NoSuffix_WhenTheCardHasNoRange()
        {
            // null = locked, or a weapon with no T11+ card at all
            Assert.AreEqual("", AppraiseInfo.MultiplierRange(null, ZoneStatResolver.WeaponSlayerKey));
            Assert.AreEqual("", AppraiseInfo.PercentRange(null, ZoneStatResolver.WeaponBiteKey, "P0"));
            // a card the record does not carry (a retail Biting Strike next to a T11+ Shield Cleaving card)
            Assert.AreEqual("", AppraiseInfo.PercentRange(Ranges(ZoneStatResolver.WeaponShieldCleaveKey, 0.5, 0.75),
                ZoneStatResolver.WeaponBiteKey, "P0"));
        }

        [TestMethod]
        public void EveryGrade_PrintsInsideItsRange()
        {
            // rounding is monotone, so a value inside the band never prints outside the printed range
            foreach (var (lo, hi, format) in new[] { (0.378571, 0.457143, "P0"), (0.654286, 0.714286, "P1"), (0.614286, 0.821429, "P0") })
            {
                var printedLo = double.Parse(AppraiseInfo.PercentNumber(lo, format), CultureInfo.InvariantCulture);
                var printedHi = double.Parse(AppraiseInfo.PercentNumber(hi, format), CultureInfo.InvariantCulture);
                for (var grade = 0; grade <= ZoneStatResolver.GradeMax; grade++)
                {
                    var v = double.Parse(AppraiseInfo.PercentNumber(ZoneStatResolver.ValueForD(lo, hi, grade), format), CultureInfo.InvariantCulture);
                    Assert.IsTrue(v >= printedLo && v <= printedHi, $"grade {grade}: {v} outside [{printedLo}-{printedHi}]");
                }
            }
        }

        [TestMethod]
        public void PercentNumber_IsPlainAscii()
        {
            foreach (var s in new[] { AppraiseInfo.PercentNumber(0.378571, "P0"), AppraiseInfo.PercentNumber(0.654286, "P1") })
                foreach (var c in s)
                    Assert.IsTrue((c >= '0' && c <= '9') || c == '.', $"'{s}' has a character the client may not draw");
        }

        [TestMethod]
        public void WeaponWithNoRecord_HasNoRanges()
        {
            var weenie = new Weenie { WeenieClassId = 424260, ClassName = "TestRollRangeSword", WeenieType = WeenieType.MeleeWeapon };
            var w = new MeleeWeapon(weenie, new ObjectGuid(0xF0004260));
            Assert.IsNull(AppraiseInfo.WeaponCardRanges(w));
        }
    }
}
