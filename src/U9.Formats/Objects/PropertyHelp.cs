using System.Collections.Generic;

namespace U9.Formats.Objects
{
    /// <summary>
    /// What each property's two values (A and B) are, as far as is known. "Seen on" texts come from the shipped
    /// maps; the rest go by the property's name in u9.exe and say so.
    /// </summary>
    public static class PropertyHelp
    {
        const string ByName = " (Meaning taken from its name; not checked in the game.)";

        static readonly Dictionary<int, string> help = new Dictionary<int, string>
        {
            [PropertyNames.Contents] = "Where the objects inside it are: A and B are one 32-bit file offset (A low). The editor writes it; it is not set by hand.",
            [PropertyNames.Quantity] = "How many: A. Seen on coins, arrows and reagents.",
            [PropertyNames.BookNumber] = "A: the book (BOOKS-EN.FLX record A - 1) whose title and text it shows. Seen on books, plaques, signs and grave markers.",
            [0x10] = "Hit Points: A (B must be 0). On a breakable item it is the item's hit points left, replacing its type's (TYPES.DAT); on spikes and fire traps it is the damage they do. The shipped maps put it on 18 NPCs on Britannia, but how the game uses it for a creature is not known yet.",
            [0x13] = "Who owns it: A." + ByName,
            [0x14] = "Enchantment: the spell (B, 1-45) an enchanted item holds; not a combat value.",
            [0x15] = "Highway Links: the paths NPCs walk between highway markers." + ByName,
            [0x20] = "A: how see-through the object is." + ByName,
            [0x21] = "A: the colour of the light it gives. Seen on light sources.",
            [0x22] = "A: how far its light reaches. Seen on light sources.",
            [0x23] = "A: how its light flickers. Seen on light sources.",
            [PropertySegment.SfTrigger] = "SF Trigger: A and B are TRIGGERS.FLX script slots (the third and fourth of the object's scripts), run on success and failure callbacks.",
            [0x3C] = "Variable: A and B, kept for scripts." + ByName,
            [PropertySegment.MiscUsecode1] = "Misc Usecode 1: A, a value its type's usecode reads. On a camera point (type 2218), the distance from the point looked at, in object units.",
            [PropertySegment.OoTrigger] = "OO Trigger: A and B are TRIGGERS.FLX script slots (the object's first and second scripts). 0: none.",
            [0x3F] = "Timer: when a timer object fires." + ByName,
            [PropertySegment.TriggerArea] = "Trigger Area: the size of a trigger zone.",
            [PropertySegment.MiscUsecode2] = "Misc Usecode 2: A, a value its type's usecode reads.",
            [0x42] = "Scale: how much bigger or smaller the model is drawn." + ByName,
            [0x43] = "Combat AI: how an NPC fights." + ByName,
            [0x44] = "Default Activity: what an NPC does when nothing else is set: A the activity, B its target." + ByName,
            [0x49] = "Scale X: the model's scale along x." + ByName,
            [0x4A] = "Scale Y: the model's scale along y." + ByName,
            [0x4B] = "Scale Z: the model's scale along z." + ByName,
            [0x4E] = "Tint: a colour laid over the model." + ByName,
            [0x54] = "Regeneration: how much it heals." + ByName,
            [0x55] = "Regeneration time: how often it heals." + ByName,
            [0x57] = "Use Distance: how near the Avatar must be to use it." + ByName,
            [0x5A] = "Ribbon Text Override: another name shown for it." + ByName,
            [0x61] = "Fog Color." + ByName,
            [0x62] = "Fog Range/Dist." + ByName,
            [0x72] = "Spell List1: spells an NPC casts." + ByName,
            [0x73] = "Spell List2: more spells an NPC casts." + ByName,
            [0x75] = "Stun Time (in sec.)." + ByName,
            [0x76] = "Min/Max Damage: a blow by this creature does A to B damage, replacing its weapon's or natural attack's range (then the target's resistances and defence apply). Seen on 90 NPCs in the shipped maps (e.g. ghosts 70, 100). On a unique NPC (not a creature template) the game reads its own NPC store, so a map edit may not take.",
            [0x77] = "Min/Max Defense: a hit on this creature is lessened by A to B, replacing the default 0 to its NPC.FLX defence. Not for the Avatar, whose armour counts instead; ignored by trap and spell damage. Seen on 19 NPCs; on a unique NPC the game reads its own NPC store instead.",
            [0x80] = "Creature Type: what a monster generator makes (A). Seen on monster generators.",
            [0x81] = "Spawn Percent: the chance a monster generator makes one (A, percent). Seen on monster generators.",
            [0x82] = "Max Of Type: how many a monster generator keeps alive at once (A). Seen on monster generators.",
            [0x83] = "Creature Link Num: A, a small number on monster generators (1 to 111 in Britannia); not an object link, it seems.",
            [0x90] = "Ambiance Min Range: where an ambient sound starts to be heard." + ByName,
            [0x91] = "Ambiance Max Range: how far an ambient sound carries." + ByName,
            [0x92] = "Ambiance Sample ID." + ByName,
            [0x93] = "Ambiance Sound ID." + ByName,
            [0x94] = "Ambiance Max VolPct: its loudest, in percent." + ByName,
            [PropertyNames.NextPropertyPointer] = "Next Property Pointer: the file offset of the object's next property segment. The editor writes it; it is not set by hand.",
        };

        /// <summary>What a property's values mean, or a line saying only its name is known.</summary>
        public static string Of(int id) =>
            help.TryGetValue(id, out var h) ? h : $"{PropertyNames.NameOf(id)}: A and B." + ByName;
    }
}
