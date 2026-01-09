using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using System.Threading.Tasks;
using Noggog;

namespace HighPolyHeadUpdateRaces
{
    public class Program
    {
        private static readonly ModKey ModKey = ModKey.FromNameAndExtension("High Poly Head.esm");

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "High Poly Head - RaceMenu Patcher.esp")
                .Run(args);
        }

        private static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LoadOrder.ContainsKey(ModKey))
            {
                throw new Exception("You need High Poly Head mod installed for this patch to do anything.");
            }

            Console.WriteLine("Building vanilla to HPH head part map...");
            var vanillaToHphParts = BuildVanillaToHphMap(state);

            var masters = new HashSet<ModKey>(state.PatchMod.ModHeader.MasterReferences.Select(x => x.Master));
            var raceHeadPartsMale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();
            var raceHeadPartsFemale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();
            
            Console.WriteLine("Patching races...");
            foreach (var raceRecord in state.LoadOrder.PriorityOrder.OnlyEnabled().Race().WinningOverrides())
            {
                if (raceRecord.EditorID == null || raceRecord.HeadData == null) continue;

                var needsPatching = raceRecord.HeadData.Male?.HeadParts.Any(h => vanillaToHphParts.ContainsKey(h.Head)) == true ||
                                    raceRecord.HeadData.Female?.HeadParts.Any(h => vanillaToHphParts.ContainsKey(h.Head)) == true;

                if (!needsPatching) continue;
                
                var newMasters = GetMastersFor(raceRecord, state.LinkCache);
                var newMasterCount = newMasters.Count(m => !masters.Contains(m));

                if (masters.Count + newMasterCount > 252)
                {
                    Console.WriteLine($"Master limit reached. Skipping remaining plugins. Last plugin considered: {raceRecord.FormKey.ModKey}");
                    break;
                }
                masters.UnionWith(newMasters);
                
                var raceOverride = state.PatchMod.Races.GetOrAddAsOverride(raceRecord);
                var raceFormLinkGetter = raceOverride.ToLinkGetter();
                
                if (raceOverride.HeadData?.Female != null)
                {
                    foreach (var raceHead in raceOverride.HeadData.Female.HeadParts)
                    {
                        if (vanillaToHphParts.TryGetValue(raceHead.Head, out var part))
                        {
                            raceHeadPartsFemale.GetOrAdd(raceFormLinkGetter).Add(raceHead.Head.FormKey);
                            raceHead.Head.SetTo(part);
                        }
                    }
                }
                if (raceOverride.HeadData?.Male != null)
                {
                    foreach (var raceHead in raceOverride.HeadData.Male.HeadParts)
                    {
                        if (vanillaToHphParts.TryGetValue(raceHead.Head, out var part))
                        {
                            raceHeadPartsMale.GetOrAdd(raceFormLinkGetter).Add(raceHead.Head.FormKey);
                            raceHead.Head.SetTo(part);
                        }
                    }
                }
            }
            
            var patchedRaces = raceHeadPartsMale.Keys.ToHashSet();
            patchedRaces.UnionWith(raceHeadPartsFemale.Keys);

            Console.WriteLine("Patching NPCs...");
            foreach (var npcPreset in state.LoadOrder.PriorityOrder.OnlyEnabled().Npc().WinningOverrides())
            {
                if (npcPreset.EditorID == null) continue;

                var npcCopy = npcPreset.DeepCopy();
                var changed = false;

                var eid = npcCopy.EditorID;
                var withoutLastTwo = (eid.Length > 2) ? eid[..^2] : eid;

                if (withoutLastTwo.EndsWith("Preset"))
                {
                    for (var index = 0; index < npcCopy.HeadParts.Count; index++)
                    {
                        if (vanillaToHphParts.TryGetValue(npcCopy.HeadParts[index], out var replacementHead))
                        {
                            npcCopy.HeadParts[index] = replacementHead;
                            changed = true;
                        }
                    }
                }
                else if (patchedRaces.Contains(npcCopy.Race))
                {
                    var npcPartTypes = new HashSet<HeadPart.TypeEnum>();
                    foreach (var part in npcCopy.HeadParts)
                    {
                        if (part.TryResolve(state.LinkCache, out var headPartGetter) && headPartGetter.Type != null)
                        {
                            npcPartTypes.Add((HeadPart.TypeEnum)headPartGetter.Type);
                        }
                    }

                    var raceHeadParts = npcCopy.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
                        ? raceHeadPartsFemale
                        : raceHeadPartsMale;

                    if (raceHeadParts.TryGetValue(npcCopy.Race, out var currentRaceHeadParts))
                    {
                        foreach (var part in currentRaceHeadParts)
                        {
                            if (part.TryResolve(state.LinkCache, out var headPartGetter) && headPartGetter?.Type != null && !npcPartTypes.Contains((HeadPart.TypeEnum)headPartGetter.Type))
                            {
                                npcCopy.HeadParts.Add(part);
                                changed = true;
                            }
                        }
                    }
                }

                if (changed)
                {
                    var newMasters = GetMastersFor(npcPreset, state.LinkCache);
                    var newMasterCount = newMasters.Count(m => !masters.Contains(m));

                    if (masters.Count + newMasterCount > 252)
                    {
                        Console.WriteLine($"Master limit reached. Skipping remaining plugins. Last plugin considered: {npcPreset.FormKey.ModKey}");
                        continue;
                    }

                    masters.UnionWith(newMasters);
                    state.PatchMod.Npcs.Set(npcCopy);
                }
            }

            Console.WriteLine("Patching complete!");
        }
        
        private static HashSet<ModKey> GetMastersFor(IMajorRecordGetter record, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var masters = new HashSet<ModKey> { record.FormKey.ModKey };
            if (record is null) return masters;

            foreach (var link in record.EnumerateFormLinks())
            {
                if (link.FormKey.IsNull) continue;
                masters.Add(link.FormKey.ModKey);
            }

            return masters;
        }

        private static Dictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>> BuildVanillaToHphMap(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var hphHeadParts = new Dictionary<string, IHeadPartGetter>();
            foreach (var hphHeadPart in state.LoadOrder.PriorityOrder.OnlyEnabled().HeadPart().WinningOverrides())
            {
                if (hphHeadPart.EditorID != null && hphHeadPart.EditorID.StartsWith("00KLH_"))
                {
                    hphHeadParts[hphHeadPart.EditorID] = hphHeadPart;
                }
            }

            var vanillaToHphParts = new Dictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>>();

            foreach (var vanillaHeadPart in state.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
            {
                if (vanillaHeadPart.EditorID == null || vanillaHeadPart.EditorID.StartsWith("00KLH_")) continue;

                var hphKey = hphHeadParts.Keys.FirstOrDefault(k => k.EndsWith(vanillaHeadPart.EditorID));
                if (hphKey == null) continue;

                if (vanillaToHphParts.ContainsKey(vanillaHeadPart.ToLinkGetter())) continue;

                vanillaToHphParts[vanillaHeadPart.ToLinkGetter()] = hphHeadParts[hphKey].ToLinkGetter();

                if (vanillaHeadPart.Flags.HasFlag(HeadPart.Flag.Playable))
                {
                    var gimmeHead = state.PatchMod.HeadParts.GetOrAddAsOverride(vanillaHeadPart);
                    gimmeHead.Flags &= ~HeadPart.Flag.Playable;
                }
            }

            return vanillaToHphParts;
        }
    }
}