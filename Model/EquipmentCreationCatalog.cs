using System.Collections.Generic;
using System.Linq;

namespace CommandCenter.Model
{
    // Which potential line set an item gets when its command is built - mirrors the
    // "PotentialArray" tab of "MS - Equip Creation v2.1.xlsx" (None = plain "/create <id>" with no
    // potential at all, e.g. Pockets, Titles, Totems, consumables).
    public enum PotentialProfile
    {
        None,
        Weapon,
        Gloves,
        Emblem,
        Other
    }

    // One row in the "Existing Equipment Creation" list (Launch panel) - a single item the game's
    // /create or /createex2 debug command can spawn. Category is only filled in where the sheet
    // actually has one (Armor is grouped by class); Detail is an optional secondary label such as
    // a weapon's type.
    public sealed class EquipmentItemDefinition
    {
        public EquipmentItemDefinition(string name, string itemId, PotentialProfile profile, string? category = null, string? detail = null)
        {
            Name = name;
            ItemId = itemId;
            Profile = profile;
            Category = category;
            Detail = detail;
        }

        public string Name { get; }

        // Usually just the numeric item id; a few consumables carry a quantity after it
        // ("4001872 2"), kept exactly as the sheet has it since it's pasted straight into the command.
        public string ItemId { get; }
        public PotentialProfile Profile { get; }
        public string? Category { get; }
        public string? Detail { get; }

        // The exact line that gets saved into the debug command list (cmd_uidebug.txt): the create
        // command, then the item's name in parentheses after a space, e.g.
        // "/create 1162081 (Cursed Blue Spellbook)" - so every line in the list says what it spawns.
        public string Command => $"{EquipmentCreationCatalog.BuildCommand(this)} ({Name})";
    }

    // One tab of the equipment list (one per relevant tab of the source sheet).
    public sealed class EquipmentTabDefinition
    {
        public EquipmentTabDefinition(string title, IReadOnlyList<EquipmentItemDefinition> items)
        {
            Title = title;
            Items = items;
        }

        public string Title { get; }
        public IReadOnlyList<EquipmentItemDefinition> Items { get; }
    }

    // Static data from "MS - Equip Creation v2.1.xlsx" (ItemCreation/PrimaryWeapon/SecondaryWeapon/
    // Armor/Emblem/Pocket/PotentialArray tabs) behind the Launch panel's "Existing Equipment
    // Creation" checkbox. Commands are built exactly the way the sheet's "Copy & Paste" column does
    // for its default selection (Legendary, 3 potential lines, 3 bonus potential lines).
    public static class EquipmentCreationCatalog
    {
        // Sheet defaults: Grade = Legendary (4), Potential Lines = 3, Bonus Potential Lines = 3.
        public const int DefaultGrade = 4;
        public const int DefaultPotentialLines = 3;
        public const int DefaultBonusPotentialLines = 3;

        // Potential / bonus potential ids per profile - the sheet's PotentialArray tab.
        private static readonly int[] OtherPotential = { 40043, 40043, 40043 };
        private static readonly int[] WeaponPotential = { 42053, 42053, 40603 };
        private static readonly int[] WeaponBonusPotential = { 42053, 42053, 42053 };
        private static readonly int[] GlovesPotential = { 40056, 40056, 40056 };
        private static readonly int[] EmblemPotential = { 42053, 42053, 40292 };
        private static readonly int[] EmblemBonusPotential = { 42053, 42053, 42053 };

        public static IReadOnlyList<EquipmentTabDefinition> Tabs { get; } = BuildTabs();

        public static string BuildCommand(EquipmentItemDefinition item)
        {
            if (item.Profile == PotentialProfile.None)
            {
                return $"/create {item.ItemId}";
            }

            (int[] potential, int[] bonus) = item.Profile switch
            {
                PotentialProfile.Weapon => (WeaponPotential, WeaponBonusPotential),
                PotentialProfile.Gloves => (GlovesPotential, GlovesPotential),
                PotentialProfile.Emblem => (EmblemPotential, EmblemBonusPotential),
                _ => (OtherPotential, OtherPotential)
            };

            return $"/createex2 {item.ItemId} {DefaultGrade} {DefaultPotentialLines} " +
                   $"{string.Join(" ", potential.Take(DefaultPotentialLines))} " +
                   $"{DefaultBonusPotentialLines} {string.Join(" ", bonus.Take(DefaultBonusPotentialLines))}";
        }

        private static EquipmentItemDefinition Item(string name, string itemId, PotentialProfile profile, string? category = null, string? detail = null) =>
            new(name, itemId, profile, category, detail);

        private static IReadOnlyList<EquipmentTabDefinition> BuildTabs() => new List<EquipmentTabDefinition>
        {
            new("Primary Weapon", new List<EquipmentItemDefinition>
            {
                Item("Destiny Shining Rod (Luminous)", "1212142", PotentialProfile.Weapon, null, "Shining Rod"),
                Item("Destiny Soul Shooter (AngelicBuster)", "1222135", PotentialProfile.Weapon, null, "Soul Shooter"),
                Item("Destiny Desperado (DemonAvenger)", "1232135", PotentialProfile.Weapon, null, "Desperado"),
                Item("Destiny Energy Chain (XenonPirate)", "1242160", PotentialProfile.Weapon, null, "Whip Blade"),
                Item("Destiny Energy Chain (XenonThief)", "1242161", PotentialProfile.Weapon, null, "Whip Blade"),
                Item("Arcane Umbra Scepter (BeastTamer)", "1252098", PotentialProfile.Weapon, null, "Beast Tamer Scepter"),
                Item("Destiny Psy-limiter (Kinesis)", "1262065", PotentialProfile.Weapon, null, "Psy-limiter"),
                Item("Destiny Saber (1HandSword)", "1302373", PotentialProfile.Weapon, null, "1-handed Sword"),
                Item("Destiny Axe (1HandAxe)", "1312226", PotentialProfile.Weapon, null, "1-handed Axe"),
                Item("Destiny Hammer", "1322279", PotentialProfile.Weapon, null, "1-handed Mace"),
                Item("Destiny Dagger (Dagger)", "1332303", PotentialProfile.Weapon, null, "Dagger"),
                Item("Destiny Cane (CaneThief)", "1362161", PotentialProfile.Weapon, null, "Cane"),
                Item("Destiny Wand (Wand)", "1372251", PotentialProfile.Weapon, null, "Wand"),
                Item("Destiny Staff (Staff)", "1382287", PotentialProfile.Weapon, null, "Staff"),
                Item("Destiny Two-handed Sword", "1402290", PotentialProfile.Weapon, null, "2-handed Sword"),
                Item("Destiny Two-Handed Axe", "1412197", PotentialProfile.Weapon, null, "2-handed Axe"),
                Item("Destiny Two-handed Hammer", "1422208", PotentialProfile.Weapon, null, "2-handed Mace"),
                Item("Destiny Spear", "1432240", PotentialProfile.Weapon, null, "Spear"),
                Item("Destiny Polearm", "1442299", PotentialProfile.Weapon, null, "Polearm"),
                Item("Destiny Bow", "1452285", PotentialProfile.Weapon, null, "Bow"),
                Item("Destiny Crossbow", "1462268", PotentialProfile.Weapon, null, "Crossbow"),
                Item("Destiny Claw", "1482245", PotentialProfile.Weapon, null, "Claw"),
                Item("Destiny Guards", "1472288", PotentialProfile.Weapon, null, "Knuckle"),
                Item("Destiny Pistol", "1492259", PotentialProfile.Weapon, null, "Gun"),
                Item("Destiny Dual Bowguns", "1522164", PotentialProfile.Weapon, null, "Dual Bowguns"),
                Item("Destiny Blast Cannon", "1532170", PotentialProfile.Weapon, null, "Hand Cannon"),
                Item("Destiny Katana (Hayato)", "1542149", PotentialProfile.Weapon, null, "Katana"),
                Item("Destiny Spirit Walker Fan (Kanna)", "1254029", PotentialProfile.Weapon, null, "Fan"),
                Item("Destiny Ellaha", "1582058", PotentialProfile.Weapon, null, "Arm Cannon"),
                Item("Destiny Chain (Cadena)", "1272054", PotentialProfile.Weapon, null, "Chain"),
                Item("Destiny Lucent Gauntlet (Illium)", "1282054", PotentialProfile.Weapon, null, "Lucent Gauntlet"),
                Item("Destiny Ancient Bow (Pathfinder)", "1592049", PotentialProfile.Weapon, null, "AncientBow"),
                Item("Destiny Ritual Fan (Hoyoung)", "1292050", PotentialProfile.Weapon, null, "RitualFan"),
                Item("Destiny Bladecaster (Adele)", "1213053", PotentialProfile.Weapon, null, "Bladecaster"),
                Item("Destiny Whispershot (Kain)", "1214049", PotentialProfile.Weapon, null, "Whispershot"),
                Item("Destiny Chakram (Khali)", "1404046", PotentialProfile.Weapon, null, "Chakram"),
                Item("Destiny Memorial Staff (Lynn)", "1252129", PotentialProfile.Weapon, null, "Memorial Staff"),
                Item("Destiny Sword (Ren)", "1215025", PotentialProfile.Weapon, null, "1-handed sword (Ren)"),
                Item("Destiny Celestial Light (Sia)", "1253029", PotentialProfile.Weapon, null, "Staff"),
                Item("Destiny Martial Brace (MoXuan)", "1403052", PotentialProfile.Weapon, null, "Martial Brace"),
                Item("Destiny Gram", "1433025", PotentialProfile.Weapon, null, "Gram"),
            }),
            new("Secondary Weapon", new List<EquipmentItemDefinition>
            {
                Item("Arcane Umbra Katara (DualBlade)", "1342104", PotentialProfile.Weapon, null, "Katara"),
                Item("Princess No's Accursed Arrow (Mercedes)", "1352009", PotentialProfile.Weapon, null, "Magic Arrow"),
                Item("Princess No's Carte (Phantom)", "1352109", PotentialProfile.Weapon, null, "Card"),
                Item("Princess No's Medal (Fighter)", "1352206", PotentialProfile.Weapon, null, "Medallions"),
                Item("Princess No's Rosary (Page)", "1352216", PotentialProfile.Weapon, null, "Rosary"),
                Item("Princess No's Flower Chain (Spearman)", "1352226", PotentialProfile.Weapon, null, "Iron Chain"),
                Item("Princess No's Flaming Book (Fire/Poison)", "1352236", PotentialProfile.Weapon, null, "Magic Book (F/P)"),
                Item("Princess No's Damp Book (Ice/Lightning)", "1352246", PotentialProfile.Weapon, null, "Magic Book (I/L)"),
                Item("Princess No's Golden Book (Cleric)", "1352256", PotentialProfile.Weapon, null, "Magic Book (C)"),
                Item("Princess No's Feather (Hunter)", "1352266", PotentialProfile.Weapon, null, "Arrow Fletching"),
                Item("Princess No's Wreath (CrossBow)", "1352276", PotentialProfile.Weapon, null, "Bow Thimble"),
                Item("Princess No's Purple Shadow (Bandit)", "1352286", PotentialProfile.Weapon, null, "Dagger Scabbard"),
                Item("Princess No's Charm (Assassin)", "1352296", PotentialProfile.Weapon, null, "Charm"),
                Item("Princess No's Soul Orb (Luminous)", "1352406", PotentialProfile.Weapon, null, "Orb"),
                Item("Princess No's Dragon Essence (Kaiser)", "1352506", PotentialProfile.Weapon, null, "Dragon Essence"),
                Item("Princess No's Soul Ring (AngelicBuster)", "1352606", PotentialProfile.Weapon, null, "Soul Ring"),
                Item("Princess No's Magnum (Mechanic)", "1352707", PotentialProfile.Weapon, null, "Magnum"),
                Item("Princess No's Wakizashi (Hayato)", "1352807", PotentialProfile.Weapon, null, "Wakizashi"),
                Item("Princess No's Magical Whisper (BeastTamer)", "1352815", PotentialProfile.Weapon, null, "Whistle"),
                Item("Princess No's Orion Fist (Jett)", "1352824", PotentialProfile.Weapon, null, "Fist"),
                Item("Princess No's Skull Armor (WristBand)", "1352906", PotentialProfile.Weapon, null, "Wrist Band"),
                Item("Princess No's Falcon Eye (Gungslinger)", "1352916", PotentialProfile.Weapon, null, "Far Sight"),
                Item("Princess No's Fire Bomb (Cannoneer)", "1352928", PotentialProfile.Weapon, null, "Powder Keg"),
                Item("Princess No's Flower Ballast (Aran)", "1352935", PotentialProfile.Weapon, null, "Mass"),
                Item("Princess No's Dragon Legacy (Evan)", "1352945", PotentialProfile.Weapon, null, "Document"),
                Item("Princess No's Accursed Marble (BattleMage)", "1352957", PotentialProfile.Weapon, null, "Magic Marble"),
                Item("Princess No's Arrowhead (WildHunter)", "1352967", PotentialProfile.Weapon, null, "Arrowhead"),
                Item("Princess No's Floral Jewel (CygnusKnight)", "1352975", PotentialProfile.Weapon, null, "Jewel"),
                Item("Princess No's Controller (Xenon)", "1353006", PotentialProfile.Weapon, null, "Controller"),
                Item("Princess No's Fox Marble (Shade)", "1353105", PotentialProfile.Weapon, null, "Fox Marble"),
                Item("Princess No's Oriental King Chess Piece (Kinesis)", "1353205", PotentialProfile.Weapon, null, "Chess Piece"),
                Item("Princess No's Megaton Charges (Blaster)", "1353405", PotentialProfile.Weapon, null, "Charge"),
                Item("Princess No's Soul Shield (Mihile)", "1098006", PotentialProfile.Weapon, null, "Soul Shield"),
                Item("Princess No's Accursed Shield (DemonDex)", "1099011", PotentialProfile.Weapon, null, "Demon Aegis (S)"),
                Item("Princess No's Accursed Shield (DemonHP)", "1099012", PotentialProfile.Weapon, null, "Demon Aegis (A)"),
                Item("Princess No's Transmitter (Cadena)", "1353306", PotentialProfile.Weapon, null, "Warp Forge"),
                Item("Princess No's Lucent Wings (Illium)", "1353505", PotentialProfile.Weapon, null, "Lucent Wings"),
                Item("Princess No's Path (Ark)", "1353606", PotentialProfile.Weapon, null, "AbyssalPath"),
                Item("Princess No's Talisman (Kanna)", "1354307", PotentialProfile.Weapon, null, "Talisman"),
                Item("Princess No's Immortal Relic (Pathfinder)", "1353707", PotentialProfile.Weapon, null, "Relic"),
                Item("Princess No's Fan Tassel (Hoyoung)", "1353807", PotentialProfile.Weapon, null, "FanTassel"),
                Item("Princess No's Imortal Bladebinder (Adele)", "1354007", PotentialProfile.Weapon, null, "Bladebinder"),
                Item("Princess No's Immortal Weapon Belt", "1354017", PotentialProfile.Weapon, null, "Weapon Belt"),
                Item("Immortal Four-Jade Ornament (Lara)", "1354027", PotentialProfile.Weapon, null, "Ornament"),
                Item("Immortal Hex Seeker", "1354037", PotentialProfile.Weapon, null, "Hex Seeker"),
                Item("Princess No's Leaf (Lynn)", "1352815", PotentialProfile.Weapon, null, "Leaf"),
                Item("Princess No's Imugi Gem (Ren)", "1354049", PotentialProfile.Weapon, null, "Imugi Gem"),
                Item("Princess No's Compass (Sia)", "1352878", PotentialProfile.Weapon, null, "Compass"),
                Item("Princess No's Brace Band (MoXuan)", "1352867", PotentialProfile.Weapon, null, "Brace Band"),
                Item("Princess No's Keir (Erel Light)", "1352888", PotentialProfile.Weapon, null, "Keir"),
            }),
            new("Armor", new List<EquipmentItemDefinition>
            {
                Item("Warrior Gloves", "1082760", PotentialProfile.Gloves, "Warrior"),
                Item("Warrior Cape", "1103433", PotentialProfile.Other, "Warrior"),
                Item("Warrior Shoes", "1073629", PotentialProfile.Other, "Warrior"),
                Item("Warrior Hat", "1005980", PotentialProfile.Other, "Warrior"),
                Item("Warrior Top", "1042433", PotentialProfile.Other, "Warrior"),
                Item("Warrior Bottom", "1062285", PotentialProfile.Other, "Warrior"),
                Item("Warrior Shoulder", "1152212", PotentialProfile.Other, "Warrior"),
                Item("Magician Gloves", "1082761", PotentialProfile.Gloves, "Magician"),
                Item("Magician Cape", "1103434", PotentialProfile.Other, "Magician"),
                Item("Magician Shoes", "1073630", PotentialProfile.Other, "Magician"),
                Item("Magician Hat", "1005981", PotentialProfile.Other, "Magician"),
                Item("Magician Top", "1042434", PotentialProfile.Other, "Magician"),
                Item("Magician Bottom", "1062286", PotentialProfile.Other, "Magician"),
                Item("Magician Shoulder", "1152213", PotentialProfile.Other, "Magician"),
                Item("Bowman Gloves", "1082762", PotentialProfile.Gloves, "Bowman"),
                Item("Bowman Cape", "1103435", PotentialProfile.Other, "Bowman"),
                Item("Bowman Shoes", "1073631", PotentialProfile.Other, "Bowman"),
                Item("Bowman Hat", "1005982", PotentialProfile.Other, "Bowman"),
                Item("Bowman Top", "1042435", PotentialProfile.Other, "Bowman"),
                Item("Bowman Bottom", "1062287", PotentialProfile.Other, "Bowman"),
                Item("Bowman Shoulder", "1152214", PotentialProfile.Other, "Bowman"),
                Item("Thief Gloves", "1082763", PotentialProfile.Gloves, "Thief"),
                Item("Thief Cape", "1103436", PotentialProfile.Other, "Thief"),
                Item("Thief Shoes", "1073632", PotentialProfile.Other, "Thief"),
                Item("Thief Hat", "1005983", PotentialProfile.Other, "Thief"),
                Item("Thief Top", "1042436", PotentialProfile.Other, "Thief"),
                Item("Thief Bottom", "1062288", PotentialProfile.Other, "Thief"),
                Item("Thief Shoulder", "1152215", PotentialProfile.Other, "Thief"),
                Item("Pirate Gloves", "1082764", PotentialProfile.Gloves, "Pirate"),
                Item("Pirate Cape", "1103437", PotentialProfile.Other, "Pirate"),
                Item("Pirate Shoes", "1073633", PotentialProfile.Other, "Pirate"),
                Item("Pirate Hat", "1005984", PotentialProfile.Other, "Pirate"),
                Item("Pirate Top", "1042437", PotentialProfile.Other, "Pirate"),
                Item("Pirate Bottom", "1062289", PotentialProfile.Other, "Pirate"),
                Item("Pirate Shoulder", "1152216", PotentialProfile.Other, "Pirate"),
            }),
            new("Accessories", new List<EquipmentItemDefinition>
            {
                Item("Belt Pitched", "1132308", PotentialProfile.Other),
                Item("Face Brilliant Pitched", "1012911", PotentialProfile.Other),
                Item("Eye Pitched", "1022278", PotentialProfile.Other),
                Item("Earring Pitched", "1032316", PotentialProfile.Other),
                Item("Pendant Pitched", "1122430", PotentialProfile.Other),
                Item("Pendant Brilliant Pitched", "1122447", PotentialProfile.Other),
                Item("Genesis Badge", "1182285", PotentialProfile.None),
                Item("Heart", "1672095", PotentialProfile.Other),
                Item("Ring Pitched", "1113306", PotentialProfile.Other),
                Item("Ring Brilliant Pitched", "1113341", PotentialProfile.Other),
                Item("Ring Brilliant Pitched", "1113360", PotentialProfile.Other),
                Item("Ring of Restraint", "1113361", PotentialProfile.Other),
            }),
            new("Emblem", new List<EquipmentItemDefinition>
            {
                Item("Mitra's Rage: Warrior", "1190555", PotentialProfile.Emblem, null, "Warrior"),
                Item("Mitra's Rage: Thief", "1190558", PotentialProfile.Emblem, null, "Thief"),
                Item("Mitra's Rage: Magician", "1190557", PotentialProfile.Emblem, null, "Magician"),
                Item("Mitra's Rage: Pirate", "1190559", PotentialProfile.Emblem, null, "Pirate"),
                Item("Mitra's Rage: Bowman", "1190556", PotentialProfile.Emblem, null, "Bowman"),
            }),
            new("Pocket", new List<EquipmentItemDefinition>
            {
                Item("Cursed Red Spellbook", "1162080", PotentialProfile.None, null, "STR"),
                Item("Cursed Blue Spellbook", "1162081", PotentialProfile.None, null, "INT"),
                Item("Cursed Green Spellbook", "1162082", PotentialProfile.None, null, "DEX"),
                Item("Cursed Yellow Spellbook", "1162083", PotentialProfile.None, null, "LUK"),
            }),
            new("Misc Items", new List<EquipmentItemDefinition>
            {
                Item("Title", "2633243", PotentialProfile.None),
                Item("Medal", "1142666", PotentialProfile.None),
                Item("Totem1", "1202138", PotentialProfile.None),
                Item("Totem2", "1202186", PotentialProfile.None),
                Item("Totem3", "1202185", PotentialProfile.None),
                Item("Frenzy Totem", "1202236", PotentialProfile.None),
                Item("Premium Hyper Teleport Rock (90 Days)", "2830170", PotentialProfile.None),
                Item("Nodestones", "2435719", PotentialProfile.None),
                Item("EXP Nodestones", "2630402", PotentialProfile.None),
                Item("Android", "1662073", PotentialProfile.None),
                Item("Starforce 20 (Lv. 250)", "2644302", PotentialProfile.None),
                Item("Starforce 15", "2049372", PotentialProfile.None),
                Item("Star Force 22 (Lv. 200)", "2644039", PotentialProfile.None),
                Item("Star Force 1-Star (Lv. 200)", "2644008", PotentialProfile.None),
                Item("Star Force 1-Star (Lv. 250)", "2644315", PotentialProfile.None),
                Item("Soul Enchanter", "2590004", PotentialProfile.None),
                Item("Magnificent Limbo Soul", "2591762", PotentialProfile.None),
                Item("Gollux Scroll (Interactive)", "2615001", PotentialProfile.None),
                Item("Chaos Scroll (Interactive)", "2049134", PotentialProfile.None),
                Item("50 Million Meso Voucher", "4001872 2", PotentialProfile.None),
                Item("Sherbet Vac Pet", "2633202", PotentialProfile.None),
                Item("Dense Sol Erda Energy", "2638392", PotentialProfile.None),
                Item("Sol Erda Fragments", "4009547", PotentialProfile.None),
            })
        };
    }
}
