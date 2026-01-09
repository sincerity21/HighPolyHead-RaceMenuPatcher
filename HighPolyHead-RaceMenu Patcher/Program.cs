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
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "High Poly Head - RaceMenu Patcher.esp")
                .Run(args);
        }
        
        private static readonly ModKey ModKey = ModKey.FromNameAndExtension("High Poly Head.esm");



        private static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            if (!state.LoadOrder.ContainsKey(ModKey))
            {
                throw new Exception("You need High Poly Head mod installed for this patch to do anything.");
            }
            
            Console.WriteLine("Running Test!");

            var masters = new HashSet<ModKey>();
            masters.Add(ModKey.FromNameAndExtension("High Poly Head.esm"));
            
            // Dictionary containing correlation between vanilla headparts to the HPH equivalent
            var vanillaToHphParts = new Dictionary<IFormLinkGetter<IHeadPartGetter>, IFormLinkGetter<IHeadPartGetter>>();
            // Dictionary of the Race record headparts NPCs inherit that need replacing in the presets
            var raceHeadPartsMale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();
            var raceHeadPartsFemale = new Dictionary<IFormLinkGetter<IRaceGetter>, HashSet<IFormLinkGetter<IHeadPartGetter>>>();
            
            // List of Brow headparts
            
            foreach (var hphHeadPart in state.LoadOrder.PriorityOrder.OnlyEnabled().HeadPart().WinningOverrides())
            {
                if (hphHeadPart.EditorID == null || !hphHeadPart.EditorID.StartsWith("00KLH_")) continue;
                // for each HPH record, loop through the vanilla ones again - seems a bit inefficient? compare two lists with LINQ instead?
                foreach (var vanillaHeadPart in state.LoadOrder.PriorityOrder.HeadPart().WinningOverrides())
                {
                    if (vanillaHeadPart.EditorID != null && hphHeadPart.EditorID.EndsWith(vanillaHeadPart.EditorID) 
                                                         && !vanillaHeadPart.EditorID.StartsWith("00KLH_") )
                    {
                        if (!vanillaToHphParts.ContainsKey(vanillaHeadPart.ToLinkGetter()))
                        {
                            vanillaToHphParts[vanillaHeadPart.ToLinkGetter()] = hphHeadPart.ToLinkGetter();
                        }
                        var modKey = vanillaHeadPart.FormKey.ModKey;
                        if (masters.Contains(modKey)) continue;
                        if (masters.Count >= 254)
                            throw new Exception($"Cannot add {modKey} as a master, as the patch has already reached the 254 master limit. Aborting to prevent a corrupt plugin.");
                        masters.Add(modKey);
                        IHeadPart gimmeHead = state.PatchMod.HeadParts.GetOrAddAsOverride(vanillaHeadPart);
                        gimmeHead.Flags &= ~HeadPart.Flag.Playable;
                    }
                }
            }
            
            var hphPartsSet = new HashSet<IFormLinkGetter<IHeadPartGetter>>(vanillaToHphParts.Values);

            foreach (var raceRecord in state.LoadOrder.PriorityOrder.OnlyEnabled().Race().WinningOverrides())
            {
                if (raceRecord.EditorID == null)
                {
                    continue;
                }
                if (raceRecord.HeadData == null)
                {
                    continue;
                }
                var hasMaleOverride = false;
                var hasFemaleOverride = false;
                if (raceRecord.HeadData.Male != null)
                {
                    // male first
                    foreach (var raceHead in raceRecord.HeadData.Male.HeadParts)
                    {
                        if (!raceHead.Head.TryResolve(state.LinkCache, out var head2)) continue;
                        if (!vanillaToHphParts.ContainsKey(head2.ToLinkGetter())) continue;
                        hasMaleOverride = true;
                        break;
                    }
                }
                if (raceRecord.HeadData.Female != null)
                {
                    foreach (var raceHead in raceRecord.HeadData.Female.HeadParts)
                    {
                        if (!raceHead.Head.TryResolve(state.LinkCache, out var head2)) continue;
                        if (!vanillaToHphParts.ContainsKey(head2.ToLinkGetter())) continue;
                        hasFemaleOverride = true;
                        break;
                    }
                }
                if(!hasFemaleOverride && !hasMaleOverride)
                {
                    bool hasHphPart = false;
                    if (raceRecord.HeadData.Male != null) {
                        foreach (var raceHead in raceRecord.HeadData.Male.HeadParts) {
                            if (hphPartsSet.Contains(raceHead.Head)) {
                                hasHphPart = true; 
                                break;
                            }
                        }
                    }
                    if (!hasHphPart && raceRecord.HeadData.Female != null) {
                         foreach (var raceHead in raceRecord.HeadData.Female.HeadParts) {
                            if (hphPartsSet.Contains(raceHead.Head)) {
                                hasHphPart = true; 
                                break;
                            }
                        }
                    }
                    if (hasHphPart) {
                        Console.WriteLine($"Skipping race {raceRecord.EditorID} as it appears to be already patched.");
                    }
                    continue;
                }
                
                var raceOverride = raceRecord.DeepCopy();
                var changed = false;

                if (raceOverride.HeadData != null )
                {
                    var raceFormLinkGetter = raceOverride.ToLinkGetter();
                    if( raceOverride.HeadData.Female != null)
                    {
                        foreach (var raceHead in raceOverride.HeadData.Female.HeadParts)
                        {
                            if (!raceHead.Head.TryResolve(state.LinkCache, out var head2)) continue;
                            if (!vanillaToHphParts.TryGetValue(head2.ToLinkGetter(), out var part)) continue;
                            raceHeadPartsFemale.GetOrAdd(raceFormLinkGetter).Add(head2.ToLinkGetter());
                            changed = true;
                            raceHead.Head.SetTo(part);
                        }
                    }
                    if (raceOverride.HeadData.Male != null)
                    {
                        foreach (var raceHead in raceOverride.HeadData.Male.HeadParts)
                        {
                            if (!raceHead.Head.TryResolve(state.LinkCache, out var head2)) continue;
                            if (!vanillaToHphParts.TryGetValue(head2.ToLinkGetter(), out var part)) continue;
                            raceHeadPartsMale.GetOrAdd(raceFormLinkGetter).Add(head2.ToLinkGetter());
                            changed = true;
                            raceHead.Head.SetTo(part);
                        }
                    }
                }
                if( changed)
                {
                    var modKey = raceRecord.FormKey.ModKey;
                    if (!masters.Contains(modKey))
                    {
                        if (masters.Count >= 254)
                            throw new Exception($"Cannot add {modKey} as a master, as the patch has already reached the 254 master limit. Aborting to prevent a corrupt plugin.");
                        masters.Add(modKey);
                    }
                    state.PatchMod.Races.Set(raceOverride);
                }

            }
            // Now NPC records for preset defaults
            // by now you can tell ive given up on efficiency and just wanted to get the damn thing working
            foreach(var npcPreset in state.LoadOrder.PriorityOrder.OnlyEnabled().Npc().WinningOverrides())
            {
                try
                {
                    if (npcPreset.EditorID == null) continue;
                    var eid = npcPreset.EditorID;

                    var withoutLastTwo = (eid.Length > 2) ? eid[..^2] : eid;

                    if (!withoutLastTwo.EndsWith("Preset") && !npcPreset.Race.Equals(Skyrim.Race.FoxRace))
                    {

                        var changed = false;
                        var npcPartTypes = new HashSet<HeadPart.TypeEnum>();

                        var npcDeepCopy = npcPreset.DeepCopy();

                        foreach (var part in npcDeepCopy.HeadParts)
                        {
                            if (!part.TryResolve(state.LinkCache, out var headPartGetter)) continue;
                            if (headPartGetter.Type != null) npcPartTypes.Add((HeadPart.TypeEnum) headPartGetter.Type);
                        }

                        var raceHeadParts = npcDeepCopy.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
                            ? raceHeadPartsFemale
                            : raceHeadPartsMale;

                        if (!raceHeadParts.TryGetValue(npcDeepCopy.Race, out var currentRaceHeadParts))
                        {
                            continue;
                        }

                        foreach (var part in currentRaceHeadParts)
                        {
                            part.TryResolve(state.LinkCache, out var headPartGetter);
                            if (headPartGetter?.Type == null) continue;
                            if (npcPartTypes.Contains((HeadPart.TypeEnum) headPartGetter.Type)) continue;
                            if (vanillaToHphParts.TryGetValue(part, out var hphEquivalent))
                            {
                                npcDeepCopy.HeadParts.Add(hphEquivalent);
                                changed = true;
                            }
                        }

                        if (changed)
                        {
                            var modKey = npcPreset.FormKey.ModKey;
                            if (!masters.Contains(modKey))
                            {
                                if (masters.Count >= 254)
                                    throw new Exception($"Cannot add {modKey} as a master, as the patch has already reached the 254 master limit. Aborting to prevent a corrupt plugin.");
                                masters.Add(modKey);
                            }
                            state.PatchMod.Npcs.Set(npcDeepCopy);
                        }
                    }

                    if (!withoutLastTwo.EndsWith("Preset")) continue;
                    
                    bool alreadyPatched = true;
                    foreach(var part in npcPreset.HeadParts) {
                        if (vanillaToHphParts.ContainsKey(part)) {
                            alreadyPatched = false;
                            break;
                        }
                    }
                    if (alreadyPatched) {
                        bool hasHphPart = false;
                        foreach(var part in npcPreset.HeadParts) {
                            if (hphPartsSet.Contains(part)) {
                                hasHphPart = true;
                                break;
                            }
                        }
                        if (hasHphPart) {
                            Console.WriteLine($"Skipping NPC preset {npcPreset.EditorID} as it appears to be already patched.");
                            continue;
                        }
                    }
                    
                    var npcOverride = state.PatchMod.Npcs.GetOrAddAsOverride(npcPreset);
                    for (var index = 0; index < npcOverride.HeadParts.Count; index++)
                    {
                        if (!vanillaToHphParts.TryGetValue(npcOverride.HeadParts[index], out var replacementHead))
                        {
                            continue;
                        }

                        npcOverride.HeadParts[index] = replacementHead;
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