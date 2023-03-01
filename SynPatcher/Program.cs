using System;
using System.Net.Http;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;

using Noggog;

using WeaponKeywords.Types;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

using MZCommonClass.JsonPatch;
using System.Drawing;
using System.Data;

namespace WeaponKeywords;
public class Program
{
    static Lazy<Database> LazyDB = new();
    static Database Settings => LazyDB.Value;
    static JsonSerializer? jss;
    public static async Task<int> Main(string[] args)
    {
        jss = JsonSerializer.Create();
        jss.Converters.Add(new Mutagen.Bethesda.Json.FormKeyJsonConverter());
        jss.Converters.Add(new Mutagen.Bethesda.Json.ModKeyJsonConverter());
        return await SynthesisPipeline.Instance
            .SetAutogeneratedSettings("Database", "database.json", out LazyDB)
            .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
            .AddRunnabilityCheck(ConvertJson)
            .SetTypicalOpen(GameRelease.SkyrimSE, "SynWeaponKeywords.esp")
            .Run(args);
    }
    public static void ConvertJson(IRunnabilityState state)
    {
        JObject? DBConv = null;
        if (!Directory.Exists(state.ExtraSettingsDataPath))
        {
            Directory.CreateDirectory(state.ExtraSettingsDataPath!);
        }
        if (File.Exists(Path.Combine(state.ExtraSettingsDataPath!, "database.json")))
        {
            DBConv = JObject.Parse(File.ReadAllText(Path.Combine(state.ExtraSettingsDataPath!, "database.json")));
        }
        if (DBConv == null || (DBConv["DBVer"]?.Value<int>() ?? 0) <= 0)
        {
            DBConv = new JObject();
            DBConv["DBVer"] = 0;
        }
        //JSON Patch based DB-Updates
        using (var HttpClient = new HttpClient())
        {
            HttpClient.Timeout = TimeSpan.FromSeconds(5);
            string resp = string.Empty;
            try
            {
                var http = HttpClient.GetStringAsync("https://raw.githubusercontent.com/minis-patchers/SynDelta/main/SynWeaponKeywords/index.json");
                http.Wait();
                resp = http.Result;
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to download patch index");
                return;
            }
            var pi = JArray.Parse(resp).ToObject<List<string>>()!;
            for (int i = DBConv["DBVer"]!.Value<int>(); i < pi.Count; i++)
            {
                try
                {
                    Console.WriteLine($"Downloading patch {pi[i]}");
                    var http = HttpClient.GetStringAsync(pi[i]);
                    http.Wait();
                    resp = http.Result;
                }
                catch (Exception)
                {
                    Console.WriteLine($"Failed to download patch {pi[i]}");
                    return;
                }
                var pch = JsonConvert.DeserializeObject<List<JsonOperation>>(resp);
                if (pch!.ApplyTo(DBConv))
                {
                    Console.WriteLine($"JsonPatch+ Successfully applied: {i}");
                }
                File.WriteAllText(Path.Combine(state.ExtraSettingsDataPath!, "database.json"), JsonConvert.SerializeObject(DBConv, Formatting.Indented));
            }
        }
    }
    public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
    {
        Console.WriteLine($"Running with Database Base-Patch: V{Settings.DBVer}");
        var SWK_PATCHES = state.DataFolderPath.EnumerateFiles().Where(x => x.NameWithoutExtension.EndsWith("_SWK"));
        var db = JObject.Parse(File.ReadAllText(Path.Combine(state.ExtraSettingsDataPath!, "database.json")));
        foreach (var patch in SWK_PATCHES)
        {
            Console.WriteLine($"Applying data-patch {patch.NameWithoutExtension}");
            var data = File.ReadAllText(patch.Path);
            var pch = JsonConvert.DeserializeObject<List<JsonOperation>>(data);
            pch!.ApplyTo(db);
        };
        var DB = db.ToObject<Database>(jss!)!;
        if ((int)DB.exp > 0)
        {
            Console.WriteLine($"WARNING: RUNNING WITH EXPERIMENTAL MODE: {DB.exp}");
        }
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
                    Console.WriteLine($"Keyword : {keyword.FormKey.IDString()}:{keyword.FormKey.ModKey}:{keyword.EditorID}");
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
                //Don't inject record if we have it...
                var type = DB.DB.Where(x => x.Value.keyword.Contains(kywd.Key ?? "")).Select(x => x.Key).ToHashSet();
                if (formkeys.Where(x => type.Contains(x.Key)).SelectMany(x => x.Value).Where(x => x.FormKey.Equals(kywd.Value)).Any()) continue;
                var key = new Keyword(kywd.Value, SkyrimRelease.SkyrimSE);
                key.EditorID = kywd.Key;
                key.Color = Color.Black;
                state.PatchMod.Keywords.Add(key);
                Console.WriteLine($"Added Keyword : {key.FormKey.IDString()}:{key.FormKey.ModKey}:{key.EditorID}");
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
                .ToHashSet();

            IWeapon? nw = null;
            if (matchingKeywords.Count > 0)
            {
                Console.WriteLine($"{edid} - {weapon.FormKey.IDString()}:{weapon.FormKey.ModKey} matches: {string.Join(",", matchingKeywords)}");
                Console.WriteLine($"\t{weapon.Name}: {weapon.EditorID} is {string.Join(" & ", DB.DB.Where(x => matchingKeywords.Contains(x.Key)).Select(x => x.Value.outputDescription))}");
                var keywords = weapon.Keywords?
                    .Select(x => x.TryResolve<IKeywordGetter>(state.LinkCache, out var kyd) ? kyd : null)
                    .Where(x => x != null)
                    .Where(x => !(x!.EditorID.StartsWith("WeapType") && (int)DB.exp >= 1))
                    .Concat(matchingKeywords.SelectMany(x => formkeys[x]))
                    .Select(x => x!)
                    .DistinctBy(x => x.FormKey)
                    .ToHashSet() ?? new();

                if (keywords.Any(x => !(weapon.Keywords?.Contains(x) ?? false)))
                {
                    nw = nw == null ? state.PatchMod.Weapons.GetOrAddAsOverride(weapon)! : nw!;
                    nw.Keywords = keywords.Select(x => x.ToLinkGetter()).ToExtendedList();
                    Console.WriteLine($"\tSetting keywords to:\n\t\t{string.Join("\n\t\t", keywords.Select(x => $"{x.EditorID} from {x.FormKey.ModKey}"))}");
                }
                if (nw != null)
                {
                    foreach (var kyd in matchingKeywords)
                    {
                        var scripts = DB.DB[kyd].Script.Where(x => state.LoadOrder.ModExists(x.Requires, true))
                            .Where(x => !x.ExcludeMods.Contains(weapon.FormKey.ModKey))
                            .Where(x => !x.ExcludeItems.Contains(weapon.FormKey))
                            .ToHashSet();
                        foreach (var scr in scripts)
                        {
                            nw.VirtualMachineAdapter = nw.VirtualMachineAdapter == null ? new() : nw.VirtualMachineAdapter;
                            if (nw.VirtualMachineAdapter.Scripts.Any(x => x.Name == scr.ScriptName)) continue;
                            Console.WriteLine($"\t\tAttaching Script {scr.ScriptName}");
                            var script = new ScriptEntry()
                            {
                                Name = scr.ScriptName,
                            };
                            foreach (var prop in scr.ObjectParam)
                            {
                                script.Properties.Add(new ScriptObjectProperty()
                                {
                                    Flags = ScriptProperty.Flag.Edited,
                                    Name = prop.Key,
                                    Object = prop.Value.ToLink<ISkyrimMajorRecordGetter>(),
                                });
                            }
                            foreach (var prop in scr.ObjectListParam)
                            {
                                script.Properties.Add(new ScriptObjectListProperty()
                                {
                                    Flags = ScriptProperty.Flag.Edited,
                                    Name = prop.Key,
                                    Objects = prop.Value.Select(x => new ScriptObjectProperty()
                                    {
                                        Object = x.ToLink<ISkyrimMajorRecordGetter>(),
                                    }).ToExtendedList(),
                                });
                            }
                            foreach (var prop in scr.FloatParam)
                            {
                                script.Properties.Add(new ScriptFloatProperty()
                                {
                                    Flags = ScriptProperty.Flag.Edited,
                                    Name = prop.Key,
                                    Data = prop.Value,
                                });
                            }
                            foreach (var prop in scr.FloatListParam)
                            {
                                script.Properties.Add(new ScriptFloatListProperty()
                                {
                                    Flags = ScriptProperty.Flag.Edited,
                                    Name = prop.Key,
                                    Data = prop.Value.ToExtendedList(),
                                });
                            }
                            nw.VirtualMachineAdapter.Scripts.Add(script);
                        }
                    }
                }
                var fKeyword = matchingKeywords.First();
                if ((int)DB.exp >= 2)
                {
                    var NameO = DB.DB[fKeyword].AnimNameOverride.FirstOrDefault(x => weapon!.Name!.String!.Contains(x.Compare));
                    var ModO = DB.DB[fKeyword].AnimModOverride.FirstOrDefault(x => weapon.FormKey.ModKey == x.Compare);
                    var ItemO = DB.DB[fKeyword].AnimItemOverride.FirstOrDefault(x => weapon.FormKey == x.Compare);
                    var Animation = ItemO.Compare.IsNull ? (ModO.Compare.IsNull ? (NameO.Compare.IsNullOrEmpty() ? DB.DB[fKeyword].AnimEQOverride.First(x => x.Compare == DBConst.EquipTypeTableR[weapon!.EquipmentType.FormKey]).Animation : NameO.Animation) : ModO.Animation) : ItemO.Animation;
                    if (
                        Animation.ContainsKey(DBConst.EquipTypeTableR[weapon.EquipmentType.FormKey]) &&
                        weapon!.Data!.AnimationType != Animation[DBConst.EquipTypeTableR[weapon.EquipmentType.FormKey]]
                    )
                    {
                        nw = nw == null ? state.PatchMod.Weapons.GetOrAddAsOverride(weapon)! : nw!;
                        if (nw.Data != null)
                        {
                            nw.Data.AnimationType = Animation[DBConst.EquipTypeTableR[weapon.EquipmentType.FormKey]];
                            Console.WriteLine($"\tSetting animation type to {Animation[DBConst.EquipTypeTableR[weapon.EquipmentType.FormKey]]}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Animation type {DBConst.EquipTypeTableR[weapon.EquipmentType.FormKey]} for {fKeyword} not defined");
                    }
                }
            }
        }
        if ((int)DB.exp > 0)
        {
            Console.WriteLine($"WARNING: RAN WITH EXPERIMENTAL MODE: {DB.exp}");
        }
    }
}