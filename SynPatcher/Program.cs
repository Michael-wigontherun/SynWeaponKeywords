using System;
using System.Net.Http;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.FormKeys.SkyrimSE;

using Noggog;

using WeaponKeywords.Types;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;

using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.JsonPatch.Operations;
using Microsoft.AspNetCore.JsonPatch.Exceptions;
using System.Configuration;
using System.Diagnostics.Tracing;
using System.Drawing;
using System.Security.Cryptography;

namespace WeaponKeywords
{
    public class Program
    {
        static Lazy<Database> LazyDB = new();
        static Database DB => LazyDB.Value;
        static List<FormKey> OneHandedType = new() { Skyrim.EquipType.EitherHand.FormKey, Skyrim.EquipType.LeftHand.FormKey, Skyrim.EquipType.RightHand.FormKey };
        static List<FormKey> TwoHandedType = new() { Skyrim.EquipType.BothHands.FormKey };
        static List<WeaponAnimationType> OneHandedAnims = new() { WeaponAnimationType.OneHandAxe, WeaponAnimationType.OneHandSword, WeaponAnimationType.OneHandDagger, WeaponAnimationType.OneHandMace, WeaponAnimationType.Staff };
        static List<WeaponAnimationType> TwoHandedAnims = new() { WeaponAnimationType.Bow, WeaponAnimationType.Crossbow, WeaponAnimationType.TwoHandSword, WeaponAnimationType.TwoHandAxe };
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .SetAutogeneratedSettings("Database", "database.json", out LazyDB)
                .AddRunnabilityCheck(ConvertJson)
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "SynWeaponKeywords.esp")
                .Run(args);
        }
        public static async void ConvertJson(IRunnabilityState state)
        {
            JObject? DBConv = null;
            string path;
            if (state.ExtraSettingsDataPath == null)
            {
                path = Path.Combine("Data", "Skyrim Special Edition", "SynWeaponKeywords");
            }
            else
            {
                path = state.ExtraSettingsDataPath!;
            }
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            if (File.Exists(Path.Combine(path, "database.json")))
            {
                DBConv = JObject.Parse(File.ReadAllText(Path.Combine(path, "database.json")));
            }
            if (DBConv == null || (DBConv["DBPatchVer"]?.Value<int>() ?? 0) <= 0)
            {
                DBConv = new JObject();
                DBConv["DBPatchVer"] = 0;
            }
            //New Age JSON Patcher // Shiny
            using (var HttpClient = new HttpClient())
            {
                HttpClient.Timeout = TimeSpan.FromSeconds(5);
                string resp = string.Empty;
                try
                {
                    resp = await HttpClient.GetStringAsync("https://raw.githubusercontent.com/minis-patchers/SynDelta/main/SynWeaponKeywords/index.json");
                }
                catch (Exception)
                {
                    Console.WriteLine("Failed to download patch index");
                    return;
                }
                var pi = JArray.Parse(resp).ToObject<List<string>>()!;
                for (int i = DBConv["DBPatchVer"]!.Value<int>(); i < pi.Count; i++)
                {
                    try
                    {
                        Console.WriteLine($"Downloading patch {pi[i]}");
                        resp = await HttpClient.GetStringAsync(pi[i]);
                    }
                    catch (Exception)
                    {
                        Console.WriteLine($"Failed to download patch {pi[i]}");
                        return;
                    }
                    var pch = new JsonPatchDocument(JsonConvert.DeserializeObject<List<Operation>>(resp), new DefaultContractResolver());
                    try
                    {
                        pch.ApplyTo(DBConv);
                    }
                    catch (JsonPatchException ex)
                    {
                        Console.WriteLine($"Failed to apply patch {pi[i]} {ex.Message}");
                        Console.WriteLine($"Failed Object {ex.AffectedObject}");
                        Console.WriteLine($"Operation {ex.FailedOperation}");
                        Console.WriteLine("Database patching terminated");
                        return;
                    }
                    File.WriteAllText(Path.Combine(path, "database.json"), JsonConvert.SerializeObject(DBConv, Formatting.Indented));
                }
            }
        }
        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            Console.WriteLine($"Running with Database Patch: {DB.DBPatchVer}");
            Dictionary<string, List<IKeywordGetter>> formkeys = new();
            var Keywords = DB.DB.SelectMany(x => x.Value.keyword).Distinct();
            foreach (var kyd in DB.DB.Select(x => x.Key))
            {
                formkeys[kyd] = new List<IKeywordGetter>();
            }
            foreach (var src in DB.sources)
            {
                if (!state.LoadOrder.PriorityOrder.Select(x => x.ModKey).Contains(src)) continue;
                state.LoadOrder.TryGetValue(src, out var mod);
                if (mod != null && mod.Mod != null && mod.Mod.Keywords != null)
                {
                    var keywords = mod.Mod.Keywords
                        .Where(x => Keywords.Contains(x.EditorID ?? ""))
                        .ToList() ?? new List<IKeywordGetter>();
                    foreach (var keyword in keywords)
                    {
                        if (keyword == null) continue;
                        var type = DB.DB.Where(x => x.Value.keyword.Contains(keyword.EditorID ?? "")).Select(x => x.Key);
                        Console.WriteLine($"Keyword : {keyword.FormKey.ModKey} : {keyword.EditorID}");
                        foreach (var tp in type)
                        {
                            formkeys[tp].Add(keyword);
                        }
                    }
                }
            }
            if (DB.Gen)
            {
                foreach (var kywd in DB.InjectedKeywords)
                {
                    var type = DB.DB.Where(x => x.Value.keyword.Contains(kywd.Key ?? "")).Select(x => x.Key).ToHashSet();
                    var fk = new FormKey(new ModKey("Update", ModType.Master), uint.Parse(kywd.Value, System.Globalization.NumberStyles.HexNumber));
                    var key = new Keyword(fk, SkyrimRelease.SkyrimSE);
                    key.EditorID = kywd.Key;
                    key.Color = Color.Black;
                    state.PatchMod.Keywords.Add(key);
                    Console.WriteLine($"Added Keyword : {key.FormKey.ModKey} : {key.EditorID} : {key.FormKey.IDString()}");
                    foreach (var tp in type)
                    {
                        formkeys[tp].Add(key);
                    }
                }
            }
            foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
            {
                if (!weapon.Template.IsNull) continue;
                var edid = weapon.EditorID;
                var matchingKeywords = DB.DB
                    .Where(kv => kv.Value.commonNames.Any(cn => weapon.Name?.String?.Contains(cn, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Where(kv => !kv.Value.excludeNames.Any(en => weapon.Name?.String?.Contains(en, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Where(kv => !kv.Value.exclude.Contains(weapon.FormKey))
                    .Where(kv => !DB.excludes.excludeMod.Contains(weapon.FormKey.ModKey))
                    .Where(kv => !DB.excludes.phrases.Any(ph => (weapon.Name?.String?.Contains(ph, StringComparison.OrdinalIgnoreCase) ?? false)))
                    .Where(kv => !DB.excludes.weapons.Contains(weapon.FormKey))
                    .Select(kv => kv.Key)
                    .Concat(DB.DB.Where(x => x.Value.include.Contains(weapon.FormKey)).Select(x => x.Key))
                    .Distinct()
                    .ToArray();
                var isOneHanded = OneHandedType.Any(x => x.Equals(weapon.EquipmentType.FormKey));
                IWeapon? nw = null;
                if (matchingKeywords.Length > 0)
                {
                    Console.WriteLine($"{edid} - {weapon.FormKey.IDString()}:{weapon.FormKey.ModKey} matches: {string.Join(",", matchingKeywords)}");
                    Console.WriteLine($"\t{weapon.Name}: {weapon.EditorID} from {weapon.FormKey.ModKey} is {string.Join(" & ", DB.DB.Where(x => matchingKeywords.Contains(x.Key)).Select(x => x.Value.outputDescription))}");
                    var keywords = weapon.Keywords?
                        .Select(x => x.TryResolve<IKeywordGetter>(state.LinkCache, out var kyd) ? kyd : null)
                        .Where(x => x != null)
                        .Where(x => !x!.EditorID.StartsWith("WeapType"))
                        .Concat(
                            matchingKeywords.SelectMany(
                                x => formkeys[x].Where(y => !DB.DB[x].excludeSource.Contains(y.FormKey.ModKey))
                            )
                        )
                        .Select(x => x!)
                        .DistinctBy(x => x.FormKey)
                        .ToList() ?? new();

                    if (keywords.Any(x => !(weapon.Keywords?.Contains(x) ?? false)))
                    {
                        nw = nw == null ? state.PatchMod.Weapons.GetOrAddAsOverride(weapon)! : nw!;
                        nw.Keywords = keywords.Select(x => x.ToLinkGetter()).ToExtendedList();
                        Console.WriteLine($"\tSetting keywords to:\n\t\t{string.Join("\n\t\t", keywords.Select(x => $"{x.EditorID} from {x.FormKey.ModKey}"))}");
                    }

                    if (weapon.Data != null)
                    {
                        var fKeyword = matchingKeywords.First();
                        if (!DB.DB[fKeyword].IgnoreWATOverrides.Contains(weapon.FormKey.ModKey))
                        {
                            WeaponAnimationType Animation = DB.DB[fKeyword].Animation;
                            if (DB.DB[fKeyword].WATModOverride.Any(x => x.Mod.Equals(weapon.FormKey.ModKey)))
                            {
                                Animation = DB.DB[fKeyword].WATModOverride.Where(x => x.Mod.Equals(weapon.FormKey.ModKey)).First().Animation;
                            }
                            if (Animation != weapon.Data.AnimationType)
                            {
                                nw = nw == null ? state.PatchMod.Weapons.GetOrAddAsOverride(weapon)! : nw!;
                                if (TwoHandedAnims.Contains(Animation) && !TwoHandedType.Contains(weapon.EquipmentType.FormKey))
                                {
                                    nw.EquipmentType.SetTo(Skyrim.EquipType.BothHands);
                                    Console.WriteLine($"\t\tChanged Equipment Type to BothHands");
                                }
                                else if (OneHandedAnims.Contains(Animation))
                                {
                                    nw.EquipmentType.SetTo(Skyrim.EquipType.EitherHand);
                                    Console.WriteLine($"\t\tChanged Equipment Type to EitherHand");
                                }
                                nw.Data!.AnimationType = Animation;
                                Console.WriteLine($"\t\tChanged Animation Type to {Animation}");
                            }
                        }
                    }
                }
            }
        }
    }
}