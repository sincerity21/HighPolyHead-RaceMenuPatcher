using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
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

            Console.WriteLine("Analyzing load order to determine required patches...");
            var (racesToPatch, npcsToPatch, masters) = PrePatch(state, vanillaToHphParts);

            var mastersToAdd = masters.Count;
            var currentMasterCount = state.PatchMod.ModHeader.MasterReferences.Count;
            if (currentMasterCount + mastersToAdd > 254)
            {
                throw new Exception($"Cannot add {mastersToAdd} new masters to the patch, as it would exceed the 254 master limit. Please reduce the number of plugins that need patching.");
            }
            
            Console.WriteLine("Patching races...");
            var (raceHeadPartsMale, raceHeadPartsFemale) = PatchRaces(state, vanillaToHphParts, racesToPatch);

            Console.WriteLine("Patching NPCs...");
            PatchNpcs(state, vanillaToHphParts, raceHeadPartsMale, raceHeadPartsFemale, npcsToPatch);

            Console.WriteLine("Patching complete!");
        }

        private static Dictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>> BuildVanillaToHphMap(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var hphHeadParts = state.LoadOrder.PriorityOrder.OnlyEnabled().HeadPart().WinningOverrides()
                .Where(h => h.EditorID != null && h.EditorID.StartsWith("00KLH_"))
                .ToDictionary(h => h.EditorID);

            var vanillaToHphParts = new Dictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>>();

            foreach (var vanillaHeadPart in state.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
            {
                if (vanillaHeadPart.EditorID == null || vanillaHeadPart.EditorID.StartsWith("00KLH_")) continue;

                var hphKey = hphHeadParts.Keys.FirstOrDefault(k => k.EndsWith(vanillaHeadPart.EditorID));
                if (hphKey == null) continue;

                if (vanillaToHphParts.ContainsKey(vanillaHeadPart.ToLinkGetter())) continue;
                
                vanillaToHphParts[vanillaHeadPart.ToLinkGetter()] = hphHeadParts[hphKey].ToLinkGetter();

                // If the vanilla head part is already not playable, we assume it's already been patched.
                if (vanillaHeadPart.Flags.HasFlag(HeadPart.Flag.Playable))
                {
                    var gimmeHead = state.PatchMod.HeadParts.GetOrAddAsOverride(vanillaHeadPart);
                    gimmeHead.Flags &= ~HeadPart.Flag.Playable;
                }
            }

            return vanillaToHphParts;
        }

        private static (HashSet<IFormLinkGetter<IRaceGetter>>, HashSet<IFormLinkGetter<INpcGetter>>, HashSet<ModKey>) PrePatch(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            IReadOnlyDictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>> vanillaToHphParts)
        {
            var racesToPatch = new HashSet<IFormLinkGetter<IRaceGetter>>();
            var npcsToPatch = new HashSet<IFormLinkGetter<INpcGetter>>();
            var masters = new HashSet<ModKey>();

            foreach (var raceRecord in state.LoadOrder.PriorityOrder.OnlyEnabled().Race().WinningOverrides())
            {
                if (raceRecord.EditorID == null || raceRecord.HeadData == null) continue;

                var needsPatching = false;
                if (raceRecord.HeadData.Male != null)
                {
                    needsPatching |= raceRecord.HeadData.Male.HeadParts.Any(h => vanillaToHphParts.ContainsKey(h.Head));
                }
                if (raceRecord.HeadData.Female != null)
                {
                    needsPatching |= raceRecord.HeadData.Female.HeadParts.Any(h => vanillaToHphParts.ContainsKey(h.Head));
                }

                if (needsPatching)
                {
                    racesToPatch.Add(raceRecord.ToLinkGetter());
                    masters.Add(raceRecord.FormKey.ModKey);
                }
            }

            foreach (var npcRecord in state.LoadOrder.PriorityOrder.OnlyEnabled().Npc().WinningOverrides())
            {
                if (npcRecord.EditorID == null) continue;

                var needsPatching = false;
                if (npcRecord.EditorID.EndsWith("Preset"))
                {
                    needsPatching = npcRecord.HeadParts.Any(h => vanillaToHphParts.ContainsKey(h));
                }
                else
                {
                    if (racesToPatch.Contains(npcRecord.Race))
                    {
                        needsPatching = true;
                    }
                }
                
                if (needsPatching)
                {
                    npcsToPatch.Add(npcRecord.ToLinkGetter());
                    masters.Add(npcRecord.FormKey.ModKey);
                }
            }

            return (racesToPatch, npcsToPatch, masters);
        }

        private static (Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>, Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>) PatchRaces(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            IReadOnlyDictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>> vanillaToHphParts,
            HashSet<IFormLinkGetter<IRaceGetter>> racesToPatch)
        {
            var raceHeadPartsMale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();
            var raceHeadPartsFemale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();

            foreach (var raceRecord in state.LoadOrder.PriorityOrder.OnlyEnabled().Race().WinningOverrides().Where(r => racesToPatch.Contains(r.ToLinkGetter())))
            {
                var raceOverride = state.PatchMod.Races.GetOrAddAsOverride(raceRecord);
                var changed = false;

                if (raceOverride.HeadData != null)
                {
                    var raceFormLinkGetter = raceOverride.ToLinkGetter();
                    if (raceOverride.HeadData.Female != null)
                    {
                        foreach (var raceHead in raceOverride.HeadData.Female.HeadParts)
                        {
                            if (!vanillaToHphParts.TryGetValue(raceHead.Head, out var part)) continue;

                            raceHeadPartsFemale.GetOrAdd(raceFormLinkGetter).Add(raceHead.Head);
                            changed = true;
                            raceHead.Head.SetTo(part);
                        }
                    }
                    if (raceOverride.HeadData.Male != null)
                    {
                        foreach (var raceHead in raceOverride.HeadData.Male.HeadParts)
                        {
                            if (!vanillaToHphParts.TryGetValue(raceHead.Head, out var part)) continue;

                            raceHeadPartsMale.GetOrAdd(raceFormLinkGetter).Add(raceHead.Head);
                            changed = true;
                            raceHead.Head.SetTo(part);
                        }
                    }
                }
            }

            return (raceHeadPartsMale, raceHeadPartsFemale);
        }

        private static void PatchNpcs(
            IPatcherState<ISkyrimMod, ISkyrimModGetter> state,
            IReadOnlyDictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>> vanillaToHphParts,
            Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>> raceHeadPartsMale,
            Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>> raceHeadPartsFemale,
            HashSet<IFormLinkGetter<INpcGetter>> npcsToPatch)
        {
            foreach (var npcPreset in state.LoadOrder.PriorityOrder.OnlyEnabled().Npc().WinningOverrides().Where(n => npcsToPatch.Contains(n.ToLinkGetter())))
            {
                try
                {
                    if (npcPreset.EditorID == null) continue;
                    var eid = npcPreset.EditorID;

                    var withoutLastTwo = (eid.Length > 2) ? eid[..^2] : eid;

                    var npcOverride = state.PatchMod.Npcs.GetOrAddAsOverride(npcPreset);
                    var changed = false;

                    if (!withoutLastTwo.EndsWith("Preset") && !npcPreset.Race.Equals(Skyrim.Race.FoxRace))
                    {
                        var npcPartTypes = new HashSet<HeadPart.TypeEnum>();

                        foreach (var part in npcOverride.HeadParts)
                        {
                            if (!part.TryResolve(state.LinkCache, out var headPartGetter)) continue;
                            if (headPartGetter.Type != null) npcPartTypes.Add((HeadPart.TypeEnum)headPartGetter.Type);
                        }

                        var raceHeadParts = npcOverride.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
                            ? raceHeadPartsFemale
                            : raceHeadPartsMale;

                        if (!raceHeadParts.TryGetValue(npcOverride.Race, out var currentRaceHeadParts))
                        {
                            continue;
                        }

                        foreach (var part in currentRaceHeadParts)
                        {
                            part.TryResolve(state.LinkCache, out var headPartGetter);
                            if (headPartGetter?.Type == null) continue;
                            if (npcPartTypes.Contains((HeadPart.TypeEnum)headPartGetter.Type)) continue;
                            npcOverride.HeadParts.Add(part);
                            changed = true;
                        }
                    }

                    if (withoutLastTwo.EndsWith("Preset"))
                    {
                        for (var index = 0; index < npcOverride.HeadParts.Count; index++)
                        {
                            if (!vanillaToHphParts.TryGetValue(npcOverride.HeadParts[index], out var replacementHead))
                            {
                                continue;
                            }

                            npcOverride.HeadParts[index] = replacementHead;
                            changed = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error: {e.Message}");
                    Console.WriteLine($"Error NPC: {npcPreset}");
                    Console.WriteLine($"Error NPC EditorID: {npcPreset.EditorID}");
                    Console.WriteLine($"Stack trace: {e.StackTrace}");
                }
            }
        }
    }
}