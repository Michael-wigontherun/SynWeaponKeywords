using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using WeaponKeywords.Types;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Newtonsoft.Json.Linq;

namespace WeaponKeywords
{
    public class Program
    {
        static ModKey NWTA = ModKey.FromNameAndExtension("NWTA.esp");
        static ModKey NA = ModKey.FromNameAndExtension("NewArmoury.esp");
        //static ModKey WOTM = ModKey.FromNameAndExtension("WayOfTheMonk.esp");
        public static int Main(string[] args)
        {
            return SynthesisPipeline.Instance.Patch<ISkyrimMod, ISkyrimModGetter>(
                args: args,
                patcher: RunPatch,
                userPreferences: new UserPreferences()
                {
                    ActionsForEmptyArgs = new RunDefaultPatcher()
                    {
                        IdentifyingModKey = "WeapTypeKeywords.esp",
                        TargetRelease = GameRelease.SkyrimSE,
                        BlockAutomaticExit = false,
                    }
                });
        }

        public static void RunPatch(SynthesisState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var db = JObject.Parse(File.ReadAllText(Path.Combine(state.ExtraSettingsDataPath, "types.json"))).ToObject<Dictionary<string, WeaponDB>>();
            var edb = JObject.Parse(File.ReadAllText(Path.Combine(state.ExtraSettingsDataPath, "excludes.json"))).ToObject<ExcludesDB>();
            var idb = JObject.Parse(File.ReadAllText(Path.Combine(state.ExtraSettingsDataPath, "includes.json"))).ToObject<Dictionary<string, string>>();
            if(db!=null) {
                Dictionary<string, FormKey> formkeys = new Dictionary<string, FormKey>();
                if(state.LoadOrder.ContainsKey(NWTA)) {
                    formkeys["Katana"] = NWTA.MakeFormKey(0xD61);
                    formkeys["Scimitar"] = NWTA.MakeFormKey(0xD71);
                }
                if(state.LoadOrder.ContainsKey(NA)) {
                    formkeys["Rapier"] = NA.MakeFormKey(0x801);
                    formkeys["Pike"] = NA.MakeFormKey(0xE457E);
                    formkeys["Spear"] = NA.MakeFormKey(0xE457F);
                    formkeys["Halberd"] = NA.MakeFormKey(0xE4580);
                    formkeys["Quarterstaff"] = NA.MakeFormKey(0xE4581);
                    formkeys["Cestus"] = NA.MakeFormKey(0x19AAB3);
                    formkeys["Claw"] = NA.MakeFormKey(0x19AAB4);
                }
                foreach(var weapon in state.LoadOrder.PriorityOrder.OnlyEnabled().Weapon().WinningOverrides()) {
                    var edid = weapon.EditorID;
                    var nameToTest = weapon.Name?.String?.ToLower();
                    var kyds = db.Where(kv => kv.Value.commonNames.Any(cn => nameToTest?.Contains(cn)??false)).Select(kd => kd.Key).ToArray();
                    var exclude = edb?.weapons.Contains(edid);
                    var orex = edb?.phrases.Any(ph => nameToTest?.Contains(ph)??false);
                    if(edid!=null && idb!=null && idb.ContainsKey(edid)) {
                        var nw = state.PatchMod.Weapons.GetOrAddAsOverride(weapon);
                        nw.Keywords?.Add(formkeys[idb[edid]]);
                        Console.WriteLine($"{nameToTest} is {db[idb[edid]].outputDescription}");
                    }
                    if(kyds.Length > 0 && !((exclude??false) || (orex??false))) {
                        if(!kyds.All(kd => weapon.Keywords?.Contains(formkeys.GetValueOrDefault(kd))??false)) {
                            var nw = state.PatchMod.Weapons.GetOrAddAsOverride(weapon);
                            foreach(var kyd in kyds) {
                                if(formkeys.ContainsKey(kyd) && !(nw.Keywords?.Contains(formkeys[kyd])??false)) {
                                    nw.Keywords?.Add(formkeys[kyd]);
                                    Console.WriteLine($"{nameToTest} is {db[kyd].outputDescription}");
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}